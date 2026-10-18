namespace ProactiveAI.Core.Assistant;

public enum ResponseKind
{
    None,
    Answer,
    Insight,
    Clarification,
}

public sealed record FollowUp(string Question, string ResponseDirection);

/// <summary>Validated structured result from the reasoning model. Field names mirror the JSON schema.</summary>
public sealed record AssistantResult
{
    public ResponseKind Kind { get; init; }
    public bool ShouldNotify { get; init; }
    public string? QuestionId { get; init; }
    public int? QuestionRevision { get; init; }
    public string? QuestionText { get; init; }
    public IReadOnlyList<string> QuestionSegmentIds { get; init; } = [];
    public string Category { get; init; } = "none";
    public string Priority { get; init; } = "normal";
    public string? Title { get; init; }
    public string? DirectPosition { get; init; }
    public string? SpokenAnswer { get; init; }
    public string? Suggestion { get; init; }
    public string? SuggestedQuestion { get; init; }
    public IReadOnlyList<string> SupportingPoints { get; init; } = [];
    public IReadOnlyList<string> AssumptionsAndLimits { get; init; } = [];
    public IReadOnlyList<FollowUp> LikelyFollowups { get; init; } = [];
    public IReadOnlyList<string> MissingFacts { get; init; } = [];
    /// <summary>Specifics about the user's own work that the model supplied (not from the notes or what the user said).</summary>
    public IReadOnlyList<string> FilledInDetails { get; init; } = [];
    public IReadOnlyList<string> EvidenceSegmentIds { get; init; } = [];
    public IReadOnlyList<string> PreparationEvidenceIds { get; init; } = [];
    public string? AnswerLanguage { get; init; }
    public string DecisionNote { get; init; } = string.Empty;
    /// <summary>question_id of the earlier question the current one follows up on or refers back to.</summary>
    public string? FollowsUpOn { get; init; }
}

public static class ResultLimits
{
    public const int Title = 120;
    public const int QuestionText = 1500;
    public const int DirectPosition = 1200;
    public const int SpokenAnswer = 12000;
    public const int Suggestion = 400;
    public const int SuggestedQuestion = 240;
    public const int ListItem = 600;
    public const int SupportingPointsMax = 8;
    public const int AssumptionsMax = 6;
    public const int FollowupsMax = 4;
    public const int FollowupQuestion = 300;
    public const int FollowupDirection = 800;
    public const int MissingFactsMax = 6;
    public const int FilledInMax = 8;
    public const int EvidenceMax = 40;
    public const int DecisionNote = 240;

    public static readonly string[] Categories = ["answer", "clarification", "risk", "alternative", "requirement", "fact_check", "follow_up", "none"];
    public static readonly string[] Priorities = ["low", "normal", "high"];
}
