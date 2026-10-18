using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Speakers;
using ProactiveAI.Core.Transcript;
using ProactiveAI.Desktop.Services;

namespace ProactiveAI.Desktop.ViewModels;

public sealed partial class SegmentViewModel : ObservableObject
{
    public SegmentViewModel(TranscriptSegment segment, SpeakerRegistry speakers)
    {
        Id = segment.Id;
        Update(segment, speakers);
    }

    public string Id { get; }

    public TranscriptSegment Model { get; private set; } = null!;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TimeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SpeakerName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AttributionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Brush SpeakerBrush { get; set; } = SpeakerPalette.Neutral();

    [ObservableProperty]
    public partial string SourceGlyph { get; set; } = "\uE7F5";

    [ObservableProperty]
    public partial string SourceName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsFinal { get; set; }

    [ObservableProperty]
    public partial bool IsSpeech { get; set; }

    [ObservableProperty]
    public partial bool IsMarker { get; set; }

    [ObservableProperty]
    public partial string? Note { get; set; }

    [ObservableProperty]
    public partial bool IsDuplicate { get; set; }

    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }

    [ObservableProperty]
    public partial string AutomationName { get; set; } = string.Empty;

    public double TextOpacity => IsFinal ? 1.0 : 0.72;

    public Windows.UI.Text.FontStyle TextStyle => IsFinal ? Windows.UI.Text.FontStyle.Normal : Windows.UI.Text.FontStyle.Italic;

    // Echo duplicates collapse into a one-line notice; the original text is kept in the tooltip.
    public Visibility SpeechVisibility => IsSpeech && !IsDuplicate ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MarkerVisibility => IsMarker || IsDuplicate ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoteVisibility => IsSpeech && !string.IsNullOrEmpty(Note) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AttributionVisibility => string.IsNullOrEmpty(AttributionText) ? Visibility.Collapsed : Visibility.Visible;

    public string MarkerText => IsDuplicate
        ? (Model.Source == AudioSourceKind.Microphone ? "Microphone echo of the system audio — hidden" : "Duplicate of microphone speech — hidden")
        : Note ?? string.Empty;

    public string MarkerToolTip => IsDuplicate ? $"Hidden from analysis: \"{Text}\"" : MarkerText;

    public void Update(TranscriptSegment s, SpeakerRegistry speakers)
    {
        Model = s;
        IsSpeech = s.Kind == SegmentKind.Speech;
        IsMarker = !IsSpeech;
        Text = s.Text;
        IsFinal = s.IsFinal;
        TimeText = Format(s.AudioStart);
        SourceGlyph = s.Source == AudioSourceKind.System ? "\uE7F5" : "\uE720";
        SourceName = s.Source == AudioSourceKind.System ? "System audio" : "Microphone";
        Note = s.Note;
        IsDuplicate = s.DuplicateOfSegmentId is not null;
        RefreshSpeaker(speakers);
        OnPropertyChanged(nameof(TextOpacity));
        OnPropertyChanged(nameof(TextStyle));
        OnPropertyChanged(nameof(SpeechVisibility));
        OnPropertyChanged(nameof(MarkerVisibility));
        OnPropertyChanged(nameof(NoteVisibility));
        OnPropertyChanged(nameof(MarkerText));
        OnPropertyChanged(nameof(MarkerToolTip));
    }

    public void RefreshSpeaker(SpeakerRegistry speakers)
    {
        var a = Model.Attribution;
        var speaker = speakers.Get(a.SpeakerId);
        (SpeakerName, AttributionText) = a.Status switch
        {
            AttributionStatus.Pending => ("Speaker pending", "label pending"),
            AttributionStatus.Assigned => (speaker?.Name ?? "Speaker", speaker?.IsProvisional == true ? "uncertain" : string.Empty),
            AttributionStatus.Multiple => ($"{speaker?.Name ?? "Speaker"} +", "multiple speakers"),
            AttributionStatus.SourceAlias => (speaker?.Name ?? "Me (microphone)", string.Empty),
            AttributionStatus.UserAssigned => (speaker?.Name ?? "Speaker", "corrected"),
            AttributionStatus.Unknown => ("Unknown speaker", "not identified"),
            _ => ("Unknown speaker", "no speaker label"),
        };
        SpeakerBrush = a.Status is AttributionStatus.Assigned or AttributionStatus.UserAssigned or AttributionStatus.Multiple or AttributionStatus.SourceAlias && speaker is not null
            ? SpeakerPalette.For(speaker.ColorIndex)
            : SpeakerPalette.Neutral();
        AutomationName = IsSpeech
            ? $"{TimeText}, {SpeakerName}{(AttributionText.Length > 0 ? " (" + AttributionText + ")" : string.Empty)}, {SourceName}: {Text}"
            : $"{TimeText}: {Note}";
        OnPropertyChanged(nameof(AttributionVisibility));
    }

    private static string Format(TimeSpan t) =>
        t < TimeSpan.Zero ? "00:00" : t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
}

public sealed partial class SpeakerViewModel : ObservableObject
{
    public SpeakerViewModel(SessionSpeaker s) => Update(s);

    public string Id { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Details { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Brush Brush { get; set; } = SpeakerPalette.Neutral();

    public SessionSpeaker Model { get; private set; } = null!;

    public void Update(SessionSpeaker s)
    {
        Model = s;
        Id = s.Id;
        Name = s.Name;
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(s.DisplayName))
        {
            parts.Add(s.Label);
        }
        parts.Add(s.Source == AudioSourceKind.System ? "system audio" : "microphone");
        if (s.Role != SpeakerRole.Unknown)
        {
            parts.Add(s.Role.ToString().ToLowerInvariant());
        }
        if (s.IsProvisional)
        {
            parts.Add("possibly a returning speaker");
        }
        if (s.IsSourceAlias)
        {
            parts.Add("source alias, not verified identity");
        }
        Details = string.Join(" · ", parts);
        Brush = SpeakerPalette.For(s.ColorIndex);
    }
}
