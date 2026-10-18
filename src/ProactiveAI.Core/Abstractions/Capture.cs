using ProactiveAI.Core.Audio;

namespace ProactiveAI.Core.Abstractions;

public interface IAudioDeviceCatalog
{
    IReadOnlyList<AudioDeviceInfo> GetDevices(AudioSourceKind kind);

    AudioDeviceInfo? GetDefault(AudioSourceKind kind);

    AudioDeviceInfo? Find(AudioSourceKind kind, string deviceId);

    /// <summary>Raised on a background thread when endpoints are added/removed or defaults change.</summary>
    event EventHandler<AudioDeviceChange>? DeviceChanged;
}

public sealed record CaptureRequest(
    AudioSourceKind Kind,
    string DeviceId,
    string? EchoReferenceRenderDeviceId,
    bool UseCommunicationsProcessing);

/// <summary>
/// One WASAPI capture stream converted to pipeline audio. <see cref="ChunkCaptured"/> is raised on the
/// capture thread and must never block; receivers copy into bounded queues.
/// </summary>
public interface ICaptureSource : IAsyncDisposable
{
    AudioSourceKind Kind { get; }

    CaptureStartInfo Start();

    Task StopAsync();

    event Action<AudioChunk>? ChunkCaptured;

    event Action<CaptureFault>? Faulted;
}

public interface ICaptureSourceFactory
{
    ICaptureSource Create(CaptureRequest request);
}
