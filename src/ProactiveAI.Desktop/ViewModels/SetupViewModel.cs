using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProactiveAI.Core.Session;
using ProactiveAI.Desktop.Services;

namespace ProactiveAI.Desktop.ViewModels;

/// <summary>
/// "New session" page: choose scenario and devices, check the microphone level and play a test sound that the app must
/// capture from the speakers, then start. Nothing is uploaded or recorded during the check.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly DeviceCheck _check;
    private readonly LiveSessionViewModel _live;
    private readonly SettingsViewModel _settings;
    private readonly AppNavigator _nav;
    private readonly UiDispatcher _ui;
    private bool _active;
    private int _restartVersion;
    private CancellationTokenSource? _testCts;
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    public SetupViewModel(DeviceCheck check, LiveSessionViewModel live, SettingsViewModel settings, AppNavigator nav, UiDispatcher ui)
    {
        _check = check;
        _live = live;
        _settings = settings;
        _nav = nav;
        _ui = ui;
        check.LevelsChanged += levels => _ui.Post(() => ApplyLevels(levels));
        settings.PropertyChanged += OnSettingsChanged;
        live.PropertyChanged += OnLiveChanged;
    }

    public LiveSessionViewModel Live => _live;

    public SettingsViewModel Settings => _settings;

    [ObservableProperty] public partial double MicLevel { get; set; }
    [ObservableProperty] public partial double SystemLevel { get; set; }
    [ObservableProperty] public partial bool MicOk { get; set; }
    [ObservableProperty] public partial bool SpeakerOk { get; set; }
    [ObservableProperty] public partial bool SpeakerTested { get; set; }
    [ObservableProperty] public partial bool IsTestingSpeakers { get; set; }
    [ObservableProperty] public partial bool IsStarting { get; set; }
    [ObservableProperty] public partial string MicStatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial InfoBarSeverity MicSeverity { get; set; } = InfoBarSeverity.Informational;
    [ObservableProperty] public partial string SpeakerStatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial InfoBarSeverity SpeakerSeverity { get; set; } = InfoBarSeverity.Informational;
    [ObservableProperty] public partial string? EchoHint { get; set; }

    public Visibility MicSectionVisibility => _live.MicrophoneEnabled ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EchoHintVisibility => string.IsNullOrEmpty(EchoHint) ? Visibility.Collapsed : Visibility.Visible;

    public bool CanTestSpeakers => !IsTestingSpeakers && !IsStarting;

    public bool CanStart => !IsStarting && _live.HasApiKey && (_live.SystemEnabled || _live.MicrophoneEnabled);

    public string StartHint => !_live.HasApiKey
        ? "Add your OpenAI API key in Settings first."
        : !_live.SystemEnabled && !_live.MicrophoneEnabled
            ? "Turn on system audio and/or the microphone."
            : (!_live.MicrophoneEnabled || MicOk) && (!_live.SystemEnabled || SpeakerOk)
                ? "Devices checked. Continue, then select Start on the session page to begin listening."
                : "You can continue without the checks, but testing first avoids surprises. Listening begins when you select Start on the next page.";

    partial void OnEchoHintChanged(string? value) => OnPropertyChanged(nameof(EchoHintVisibility));

    partial void OnIsTestingSpeakersChanged(bool value) => OnPropertyChanged(nameof(CanTestSpeakers));

    partial void OnIsStartingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanTestSpeakers));
        OnPropertyChanged(nameof(CanStart));
    }

    partial void OnMicOkChanged(bool value) => OnPropertyChanged(nameof(StartHint));

    partial void OnSpeakerOkChanged(bool value) => OnPropertyChanged(nameof(StartHint));

    /// <summary>The page became visible: start monitoring the selected devices.</summary>
    public void Activate()
    {
        _active = true;
        _live.RefreshCredentialState();
        _settings.RefreshDevices();
        MicOk = SpeakerOk = SpeakerTested = false;
        EchoHint = null;
        SpeakerSeverity = InfoBarSeverity.Informational;
        SpeakerStatusText = "Select Play test sound. You should hear a short chime, and the app checks that it can capture it.";
        RaiseDerived();
        Restart();
    }

    /// <summary>The page was left: release the devices.</summary>
    public void Deactivate()
    {
        _active = false;
        _testCts?.Cancel();
        Interlocked.Increment(ref _restartVersion);
        _ = RunCheckAsync(_check.StopAsync);
        MicLevel = SystemLevel = 0;
    }

    [RelayCommand]
    private void Back() => _nav.Navigate(AppPage.Home);

    [RelayCommand]
    private async Task TestSpeakersAsync()
    {
        if (IsTestingSpeakers)
        {
            return;
        }
        IsTestingSpeakers = true;
        SpeakerSeverity = InfoBarSeverity.Informational;
        SpeakerStatusText = "Playing the test sound…";
        _testCts = new CancellationTokenSource();
        try
        {
            var r = await _check.TestSpeakersAsync(_testCts.Token);
            SpeakerTested = true;
            SpeakerOk = r.Played && r.CapturedBySystemAudio;
            if (SpeakerOk)
            {
                SpeakerSeverity = InfoBarSeverity.Success;
                SpeakerStatusText = $"The app captured the test sound from {_check.PlaybackName ?? "the playback device"}. If you did not hear it, raise the volume or pick the device you listen on.";
            }
            else if (!r.Played)
            {
                SpeakerSeverity = InfoBarSeverity.Error;
                SpeakerStatusText = r.Error ?? "The test sound could not be played.";
            }
            else
            {
                SpeakerSeverity = InfoBarSeverity.Warning;
                SpeakerStatusText = "The sound was played but the app could not capture it. Check that the selected playback device is the one your meeting app uses, and that it is not muted.";
            }
            EchoHint = r.HeardByMicrophone && _live.MicrophoneEnabled
                ? "Your microphone also hears the speakers. The built-in echo canceller removes this; headphones give the cleanest results."
                : null;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsTestingSpeakers = false;
            RaiseDerived();
        }
    }

    /// <summary>Opens the session page ready to start; listening begins only when the user selects Start there.</summary>
    [RelayCommand]
    private async Task ContinueAsync()
    {
        if (IsStarting)
        {
            return;
        }
        IsStarting = true;
        try
        {
            _testCts?.Cancel();
            Interlocked.Increment(ref _restartVersion);
            await RunCheckAsync(_check.StopAsync); // release the devices before the session opens them
            await _live.PrepareNewSessionAsync();
            _nav.Navigate(AppPage.Live);
        }
        finally
        {
            IsStarting = false;
        }
    }

    private void Restart()
    {
        if (!_active)
        {
            return;
        }
        int version = Interlocked.Increment(ref _restartVersion);
        var s = _live.Session.Settings;
        bool mic = _live.MicrophoneEnabled;
        MicOk = false;
        MicSeverity = InfoBarSeverity.Informational;
        MicStatusText = mic ? "Starting the microphone…" : string.Empty;
        _ = RunCheckAsync(async () =>
        {
            if (version != Volatile.Read(ref _restartVersion))
            {
                return; // superseded by a newer restart or by leaving the page
            }
            await _check.StartAsync(s.MicrophoneDeviceId, mic, s.PlaybackDeviceId);
            if (version != Volatile.Read(ref _restartVersion))
            {
                await _check.StopAsync();
                return;
            }
            _ui.Post(() =>
            {
                if (mic)
                {
                    if (_check.MicrophoneError is { } err)
                    {
                        MicSeverity = InfoBarSeverity.Error;
                        MicStatusText = err;
                    }
                    else
                    {
                        MicStatusText = $"Say a few words — the bar should move ({_check.MicrophoneName}).";
                    }
                }
                if (_check.SystemError is { } sysErr)
                {
                    SpeakerSeverity = InfoBarSeverity.Error;
                    SpeakerStatusText = sysErr;
                }
            });
        });
    }

    /// <summary>Device-check start/stop calls run one at a time, off the UI thread.</summary>
    private Task RunCheckAsync(Func<Task> action) => Task.Run(async () =>
    {
        await _checkGate.WaitAsync();
        try
        {
            await action();
        }
        catch (Exception)
        {
            // device errors are reported through DeviceCheck's error properties
        }
        finally
        {
            _checkGate.Release();
        }
    });

    private void ApplyLevels(DeviceLevels levels)
    {
        if (!_active)
        {
            return;
        }
        MicLevel = ToPercent(levels.MicrophoneDbfs);
        SystemLevel = ToPercent(levels.SystemDbfs);
        if (levels.MicrophoneHeardSpeech && !MicOk && _check.MicrophoneError is null && _live.MicrophoneEnabled)
        {
            MicOk = true;
            MicSeverity = InfoBarSeverity.Success;
            MicStatusText = $"Microphone is working ({_check.MicrophoneName}).";
        }
    }

    private static double ToPercent(double dbfs) => Math.Clamp((dbfs + 60) / 60 * 100, 0, 100);

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_active && e.PropertyName is nameof(SettingsViewModel.SelectedMicrophone) or nameof(SettingsViewModel.SelectedPlayback))
        {
            SpeakerOk = SpeakerTested = false;
            EchoHint = null;
            SpeakerSeverity = InfoBarSeverity.Informational;
            SpeakerStatusText = "Device changed. Play the test sound again.";
            Restart();
        }
    }

    private void OnLiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LiveSessionViewModel.MicrophoneEnabled))
        {
            OnPropertyChanged(nameof(MicSectionVisibility));
            RaiseDerived();
            if (_active)
            {
                Restart();
            }
        }
        else if (e.PropertyName is nameof(LiveSessionViewModel.SystemEnabled) or nameof(LiveSessionViewModel.HasApiKey))
        {
            RaiseDerived();
        }
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(StartHint));
        OnPropertyChanged(nameof(CanTestSpeakers));
    }
}
