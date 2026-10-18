using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Transcript;

namespace ProactiveAI.Core.Session;

/// <summary>Callbacks from a source pipeline. Invoked on pipeline worker threads.</summary>
public interface ISourcePipelineSink
{
    void OnTurnOpened(SourcePipeline pipeline, TurnKey key, TimeSpan start);

    /// <summary>
    /// The open turn has been silent for <see cref="SourcePipelineOptions.PauseThreshold"/> (<paramref name="paused"/> true)
    /// or speech resumed after such a pause (false). The turn itself is only closed after the longer VAD end silence.
    /// </summary>
    void OnTurnPause(SourcePipeline pipeline, TurnKey key, bool paused);

    void OnTurnClosed(SourcePipeline pipeline, TurnKey key, TimeSpan start, TimeSpan end, bool committed);

    void OnTranscriptDelta(SourcePipeline pipeline, TurnKey key, string delta, TimeSpan start, TimeSpan end);

    void OnTranscriptFinal(SourcePipeline pipeline, TurnKey key, string text, TimeSpan start, TimeSpan end, string itemId, UsageInfo? usage);

    void OnTranscriptItemFailed(SourcePipeline pipeline, TurnKey key, string reason);

    void OnAudioGap(SourcePipeline pipeline, TimeSpan at, TimeSpan dropped, string reason);

    void OnLevel(SourcePipeline pipeline, double levelDbfs, bool speech);

    void OnStreamFailed(SourcePipeline pipeline, ProviderFailureKind kind, string detail);
}

public sealed record SourcePipelineOptions
{
    /// <summary>Live-path queue capacity expressed as audio duration (≈2 s of 24 kHz PCM16 = 96 KB).</summary>
    public TimeSpan QueueCapacity { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan SendBatch { get; init; } = TimeSpan.FromMilliseconds(100);
    public VadOptions Vad { get; init; } = new();
    /// <summary>Trailing silence after which a pause in the open turn is reported (early question detection).</summary>
    public TimeSpan PauseThreshold { get; init; } = TimeSpan.FromMilliseconds(300);
}

public sealed record PipelineMetrics(int QueuedMs, long DroppedMs, long SentMs, int Turns, int Overflows, double? DriftPpm);

/// <summary>
/// Live transcription path for one source generation. Capture callbacks only enqueue into a bounded
/// queue measured in milliseconds; a worker performs VAD, sends audio and commits turns; another
/// worker maps provider events onto local turns. Overflow triggers a controlled restart: the open
/// turn is cleared, stale audio is discarded and a gap is recorded — old audio is never replayed.
/// </summary>
public sealed class SourcePipeline : IAsyncDisposable
{
    private readonly Channel<AudioChunk> _queue;
    private readonly ISourcePipelineSink _sink;
    private readonly ITranscriptionStream _stream;
    private readonly AudioTimeline _timeline;
    private readonly SessionClock _clock;
    private readonly ILogger _log;
    private readonly SourcePipelineOptions _o;
    private readonly VoiceActivityDetector _vad;
    private readonly DriftMeter _drift = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _turnGate = new();
    private readonly LinkedList<LocalTurn> _awaitingItem = new();
    private readonly Dictionary<string, LocalTurn> _byItem = new(StringComparer.Ordinal);
    private readonly int _capacitySamples;
    private readonly List<short> _sendBuffer = new();
    private readonly List<short> _frameRemainder = new();
    private readonly IAudioPreprocessor? _preprocessor;
    private readonly AudioTimeline? _rawTimeline;

    private Task? _audioWorker;
    private Task? _eventWorker;
    private LocalTurn? _open;
    private long _turnCounter;
    private int _queuedSamples;
    private long _droppedSamples;
    private long _totalDroppedSamples;
    private long _sentSamples;
    private int _overflowPending;
    private int _overflows;
    private TimeSpan _lastAudioEnd;
    private TimeSpan? _lastChunkEnd;
    private long _packets;
    private DateTimeOffset _lastChunkUtc;
    private TimeSpan _overflowStart;
    private bool _stopping;
    private bool _pauseSignaled;

    public SourcePipeline(
        AudioSourceKind source,
        long sessionId,
        int generation,
        ITranscriptionStream stream,
        AudioTimeline timeline,
        SessionClock clock,
        ISourcePipelineSink sink,
        SourcePipelineOptions options,
        ILogger log,
        IAudioPreprocessor? preprocessor = null,
        AudioTimeline? rawTimeline = null)
    {
        Source = source;
        SessionId = sessionId;
        Generation = generation;
        _stream = stream;
        _timeline = timeline;
        _clock = clock;
        _sink = sink;
        _o = options;
        _log = log;
        _preprocessor = preprocessor;
        _rawTimeline = rawTimeline;
        _vad = new VoiceActivityDetector(options.Vad);
        _capacitySamples = PipelineAudio.Samples(options.QueueCapacity);
        _queue = Channel.CreateUnbounded<AudioChunk>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    }

    public AudioSourceKind Source { get; }
    public long SessionId { get; }
    public int Generation { get; }

    public PipelineMetrics Metrics => new(
        (int)PipelineAudio.Duration(Volatile.Read(ref _queuedSamples)).TotalMilliseconds,
        (long)PipelineAudio.Duration(Interlocked.Read(ref _totalDroppedSamples) + Interlocked.Read(ref _droppedSamples)).TotalMilliseconds,
        (long)PipelineAudio.Duration(Interlocked.Read(ref _sentSamples)).TotalMilliseconds,
        (int)Interlocked.Read(ref _turnCounter),
        Volatile.Read(ref _overflows),
        _drift.DriftPpm);

    public void Start()
    {
        _lastChunkUtc = _clock.TimeProvider.GetUtcNow();
        _audioWorker = Task.Run(() => AudioLoopAsync(_cts.Token));
        _eventWorker = Task.Run(() => EventLoopAsync(_cts.Token));
    }

    /// <summary>Capture-thread entry point. Never blocks; drops audio beyond the queue capacity.</summary>
    public void Enqueue(AudioChunk chunk)
    {
        if (_stopping)
        {
            return;
        }
        int n = chunk.Samples.Length;
        if (Volatile.Read(ref _queuedSamples) + n > _capacitySamples || Volatile.Read(ref _overflowPending) == 1)
        {
            if (Interlocked.Exchange(ref _overflowPending, 1) == 0)
            {
                _overflowStart = _clock.FromQpc100ns(chunk.Qpc100ns);
            }
            Interlocked.Add(ref _droppedSamples, n);
            return;
        }
        Interlocked.Add(ref _queuedSamples, n);
        _queue.Writer.TryWrite(chunk);
    }

    /// <summary>Stops the pipeline without committing the open turn; unsent audio is discarded.</summary>
    public async Task StopAsync()
    {
        if (_stopping)
        {
            return;
        }
        _stopping = true;
        _queue.Writer.TryComplete();
        _cts.Cancel();
        foreach (var t in new[] { _audioWorker, _eventWorker })
        {
            if (t is null)
            {
                continue;
            }
            try
            {
                await t.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
                // cancellation or timeout during shutdown
            }
        }
        while (_queue.Reader.TryRead(out _))
        {
        }
        Interlocked.Exchange(ref _queuedSamples, 0);
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug("Stream dispose failed: {Type}", ex.GetType().Name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task AudioLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                AudioChunk? chunk = null;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromMilliseconds(100));
                    try
                    {
                        if (await _queue.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false))
                        {
                            _queue.Reader.TryRead(out chunk);
                        }
                        else
                        {
                            return; // completed
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // idle timeout
                    }
                }

                if (Volatile.Read(ref _overflowPending) == 1)
                {
                    await HandleOverflowAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (chunk is null)
                {
                    var silence = _clock.TimeProvider.GetUtcNow() - _lastChunkUtc;
                    // Loopback delivers nothing while playback is silent: the wall-clock gap counts as silence.
                    UpdatePause(speech: false, _vad.TrailingSilence + silence);
                    if (_open is not null && _vad.ShouldEndOnGap(silence))
                    {
                        await FlushSendAsync(ct).ConfigureAwait(false);
                        await CloseTurnAsync(_lastAudioEnd, commit: true, ct).ConfigureAwait(false);
                    }
                    continue;
                }

                Interlocked.Add(ref _queuedSamples, -chunk.Samples.Length);
                _lastChunkUtc = _clock.TimeProvider.GetUtcNow();
                _drift.Observe(chunk.Qpc100ns, chunk.Samples.Length, chunk.Discontinuity);
                var chunkStart = _clock.FromQpc100ns(chunk.Qpc100ns);
                if (Interlocked.Increment(ref _packets) == 1)
                {
                    _log.LogInformation("First audio received source={Source} packetMs={Ms:F1}", Source, PipelineAudio.Duration(chunk.Samples.Length).TotalMilliseconds);
                }
                if (_preprocessor is null)
                {
                    await ProcessAudioAsync(chunkStart, chunk.Samples, chunk.Discontinuity, ct).ConfigureAwait(false);
                }
                else
                {
                    _rawTimeline?.Append(chunkStart, chunk.Samples);
                    foreach (var piece in _preprocessor.Process(chunkStart, chunk.Samples, chunk.Discontinuity))
                    {
                        await ProcessAudioAsync(piece.Start, piece.Samples, piece.Discontinuity, ct).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!_stopping)
        {
            _log.LogWarning("Audio loop failed source={Source}: {Type}", Source, ex.GetType().Name);
            _sink.OnStreamFailed(this, ex is ProviderException pe ? pe.Kind : ProviderFailureKind.Network, ex is ProviderException p2 ? p2.Message : ex.GetType().Name);
        }
    }

    private async Task ProcessAudioAsync(TimeSpan start, short[] samples, bool discontinuity, CancellationToken ct)
    {
        // WASAPI delivers ~10 ms packets, smaller than a 20 ms VAD frame: continuity is judged against the
        // end of the previous packet, not the previous complete frame.
        if (discontinuity || _lastChunkEnd is not { } prevEnd || (start - prevEnd).Duration() > TimeSpan.FromMilliseconds(80))
        {
            _frameRemainder.Clear();
        }
        _lastChunkEnd = start + PipelineAudio.Duration(samples.Length);
        _timeline.Append(start, samples);
        await ProcessFramesAsync(samples, start, ct).ConfigureAwait(false);
    }

    private async Task ProcessFramesAsync(short[] samples, TimeSpan chunkStart, CancellationToken ct)
    {
        // Frame boundaries carry over between contiguous chunks.
        var frameStartTime = chunkStart - PipelineAudio.Duration(_frameRemainder.Count);
        int offset = 0;
        bool anySpeech = false;
        while (true)
        {
            int need = PipelineAudio.FrameSamples - _frameRemainder.Count;
            if (samples.Length - offset < need)
            {
                for (int i = offset; i < samples.Length; i++)
                {
                    _frameRemainder.Add(samples[i]);
                }
                break;
            }
            short[] frame = new short[PipelineAudio.FrameSamples];
            _frameRemainder.CopyTo(frame);
            Array.Copy(samples, offset, frame, _frameRemainder.Count, need);
            offset += need;
            _frameRemainder.Clear();

            var fStart = frameStartTime;
            var fEnd = fStart + PipelineAudio.Duration(frame.Length);
            frameStartTime = fEnd;
            _lastAudioEnd = fEnd;

            var decision = _vad.Process(frame, out var preRoll);
            anySpeech |= decision.IsSpeech;
            switch (decision.Action)
            {
                case VadAction.StartTurn:
                    var start = fStart - PipelineAudio.Duration(preRoll.Sum(p => p.Length));
                    OpenTurn(start);
                    foreach (var p in preRoll)
                    {
                        await SendAsync(p, ct).ConfigureAwait(false);
                    }
                    await SendAsync(frame, ct).ConfigureAwait(false);
                    break;
                case VadAction.Continue:
                    await SendAsync(frame, ct).ConfigureAwait(false);
                    if (_open is not null)
                    {
                        _open.End = fEnd;
                    }
                    break;
                case VadAction.EndTurn:
                    await SendAsync(frame, ct).ConfigureAwait(false);
                    await FlushSendAsync(ct).ConfigureAwait(false);
                    await CloseTurnAsync(fEnd, commit: true, ct).ConfigureAwait(false);
                    break;
                case VadAction.SplitTurn:
                    await SendAsync(frame, ct).ConfigureAwait(false);
                    await FlushSendAsync(ct).ConfigureAwait(false);
                    await CloseTurnAsync(fEnd, commit: true, ct).ConfigureAwait(false);
                    OpenTurn(fEnd);
                    break;
            }
            if (decision.Action == VadAction.Continue)
            {
                UpdatePause(decision.IsSpeech, _vad.TrailingSilence);
            }
        }
        _sink.OnLevel(this, VoiceActivityDetector.LevelDbfs(samples), anySpeech || _open is not null);
    }

    /// <summary>Reports a pause of the open turn once it reaches the threshold, and its end when speech resumes.</summary>
    private void UpdatePause(bool speech, TimeSpan trailingSilence)
    {
        var open = _open;
        if (open is null)
        {
            return;
        }
        if (speech)
        {
            if (_pauseSignaled)
            {
                _pauseSignaled = false;
                _sink.OnTurnPause(this, open.Key, paused: false);
            }
        }
        else if (!_pauseSignaled && trailingSilence >= _o.PauseThreshold)
        {
            _pauseSignaled = true;
            _sink.OnTurnPause(this, open.Key, paused: true);
        }
    }

    private void OpenTurn(TimeSpan start)
    {
        var turn = new LocalTurn(new TurnKey(SessionId, Source, Generation, Interlocked.Increment(ref _turnCounter)), start) { End = start };
        lock (_turnGate)
        {
            _open = turn;
            _awaitingItem.AddLast(turn);
        }
        _pauseSignaled = false;
        _sink.OnTurnOpened(this, turn.Key, start);
    }

    private async Task CloseTurnAsync(TimeSpan end, bool commit, CancellationToken ct)
    {
        LocalTurn? turn;
        lock (_turnGate)
        {
            turn = _open;
            _open = null;
        }
        if (turn is null)
        {
            return;
        }
        turn.End = end;
        bool longEnough = turn.End - turn.Start >= _o.Vad.MinTurn && turn.SentSamples >= PipelineAudio.Samples(TimeSpan.FromMilliseconds(120));
        if (commit && longEnough)
        {
            turn.Committed = true;
            await _stream.CommitAsync($"commit-{turn.Key.TurnId}", ct).ConfigureAwait(false);
        }
        else
        {
            lock (_turnGate)
            {
                _awaitingItem.Remove(turn);
            }
            await _stream.ClearAsync(ct).ConfigureAwait(false);
        }
        _sink.OnTurnClosed(this, turn.Key, turn.Start, turn.End, turn.Committed);
        _log.LogInformation("Turn {Turn} source={Source} durationMs={Ms:F0} committed={Committed}",
            turn.Key.TurnId, Source, (turn.End - turn.Start).TotalMilliseconds, turn.Committed);
    }

    private async Task SendAsync(short[] frame, CancellationToken ct)
    {
        if (_open is null)
        {
            return;
        }
        _sendBuffer.AddRange(frame);
        _open.SentSamples += frame.Length;
        if (_sendBuffer.Count >= PipelineAudio.Samples(_o.SendBatch))
        {
            await FlushSendAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task FlushSendAsync(CancellationToken ct)
    {
        if (_sendBuffer.Count == 0)
        {
            return;
        }
        var data = _sendBuffer.ToArray();
        _sendBuffer.Clear();
        await _stream.AppendAsync(data, ct).ConfigureAwait(false);
        Interlocked.Add(ref _sentSamples, data.Length);
    }

    private async Task HandleOverflowAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _overflows);
        while (_queue.Reader.TryRead(out var stale))
        {
            Interlocked.Add(ref _queuedSamples, -stale.Samples.Length);
            Interlocked.Add(ref _droppedSamples, stale.Samples.Length);
        }
        _sendBuffer.Clear();
        _frameRemainder.Clear();
        LocalTurn? turn;
        lock (_turnGate)
        {
            turn = _open;
            _open = null;
            if (turn is not null)
            {
                _awaitingItem.Remove(turn);
            }
        }
        await _stream.ClearAsync(ct).ConfigureAwait(false);
        if (turn is not null)
        {
            _sink.OnTranscriptItemFailed(this, turn.Key, "Audio dropped: transcription could not keep up");
            _sink.OnTurnClosed(this, turn.Key, turn.Start, turn.End, false);
        }
        _vad.Reset();
        var dropped = PipelineAudio.Duration(Interlocked.Exchange(ref _droppedSamples, 0));
        Interlocked.Add(ref _totalDroppedSamples, PipelineAudio.Samples(dropped));
        _sink.OnAudioGap(this, _overflowStart, dropped, "Transcription backlog: stale audio discarded");
        _log.LogWarning("Queue overflow source={Source} droppedMs={Dropped}", Source, (int)dropped.TotalMilliseconds);
        Interlocked.Exchange(ref _overflowPending, 0);
    }

    private async Task EventLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var ev in _stream.ReadEventsAsync(ct).ConfigureAwait(false))
            {
                if (_stopping)
                {
                    return;
                }
                switch (ev)
                {
                    case TranscriptionCommitted c:
                        Assign(c.ItemId, preferCommitted: true);
                        break;
                    case TranscriptionDelta d:
                        if (Assign(d.ItemId, preferCommitted: false) is { } dt && !string.IsNullOrEmpty(d.Delta))
                        {
                            _sink.OnTranscriptDelta(this, dt.Key, d.Delta, dt.Start, dt.End);
                        }
                        break;
                    case TranscriptionCompleted f:
                        if (Assign(f.ItemId, preferCommitted: true) is { } ft)
                        {
                            _sink.OnTranscriptFinal(this, ft.Key, f.Transcript, ft.Start, ft.End, f.ItemId, f.Usage);
                            lock (_turnGate)
                            {
                                _byItem.Remove(f.ItemId);
                            }
                        }
                        break;
                    case TranscriptionItemFailed x:
                        if (Assign(x.ItemId, preferCommitted: true) is { } xt)
                        {
                            _sink.OnTranscriptItemFailed(this, xt.Key, x.SanitizedMessage);
                        }
                        break;
                    case TranscriptionError err when err.ClientEventId is { } cid && cid.StartsWith("commit-", StringComparison.Ordinal):
                        DropRejectedCommit(cid);
                        _log.LogWarning("Commit rejected source={Source} code={Code}", Source, err.Code);
                        break;
                    case TranscriptionError err when err.Fatal:
                        _sink.OnStreamFailed(this, err.Kind, err.SanitizedMessage);
                        return;
                    case TranscriptionError err:
                        _log.LogWarning("Transcription error source={Source} kind={Kind} code={Code}", Source, err.Kind, err.Code);
                        break;
                    case TranscriptionClosed closed:
                        if (!_stopping)
                        {
                            _sink.OnStreamFailed(this, closed.Kind == ProviderFailureKind.None ? ProviderFailureKind.Network : closed.Kind, closed.Detail ?? "connection closed");
                        }
                        return;
                }
            }
            if (!_stopping)
            {
                _sink.OnStreamFailed(this, ProviderFailureKind.Network, "connection closed");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!_stopping)
        {
            _sink.OnStreamFailed(this, ex is ProviderException pe ? pe.Kind : ProviderFailureKind.Network, ex is ProviderException p2 ? p2.Message : ex.GetType().Name);
        }
    }

    /// <summary>
    /// Maps a provider item id to a local turn. Items are created in commit order, so the first
    /// unseen id belongs to the oldest turn still awaiting one (FIFO).
    /// </summary>
    private LocalTurn? Assign(string itemId, bool preferCommitted)
    {
        lock (_turnGate)
        {
            if (_byItem.TryGetValue(itemId, out var known))
            {
                return known;
            }
            var node = _awaitingItem.First;
            if (node is null)
            {
                return null;
            }
            if (preferCommitted && !node.Value.Committed && node.Value != _open)
            {
                return null;
            }
            _awaitingItem.RemoveFirst();
            node.Value.ItemId = itemId;
            _byItem[itemId] = node.Value;
            return node.Value;
        }
    }

    private void DropRejectedCommit(string clientEventId)
    {
        lock (_turnGate)
        {
            var node = _awaitingItem.First;
            while (node is not null)
            {
                if ($"commit-{node.Value.Key.TurnId}" == clientEventId)
                {
                    _awaitingItem.Remove(node);
                    _sink.OnTranscriptItemFailed(this, node.Value.Key, "Turn rejected by provider");
                    return;
                }
                node = node.Next;
            }
        }
    }

    private sealed class LocalTurn(TurnKey key, TimeSpan start)
    {
        public TurnKey Key { get; } = key;
        public TimeSpan Start { get; } = start;
        public TimeSpan End { get; set; }
        public bool Committed { get; set; }
        public string? ItemId { get; set; }
        public int SentSamples { get; set; }
    }
}
