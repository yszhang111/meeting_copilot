using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Assistant;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Speakers;

namespace ProactiveAI.Core.Session;

public enum SessionState
{
    Stopped,
    Starting,
    Listening,
    Paused,
    Reconnecting,
    Stopping,
    Error,
}

public enum TranscriptionHealth
{
    Off,
    Connecting,
    Connected,
    Reconnecting,
    Failed,
}

public sealed record SourceStatus(
    AudioSourceKind Kind,
    bool Enabled,
    bool Muted,
    bool Active,
    bool Missing,
    string? DeviceName,
    double LevelDbfs,
    bool Speaking,
    TranscriptionHealth Transcription,
    DiarizationStatus? Diarization,
    EchoControlStatus EchoControl,
    string? EchoDetail,
    int Generation,
    PipelineMetrics? Metrics);

public sealed record SessionSnapshot(
    SessionState State,
    string? Message,
    bool CanRetry,
    long SessionId,
    TimeSpan Elapsed,
    SourceStatus System,
    SourceStatus Microphone,
    AssistantStatus Assistant);

public sealed record LatencySummary(string Metric, int Samples, double? MedianSeconds, double? P90Seconds);

public static class LatencyMath
{
    public static LatencySummary Summarize(string metric, IReadOnlyList<TimeSpan> samples)
    {
        if (samples.Count == 0)
        {
            return new LatencySummary(metric, 0, null, null);
        }
        var sorted = samples.Select(s => s.TotalSeconds).Order().ToArray();
        double Pct(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new LatencySummary(metric, sorted.Length, Pct(0.5), Pct(0.9));
    }
}
