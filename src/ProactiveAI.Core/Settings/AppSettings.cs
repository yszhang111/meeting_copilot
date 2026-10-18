using System.Text.Json.Serialization;

namespace ProactiveAI.Core.Settings;

public enum Scenario
{
    Meeting,
    Interview,
    Defense,
}

public enum CaptureMode
{
    SystemOnly,
    MicrophoneOnly,
    SystemAndMicrophone,
}

public static class CaptureModes
{
    public static CaptureMode From(bool system, bool microphone) => (system, microphone) switch
    {
        (true, true) => CaptureMode.SystemAndMicrophone,
        (false, true) => CaptureMode.MicrophoneOnly,
        _ => CaptureMode.SystemOnly,
    };
}

public enum MicrophoneUsage
{
    /// <summary>Personal microphone: its speech is shown with the "Me (microphone)" source alias.</summary>
    Personal,
    /// <summary>Shared microphone: speakers on the microphone are diarized.</summary>
    Shared,
}

public enum EchoCancellationMode
{
    /// <summary>
    /// The app's acoustic echo canceller removes the captured system playback from the raw microphone signal
    /// (requires system audio capture as the reference; otherwise Windows processing is used).
    /// </summary>
    BuiltIn,
    /// <summary>Windows communications processing with the playback device as echo reference where supported.</summary>
    Windows,
    /// <summary>No echo cancellation (headphones).</summary>
    Off,
}

public enum AnswerDepth
{
    Normal,
    Detailed,
}

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public enum OverlayPlacement
{
    TopRight,
    BottomRight,
    TopLeft,
    BottomLeft,
}

/// <summary>Non-sensitive, persisted settings. The API key is stored separately in Windows Credential Manager.</summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    // Sources. Device selections persist. The microphone is on by default (user decision 2026-10-17); turning it off
    // is remembered.
    public string? PlaybackDeviceId { get; init; }          // null = follow current default at Start
    public string? MicrophoneDeviceId { get; init; }        // null = follow current default at Start
    public bool SystemAudioEnabled { get; init; } = true;
    public bool MicrophoneEnabled { get; init; } = true;
    public MicrophoneUsage MicrophoneUsage { get; init; } = MicrophoneUsage.Personal;
    public EchoCancellationMode EchoCancellation { get; init; } = EchoCancellationMode.BuiltIn;

    /// <summary>Save each session's transcript text and answers (never audio) to the encrypted local history.</summary>
    public bool SaveSessionHistory { get; init; } = true;

    /// <summary>Résumé from the library chosen for the next interview session (pre-selected on the new-session page; can be changed).</summary>
    public string? SelectedResumeId { get; init; }

    // Models (configurable; availability is verified by real requests)
    public string TranscriptionModel { get; init; } = "gpt-live-transcribe";
    public string TranscriptionDelay { get; init; } = "low";
    public List<string> TranscriptionLanguages { get; init; } = ["zh", "en"];
    public string DiarizationModel { get; init; } = "gpt-4o-transcribe-diarize";
    // gpt-6-sol with effort "none": first answer text ~1.2-2.4 s and about twice the streaming speed of gpt-6.1-sol in
    // live measurements (Probe latency, 2026-10-17); gpt-6.1-sol remains selectable for maximum quality.
    public string AnswerModel { get; init; } = "gpt-6-sol";
    public string AnswerReasoningEffort { get; init; } = "none";
    /// <summary>
    /// OpenAI Fast mode (service_tier "fast") for answer requests: measured first answer text ~0.9–1.1 s instead of
    /// ~1.1–2.1 s and ~25 % faster completion with gpt-6-sol, at about twice the token price. Off by default.
    /// </summary>
    public bool AnswerFastMode { get; init; }
    public string InsightModel { get; init; } = "gpt-6-luna";
    public string InsightReasoningEffort { get; init; } = "low";
    /// <summary>Hybrid question detection: turns the local rules are unsure about are classified by this small model.</summary>
    public bool AiQuestionDetection { get; init; } = true;
    public string IntentModel { get; init; } = "gpt-6-luna";
    public string IntentReasoningEffort { get; init; } = "none";

    // Scenario / answers
    public Scenario Scenario { get; init; } = Scenario.Interview;
    public AnswerDepth AnswerDepth { get; init; } = AnswerDepth.Normal;
    /// <summary>"auto" matches the question language; otherwise a language code such as "zh" or "en".</summary>
    public string AnswerLanguage { get; init; } = "auto";

    // Analysis sensitivity and budgets
    public int Sensitivity { get; init; } = 2;               // 1 = conservative, 3 = eager
    public int MeetingAnalysisIntervalSeconds { get; init; } = 8;
    public int MeetingMaxRequestsPerMinute { get; init; } = 4;
    public int MeetingCooldownSeconds { get; init; } = 30;
    public int MeetingMaxNotificationsPerMinute { get; init; } = 2;
    public int MeetingSuppressionMinutes { get; init; } = 3;
    public int InsightDisplaySeconds { get; init; } = 12;
    public int InterviewMaxRequestsPerMinute { get; init; } = 8;
    /// <summary>
    /// Wait after a finalized segment before evaluating the question turn. Each segment already ends with ~0.7 s of
    /// silence (VAD) plus the transcription finalization, so a continuation almost never arrives within a longer wait;
    /// additions after an answer started produce a revision instead.
    /// </summary>
    public int QuestionDebounceMilliseconds { get; init; } = 300;
    public int DiarizationMaxRequestsPerMinute { get; init; } = 12;
    public int IntentMaxRequestsPerMinute { get; init; } = 20;

    // Appearance / overlay
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public bool OverlayEnabled { get; init; } = true;
    public bool OverlayTopmost { get; init; } = true;
    public OverlayPlacement OverlayPlacement { get; init; } = OverlayPlacement.TopRight;
    public int? OverlayX { get; init; }
    public int? OverlayY { get; init; }
    public int OverlayWidth { get; init; } = 460;
    public int OverlayHeight { get; init; } = 520;
    public bool CloseToTray { get; init; }

    public AppSettings Normalize()
    {
        var s = SchemaVersion < CurrentSchemaVersion ? Migrate() : this;
        return s with
        {
            Sensitivity = Math.Clamp(s.Sensitivity, 1, 3),
            MeetingAnalysisIntervalSeconds = Math.Clamp(s.MeetingAnalysisIntervalSeconds, 4, 60),
            MeetingMaxRequestsPerMinute = Math.Clamp(s.MeetingMaxRequestsPerMinute, 1, 10),
            MeetingCooldownSeconds = Math.Clamp(s.MeetingCooldownSeconds, 10, 300),
            MeetingMaxNotificationsPerMinute = Math.Clamp(s.MeetingMaxNotificationsPerMinute, 1, 6),
            MeetingSuppressionMinutes = Math.Clamp(s.MeetingSuppressionMinutes, 1, 15),
            InsightDisplaySeconds = Math.Clamp(s.InsightDisplaySeconds, 5, 60),
            InterviewMaxRequestsPerMinute = Math.Clamp(s.InterviewMaxRequestsPerMinute, 2, 20),
            QuestionDebounceMilliseconds = Math.Clamp(s.QuestionDebounceMilliseconds, 300, 4000),
            DiarizationMaxRequestsPerMinute = Math.Clamp(s.DiarizationMaxRequestsPerMinute, 2, 30),
            IntentMaxRequestsPerMinute = Math.Clamp(s.IntentMaxRequestsPerMinute, 2, 60),
            IntentModel = string.IsNullOrWhiteSpace(s.IntentModel) ? "gpt-6-luna" : s.IntentModel,
            AnswerReasoningEffort = SupportedEffort(s.AnswerModel, s.AnswerReasoningEffort),
            InsightReasoningEffort = SupportedEffort(s.InsightModel, s.InsightReasoningEffort),
            IntentReasoningEffort = SupportedEffort(s.IntentModel, s.IntentReasoningEffort),
            OverlayWidth = Math.Clamp(s.OverlayWidth, 320, 1400),
            OverlayHeight = Math.Clamp(s.OverlayHeight, 160, 1400),
            TranscriptionLanguages = s.TranscriptionLanguages is { Count: > 0 } ? s.TranscriptionLanguages : ["zh", "en"],
        };
    }

    /// <summary>GPT-6.1 Sol and GPT-6 Astra reject reasoning effort "none" (HTTP 400); their lowest effort is "low".</summary>
    public static string SupportedEffort(string model, string effort) =>
        effort == "none" && (model.StartsWith("gpt-6.1-sol", StringComparison.OrdinalIgnoreCase) || model.StartsWith("gpt-6-astra", StringComparison.OrdinalIgnoreCase))
            ? "low"
            : effort;

    /// <summary>Upgrades settings saved by an older version; values the user changed from the old defaults are kept.</summary>
    private AppSettings Migrate()
    {
        var s = this;
        if (s.SchemaVersion < 2)
        {
            // v2: faster question-to-answer path (debounce 1000 → 300 ms; answer model gpt-6.1-sol/low → gpt-6-sol/none).
            if (s.QuestionDebounceMilliseconds == 1000)
            {
                s = s with { QuestionDebounceMilliseconds = 300 };
            }
            if (s.AnswerModel == "gpt-6.1-sol" && s.AnswerReasoningEffort == "low")
            {
                s = s with { AnswerModel = "gpt-6-sol", AnswerReasoningEffort = "none" };
            }
        }
        return s with { SchemaVersion = CurrentSchemaVersion };
    }
}

public interface ISettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}
