namespace ProactiveAI.Core.Audio;

public enum AudioSourceKind
{
    System = 0,
    Microphone = 1,
}

/// <summary>Pipeline audio format: 24 kHz, mono, signed 16-bit little-endian PCM.</summary>
public static class PipelineAudio
{
    public const int SampleRate = 24_000;
    public const int BytesPerSample = 2;
    public const int BytesPerSecond = SampleRate * BytesPerSample;
    public const int FrameSamples = SampleRate / 50; // 20 ms

    public static TimeSpan Duration(long samples) =>
        TimeSpan.FromTicks(samples * TimeSpan.TicksPerSecond / SampleRate);

    public static int Samples(TimeSpan duration) =>
        (int)Math.Round(duration.TotalSeconds * SampleRate);
}

/// <summary>
/// Audio converted to the pipeline format. <see cref="Samples"/> is owned by the receiver.
/// <see cref="Qpc100ns"/> is the QueryPerformanceCounter time of the first sample in 100 ns units.
/// </summary>
public sealed record AudioChunk(short[] Samples, long Qpc100ns, bool Discontinuity);

public sealed record AudioDeviceInfo(string Id, string Name, AudioSourceKind Kind, bool IsDefault);

public enum AudioDeviceChangeKind
{
    Removed,
    StateChanged,
    DefaultChanged,
    Added,
}

public sealed record AudioDeviceChange(AudioDeviceChangeKind Kind, AudioSourceKind? Flow, string? DeviceId);

public enum EchoControlStatus
{
    NotApplicable,
    ReferenceEndpointSet,
    CommunicationsModeOnly,
    Unavailable,
    /// <summary>The app's own acoustic echo canceller removes system playback from the raw microphone signal.</summary>
    BuiltInCanceller,
}

public sealed record CaptureStartInfo(string DeviceId, string DeviceName, string NativeFormat, EchoControlStatus EchoControl, string? EchoControlDetail);

public sealed record CaptureFault(AudioSourceKind Source, CaptureFaultKind Kind, string Message);

public enum CaptureFaultKind
{
    DeviceLost,
    AccessDenied,
    Failed,
}
