namespace ProactiveAI.Core.Assistant;

/// <summary>What the request was about; used to check revisions and evidence membership.</summary>
public sealed record ValidationContext(
    string? ExpectedQuestionId,
    int? ExpectedRevision,
    IReadOnlySet<string> TranscriptEvidenceIds,
    IReadOnlySet<string> PreparationEvidenceIds,
    bool AnswerExpected);

public sealed record ValidationOutcome(bool Valid, IReadOnlyList<string> Errors);

/// <summary>
/// Application-side validation of structure, enums, lengths, question revision and evidence
/// membership. Evidence ids prove what the conversation/notes contained, not that a fact is true.
/// </summary>
public static class AssistantResultValidator
{
    public static ValidationOutcome Validate(AssistantResult r, ValidationContext ctx)
    {
        var e = new List<string>();
        if (!ResultLimits.Categories.Contains(r.Category))
        {
            e.Add($"invalid category '{r.Category}'");
        }
        if (!ResultLimits.Priorities.Contains(r.Priority))
        {
            e.Add($"invalid priority '{r.Priority}'");
        }
        Len(e, "title", r.Title, ResultLimits.Title);
        Len(e, "question_text", r.QuestionText, ResultLimits.QuestionText);
        Len(e, "direct_position", r.DirectPosition, ResultLimits.DirectPosition);
        Len(e, "spoken_answer", r.SpokenAnswer, ResultLimits.SpokenAnswer);
        Len(e, "suggestion", r.Suggestion, ResultLimits.Suggestion);
        Len(e, "suggested_question", r.SuggestedQuestion, ResultLimits.SuggestedQuestion);
        Len(e, "decision_note", r.DecisionNote, ResultLimits.DecisionNote);
        List(e, "supporting_points", r.SupportingPoints, ResultLimits.SupportingPointsMax, ResultLimits.ListItem);
        List(e, "assumptions_and_limits", r.AssumptionsAndLimits, ResultLimits.AssumptionsMax, ResultLimits.ListItem);
        List(e, "missing_facts", r.MissingFacts, ResultLimits.MissingFactsMax, ResultLimits.ListItem);
        List(e, "filled_in_details", r.FilledInDetails, ResultLimits.FilledInMax, ResultLimits.ListItem);
        if (r.LikelyFollowups.Count > ResultLimits.FollowupsMax)
        {
            e.Add("too many likely_followups");
        }
        foreach (var f in r.LikelyFollowups)
        {
            Len(e, "followup.question", f.Question, ResultLimits.FollowupQuestion);
            Len(e, "followup.response_direction", f.ResponseDirection, ResultLimits.FollowupDirection);
        }
        if (r.EvidenceSegmentIds.Count > ResultLimits.EvidenceMax || r.PreparationEvidenceIds.Count > ResultLimits.EvidenceMax)
        {
            e.Add("too many evidence ids");
        }

        // Evidence membership: transcript ids and preparation ids are checked against separate sets.
        foreach (var id in r.EvidenceSegmentIds.Concat(r.QuestionSegmentIds))
        {
            if (!ctx.TranscriptEvidenceIds.Contains(id))
            {
                e.Add($"unknown transcript evidence '{id}'");
            }
        }
        foreach (var id in r.PreparationEvidenceIds)
        {
            if (!ctx.PreparationEvidenceIds.Contains(id))
            {
                e.Add($"unknown preparation evidence '{id}'");
            }
        }

        switch (r.Kind)
        {
            case ResponseKind.None:
                if (r.ShouldNotify)
                {
                    e.Add("should_notify must be false for response_kind none");
                }
                break;
            case ResponseKind.Insight:
                Require(e, r.ShouldNotify, "insight requires should_notify");
                Require(e, r.Title is not null, "insight requires title");
                Require(e, r.Suggestion is not null, "insight requires suggestion");
                Require(e, r.EvidenceSegmentIds.Count > 0, "insight requires evidence");
                break;
            case ResponseKind.Clarification:
                Require(e, r.Title is not null, "clarification requires title");
                Require(e, r.Suggestion is not null || r.SuggestedQuestion is not null, "clarification requires suggestion or suggested_question");
                Require(e, r.EvidenceSegmentIds.Count > 0 || r.QuestionSegmentIds.Count > 0, "clarification requires evidence");
                break;
            case ResponseKind.Answer:
                Require(e, r.DirectPosition is not null, "answer requires direct_position");
                Require(e, r.SpokenAnswer is not null, "answer requires spoken_answer");
                Require(e, r.QuestionText is not null, "answer requires question_text");
                Require(e, r.EvidenceSegmentIds.Count > 0 || r.QuestionSegmentIds.Count > 0, "answer requires transcript evidence");
                break;
        }

        if (r.Kind is ResponseKind.Answer or ResponseKind.Clarification && ctx.ExpectedQuestionId is not null)
        {
            if (r.QuestionId != ctx.ExpectedQuestionId)
            {
                e.Add($"question_id mismatch ({r.QuestionId} != {ctx.ExpectedQuestionId})");
            }
            if (ctx.ExpectedRevision is { } rev && r.QuestionRevision != rev)
            {
                e.Add($"question_revision mismatch ({r.QuestionRevision} != {rev})");
            }
        }
        return new ValidationOutcome(e.Count == 0, e);
    }

    private static void Require(List<string> e, bool ok, string message)
    {
        if (!ok)
        {
            e.Add(message);
        }
    }

    private static void Len(List<string> e, string name, string? value, int max)
    {
        if (value is not null && value.Length > max)
        {
            e.Add($"{name} exceeds {max} characters");
        }
    }

    private static void List(List<string> e, string name, IReadOnlyList<string> items, int maxItems, int maxChars)
    {
        if (items.Count > maxItems)
        {
            e.Add($"{name} has more than {maxItems} items");
        }
        foreach (var i in items)
        {
            Len(e, name, i, maxChars);
        }
    }
}
