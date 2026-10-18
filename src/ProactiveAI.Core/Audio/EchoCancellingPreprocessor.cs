namespace ProactiveAI.Core.Audio;

/// <summary>A run of contiguous processed audio on the session timeline.</summary>
public readonly record struct AudioPiece(TimeSpan Start, short[] Samples, bool Discontinuity);

/// <summary>
/// Transforms captured audio before VAD, transcription and the in-memory timeline. May hold audio briefly
/// (e.g. while waiting for an echo reference); returned pieces are in time order. Called from a single thread.
/// </summary>
public interface IAudioPreprocessor
{
    IReadOnlyList<AudioPiece> Process(TimeSpan start, short[] samples, bool discontinuity);
}

public sealed record EchoCancellingOptions
{
    /// <summary>Reference is read this much ahead of the microphone so slightly negative timestamp lags are still modeled.</summary>
    public TimeSpan Lead { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Echo path length modeled by the adaptive filter (lag up to Tail − Lead).</summary>
    public TimeSpan Tail { get; init; } = TimeSpan.FromMilliseconds(340);

    /// <summary>Longest a microphone block waits for the reference before it is processed with what is available.</summary>
    public TimeSpan MaxHold { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>When the reference ended this long before a block, playback is idle and the block is not held.</summary>
    public TimeSpan IdleReference { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Microphone timestamps further than this from the sample count re-anchor the block timing (clock drift).</summary>
    public TimeSpan Resync { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Gaps larger than this are a discontinuity: held audio is released unprocessed and timing restarts.</summary>
    public TimeSpan Discontinuity { get; init; } = TimeSpan.FromMilliseconds(80);
}

/// <summary>
/// Removes loudspeaker echo from the raw microphone signal with <see cref="EchoCanceller"/>, using the
/// captured system playback (its <see cref="AudioTimeline"/>) as the time-aligned reference. Microphone
/// block times are derived from the sample count since the last anchor so the reference stays sample aligned.
/// </summary>
public sealed class EchoCancellingPreprocessor : IAudioPreprocessor
{
    private const int B = EchoCanceller.Block;
    private readonly Func<AudioTimeline?> _reference;
    private readonly EchoCancellingOptions _o;
    private readonly EchoCanceller _aec;
    private readonly short[] _zeros = new short[B];
    private readonly short[] _ref = new short[B];
    private short[] _pending = new short[8 * B];
    private int _count;
    private TimeSpan _anchor;
    private long _consumed;
    private bool _anchored;
    private bool _discontinuityPending;
    private long _blocks;
    private long _blocksWithReference;
    private long _blocksHeldOut;

    public EchoCancellingPreprocessor(Func<AudioTimeline?> reference, EchoCancellingOptions? options = null)
    {
        _reference = reference;
        _o = options ?? new EchoCancellingOptions();
        _aec = new EchoCanceller(_o.Tail);
    }

    public EchoCanceller Canceller => _aec;

    public long Blocks => Interlocked.Read(ref _blocks);

    /// <summary>Blocks processed with playback audio available as reference.</summary>
    public long BlocksWithReference => Interlocked.Read(ref _blocksWithReference);

    /// <summary>Blocks processed after waiting <see cref="EchoCancellingOptions.MaxHold"/> without a complete reference.</summary>
    public long BlocksHeldOut => Interlocked.Read(ref _blocksHeldOut);

    private TimeSpan PendingStart => _anchor + PipelineAudio.Duration(_consumed);

    public IReadOnlyList<AudioPiece> Process(TimeSpan start, short[] samples, bool discontinuity)
    {
        List<AudioPiece>? pieces = null;
        if (!_anchored)
        {
            Anchor(start);
            _discontinuityPending = true;
        }
        else
        {
            var drift = start - (PendingStart + PipelineAudio.Duration(_count));
            if (discontinuity || drift.Duration() > _o.Discontinuity)
            {
                if (_count > 0)
                {
                    // Release held audio unprocessed rather than lose local speech.
                    (pieces ??= new()).Add(new AudioPiece(PendingStart, _pending.AsSpan(0, _count).ToArray(), _discontinuityPending));
                }
                Anchor(start);
                _discontinuityPending = true;
            }
            else if (drift.Duration() > _o.Resync)
            {
                _anchor += drift;
            }
        }

        if (_pending.Length < _count + samples.Length)
        {
            Array.Resize(ref _pending, Math.Max(_pending.Length * 2, _count + samples.Length));
        }
        samples.CopyTo(_pending, _count);
        _count += samples.Length;

        int ready = 0;
        var firstStart = PendingStart;
        var latestEnd = PendingStart + PipelineAudio.Duration(_count);
        short[]? output = null;
        while (_count - ready * B >= B)
        {
            var blockStart = PendingStart + PipelineAudio.Duration((long)ready * B);
            var blockEnd = blockStart + PipelineAudio.Duration(B);
            var from = blockStart + _o.Lead;
            var to = blockEnd + _o.Lead;
            var timeline = _reference();
            var refEnd = timeline?.End;
            bool complete = refEnd is { } e1 && e1 >= to;
            bool idle = refEnd is not { } e2 || e2 < blockStart - _o.IdleReference;
            bool heldOut = latestEnd - blockEnd >= _o.MaxHold;
            if (!complete && !idle && !heldOut)
            {
                break;
            }
            ReadOnlySpan<short> reference = _zeros;
            if (!idle && timeline?.Extract(from, to) is { } r)
            {
                Array.Clear(_ref);
                r.AsSpan(0, Math.Min(B, r.Length)).CopyTo(_ref);
                reference = _ref;
                Interlocked.Increment(ref _blocksWithReference);
            }
            if (!complete && !idle)
            {
                Interlocked.Increment(ref _blocksHeldOut);
            }
            output ??= new short[(_count / B) * B];
            _aec.Process(_pending.AsSpan(ready * B, B), reference, output.AsSpan(ready * B, B));
            Interlocked.Increment(ref _blocks);
            ready++;
        }

        if (ready > 0)
        {
            int n = ready * B;
            var processed = output!.Length == n ? output : output.AsSpan(0, n).ToArray();
            (pieces ??= new()).Add(new AudioPiece(firstStart, processed, _discontinuityPending));
            _discontinuityPending = false;
            Array.Copy(_pending, n, _pending, 0, _count - n);
            _count -= n;
            _consumed += n;
        }
        return pieces ?? (IReadOnlyList<AudioPiece>)Array.Empty<AudioPiece>();
    }

    /// <summary>Short status for diagnostics and the UI.</summary>
    public string Describe()
    {
        if (BlocksWithReference == 0)
        {
            return "Built-in echo canceller ready (no system playback yet)";
        }
        return _aec.Adapted
            ? $"Built-in echo canceller active: removing ≈{Math.Max(0, _aec.EchoReductionDb):F0} dB of loudspeaker echo"
            : "Built-in echo canceller is learning the loudspeaker echo path";
    }

    private void Anchor(TimeSpan start)
    {
        _anchor = start;
        _consumed = 0;
        _count = 0;
        _anchored = true;
    }
}
