using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Audio;

namespace ProactiveAI.Infrastructure.Audio;

/// <summary>Enumerates active playback/capture endpoints and forwards endpoint notifications.</summary>
public sealed class WindowsAudioDeviceCatalog : IAudioDeviceCatalog, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient? _notifications;
    private readonly ILogger _log;

    public WindowsAudioDeviceCatalog(ILogger<WindowsAudioDeviceCatalog> log)
    {
        _log = log;
        try
        {
            _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            _notifications.DeviceRemoved += (_, e) => Raise(new AudioDeviceChange(AudioDeviceChangeKind.Removed, null, e.DeviceId));
            _notifications.DeviceAdded += (_, e) => Raise(new AudioDeviceChange(AudioDeviceChangeKind.Added, null, e.DeviceId));
            _notifications.DeviceStateChanged += (_, e) => Raise(new AudioDeviceChange(
                e.NewState == DeviceState.Active ? AudioDeviceChangeKind.Added : AudioDeviceChangeKind.StateChanged, null, e.DeviceId));
            _notifications.DefaultDeviceChanged += (_, e) =>
            {
                if (e.Role != Role.Console)
                {
                    return;
                }
                Raise(new AudioDeviceChange(AudioDeviceChangeKind.DefaultChanged,
                    e.Flow == DataFlow.Render ? AudioSourceKind.System : AudioSourceKind.Microphone, e.DeviceId));
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning("Device notifications unavailable: {Type}", ex.GetType().Name);
        }
    }

    public event EventHandler<AudioDeviceChange>? DeviceChanged;

    public IReadOnlyList<AudioDeviceInfo> GetDevices(AudioSourceKind kind)
    {
        var flow = kind == AudioSourceKind.System ? DataFlow.Render : DataFlow.Capture;
        string? defaultId = GetDefault(kind)?.Id;
        var list = new List<AudioDeviceInfo>();
        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    list.Add(new AudioDeviceInfo(device.ID, SafeName(device), kind, device.ID == defaultId));
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Endpoint enumeration failed: {Type}", ex.GetType().Name);
        }
        return list.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public AudioDeviceInfo? GetDefault(AudioSourceKind kind)
    {
        var flow = kind == AudioSourceKind.System ? DataFlow.Render : DataFlow.Capture;
        try
        {
            if (_enumerator.TryGetDefaultAudioEndpoint(flow, Role.Console, out var device) && device is not null)
            {
                using (device)
                {
                    return new AudioDeviceInfo(device.ID, SafeName(device), kind, true);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug("Default endpoint lookup failed: {Type}", ex.GetType().Name);
        }
        return null;
    }

    public AudioDeviceInfo? Find(AudioSourceKind kind, string deviceId)
    {
        try
        {
            using var device = _enumerator.GetDevice(deviceId);
            if (device.State != DeviceState.Active)
            {
                return null;
            }
            var expected = kind == AudioSourceKind.System ? DataFlow.Render : DataFlow.Capture;
            if (device.DataFlow != expected)
            {
                return null;
            }
            return new AudioDeviceInfo(device.ID, SafeName(device), kind, device.ID == GetDefault(kind)?.Id);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _notifications?.Dispose();
        _enumerator.Dispose();
    }

    private void Raise(AudioDeviceChange change)
    {
        try
        {
            DeviceChanged?.Invoke(this, change);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Device change handler failed: {Type}", ex.GetType().Name);
        }
    }

    private static string SafeName(MMDevice d)
    {
        try
        {
            return d.FriendlyName;
        }
        catch
        {
            return d.ID;
        }
    }
}
