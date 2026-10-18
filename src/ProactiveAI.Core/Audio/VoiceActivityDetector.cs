namespace ProactiveAI.Core.Audio;

public sealed record VadOptions
{
    public int StartFrames { get; init; } = 3;               // 60 ms of speech to open a turn
    public TimeSpan EndSilence { get; init; } = TimeSpan.FromMilliseconds(700);
    public TimeSpan PreRoll { get; init; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan SoftMaxTurn { get; init; } = TimeSpan.FromSeconds(12);
    public TimeSpan HardMaxTurn { get; init; } = TimeSpan.FromSeconds(18);
    public TimeSpan SoftSplitSilence { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan MinTurn { get; init; } = TimeSpan.FromMilliseconds(250);
    public double MinSpeechDbfs { get; init; } = -48;
    public double SnrDb { get; init; } = 9;
}

public enum VadAction
{
    /// <summary>Frame is not part of a turn (kept in pre-roll).</summary>
    Idle,
    /// <summary>A turn starts; send the returned pre-roll frames, then this frame.</summary>
    StartTurn,
    /// <summary>Send this frame within the open turn.</summary>
    Continue,
    /// <summary>Send this frame, then commit the turn.</summary>
    EndTurn,
    /// <summary>Send this frame, commit, and immediately open a new turn (long monologue split).</summary>
    SplitTurn,
}

public readonly record struct VadDecision(VadAction Action, bool IsSpeech, double LevelDbfs);

/// <summary>
/// Energy-based client-side voice activity detector with an adaptive noise floor. The live
/// transcription model does not provide server VAD, so the client decides turn boundaries.
/// Operates on 20 ms frames of pipeline audio.
/// </summary>
public sealed class VoiceActivityDetector
{
    private readonly VadOptions _o;
    private readonly Queue<short[]> _preRoll = new();
    private readonly int _preRollFrames;
    private double _noiseFloorDb = -60;
    private int _speechRun;
    private bool _inTurn;
    private int _turnFrames;
    private int _silenceFrames;

    public VoiceActivityDetector(VadOptions? options = null)
    {
        _o = options ?? new VadOptions();
        _preRollFrames = Math.Max(1, (int)(_o.PreRoll.TotalMilliseconds / 20));
    }

    public bool InTurn => _inTurn;
    public double NoiseFloorDb => _noiseFloorDb;
    public TimeSpan TurnDuration => TimeSpan.FromMilliseconds(_turnFrames * 20);

    /// <summary>Silence at the end of the open turn so far (zero when no turn is open).</summary>
    public TimeSpan TrailingSilence => _inTurn ? TimeSpan.FromMilliseconds(_silenceFrames * 20) : TimeSpan.Zero;

    public static double LevelDbfs(ReadOnlySpan<short> frame)
    {
        if (frame.IsEmpty)
        {
            return -120;
        }
        double sum = 0;
        foreach (short s in frame)
        {
            double v = s / 32768.0;
            sum += v * v;
        }
        double rms = Math.Sqrt(sum / frame.Length);
        return rms <= 1e-6 ? -120 : 20 * Math.Log10(rms);
    }

    /// <summary>Processes one frame. When the action is StartTurn, <paramref name="preRoll"/> holds frames to send first.</summary>
    public VadDecision Process(short[] frame, out IReadOnlyList<short[]> preRoll)
    {
        preRoll = Array.Empty<short[]>();
        double db = LevelDbfs(frame);
        bool loud = db > Math.Max(_o.MinSpeechDbfs, _noiseFloorDb + _o.SnrDb);
        UpdateNoiseFloor(db, loud);

        if (!_inTurn)
        {
            _speechRun = loud ? _speechRun + 1 : 0;
            if (_speechRun >= _o.StartFrames)
            {
                _inTurn = true;
                _turnFrames = 1 + _preRoll.Count;
                _silenceFrames = 0;
                _speechRun = 0;
                preRoll = _preRoll.ToArray();
                _preRoll.Clear();
                return new VadDecision(VadAction.StartTurn, true, db);
            }
            _preRoll.Enqueue(frame);
            while (_preRoll.Count > _preRollFrames)
            {
                _preRoll.Dequeue();
            }
            return new VadDecision(VadAction.Idle, loud, db);
        }

        _turnFrames++;
        _silenceFrames = loud ? 0 : _silenceFrames + 1;
        var turn = TimeSpan.FromMilliseconds(_turnFrames * 20);
        var silence = TimeSpan.FromMilliseconds(_silenceFrames * 20);

        if (silence >= _o.EndSilence)
        {
            ResetTurn();
            return new VadDecision(VadAction.EndTurn, loud, db);
        }
        if ((turn >= _o.SoftMaxTurn && silence >= _o.SoftSplitSilence) || turn >= _o.HardMaxTurn)
        {
            _turnFrames = 0;
            _silenceFrames = 0;
            return new VadDecision(VadAction.SplitTurn, loud, db);
        }
        return new VadDecision(VadAction.Continue, loud, db);
    }

    /// <summary>Called when no audio arrived for <paramref name="silence"/> (loopback delivers nothing while idle).</summary>
    public bool ShouldEndOnGap(TimeSpan silence)
    {
        if (!_inTurn)
        {
            return false;
        }
        if (silence + TimeSpan.FromMilliseconds(_silenceFrames * 20) >= _o.EndSilence)
        {
            ResetTurn();
            return true;
        }
        return false;
    }

    public void Reset()
    {
        ResetTurn();
        _preRoll.Clear();
        _speechRun = 0;
    }

    private void ResetTurn()
    {
        _inTurn = false;
        _turnFrames = 0;
        _silenceFrames = 0;
        _speechRun = 0;
    }

    private void UpdateNoiseFloor(double db, bool loud)
    {
        db = Math.Max(db, -90);
        if (db < _noiseFloorDb)
        {
            _noiseFloorDb += (db - _noiseFloorDb) * 0.3; // fall quickly
        }
        else if (!loud)
        {
            _noiseFloorDb += (db - _noiseFloorDb) * 0.02; // rise slowly in non-speech
        }
        else
        {
            _noiseFloorDb += 0.002; // creep upward under sustained loudness
        }
        _noiseFloorDb = Math.Clamp(_noiseFloorDb, -90, -25);
    }
}
