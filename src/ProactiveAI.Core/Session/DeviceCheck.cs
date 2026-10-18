using Microsoft.Extensions.Logging;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Audio;

namespace ProactiveAI.Core.Session;

/// <summary>Plays a short test sound on a playback endpoint.</summary>
public interface ITestSoundPlayer
{
    TimeSpan Duration { get; }

    /// <summary>Plays the sound and completes when it has finished. Throws on failure.</summary>
    Task PlayAsync(string playbackDeviceId, CancellationToken cancellationToken);
}

/// <summary>Current levels during the device check (dBFS, -120 when silent or not running).</summary>
public sealed record DeviceLevels(double MicrophoneDbfs, double SystemDbfs, bool MicrophoneRunning, bool SystemRunning, bool MicrophoneHeardSpeech);

/// <param name="Played">The test sound was played without an error.</param>
/// <param name="CapturedBySystemAudio">System-audio capture of the same device picked the sound up (what the app needs).</param>
/// <param name="HeardByMicrophone">The microphone heard the loudspeaker clearly (loudspeaker echo will be cancelled; headphones avoid it).</param>
public sealed record SpeakerTestResult(bool Played, bool CapturedBySystemAudio, double CapturePeakDbfs, bool HeardByMicrophone, string? Error);

/// <summary>
/// Pre-session check of the selected microphone and playback device: live microphone level, and a test sound on the
/// speakers that must be picked up by system-audio (loopback) capture. Nothing is recorded or uploaded.
/// </summary>
public sealed class DeviceCheck : IAsyncDisposable
{
    public const double SpeechDbfs = -45;
    public const double CaptureThresholdDbfs = -50;

    private readonly ICaptureSourceFactory _factory;
    private readonly ITestSoundPlayer _player;
    private readonly IAudioDeviceCatalog _devices;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly Queue<double> _micRecent = new();
    private ICaptureSource? _mic;
    private ICaptureSource? _system;
    private string? _playbackId;
    private CancellationTokenSource? _loopCts;
    private double _micWindowMax = -120;
    private double _sysWindowMax = -120;
    private double _sysPeak = -120;
    private List<double>? _micDuringTest;
    private bool _heardSpeech;

    public DeviceCheck(ICaptureSourceFactory factory, ITestSoundPlayer player, IAudioDeviceCatalog devices, ILogger<DeviceCheck> log)
    {
        _factory = factory;
        _player = player;
        _devices = devices;
        _log = log;
    }

    /// <summary>Raised about 15 times per second on a background thread while the check runs.</summary>
    public event Action<DeviceLevels>? LevelsChanged;

    public string? MicrophoneError { get; private set; }

    public string? SystemError { get; private set; }

    public string? MicrophoneName { get; private set; }

    public string? PlaybackName { get; private set; }

    public bool IsRunning
    {
        get { lock (_gate) { return _loopCts is not null; } }
    }

    /// <summary>Starts monitoring. A null device id means the current default device.</summary>
    public async Task StartAsync(string? microphoneId, bool microphone, string? playbackId)
    {
        await StopAsync().ConfigureAwait(false);
        MicrophoneError = SystemError = null;
        MicrophoneName = PlaybackName = null;
        lock (_gate)
        {
            _heardSpeech = false;
            _micRecent.Clear();
            _micWindowMax = _sysWindowMax = _sysPeak = -120;
        }

        if (microphone)
        {
            var device = microphoneId is not null ? _devices.Find(AudioSourceKind.Microphone, microphoneId) : _devices.GetDefault(AudioSourceKind.Microphone);
            if (device is null)
            {
                MicrophoneError = "No microphone found.";
            }
            else
            {
                MicrophoneName = device.Name;
                _mic = await StartCaptureAsync(new CaptureRequest(AudioSourceKind.Microphone, device.Id, null, false), OnMicrophone, e => MicrophoneError = e).ConfigureAwait(false);
            }
        }

        var playback = playbackId is not null ? _devices.Find(AudioSourceKind.System, playbackId) : _devices.GetDefault(AudioSourceKind.System);
        if (playback is null)
        {
            SystemError = "No playback device found.";
        }
        else
        {
            PlaybackName = playback.Name;
            _playbackId = playback.Id;
            _system = await StartCaptureAsync(new CaptureRequest(AudioSourceKind.System, playback.Id, null, false), OnSystem, e => SystemError = e).ConfigureAwait(false);
        }

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _loopCts = cts;
        }
        _ = Task.Run(() => EmitLoopAsync(cts.Token));
    }

    /// <summary>Plays the test sound on the playback device and checks that system-audio capture hears it.</summary>
    public async Task<SpeakerTestResult> TestSpeakersAsync(CancellationToken cancellationToken)
    {
        string? device;
        lock (_gate)
        {
            device = _playbackId;
            _sysPeak = -120;
            _micDuringTest = new List<double>();
        }
        if (device is null)
        {
            return new SpeakerTestResult(false, false, -120, false, SystemError ?? "No playback device found.");
        }
        double micBaseline = MicBaseline();
        string? error = null;
        try
        {
            await _player.PlayAsync(device, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false); // capture latency tail
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = "The test sound could not be played on this device.";
            _log.LogWarning("Test sound failed: {Type} 0x{HResult:X8}", ex.GetType().Name, ex.HResult);
        }
        double peak;
        List<double> mic;
        lock (_gate)
        {
            peak = _sysPeak;
            mic = _micDuringTest ?? new List<double>();
            _micDuringTest = null;
        }
        bool captured = error is null && _system is not null && peak > CaptureThresholdDbfs;
        double micLoud = mic.Count == 0 ? -120 : mic.Order().ElementAt((int)(mic.Count * 0.9));
        bool heard = mic.Count > 0 && micLoud > -55 && micLoud > micBaseline + 10;
        _log.LogInformation("Speaker test: played={Played} captured={Captured} peakDbfs={Peak:F0} micHeard={Heard}", error is null, captured, peak, heard);
        return new SpeakerTestResult(error is null, captured, peak, heard, error ?? (captured ? null : SystemError));
    }

    public async Task StopAsync()
    {
        ICaptureSource? mic, system;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            mic = _mic;
            system = _system;
            cts = _loopCts;
            _mic = _system = null;
            _loopCts = null;
            _playbackId = null;
        }
        cts?.Cancel();
        foreach (var c in new[] { mic, system })
        {
            if (c is null)
            {
                continue;
            }
            try
            {
                await c.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug("Device check capture stop: {Type}", ex.GetType().Name);
            }
        }
        cts?.Dispose();
        if (mic is not null || system is not null)
        {
            _log.LogInformation("Device check stopped (microphone={Mic} system={System})", mic is not null, system is not null);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task<ICaptureSource?> StartCaptureAsync(CaptureRequest request, Action<AudioChunk> onChunk, Action<string> onError)
    {
        var capture = _factory.Create(request);
        capture.ChunkCaptured += onChunk;
        try
        {
            await Task.Run(capture.Start).ConfigureAwait(false);
            return capture;
        }
        catch (Exception ex)
        {
            capture.ChunkCaptured -= onChunk;
            onError(ex.Message);
            _log.LogInformation("Device check: {Kind} capture failed: {Type}", request.Kind, ex.GetType().Name);
            await capture.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    private void OnMicrophone(AudioChunk chunk)
    {
        double db = VoiceActivityDetector.LevelDbfs(chunk.Samples);
        lock (_gate)
        {
            _micWindowMax = Math.Max(_micWindowMax, db);
            _micDuringTest?.Add(db);
            if (_micDuringTest is null)
            {
                _micRecent.Enqueue(db);
                while (_micRecent.Count > 150)
                {
                    _micRecent.Dequeue();
                }
            }
            _heardSpeech |= db > SpeechDbfs;
        }
    }

    private void OnSystem(AudioChunk chunk)
    {
        double db = VoiceActivityDetector.LevelDbfs(chunk.Samples);
        lock (_gate)
        {
            _sysWindowMax = Math.Max(_sysWindowMax, db);
            _sysPeak = Math.Max(_sysPeak, db);
        }
    }

    private double MicBaseline()
    {
        lock (_gate)
        {
            return _micRecent.Count == 0 ? -120 : _micRecent.Order().ElementAt(_micRecent.Count / 2);
        }
    }

    private async Task EmitLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(66));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                DeviceLevels levels;
                lock (_gate)
                {
                    levels = new DeviceLevels(_micWindowMax, _sysWindowMax, _mic is not null, _system is not null, _heardSpeech);
                    _micWindowMax = _sysWindowMax = -120;
                }
                LevelsChanged?.Invoke(levels);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
