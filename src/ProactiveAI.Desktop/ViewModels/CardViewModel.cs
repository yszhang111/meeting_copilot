using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProactiveAI.Core.Assistant;

namespace ProactiveAI.Desktop.ViewModels;

public sealed partial class CardViewModel : ObservableObject
{
    public CardViewModel(AssistantCard card) => Update(card);

    public string Id { get; private set; } = string.Empty;

    public AssistantCard Model { get; private set; } = null!;

    [ObservableProperty]
    public partial string KindText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity StateSeverity { get; set; }

    [ObservableProperty]
    public partial string Heading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? QuestionText { get; set; }

    [ObservableProperty]
    public partial string? DirectPosition { get; set; }

    [ObservableProperty]
    public partial string? SpokenAnswer { get; set; }

    [ObservableProperty]
    public partial string? Suggestion { get; set; }

    [ObservableProperty]
    public partial string? SuggestedQuestion { get; set; }

    [ObservableProperty]
    public partial string SupportingText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LimitsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FollowupsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MissingFactsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FilledInText { get; set; } = string.Empty;

    public const string FilledInHeading = "AI filled in these details (not from your notes or what you said) — check before saying them:";

    [ObservableProperty]
    public partial string? StatusDetail { get; set; }

    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    [ObservableProperty]
    public partial bool IsDismissed { get; set; }

    [ObservableProperty]
    public partial bool IsGenerating { get; set; }

    [ObservableProperty]
    public partial bool IsDraft { get; set; }

    [ObservableProperty]
    public partial string TimeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MetaText { get; set; } = string.Empty;

    public bool IsAnswerLike => Model.Kind is CardKind.Answer or CardKind.Clarification or CardKind.NoAction;

    public bool IsInsight => Model.Kind == CardKind.Insight;

    public bool CanCancel => Model.State is CardState.Generating or CardState.Queued or CardState.Limited;

    public bool CanRegenerate => Model.QuestionId is not null && !CanCancel;

    public Visibility QuestionVisibility => Vis(QuestionText);

    public Visibility DirectVisibility => Vis(DirectPosition);

    public Visibility SpokenVisibility => Vis(SpokenAnswer);

    public Visibility SuggestionVisibility => Vis(Suggestion);

    public Visibility SuggestedQuestionVisibility => Vis(SuggestedQuestion);

    public Visibility SupportingVisibility => Vis(SupportingText);

    public Visibility LimitsVisibility => Vis(LimitsText);

    public Visibility FollowupsVisibility => Vis(FollowupsText);

    public Visibility MissingFactsVisibility => Vis(MissingFactsText);

    public Visibility FilledInVisibility => Vis(FilledInText);

    public Visibility StatusVisibility => Vis(StatusDetail);

    public Visibility GeneratingVisibility => IsGenerating ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CancelVisibility => CanCancel ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RegenerateVisibility => CanRegenerate ? Visibility.Visible : Visibility.Collapsed;

    public Visibility WaitingVisibility => Model.State is CardState.Queued or CardState.Limited || (IsGenerating && string.IsNullOrEmpty(DirectPosition) && string.IsNullOrEmpty(SpokenAnswer))
        ? Visibility.Visible : Visibility.Collapsed;

    public string PinGlyph => IsPinned ? "\uE77A" : "\uE718";

    public string PinLabel => IsPinned ? "Unpin" : "Pin";

    public string PlainText
    {
        get
        {
            var sb = new StringBuilder();
            if (QuestionText is not null) { sb.AppendLine("Q: " + QuestionText).AppendLine(); }
            if (Model.Title is not null && Model.Kind != CardKind.Answer) { sb.AppendLine(Model.Title); }
            if (DirectPosition is not null) { sb.AppendLine(DirectPosition).AppendLine(); }
            if (SpokenAnswer is not null) { sb.AppendLine(SpokenAnswer).AppendLine(); }
            if (FilledInText.Length > 0) { sb.AppendLine(FilledInHeading).AppendLine(FilledInText).AppendLine(); }
            if (Suggestion is not null) { sb.AppendLine(Suggestion); }
            if (SuggestedQuestion is not null) { sb.AppendLine("Ask: " + SuggestedQuestion); }
            if (SupportingText.Length > 0) { sb.AppendLine("Support:").AppendLine(SupportingText).AppendLine(); }
            if (LimitsText.Length > 0) { sb.AppendLine("Assumptions and limits:").AppendLine(LimitsText).AppendLine(); }
            if (FollowupsText.Length > 0) { sb.AppendLine("Likely follow-ups:").AppendLine(FollowupsText).AppendLine(); }
            if (MissingFactsText.Length > 0) { sb.AppendLine("Facts to supply:").AppendLine(MissingFactsText); }
            if (IsDraft) { sb.AppendLine().AppendLine($"[{StateText}]"); }
            return sb.ToString().TrimEnd();
        }
    }

    public void Update(AssistantCard c)
    {
        Model = c;
        Id = c.Id;
        KindText = c.Kind switch
        {
            CardKind.Answer => "Answer",
            CardKind.Insight => "Insight",
            CardKind.Clarification => "Clarification needed",
            _ => "No answer needed",
        };
        (StateText, StateSeverity) = c.State switch
        {
            CardState.Queued => ("Waiting", InfoBarSeverity.Informational),
            CardState.Limited => ("Rate limited — waiting", InfoBarSeverity.Warning),
            CardState.Generating => ("Draft — generating", InfoBarSeverity.Informational),
            CardState.Complete => ("Complete", InfoBarSeverity.Success),
            CardState.Interrupted => ("Interrupted — incomplete draft", InfoBarSeverity.Warning),
            CardState.Invalid => ("Invalid — not validated", InfoBarSeverity.Error),
            CardState.Failed => ("Failed", InfoBarSeverity.Error),
            _ => (c.State.ToString(), InfoBarSeverity.Informational),
        };
        QuestionText = c.QuestionText;
        Heading = c.Kind == CardKind.Answer ? (c.QuestionText ?? c.Title ?? "Answer") : (c.Title ?? KindText);
        DirectPosition = c.DirectPosition;
        SpokenAnswer = c.SpokenAnswer;
        Suggestion = c.Suggestion;
        SuggestedQuestion = c.SuggestedQuestion;
        SupportingText = Bullets(c.SupportingPoints);
        LimitsText = Bullets(c.AssumptionsAndLimits);
        FollowupsText = string.Join("\n\n", c.LikelyFollowups.Select(f => $"• {f.Question}\n   → {f.ResponseDirection}"));
        MissingFactsText = Bullets(c.MissingFacts);
        FilledInText = Bullets(c.FilledInDetails);
        StatusDetail = c.Kind == CardKind.NoAction && c.State == CardState.Complete
            ? "The assistant judged that no answer was needed" + (string.IsNullOrEmpty(c.DecisionNote) ? "." : $": {c.DecisionNote}")
            : c.StatusDetail;
        IsPinned = c.Pinned;
        IsDismissed = c.Dismissed;
        IsGenerating = c.State == CardState.Generating;
        IsDraft = c.IsDraft;
        TimeText = c.CreatedUtc.ToLocalTime().ToString("HH:mm:ss");
        var meta = new List<string>();
        if (c.Manual) { meta.Add("requested manually"); }
        if (c.Revision > 1) { meta.Add($"revision {c.Revision}"); }
        if (c.AskedAgain) { meta.Add("asked again"); }
        if (c.SpeakerUncertain) { meta.Add("speaker label unconfirmed"); }
        if (c.FirstUsefulTextLatency is { } f) { meta.Add($"first text {f.TotalSeconds:F1}s"); }
        if (c.CompleteLatency is { } d) { meta.Add($"complete {d.TotalSeconds:F1}s"); }
        if (c.PreparationEvidenceIds.Count > 0) { meta.Add($"{c.PreparationEvidenceIds.Count} preparation note(s) used"); }
        MetaText = string.Join(" · ", meta);
        foreach (var name in new[]
                 {
                     nameof(QuestionVisibility), nameof(DirectVisibility), nameof(SpokenVisibility), nameof(SuggestionVisibility),
                     nameof(SuggestedQuestionVisibility), nameof(SupportingVisibility), nameof(LimitsVisibility), nameof(FollowupsVisibility),
                     nameof(MissingFactsVisibility), nameof(FilledInVisibility), nameof(StatusVisibility), nameof(GeneratingVisibility), nameof(CancelVisibility),
                     nameof(RegenerateVisibility), nameof(WaitingVisibility), nameof(PinGlyph), nameof(PinLabel), nameof(IsAnswerLike), nameof(IsInsight),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private static string Bullets(IReadOnlyList<string> items) => string.Join("\n", items.Select(i => "• " + i));

    private static Visibility Vis(string? s) => string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible;
}
