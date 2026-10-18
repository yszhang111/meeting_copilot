using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Audio;

namespace ProactiveAI.Infrastructure.Audio;

/// <summary>
/// WASAPI capture of one endpoint via NAudio's <see cref="WasapiRecorder"/>. System playback uses
/// loopback capture; microphones optionally use the Windows communications pipeline with the selected
/// playback endpoint as the acoustic echo cancellation reference (Windows 11 22621+, endpoint-dependent).
/// </summary>
internal sealed class WasapiCaptureSource : ICaptureSource
{
    private const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
    private const int AudclntEServiceNotRunning = unchecked((int)0x88890010);
    private const int EAccessDenied = unchecked((int)0x80070005);

    private readonly CaptureRequest _request;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private WasapiRecorder? _recorder;
    private MMDevice? _device;
    private MMDevice? _reference;
    private PcmConverter? _converter;
    private bool _stopping;

    public WasapiCaptureSource(CaptureRequest request, ILogger log)
    {
        _request = request;
        _log = log;
    }

    public AudioSourceKind Kind => _request.Kind;

    public event Action<AudioChunk>? ChunkCaptured;

    public event Action<CaptureFault>? Faulted;

    public CaptureStartInfo Start()
    {
        var enumerator = new MMDeviceEnumerator();
        try
        {
            _device = enumerator.GetDevice(_request.DeviceId);
        }
        catch (Exception ex)
        {
            throw MapStartException(ex, "Audio endpoint not found");
        }

        string name = SafeName(_device);
        EchoControlStatus echo = EchoControlStatus.NotApplicable;
        string? echoDetail = null;
        var attempts = new List<(string Label, Func<WasapiRecorderBuilder, WasapiRecorderBuilder> Configure, EchoControlStatus Status)>();

        if (_request.Kind == AudioSourceKind.System)
        {
            attempts.Add(("loopback", b => b.WithLoopbackCapture(), EchoControlStatus.NotApplicable));
        }
        else
        {
            if (_request.UseCommunicationsProcessing && _request.EchoReferenceRenderDeviceId is { } refId)
            {
                try
                {
                    _reference = enumerator.GetDevice(refId);
                    var reference = _reference;
                    attempts.Add(("aec-reference", b => b.WithEchoCancellationReferenceEndpoint(reference), EchoControlStatus.ReferenceEndpointSet));
                }
                catch (Exception ex)
                {
                    _log.LogInformation("AEC reference endpoint unavailable: {Type}", ex.GetType().Name);
                }
            }
            if (_request.UseCommunicationsProcessing)
            {
                attempts.Add(("communications", b => b.WithCommunicationsMode(), EchoControlStatus.CommunicationsModeOnly));
            }
            attempts.Add(("plain", b => b, EchoControlStatus.Unavailable));
        }

        Exception? last = null;
        foreach (var attempt in attempts)
        {
            foreach (bool engineConversion in new[] { true, false })
            {
                try
                {
                    var builder = attempt.Configure(new WasapiRecorderBuilder().WithDevice(_device).WithBufferLength(100));
                    if (engineConversion)
                    {
                        builder = builder.WithFormat(new WaveFormat(PipelineAudio.SampleRate, 16, 1));
                    }
                    var recorder = builder.Build();
                    var converter = new PcmConverter(recorder.WaveFormat);
                    recorder.DataAvailable += OnData;
                    recorder.RecordingStopped += OnStopped;
                    lock (_gate)
                    {
                        _recorder = recorder;
                        _converter = converter;
                    }
                    try
                    {
                        recorder.StartRecording();
                        WaitUntilStarted(recorder);
                    }
                    catch
                    {
                        recorder.DataAvailable -= OnData;
                        recorder.RecordingStopped -= OnStopped;
                        lock (_gate)
                        {
                            _recorder = null;
                        }
                        recorder.Dispose();
                        throw;
                    }
                    echo = attempt.Status;
                    echoDetail = attempt.Status switch
                    {
                        EchoControlStatus.ReferenceEndpointSet => "Windows AEC active with the selected playback device as reference",
                        EchoControlStatus.CommunicationsModeOnly => "Communications processing requested; AEC reference control not available on this endpoint",
                        EchoControlStatus.Unavailable when _request.UseCommunicationsProcessing => "No echo cancellation available on this microphone; use headphones",
                        EchoControlStatus.Unavailable => "Echo cancellation disabled",
                        _ => null,
                    };
                    _log.LogInformation("Capture started kind={Kind} mode={Mode} engineConversion={Conv} format={Format}",
                        _request.Kind, attempt.Label, engineConversion, recorder.WaveFormat.ToString());
                    return new CaptureStartInfo(_request.DeviceId, name, recorder.WaveFormat.ToString(), echo, echoDetail);
                }
                catch (Exception ex) when (ex is NotSupportedException or COMException or InvalidOperationException or ArgumentException)
                {
                    last = ex;
                    _log.LogInformation("Capture attempt failed kind={Kind} mode={Mode} conv={Conv}: {Type} 0x{HResult:X8}",
                        _request.Kind, attempt.Label, engineConversion, ex.GetType().Name, ex.HResult);
                    if (IsAccessDenied(ex) || ex.HResult == AudclntEDeviceInvalidated)
                    {
                        throw MapStartException(ex, "Capture failed");
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw MapStartException(ex, "Microphone access denied");
                }
            }
        }
        throw MapStartException(last ?? new InvalidOperationException("no capture mode"), "Capture could not start");
    }

    public async Task StopAsync()
    {
        WasapiRecorder? recorder;
        lock (_gate)
        {
            _stopping = true;
            recorder = _recorder;
            _recorder = null;
        }
        if (recorder is null)
        {
            return;
        }
        recorder.DataAvailable -= OnData;
        recorder.RecordingStopped -= OnStopped;
        // NAudio 3.2.0's capture thread sets its state to Capturing unconditionally once the audio client starts, so a stop
        // issued just before that is lost and DisposeAsync would join the still-running thread forever. Re-assert the stop
        // until the thread has really finished, and never wait unboundedly.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (recorder.CaptureState != CaptureState.Stopped)
        {
            if (sw.Elapsed > StopTimeout)
            {
                _log.LogWarning("Capture kind={Kind} did not stop within {Ms} ms; releasing it in the background", _request.Kind, (int)StopTimeout.TotalMilliseconds);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await recorder.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug("Background capture dispose: {Type}", ex.GetType().Name);
                    }
                });
                return;
            }
            try
            {
                recorder.StopRecording();
            }
            catch (Exception ex)
            {
                _log.LogDebug("StopRecording: {Type}", ex.GetType().Name);
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
        await recorder.DisposeAsync().ConfigureAwait(false);
    }

    private static readonly TimeSpan StartSettleTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Returns once the capture thread has left the Starting state, so a stop requested right after Start can't be lost
    /// (see <see cref="StopAsync"/>). Usually a few milliseconds.
    /// </summary>
    private void WaitUntilStarted(WasapiRecorder recorder)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (recorder.CaptureState == CaptureState.Starting)
        {
            if (sw.Elapsed > StartSettleTimeout)
            {
                _log.LogWarning("Capture kind={Kind} still starting after {Ms} ms", _request.Kind, (int)StartSettleTimeout.TotalMilliseconds);
                return;
            }
            Thread.Sleep(1);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _device?.Dispose();
        _reference?.Dispose();
        _device = null;
        _reference = null;
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (_stopping)
        {
            return;
        }
        var converter = _converter;
        if (converter is null || buffer.IsEmpty)
        {
            return;
        }
        bool silent = (flags & AudioClientBufferFlags.Silent) != 0;
        short[] samples = converter.Convert(buffer, silent);
        if (samples.Length == 0)
        {
            return;
        }
        long qpc = (flags & AudioClientBufferFlags.TimestampError) != 0 || qpcPosition == 0 ? Qpc100ns() : qpcPosition;
        bool discontinuity = (flags & AudioClientBufferFlags.DataDiscontinuity) != 0;
        ChunkCaptured?.Invoke(new AudioChunk(samples, qpc, discontinuity));
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopping || e.Exception is null)
        {
            return;
        }
        var ex = e.Exception;
        var kind = ex.HResult is AudclntEDeviceInvalidated or AudclntEServiceNotRunning ? CaptureFaultKind.DeviceLost
            : IsAccessDenied(ex) ? CaptureFaultKind.AccessDenied
            : CaptureFaultKind.Failed;
        _log.LogWarning("Capture stopped unexpectedly kind={Kind} fault={Fault} 0x{HResult:X8}", _request.Kind, kind, ex.HResult);
        Faulted?.Invoke(new CaptureFault(_request.Kind, kind, kind switch
        {
            CaptureFaultKind.DeviceLost => "The audio device was disconnected or became unavailable",
            CaptureFaultKind.AccessDenied => "Access to the microphone was denied",
            _ => "Audio capture stopped unexpectedly",
        }));
    }

    private static long Qpc100ns()
    {
        long ts = System.Diagnostics.Stopwatch.GetTimestamp();
        long f = System.Diagnostics.Stopwatch.Frequency;
        return (ts / f * TimeSpan.TicksPerSecond) + (ts % f * TimeSpan.TicksPerSecond / f);
    }

    private static bool IsAccessDenied(Exception ex) => ex is UnauthorizedAccessException || ex.HResult == EAccessDenied;

    private static string SafeName(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch
        {
            return "Audio device";
        }
    }

    private static CaptureStartException MapStartException(Exception ex, string message)
    {
        var kind = IsAccessDenied(ex) ? CaptureFaultKind.AccessDenied
            : ex.HResult == AudclntEDeviceInvalidated || ex is COMException { HResult: unchecked((int)0x80070490) } ? CaptureFaultKind.DeviceLost
            : CaptureFaultKind.Failed;
        return new CaptureStartException(kind, kind switch
        {
            CaptureFaultKind.AccessDenied => "Microphone access is denied. Enable it in Windows Settings > Privacy & security > Microphone (allow desktop apps).",
            CaptureFaultKind.DeviceLost => "The selected audio device is not available.",
            _ => $"{message} ({ex.GetType().Name} 0x{ex.HResult:X8}).",
        }, ex);
    }
}

public sealed class CaptureStartException(CaptureFaultKind kind, string message, Exception inner) : Exception(message, inner), Core.Session.ICaptureFaultSource
{
    public CaptureFaultKind Kind { get; } = kind;

    public CaptureFaultKind FaultKind => Kind;
}

public sealed class WasapiCaptureSourceFactory(ILoggerFactory loggers) : ICaptureSourceFactory
{
    public ICaptureSource Create(CaptureRequest request) =>
        new WasapiCaptureSource(request, loggers.CreateLogger<WasapiCaptureSource>());
}
