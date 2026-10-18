using System.Text;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Context;
using ProactiveAI.Core.Questions;
using ProactiveAI.Core.Settings;
using ProactiveAI.Core.Speakers;
using ProactiveAI.Core.Transcript;

namespace ProactiveAI.Core.Assistant;

public sealed record PromptBudgets
{
    public int TotalInput { get; init; } = 28_000;
    public int Conversation { get; init; } = ConversationWindow.DefaultMaxTokens;
    public TimeSpan ConversationAge { get; init; } = ConversationWindow.DefaultMaxAge;
    public MemoryBudgets Memory { get; init; } = new();
    /// <summary>Notes plus selected documents. Notes, résumé and job description are sent in full (cached) when they fit.</summary>
    public int Preparation { get; init; } = 10_000;
    public int Question { get; init; } = 1_500;
}

public sealed record PromptRequest(
    Scenario Scenario,
    AnswerDepth Depth,
    string LanguageSetting,
    QuestionCandidate? Question,
    IReadOnlyList<MeetingTrigger> Triggers,
    string? NewSinceSegmentId);

/// <param name="Input">Request-specific input (recent exchanges, transcript window, task and question).</param>
public sealed record BuiltPrompt(
    string Instructions,
    string Input,
    string SchemaJson,
    IReadOnlySet<string> TranscriptIds,
    IReadOnlySet<string> PreparationIds,
    int InputTokens,
    IReadOnlyList<string> Notes)
{
    /// <summary>
    /// Reusable context sent between the instructions and <see cref="Input"/>, oldest-changing first: the preparation notes,
    /// then the session question log with one block per entry. Breakpoints after the last two entries let the next question,
    /// which appends one entry, reuse the cached log.
    /// </summary>
    public IReadOnlyList<ContextBlock> Context { get; init; } = [];
}

/// <summary>
/// Assembles a bounded model request. Conversation text is delimited as untrusted data, AI drafts are labeled separately
/// from what the user actually said, and the total input stays within budget. Content that rarely changes comes first so
/// it can be served from the prompt cache; the parts that change with every request come last.
/// </summary>
public sealed class PromptBuilder
{
    private readonly TranscriptStore _transcript;
    private readonly SpeakerRegistry _speakers;
    private readonly QuestionHistory _history;
    private readonly PreparationNotes _prep;
    private readonly InsightFilter _filter;
    private readonly ITokenCounter _tokens;
    private readonly SessionClock _clock;
    private readonly Func<TranscriptSegment, Party> _party;

    public PromptBuilder(TranscriptStore transcript, SpeakerRegistry speakers, QuestionHistory history, PreparationNotes prep,
        InsightFilter filter, ITokenCounter tokens, SessionClock clock, Func<TranscriptSegment, Party> party)
    {
        _transcript = transcript;
        _speakers = speakers;
        _history = history;
        _prep = prep;
        _filter = filter;
        _tokens = tokens;
        _clock = clock;
        _party = party;
    }

    public PromptBudgets Budgets { get; set; } = new();

    public BuiltPrompt Build(PromptRequest request)
    {
        var b = Budgets;
        var notes = new List<string>();
        string instructions = BuildInstructions(request);
        int used = _tokens.Count(instructions) + 300; // schema and framing overhead

        var all = _transcript.Snapshot();
        var transcriptIds = new HashSet<string>(StringComparer.Ordinal);
        var context = new List<ContextBlock>();
        var input = new StringBuilder(8_192);

        string? questionText = request.Question is { } q0 ? Clip(q0.Text, b.Question) : null;
        if (questionText is not null)
        {
            used += _tokens.Count(questionText) + 40;
        }

        // Preparation: notes plus (in interview mode) the selected résumé. A reusable cached block when everything fits,
        // otherwise the most relevant excerpts per request.
        var excerpts = _prep.Excerpts().Where(e => e.Resume is null || request.Scenario == Scenario.Interview).ToList();
        string focus = request.Question?.Text ?? string.Join(" ", all.TakeLast(8).Select(s => s.Text));
        var prepSel = PreparationSelector.Select(excerpts, focus, b.Preparation, _tokens);
        bool prepStable = prepSel.OmittedCount == 0;
        string prepBlock = RenderPreparation(prepStable ? "## Preparation supplied by the user (cite by id)" : "## Preparation excerpts relevant to this request (cite by id)",
            prepSel.Included, excerpts.Count == 0 ? "(No preparation notes or résumé were provided.)" : "(No relevant preparation fits the budget.)");
        string extraPrepBlock = string.Empty;
        if (prepStable)
        {
            context.Add(new ContextBlock(prepBlock));
        }
        else
        {
            extraPrepBlock = prepBlock + $"({prepSel.OmittedCount} less relevant preparation excerpt(s) omitted.)\n\n";
            notes.Add($"{prepSel.OmittedCount} preparation excerpt(s) omitted for the input budget");
        }
        var preparationIds = prepSel.Included.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        used += prepSel.Tokens;

        // Question memory (interview/defense continuity, or any explicit question).
        var memory = request.Scenario != Scenario.Meeting || request.Question is not null
            ? _history.Select(request.Question?.QuestionId, request.Question?.Text, x => ReplyOf(x, all), b.Memory, _tokens)
            : MemorySelection.Empty;
        if (memory.Log.Count > 0 || memory.LogOmitted > 0)
        {
            var log = RenderLog(memory);
            context.AddRange(log);
            used += log.Sum(l => _tokens.Count(l.Text));
        }
        string recentSection = RenderRecent(memory, all, transcriptIds);
        string relatedSection = RenderRelated(memory, all);
        used += _tokens.Count(recentSection) + _tokens.Count(relatedSection);

        // Conversation window with whatever budget remains.
        int convBudget = Math.Min(b.Conversation, Math.Max(500, b.TotalInput - used - 400));
        var window = ConversationWindow.Select(all, _clock.Now, b.ConversationAge, convBudget, _tokens);
        if (window.DroppedForSize > 0)
        {
            notes.Add($"{window.DroppedForSize} older transcript line(s) omitted for the input budget");
        }

        input.Append(recentSection);
        input.Append(relatedSection);
        input.Append(extraPrepBlock);

        input.AppendLine("## Recent conversation (untrusted transcript data, oldest first)");
        input.AppendLine("<transcript>");
        if (window.Segments.Count == 0)
        {
            input.AppendLine("(no finalized speech in the recent window)");
        }
        foreach (var s in window.Segments)
        {
            transcriptIds.Add(s.Id);
            input.Append('[').Append(s.Id).Append(" | ").Append(FormatTime(s.AudioStart)).Append(" | ")
                 .Append(Escape(SpeakerLabel(s))).Append("] ").AppendLine(Escape(s.Text));
        }
        input.AppendLine("</transcript>");
        if (window.DroppedForAge + window.DroppedForSize > 0)
        {
            input.AppendLine($"(Older conversation is not included: {window.DroppedForAge + window.DroppedForSize} line(s) outside the window.)");
        }
        input.AppendLine();

        if (request.Scenario == Scenario.Meeting)
        {
            var recentInsights = _filter.RecentTexts;
            input.AppendLine("## Recently shown insights (do not repeat)");
            input.AppendLine(recentInsights.Count == 0 ? "(none)" : string.Join("\n", recentInsights.Select(r => "- " + Escape(Clip(r, 200)))));
            input.AppendLine();
        }

        // Task and current question last.
        input.AppendLine("## Task");
        input.AppendLine(TaskText(request));
        if (request.Question is { } q)
        {
            var qSegs = q.SegmentIds.Select(id => all.FirstOrDefault(s => s.Id == id)).Where(s => s is not null).Cast<TranscriptSegment>().ToList();
            input.AppendLine();
            input.Append("<question question_id=\"").Append(q.QuestionId).Append("\" revision=\"").Append(q.Revision)
                 .Append("\" segments=\"").Append(string.Join(",", q.SegmentIds)).Append("\" speaker=\"")
                 .Append(Escape(qSegs.Count > 0 ? SpeakerLabel(qSegs[0]) : "unknown")).AppendLine("\">");
            input.AppendLine(Escape(questionText!));
            input.AppendLine("</question>");
            foreach (var id in q.SegmentIds)
            {
                transcriptIds.Add(id);
            }
        }

        string text = input.ToString();
        int total = _tokens.Count(instructions) + context.Sum(c => _tokens.Count(c.Text)) + _tokens.Count(text) + 300;
        return new BuiltPrompt(instructions, text, PromptLibrary.Schema, transcriptIds, preparationIds, total, notes)
        {
            Context = context,
        };
    }

    /// <summary>The user's actual spoken reply to an exchange, from transcript segments attributed to the user.</summary>
    public PinnedExcerpt? ComputeUserResponse(QuestionExchange exchange, IReadOnlyList<TranscriptSegment>? all = null)
    {
        all ??= _transcript.Snapshot();
        var later = _history.Items.Where(x => x.AskedAt > exchange.AskedAt).Select(x => x.AskedAt).DefaultIfEmpty(TimeSpan.MaxValue).Min();
        var segs = all.Where(s => s.IsAnalyzable && s.AudioStart >= exchange.AskedAt - TimeSpan.FromSeconds(1) && s.AudioStart < later && _party(s) == Party.Self).ToList();
        if (segs.Count == 0)
        {
            return null;
        }
        return new PinnedExcerpt(segs.Select(s => s.Id).ToList(), Clip(string.Join(" ", segs.Select(s => s.Text)), 1200));
    }

    private PinnedExcerpt? ReplyOf(QuestionExchange x, IReadOnlyList<TranscriptSegment> all) => x.UserResponse ?? ComputeUserResponse(x, all);

    /// <summary>Notes first, then the résumé under its own header.</summary>
    private static string RenderPreparation(string heading, IReadOnlyList<PrepExcerpt> excerpts, string emptyText)
    {
        var sb = new StringBuilder();
        sb.AppendLine(heading);
        sb.AppendLine("<preparation>");
        if (excerpts.Count == 0)
        {
            sb.AppendLine(emptyText);
        }
        string? group = null;
        foreach (var e in excerpts)
        {
            string g = e.Resume is { } r ? $"The user's résumé \"{Escape(r.Name)}\" (facts about the user):" : "Notes written by the user:";
            if (g != group)
            {
                sb.AppendLine(g);
                group = g;
            }
            sb.Append('[').Append(e.Id).Append("] ");
            if (e.Resume is null)
            {
                sb.Append('(').Append(FieldLabel(e.Field)).Append(") ");
            }
            sb.AppendLine(Escape(e.Text));
        }
        sb.AppendLine("</preparation>");
        return sb.ToString();
    }

    /// <summary>
    /// Chronological log of older exchanges: a header block, then one block per entry. Entries depend only on stored
    /// exchange fields, so between questions the log renders identically except for the entry appended at its end.
    /// </summary>
    private static List<ContextBlock> RenderLog(MemorySelection memory)
    {
        var blocks = new List<ContextBlock>();
        var header = new StringBuilder();
        header.AppendLine("## Session question log (earlier questions in this session, oldest first, abbreviated)");
        if (memory.LogOmitted > 0)
        {
            header.AppendLine($"({memory.LogOmitted} earliest question(s) are no longer listed.)");
        }
        blocks.Add(new ContextBlock(header.ToString(), CacheBreakpoint: false));
        for (int i = 0; i < memory.Log.Count; i++)
        {
            var x = memory.Log[i];
            var sb = new StringBuilder();
            sb.Append("<session_log_entry question_id=\"").Append(x.QuestionId).Append("\" at=\"").Append(FormatTime(x.AskedAt)).Append('"');
            if (x.FollowsUpOn is not null)
            {
                sb.Append(" follows_up_on=\"").Append(x.FollowsUpOn).Append('"');
            }
            if (x.LogForm == LogForm.OneLine)
            {
                sb.Append(" details=\"omitted\"><q>").Append(Escape(QuestionHistory.Clip(x.Question.Text, QuestionHistory.OneLineChars)!)).AppendLine("</q></session_log_entry>");
            }
            else
            {
                sb.AppendLine(">");
                sb.Append("<q>").Append(Escape(QuestionHistory.Clip(x.Question.Text, QuestionHistory.LogQuestionChars)!)).AppendLine("</q>");
                if (!string.IsNullOrWhiteSpace(x.UserResponse?.Text))
                {
                    sb.Append("<user_actually_said>").Append(Escape(QuestionHistory.Clip(x.UserResponse!.Text, QuestionHistory.LogReplyChars)!)).AppendLine("</user_actually_said>");
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(x.DraftSummary))
                    {
                        sb.Append("<ai_draft_shown_not_necessarily_spoken>").Append(Escape(QuestionHistory.Clip(x.DraftSummary, QuestionHistory.LogDraftChars)!))
                          .AppendLine("</ai_draft_shown_not_necessarily_spoken>");
                    }
                    sb.AppendLine("<user_actually_said>(no transcript of the user's reply)</user_actually_said>");
                }
                sb.AppendLine("</session_log_entry>");
            }
            blocks.Add(new ContextBlock(sb.ToString(), CacheBreakpoint: i >= memory.Log.Count - 2));
        }
        return blocks;
    }

    private string RenderRecent(MemorySelection memory, IReadOnlyList<TranscriptSegment> all, HashSet<string> transcriptIds)
    {
        if (memory.Recent.Count == 0)
        {
            return string.Empty;
        }
        var sb = new StringBuilder();
        sb.AppendLine("## Latest questions before the current one (full)");
        sb.AppendLine("<question_history>");
        foreach (var x in memory.Recent)
        {
            var response = ReplyOf(x, all);
            sb.Append("<exchange question_id=\"").Append(x.QuestionId).Append("\" at=\"").Append(FormatTime(x.AskedAt)).Append('"');
            if (x.FollowsUpOn is not null)
            {
                sb.Append(" follows_up_on=\"").Append(x.FollowsUpOn).Append('"');
            }
            sb.AppendLine(">");
            sb.Append("<question segments=\"").Append(string.Join(",", x.Question.SegmentIds)).Append("\">")
              .Append(Escape(QuestionHistory.Clip(x.Question.Text, QuestionHistory.RecentQuestionChars)!)).AppendLine("</question>");
            if (!string.IsNullOrWhiteSpace(x.DraftSummary))
            {
                sb.Append("<ai_draft_shown_not_necessarily_spoken>").Append(Escape(x.DraftSummary!)).AppendLine("</ai_draft_shown_not_necessarily_spoken>");
            }
            if (response is not null && !string.IsNullOrWhiteSpace(response.Text))
            {
                sb.Append("<user_actually_said segments=\"").Append(string.Join(",", response.SegmentIds)).Append("\">")
                  .Append(Escape(response.Text)).AppendLine("</user_actually_said>");
                foreach (var id in response.SegmentIds)
                {
                    transcriptIds.Add(id);
                }
            }
            else
            {
                sb.AppendLine("<user_actually_said>(no transcript of the user's reply)</user_actually_said>");
            }
            sb.AppendLine("</exchange>");
            foreach (var id in x.Question.SegmentIds)
            {
                transcriptIds.Add(id);
            }
        }
        sb.AppendLine("</question_history>");
        sb.AppendLine();
        return sb.ToString();
    }

    private string RenderRelated(MemorySelection memory, IReadOnlyList<TranscriptSegment> all)
    {
        if (memory.Related.Count == 0 && memory.Thread.Count == 0)
        {
            return string.Empty;
        }
        var sb = new StringBuilder();
        sb.AppendLine("## Follow-up chain and related earlier exchanges (more detail than the session log)");
        if (memory.Thread.Count > 0)
        {
            sb.Append("Follow-up chain ending at the latest exchange: ").AppendLine(string.Join(" → ", memory.Thread));
        }
        if (memory.Related.Count > 0)
        {
            sb.AppendLine("<related_exchanges>");
            foreach (var x in memory.Related)
            {
                var response = ReplyOf(x, all);
                sb.Append("<exchange question_id=\"").Append(x.QuestionId).Append("\" at=\"").Append(FormatTime(x.AskedAt))
                  .Append("\" relation=\"").Append(memory.Thread.Contains(x.QuestionId) ? "follow-up chain" : "earlier exchange").AppendLine("\">");
                sb.Append("<question>").Append(Escape(QuestionHistory.Clip(x.Question.Text, QuestionHistory.RelatedQuestionChars)!)).AppendLine("</question>");
                if (!string.IsNullOrWhiteSpace(x.DraftSummary))
                {
                    sb.Append("<ai_draft_shown_not_necessarily_spoken>").Append(Escape(QuestionHistory.Clip(x.DraftSummary, QuestionHistory.RelatedDraftChars)!))
                      .AppendLine("</ai_draft_shown_not_necessarily_spoken>");
                }
                sb.Append("<user_actually_said>")
                  .Append(string.IsNullOrWhiteSpace(response?.Text) ? "(no transcript of the user's reply)" : Escape(QuestionHistory.Clip(response!.Text, QuestionHistory.RelatedReplyChars)!))
                  .AppendLine("</user_actually_said>");
                sb.AppendLine("</exchange>");
            }
            sb.AppendLine("</related_exchanges>");
        }
        sb.AppendLine();
        return sb.ToString();
    }

    private string BuildInstructions(PromptRequest r)
    {
        string scenarioGuidance = r.Scenario switch
        {
            Scenario.Interview =>
                "The user is the candidate in a job interview; other speakers are interviewers. Identify what the interviewer actually asked and write the answer the candidate can give. " +
                "For technical questions explain the mechanism, justify trade-offs against alternatives and give a concrete example. " +
                "For behavioral or experience questions tell a clear first-person story (situation, task, action, result) built on the user's established facts (notes, résumé, what the user said), supplying missing specifics as the content rules describe.",
            Scenario.Defense =>
                "The user is defending academic work (thesis, dissertation or proposal) before examiners. Answer the examiner's question as the candidate. " +
                "Where relevant, relate the answer to the research question, the method and its justification against the comparison baseline, the results, the contribution and the limitations - but do not force every answer into every section. " +
                "Never invent citations. Acknowledge real limitations of the work when asked, and supply missing specifics of the user's own work as the content rules describe.",
            _ =>
                "The user is a participant in a meeting. Intervene rarely: prefer none unless there is a clear unresolved requirement, risk, overlooked alternative or consequential claim worth checking. " +
                "If someone directly asks the user a substantive question, a full answer is appropriate.",
        };
        string depth = r.Depth == AnswerDepth.Detailed
            ? "Depth: detailed. For a substantive question aim for roughly 300-600 English words or 600-1200 Chinese characters in spoken_answer (about 2-3 minutes of speech), with richer examples and justification."
            : "Depth: normal. For a substantive question aim for roughly 120-300 English words or 250-600 Chinese characters in spoken_answer (about 1-2 minutes of speech).";
        string language = string.Equals(r.LanguageSetting, "auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(r.LanguageSetting)
            ? "Write in the language of the question (Chinese question -> Chinese answer, English question -> English answer; for mixed speech use the dominant language). Keep technical terms in their customary form."
            : $"Write the result in this language: {r.LanguageSetting}. Keep technical terms in their customary form.";
        return PromptLibrary.Instructions
            .Replace("{{SCENARIO}}", r.Scenario.ToString(), StringComparison.Ordinal)
            .Replace("{{SCENARIO_GUIDANCE}}", scenarioGuidance, StringComparison.Ordinal)
            .Replace("{{DEPTH_GUIDANCE}}", depth, StringComparison.Ordinal)
            .Replace("{{LANGUAGE_GUIDANCE}}", language, StringComparison.Ordinal);
    }

    private string TaskText(PromptRequest r)
    {
        if (r.Question is { } q)
        {
            var sb = new StringBuilder();
            sb.Append(q.Manual
                ? $"The user selected transcript text and explicitly asked for an answer (question_id={q.QuestionId}, revision={q.Revision}). Answer it unless it is not a question at all."
                : $"A question was detected in the conversation (question_id={q.QuestionId}, revision={q.Revision}). Decide the response kind and, if it is a substantive question directed at the user, write the full answer.");
            if (q.Revision > 1)
            {
                sb.Append(" The question was extended or corrected since an earlier revision; answer the complete current version.");
            }
            if (q.SpeakerUncertain)
            {
                sb.Append(" The speaker label for this question is not confirmed.");
            }
            if (q.MultiPart)
            {
                sb.Append(" It may contain several parts; address every part.");
            }
            return sb.ToString();
        }
        string triggers = r.Triggers.Count == 0 ? "none" : string.Join(", ", r.Triggers);
        return "Review the newest part of the recent conversation" +
               (r.NewSinceSegmentId is null ? string.Empty : $" (segments after {r.NewSinceSegmentId})") +
               $". Simple local heuristics flagged: {triggers}. These flags are hints, not conclusions. Return none unless a brief insight, clarification or answer would genuinely help the user now.";
    }

    private string SpeakerLabel(TranscriptSegment s)
    {
        string source = s.Source == AudioSourceKind.System ? "system audio" : "microphone";
        var a = s.Attribution;
        var speaker = _speakers.Get(a.SpeakerId);
        string role = speaker is { Role: not SpeakerRole.Unknown and not SpeakerRole.Me } ? $", role: {speaker.Role}" : string.Empty;
        return a.Status switch
        {
            AttributionStatus.SourceAlias => $"Me (microphone alias, not verified identity)",
            AttributionStatus.UserAssigned when speaker is not null => $"{speaker.Name}{role} | {source} | assigned by user",
            AttributionStatus.Assigned when speaker is not null => $"{speaker.Name}{role} | {source}{(speaker.IsProvisional ? " | label uncertain" : string.Empty)}",
            AttributionStatus.Multiple when speaker is not null => $"multiple speakers (mostly {speaker.Name}) | {source} | label uncertain",
            AttributionStatus.Pending => $"speaker label pending | {source}",
            _ => $"unknown speaker | {source}",
        };
    }

    private static string FieldLabel(PrepField f) => f switch
    {
        PrepField.RoleOrTopic => "role / research topic",
        PrepField.Contribution => "personal contribution",
        PrepField.ProjectOrMethod => "project / method details",
        PrepField.VerifiedResults => "verified results",
        PrepField.Limitations => "limitations",
        PrepField.Terminology => "terminology",
        _ => "other",
    };

    internal static string Escape(string text) => text.Replace('<', '‹').Replace('>', '›');

    private string Clip(string text, int maxTokens)
    {
        if (_tokens.Count(text) <= maxTokens)
        {
            return text;
        }
        int chars = Math.Max(100, text.Length * maxTokens / Math.Max(1, _tokens.Count(text)));
        return text[..Math.Min(text.Length, chars)] + "…";
    }

    private static string FormatTime(TimeSpan t) =>
        t < TimeSpan.Zero ? "00:00" : t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
}
