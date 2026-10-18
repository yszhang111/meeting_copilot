using System.Text.Json;

namespace ProactiveAI.Core.Assistant;

public sealed record ParseOutcome(AssistantResult? Result, IReadOnlyList<string> Errors)
{
    public bool Ok => Result is not null && Errors.Count == 0;
}

/// <summary>Strictly parses the completed structured output; malformed output is rejected, never repaired silently.</summary>
public static class AssistantResultParser
{
    public static ParseOutcome Parse(string json)
    {
        var errors = new List<string>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException ex)
        {
            return new ParseOutcome(null, [$"invalid JSON: {ex.Message}"]);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ParseOutcome(null, ["root is not an object"]);
            }
            var kindText = Str(root, "response_kind", errors, required: true);
            ResponseKind kind = kindText switch
            {
                "answer" => ResponseKind.Answer,
                "insight" => ResponseKind.Insight,
                "clarification" => ResponseKind.Clarification,
                "none" => ResponseKind.None,
                _ => Fail(errors, $"invalid response_kind '{kindText}'"),
            };
            var result = new AssistantResult
            {
                Kind = kind,
                ShouldNotify = Bool(root, "should_notify", errors),
                QuestionId = Str(root, "question_id", errors),
                QuestionRevision = Int(root, "question_revision", errors),
                QuestionText = Str(root, "question_text", errors),
                QuestionSegmentIds = StrList(root, "question_segment_ids", errors),
                Category = Str(root, "category", errors, required: true) ?? "none",
                Priority = Str(root, "priority", errors, required: true) ?? "normal",
                Title = Str(root, "title", errors),
                DirectPosition = Str(root, "direct_position", errors),
                SpokenAnswer = Str(root, "spoken_answer", errors),
                Suggestion = Str(root, "suggestion", errors),
                SuggestedQuestion = Str(root, "suggested_question", errors),
                SupportingPoints = StrList(root, "supporting_points", errors),
                AssumptionsAndLimits = StrList(root, "assumptions_and_limits", errors),
                LikelyFollowups = Followups(root, errors),
                MissingFacts = StrList(root, "missing_facts", errors),
                EvidenceSegmentIds = StrList(root, "evidence_segment_ids", errors),
                PreparationEvidenceIds = StrList(root, "preparation_evidence_ids", errors),
                AnswerLanguage = Str(root, "answer_language", errors),
                DecisionNote = Str(root, "decision_note", errors) ?? string.Empty,
                FollowsUpOn = OptionalStr(root, "follows_up_on", errors),
                FilledInDetails = root.TryGetProperty("filled_in_details", out _) ? StrList(root, "filled_in_details", errors) : [],
            };
            return new ParseOutcome(errors.Count == 0 ? result : null, errors);
        }
    }

    private static ResponseKind Fail(List<string> errors, string message)
    {
        errors.Add(message);
        return ResponseKind.None;
    }

    private static string? Str(JsonElement root, string name, List<string> errors, bool required = false)
    {
        if (!root.TryGetProperty(name, out var v))
        {
            errors.Add($"missing '{name}'");
            return null;
        }
        if (v.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                errors.Add($"'{name}' must not be null");
            }
            return null;
        }
        if (v.ValueKind != JsonValueKind.String)
        {
            errors.Add($"'{name}' must be a string");
            return null;
        }
        var s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
    }

    /// <summary>A field that only adds metadata: absent is fine, but a present value must be a string or null.</summary>
    private static string? OptionalStr(JsonElement root, string name, List<string> errors) =>
        root.TryGetProperty(name, out _) ? Str(root, name, errors) : null;

    private static bool Bool(JsonElement root, string name, List<string> errors)
    {
        if (!root.TryGetProperty(name, out var v) || (v.ValueKind != JsonValueKind.True && v.ValueKind != JsonValueKind.False))
        {
            errors.Add($"'{name}' must be a boolean");
            return false;
        }
        return v.GetBoolean();
    }

    private static int? Int(JsonElement root, string name, List<string> errors)
    {
        if (!root.TryGetProperty(name, out var v))
        {
            errors.Add($"missing '{name}'");
            return null;
        }
        if (v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int i))
        {
            errors.Add($"'{name}' must be an integer");
            return null;
        }
        return i;
    }

    private static IReadOnlyList<string> StrList(JsonElement root, string name, List<string> errors)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"'{name}' must be an array");
            return [];
        }
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                errors.Add($"'{name}' items must be strings");
                continue;
            }
            var s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                list.Add(s.Trim());
            }
        }
        return list;
    }

    private static IReadOnlyList<FollowUp> Followups(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("likely_followups", out var v) || v.ValueKind != JsonValueKind.Array)
        {
            errors.Add("'likely_followups' must be an array");
            return [];
        }
        var list = new List<FollowUp>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("question", out var q) || q.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("response_direction", out var d) || d.ValueKind != JsonValueKind.String)
            {
                errors.Add("invalid likely_followups item");
                continue;
            }
            list.Add(new FollowUp(q.GetString()!.Trim(), d.GetString()!.Trim()));
        }
        return list;
    }
}
