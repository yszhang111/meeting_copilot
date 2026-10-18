namespace ProactiveAI.Core.Audio;

/// <summary>
/// Shared monotonic session clock. Audio packets are stamped with QPC time by WASAPI, so both
/// sources are placed on one timeline without relying on sample counting (loopback stops
/// delivering packets during silence, and each device has its own clock).
/// </summary>
public sealed class SessionClock
{
    private readonly TimeProvider _time;
    private long _origin100ns;

    public SessionClock(TimeProvider time, TimeSpan? initialElapsed = null)
    {
        _time = time;
        _origin100ns = Now100ns() - (initialElapsed ?? TimeSpan.Zero).Ticks;
    }

    public TimeProvider TimeProvider => _time;

    public void Restart() => Interlocked.Exchange(ref _origin100ns, Now100ns());

    public long Now100ns()
    {
        long ts = _time.GetTimestamp();
        long freq = _time.TimestampFrequency;
        return (ts / freq * TimeSpan.TicksPerSecond) + (ts % freq * TimeSpan.TicksPerSecond / freq);
    }

    public TimeSpan Now => TimeSpan.FromTicks(Now100ns() - Interlocked.Read(ref _origin100ns));

    public TimeSpan FromQpc100ns(long qpc100ns) => TimeSpan.FromTicks(qpc100ns - Interlocked.Read(ref _origin100ns));
}

/// <summary>
/// Measures the ratio between samples delivered and QPC time elapsed for diagnostics (device clock drift).
/// Placement on the timeline always uses per-packet QPC stamps, which already absorbs drift.
/// </summary>
public sealed class DriftMeter
{
    private long _firstQpc = -1;
    private long _lastQpc;
    private long _samples;
    private long _lastPacketSamples;

    public void Observe(long qpc100ns, int samples, bool discontinuity)
    {
        if (_firstQpc < 0 || discontinuity)
        {
            _firstQpc = qpc100ns;
            _samples = 0;
        }
        else
        {
            _samples += _lastPacketSamples;
        }
        _lastQpc = qpc100ns;
        _lastPacketSamples = samples;
    }

    /// <summary>Parts per million difference between nominal sample rate and observed rate; null until 10 s observed.</summary>
    public double? DriftPpm
    {
        get
        {
            long elapsed = _lastQpc - _firstQpc;
            if (_firstQpc < 0 || elapsed < 10 * TimeSpan.TicksPerSecond || _samples == 0)
            {
                return null;
            }
            double expected = elapsed / (double)TimeSpan.TicksPerSecond * PipelineAudio.SampleRate;
            return (_samples - expected) / expected * 1_000_000.0;
        }
    }
}
