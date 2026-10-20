using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Session;
using ProactiveAI.Core.Settings;
using ProactiveAI.Desktop.Services;

namespace ProactiveAI.Desktop.ViewModels;

public sealed record DeviceOption(string? Id, string Name)
{
    public override string ToString() => Name;
}

public enum ApiKeySaveOutcome
{
    /// <summary>Not stored: the text does not look like an API key.</summary>
    Invalid,

    /// <summary>Not stored: OpenAI rejected the key.</summary>
    Rejected,

    /// <summary>Stored and accepted by OpenAI.</summary>
    Saved,

    /// <summary>Stored, but the check found a problem other than an invalid key (no connection, model not available).</summary>
    SavedWithWarning,
}

public sealed record ApiKeySaveResult(ApiKeySaveOutcome Outcome, string Message);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SessionCoordinator _session;
    private readonly ISettingsStore _store;
    private readonly ICredentialStore _credentials;
    private readonly IAudioDeviceCatalog _devices;
    private readonly IModelAccessChecker _checker;
    private readonly UiDispatcher _ui;
    private bool _loading;

    public SettingsViewModel(SessionCoordinator session, ISettingsStore store, ICredentialStore credentials, IAudioDeviceCatalog devices,
        IModelAccessChecker checker, UiDispatcher ui)
    {
        _session = session;
        _store = store;
        _credentials = credentials;
        _devices = devices;
        _checker = checker;
        _ui = ui;
        devices.DeviceChanged += (_, _) => _ui.Post(RefreshDevices);
        Load();
    }

    /// <summary>Raised after settings that affect appearance or the overlay were saved.</summary>
    public event Action<AppSettings>? Applied;

    public ObservableCollection<DeviceOption> PlaybackDevices { get; } = new();

    public ObservableCollection<DeviceOption> MicrophoneDevices { get; } = new();

    public IReadOnlyList<string> EffortOptions { get; } = ["none", "low", "medium", "high"];

    public IReadOnlyList<string> DelayOptions { get; } = ["minimal", "low", "medium", "high"];

    [ObservableProperty] public partial DeviceOption? SelectedPlayback { get; set; }
    [ObservableProperty] public partial DeviceOption? SelectedMicrophone { get; set; }
    [ObservableProperty] public partial bool SharedMicrophone { get; set; }
    [ObservableProperty] public partial int EchoModeIndex { get; set; }

    [ObservableProperty] public partial bool HasApiKey { get; set; }
    [ObservableProperty] public partial string ApiKeyStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial string ConnectionTestResult { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsTesting { get; set; }

    [ObservableProperty] public partial string TranscriptionModel { get; set; } = string.Empty;
    [ObservableProperty] public partial string TranscriptionDelay { get; set; } = "low";
    [ObservableProperty] public partial string TranscriptionLanguages { get; set; } = string.Empty;
    [ObservableProperty] public partial string DiarizationModel { get; set; } = string.Empty;
    [ObservableProperty] public partial string AnswerModel { get; set; } = string.Empty;
    [ObservableProperty] public partial string AnswerEffort { get; set; } = "low";
    [ObservableProperty] public partial string InsightModel { get; set; } = string.Empty;
    [ObservableProperty] public partial string InsightEffort { get; set; } = "low";
    [ObservableProperty] public partial string IntentModel { get; set; } = string.Empty;
    [ObservableProperty] public partial string IntentEffort { get; set; } = "none";
    [ObservableProperty] public partial bool AiQuestionDetection { get; set; }
    [ObservableProperty] public partial bool AnswerFastMode { get; set; }
    [ObservableProperty] public partial bool SaveSessionHistory { get; set; }
    [ObservableProperty] public partial string HistoryStatus { get; set; } = string.Empty;

    [ObservableProperty] public partial int ScenarioIndex { get; set; }
    [ObservableProperty] public partial int DepthIndex { get; set; }
    [ObservableProperty] public partial int LanguageIndex { get; set; }
    [ObservableProperty] public partial double Sensitivity { get; set; }

    [ObservableProperty] public partial double MeetingInterval { get; set; }
    [ObservableProperty] public partial double MeetingRequests { get; set; }
    [ObservableProperty] public partial double MeetingCooldown { get; set; }
    [ObservableProperty] public partial double MeetingNotifications { get; set; }
    [ObservableProperty] public partial double MeetingSuppression { get; set; }
    [ObservableProperty] public partial double InsightDisplay { get; set; }
    [ObservableProperty] public partial double InterviewRequests { get; set; }
    [ObservableProperty] public partial double QuestionDebounce { get; set; }
    [ObservableProperty] public partial double DiarizationRequests { get; set; }

    [ObservableProperty] public partial int ThemeIndex { get; set; }
    [ObservableProperty] public partial bool OverlayEnabled { get; set; }
    [ObservableProperty] public partial bool OverlayTopmost { get; set; }
    [ObservableProperty] public partial int PlacementIndex { get; set; }
    [ObservableProperty] public partial bool CloseToTray { get; set; }

    [ObservableProperty] public partial string SaveStatus { get; set; } = string.Empty;

    public static readonly string[] LanguageCodes = ["auto", "zh", "en"];

    public void Load()
    {
        _loading = true;
        var s = _session.Settings;
        RefreshDevices();
        SharedMicrophone = s.MicrophoneUsage == MicrophoneUsage.Shared;
        EchoModeIndex = (int)s.EchoCancellation;
        TranscriptionModel = s.TranscriptionModel;
        TranscriptionDelay = s.TranscriptionDelay;
        TranscriptionLanguages = string.Join(", ", s.TranscriptionLanguages);
        DiarizationModel = s.DiarizationModel;
        AnswerModel = s.AnswerModel;
        AnswerEffort = s.AnswerReasoningEffort;
        InsightModel = s.InsightModel;
        InsightEffort = s.InsightReasoningEffort;
        IntentModel = s.IntentModel;
        IntentEffort = s.IntentReasoningEffort;
        AiQuestionDetection = s.AiQuestionDetection;
        AnswerFastMode = s.AnswerFastMode;
        SaveSessionHistory = s.SaveSessionHistory;
        ScenarioIndex = (int)s.Scenario;
        DepthIndex = (int)s.AnswerDepth;
        LanguageIndex = Math.Max(0, Array.IndexOf(LanguageCodes, s.AnswerLanguage));
        Sensitivity = s.Sensitivity;
        MeetingInterval = s.MeetingAnalysisIntervalSeconds;
        MeetingRequests = s.MeetingMaxRequestsPerMinute;
        MeetingCooldown = s.MeetingCooldownSeconds;
        MeetingNotifications = s.MeetingMaxNotificationsPerMinute;
        MeetingSuppression = s.MeetingSuppressionMinutes;
        InsightDisplay = s.InsightDisplaySeconds;
        InterviewRequests = s.InterviewMaxRequestsPerMinute;
        QuestionDebounce = s.QuestionDebounceMilliseconds;
        DiarizationRequests = s.DiarizationMaxRequestsPerMinute;
        ThemeIndex = (int)s.Theme;
        OverlayEnabled = s.OverlayEnabled;
        OverlayTopmost = s.OverlayTopmost;
        PlacementIndex = (int)s.OverlayPlacement;
        CloseToTray = s.CloseToTray;
        RefreshKeyState();
        _loading = false;
    }

    public void RefreshDevices()
    {
        bool wasLoading = _loading;
        _loading = true;
        var s = _session.Settings;
        Fill(PlaybackDevices, AudioSourceKind.System, s.PlaybackDeviceId, out var pb);
        Fill(MicrophoneDevices, AudioSourceKind.Microphone, s.MicrophoneDeviceId, out var mic);
        SelectedPlayback = pb;
        SelectedMicrophone = mic;
        _loading = wasLoading;
    }

    private void Fill(ObservableCollection<DeviceOption> target, AudioSourceKind kind, string? selectedId, out DeviceOption? selected)
    {
        target.Clear();
        var def = _devices.GetDefault(kind);
        target.Add(new DeviceOption(null, def is null ? "Windows default (none available)" : $"Windows default (currently {def.Name})"));
        foreach (var d in _devices.GetDevices(kind))
        {
            target.Add(new DeviceOption(d.Id, d.Name));
        }
        selected = target.FirstOrDefault(o => o.Id == selectedId);
        if (selected is null && selectedId is not null)
        {
            selected = new DeviceOption(selectedId, "Selected device (not connected)");
            target.Add(selected);
        }
        selected ??= target[0];
    }

    private void RefreshKeyState()
    {
        HasApiKey = _credentials.HasApiKey();
        ApiKeyStatus = HasApiKey
            ? "An API key is stored in Windows Credential Manager. It is never shown or written to settings or logs."
            : "No API key stored. Create one at platform.openai.com and paste it here.";
    }

    public void SaveApiKey(string key)
    {
        key = key.Trim();
        if (!LooksLikeApiKey(key))
        {
            ApiKeyStatus = "That does not look like an OpenAI API key. Nothing was stored.";
            return;
        }
        _credentials.WriteApiKey(key);
        RefreshKeyState();
        ConnectionTestResult = string.Empty;
    }

    /// <summary>
    /// Used by the first-start prompt: stores the key, then asks OpenAI once whether it is accepted (the answer model
    /// must be visible to it). A key OpenAI rejects is removed again and any previous key restored.
    /// </summary>
    public async Task<ApiKeySaveResult> SaveAndVerifyApiKeyAsync(string key, CancellationToken cancellationToken)
    {
        key = key.Trim();
        if (!LooksLikeApiKey(key))
        {
            return new(ApiKeySaveOutcome.Invalid, "That does not look like an OpenAI API key. Paste the whole key (it starts with \"sk-\").");
        }
        string? previous = _credentials.ReadApiKey();
        _credentials.WriteApiKey(key);
        string model = _session.Settings.AnswerModel;
        var check = await Task.Run(() => _checker.CheckAsync(model, cancellationToken), cancellationToken);
        if (check.Failure == ProviderFailureKind.Authentication)
        {
            if (string.IsNullOrWhiteSpace(previous))
            {
                _credentials.DeleteApiKey();
            }
            else
            {
                _credentials.WriteApiKey(previous);
            }
            RefreshKeyState();
            return new(ApiKeySaveOutcome.Rejected, "OpenAI did not accept this key. Check that you copied the whole key and that it has not been revoked.");
        }
        RefreshKeyState();
        ConnectionTestResult = string.Empty;
        return check.Failure switch
        {
            ProviderFailureKind.None when check.Accessible => new(ApiKeySaveOutcome.Saved, "Key saved."),
            ProviderFailureKind.Network or ProviderFailureKind.Cancelled or ProviderFailureKind.ServerError =>
                new(ApiKeySaveOutcome.SavedWithWarning, "Key saved, but it could not be checked right now (no connection to OpenAI). It is checked again when you start a session."),
            _ => new(ApiKeySaveOutcome.SavedWithWarning, $"Key saved, but OpenAI reported a problem with model {model}: {check.Detail} You can check all models in Settings > Test models."),
        };
    }

    private static bool LooksLikeApiKey(string key) => key.Length >= 20 && !key.Any(char.IsWhiteSpace);

    [RelayCommand]
    private void DeleteApiKey()
    {
        _credentials.DeleteApiKey();
        RefreshKeyState();
        ConnectionTestResult = string.Empty;
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (!_credentials.HasApiKey())
        {
            ConnectionTestResult = "Store an API key first.";
            return;
        }
        IsTesting = true;
        ConnectionTestResult = "Checking models…";
        var s = _session.Settings;
        var lines = new List<string>();
        foreach (var model in new[] { s.TranscriptionModel, s.DiarizationModel, s.AnswerModel, s.InsightModel, s.IntentModel }.Distinct())
        {
            var r = await Task.Run(() => _checker.CheckAsync(model, CancellationToken.None));
            lines.Add(r.Accessible ? $"✓ {model}: visible to this key" : $"✗ {model}: {r.Detail}");
        }
        lines.Add("Model visibility does not guarantee call-time access; a real session confirms it.");
        ConnectionTestResult = string.Join("\n", lines);
        IsTesting = false;
    }

    [RelayCommand]
    private void ResetOverlayPosition()
    {
        Save(s => s with { OverlayX = null, OverlayY = null });
    }

    partial void OnSelectedPlaybackChanged(DeviceOption? value) => Save(s => s with { PlaybackDeviceId = value?.Id });
    partial void OnSelectedMicrophoneChanged(DeviceOption? value) => Save(s => s with { MicrophoneDeviceId = value?.Id });
    partial void OnSharedMicrophoneChanged(bool value) => Save(s => s with { MicrophoneUsage = value ? MicrophoneUsage.Shared : MicrophoneUsage.Personal });
    partial void OnEchoModeIndexChanged(int value) => Save(s => s with { EchoCancellation = (EchoCancellationMode)Math.Clamp(value, 0, 2) });
    partial void OnTranscriptionModelChanged(string value) => SaveText(value, v => s => s with { TranscriptionModel = v });
    partial void OnTranscriptionDelayChanged(string value) => Save(s => s with { TranscriptionDelay = value });
    partial void OnTranscriptionLanguagesChanged(string value)
    {
        var langs = value.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length is >= 2 and <= 6 && l.All(c => char.IsLetter(c) || c == '-')).Select(l => l.ToLowerInvariant()).Distinct().ToList();
        if (langs.Count > 0)
        {
            Save(s => s with { TranscriptionLanguages = langs });
        }
    }
    partial void OnDiarizationModelChanged(string value) => SaveText(value, v => s => s with { DiarizationModel = v });
    partial void OnAnswerModelChanged(string value) => SaveText(value, v => s => s with { AnswerModel = v });
    partial void OnAnswerEffortChanged(string value) => Save(s => s with { AnswerReasoningEffort = value });
    partial void OnInsightModelChanged(string value) => SaveText(value, v => s => s with { InsightModel = v });
    partial void OnInsightEffortChanged(string value) => Save(s => s with { InsightReasoningEffort = value });
    partial void OnIntentModelChanged(string value) => SaveText(value, v => s => s with { IntentModel = v });
    partial void OnAnswerFastModeChanged(bool value) => Save(s => s with { AnswerFastMode = value });
    partial void OnSaveSessionHistoryChanged(bool value) => Save(s => s with { SaveSessionHistory = value });

    public string HistoryLocation => "Stored on this PC in " + Path.Combine(Infrastructure.Settings.AppDataLocation.Default, "sessions")
                                     + ", encrypted with Windows data protection so only your Windows account can read it.";

    /// <summary>Raised after all saved sessions were deleted (the Home list refreshes).</summary>
    public event Action? HistoryCleared;

    public async Task DeleteAllHistoryAsync()
    {
        if (_session.Archive is not { } archive)
        {
            return;
        }
        try
        {
            await Task.Run(archive.DeleteAll);
            HistoryStatus = "All saved sessions were deleted.";
            HistoryCleared?.Invoke();
        }
        catch (IOException)
        {
            HistoryStatus = "Some files could not be deleted.";
        }
    }
    partial void OnIntentEffortChanged(string value) => Save(s => s with { IntentReasoningEffort = value });
    partial void OnAiQuestionDetectionChanged(bool value) => Save(s => s with { AiQuestionDetection = value });
    partial void OnScenarioIndexChanged(int value) => Save(s => s with { Scenario = (Scenario)Math.Max(0, value) });
    partial void OnDepthIndexChanged(int value) => Save(s => s with { AnswerDepth = (AnswerDepth)Math.Max(0, value) });
    partial void OnLanguageIndexChanged(int value) => Save(s => s with { AnswerLanguage = LanguageCodes[Math.Clamp(value, 0, LanguageCodes.Length - 1)] });
    partial void OnSensitivityChanged(double value) => Save(s => s with { Sensitivity = (int)Math.Round(value) });
    partial void OnMeetingIntervalChanged(double value) => Save(s => s with { MeetingAnalysisIntervalSeconds = (int)value });
    partial void OnMeetingRequestsChanged(double value) => Save(s => s with { MeetingMaxRequestsPerMinute = (int)value });
    partial void OnMeetingCooldownChanged(double value) => Save(s => s with { MeetingCooldownSeconds = (int)value });
    partial void OnMeetingNotificationsChanged(double value) => Save(s => s with { MeetingMaxNotificationsPerMinute = (int)value });
    partial void OnMeetingSuppressionChanged(double value) => Save(s => s with { MeetingSuppressionMinutes = (int)value });
    partial void OnInsightDisplayChanged(double value) => Save(s => s with { InsightDisplaySeconds = (int)value });
    partial void OnInterviewRequestsChanged(double value) => Save(s => s with { InterviewMaxRequestsPerMinute = (int)value });
    partial void OnQuestionDebounceChanged(double value) => Save(s => s with { QuestionDebounceMilliseconds = (int)value });
    partial void OnDiarizationRequestsChanged(double value) => Save(s => s with { DiarizationMaxRequestsPerMinute = (int)value });
    partial void OnThemeIndexChanged(int value) => Save(s => s with { Theme = (ThemePreference)Math.Max(0, value) });
    partial void OnOverlayEnabledChanged(bool value) => Save(s => s with { OverlayEnabled = value });
    partial void OnOverlayTopmostChanged(bool value) => Save(s => s with { OverlayTopmost = value });
    partial void OnPlacementIndexChanged(int value) => Save(s => s with { OverlayPlacement = (OverlayPlacement)Math.Max(0, value), OverlayX = null, OverlayY = null });
    partial void OnCloseToTrayChanged(bool value) => Save(s => s with { CloseToTray = value });

    private void SaveText(string value, Func<string, Func<AppSettings, AppSettings>> f)
    {
        var v = value.Trim();
        if (v.Length is > 0 and <= 80 && !v.Contains(' '))
        {
            Save(f(v));
        }
    }

    public void Save(Func<AppSettings, AppSettings> change)
    {
        if (_loading || double.IsNaN(Sensitivity))
        {
            return;
        }
        var next = change(_session.Settings).Normalize();
        _session.UpdateSettings(next);
        try
        {
            _store.Save(next);
            SaveStatus = "Saved";
        }
        catch (IOException)
        {
            SaveStatus = "Could not save settings to disk";
        }
        Applied?.Invoke(next);
    }
}
