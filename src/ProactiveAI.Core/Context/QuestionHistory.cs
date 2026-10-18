using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Text;

namespace ProactiveAI.Core.Context;

/// <summary>A pinned excerpt of conversation text with its evidence ids.</summary>
public sealed record PinnedExcerpt(IReadOnlyList<string> SegmentIds, string Text)
{
    /// <summary>Marks an exchange whose reply window closed without any speech from the user.</summary>
    public static readonly PinnedExcerpt NoReply = new([], string.Empty);
}

/// <summary>How an exchange appears in the session log. Transitions only go forward so the logged text stays stable.</summary>
public enum LogForm
{
    Compact,
    OneLine,
    Omitted,
}

/// <summary>
/// One question/answer exchange. <see cref="DraftSummary"/> is AI-generated and was not necessarily
/// said; <see cref="UserResponse"/> is what the transcript shows the user actually said.
/// </summary>
public sealed record QuestionExchange(
    string QuestionId,
    int Revision,
    PinnedExcerpt Question,
    string? DraftSummary,
    PinnedExcerpt? UserResponse,
    TimeSpan AskedAt)
{
    /// <summary>Earlier question this one follows up on or refers back to, as identified by the answer model.</summary>
    public string? FollowsUpOn { get; init; }

    public LogForm LogForm { get; init; }
}

public sealed record MemoryBudgets
{
    /// <summary>Number of latest exchanges before the current question that are shown in full.</summary>
    public int RecentCount { get; init; } = 2;
    public int Recent { get; init; } = 2_200;
    /// <summary>Compact log of all older exchanges; when exceeded, the oldest entries are condensed until the log fits <see cref="LogTarget"/>.</summary>
    public int Log { get; init; } = 3_000;
    public int LogTarget { get; init; } = 1_800;
    /// <summary>Detailed copies of older exchanges on the follow-up chain or relevant to the current question.</summary>
    public int Related { get; init; } = 1_500;
    public int MaxRelevant { get; init; } = 2;
    public double MinRelevance { get; init; } = 0.15;
}

/// <param name="Log">Older exchanges, chronological, in their <see cref="LogForm"/>.</param>
/// <param name="LogOmitted">Oldest exchanges no longer listed at all.</param>
/// <param name="Recent">Latest exchanges before the current question, full form.</param>
/// <param name="Related">Detailed copies of older exchanges (follow-up chain, relevant), chronological.</param>
/// <param name="Thread">Question ids of the follow-up chain ending at the latest exchange, root first; empty when there is none.</param>
public sealed record MemorySelection(
    IReadOnlyList<QuestionExchange> Log,
    int LogOmitted,
    IReadOnlyList<QuestionExchange> Recent,
    IReadOnlyList<QuestionExchange> Related,
    IReadOnlyList<string> Thread)
{
    public static readonly MemorySelection Empty = new([], 0, [], [], []);
}

/// <summary>
/// Session-long question/answer memory for interview and defense mode. Every exchange of the session is kept (bounded);
/// a request sees the latest exchanges in full, all older ones as a compact chronological log, and detailed copies of
/// the exchanges on the current follow-up chain or relevant to the current question.
/// </summary>
public sealed class QuestionHistory
{
    public const int MaxExchanges = 200;

    // Clip lengths (characters) per form; selection and rendering use the same limits.
    public const int LogQuestionChars = 200;
    public const int LogReplyChars = 300;
    public const int LogDraftChars = 120;
    public const int OneLineChars = 120;
    public const int RelatedQuestionChars = 400;
    public const int RelatedReplyChars = 900;
    public const int RelatedDraftChars = 300;
    public const int RecentQuestionChars = 1000;
    public const int ReducedReplyChars = 400;
    public const int ReducedDraftChars = 300;

    private readonly object _gate = new();
    private readonly List<QuestionExchange> _items = new();

    public IReadOnlyList<QuestionExchange> Items
    {
        get { lock (_gate) { return _items.ToList(); } }
    }

    /// <summary>Adds an exchange, or replaces an earlier revision of it while keeping its follow-up link and log form.</summary>
    public void Upsert(QuestionExchange exchange)
    {
        lock (_gate)
        {
            int i = _items.FindIndex(x => x.QuestionId == exchange.QuestionId);
            if (i >= 0)
            {
                var old = _items[i];
                _items[i] = exchange with { FollowsUpOn = exchange.FollowsUpOn ?? old.FollowsUpOn, LogForm = old.LogForm };
            }
            else
            {
                _items.Add(exchange);
            }
            while (_items.Count > MaxExchanges)
            {
                _items.RemoveAt(0);
            }
        }
    }

    public void Update(string questionId, Func<QuestionExchange, QuestionExchange> f)
    {
        lock (_gate)
        {
            int i = _items.FindIndex(x => x.QuestionId == questionId);
            if (i >= 0)
            {
                _items[i] = f(_items[i]);
            }
        }
    }

    public QuestionExchange? Find(string questionId)
    {
        lock (_gate)
        {
            return _items.FirstOrDefault(x => x.QuestionId == questionId);
        }
    }

    /// <summary>Records that <paramref name="questionId"/> follows up on an earlier exchange. Unknown, later and self links are ignored.</summary>
    public bool LinkFollowUp(string questionId, string? earlierQuestionId)
    {
        if (string.IsNullOrWhiteSpace(earlierQuestionId))
        {
            return false;
        }
        lock (_gate)
        {
            int i = _items.FindIndex(x => x.QuestionId == questionId);
            int j = _items.FindIndex(x => x.QuestionId == earlierQuestionId);
            if (i < 0 || j < 0 || j >= i || _items[i].FollowsUpOn == earlierQuestionId)
            {
                return false;
            }
            _items[i] = _items[i] with { FollowsUpOn = earlierQuestionId };
            return true;
        }
    }

    /// <summary>
    /// Chooses what the next request sees. Exchanges leave the recent window into the log in order, and log entries are
    /// condensed (finally omitted) oldest first in batches, so between questions the rendered log usually only grows at
    /// its end — which keeps it reusable as a cached prompt prefix.
    /// </summary>
    /// <param name="replyOf">The user's reply to an exchange (pinned, or computed from the transcript for the latest one).</param>
    public MemorySelection Select(string? currentQuestionId, string? focusText, Func<QuestionExchange, PinnedExcerpt?> replyOf, MemoryBudgets b, ITokenCounter tokens)
    {
        lock (_gate)
        {
            var prior = _items.Where(x => x.QuestionId != currentQuestionId).ToList();
            if (prior.Count == 0)
            {
                return MemorySelection.Empty;
            }
            var order = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < prior.Count; i++)
            {
                order[prior[i].QuestionId] = i;
            }
            var replies = prior.ToDictionary(x => x.QuestionId, replyOf, StringComparer.Ordinal);

            // Recent: full form, newest first; reduced form when over budget. Whatever does not fit goes to the log.
            var recent = new List<QuestionExchange>();
            int used = 0;
            for (int i = prior.Count - 1; i >= Math.Max(0, prior.Count - b.RecentCount); i--)
            {
                var x = prior[i];
                var reply = replies[x.QuestionId]?.Text;
                int t = Cost(tokens, Clip(x.Question.Text, RecentQuestionChars), x.DraftSummary, reply);
                if (used + t > b.Recent)
                {
                    t = Cost(tokens, Clip(x.Question.Text, RecentQuestionChars), Clip(x.DraftSummary, ReducedDraftChars), Clip(reply, ReducedReplyChars));
                    if (used + t > b.Recent)
                    {
                        break;
                    }
                }
                recent.Add(x);
                used += t;
            }
            recent.Reverse();
            var recentIds = recent.Select(x => x.QuestionId).ToHashSet(StringComparer.Ordinal);
            var older = prior.Where(x => !recentIds.Contains(x.QuestionId)).ToList();

            // Log: when over budget, in one batch: drop the oldest one-liners beyond half the target, condense the oldest
            // compact entries, and finally drop more one-liners if still needed.
            int LogCost(QuestionExchange x) => x.LogForm switch
            {
                LogForm.Compact => LogEntryCost(tokens, x, replies[x.QuestionId]),
                LogForm.OneLine => tokens.Count(Clip(x.Question.Text, OneLineChars)!) + 30,
                _ => 0,
            };
            int total = older.Sum(LogCost);
            if (total > b.Log)
            {
                int oneLiners = older.Where(x => x.LogForm == LogForm.OneLine).Sum(LogCost);
                for (int i = 0; i < older.Count && oneLiners > b.LogTarget / 2; i++)
                {
                    if (older[i].LogForm == LogForm.OneLine)
                    {
                        int c = LogCost(older[i]);
                        oneLiners -= c;
                        total -= c;
                        older[i] = SetFormLocked(older[i], LogForm.Omitted);
                    }
                }
                foreach (var (from, to) in new[] { (LogForm.Compact, LogForm.OneLine), (LogForm.OneLine, LogForm.Omitted) })
                {
                    for (int i = 0; i < older.Count && total > b.LogTarget; i++)
                    {
                        if (older[i].LogForm == from)
                        {
                            total -= LogCost(older[i]);
                            older[i] = SetFormLocked(older[i], to);
                            total += LogCost(older[i]);
                        }
                    }
                }
            }
            var log = older.Where(x => x.LogForm != LogForm.Omitted).ToList();
            int omitted = older.Count - log.Count;

            // Follow-up chains through the recent window; the one ending at the latest exchange is the thread.
            var byId = prior.ToDictionary(x => x.QuestionId, StringComparer.Ordinal);
            var thread = new List<string>();
            var ancestorIds = new List<string>();
            for (int r = recent.Count - 1; r >= 0; r--)
            {
                var chain = new List<string> { recent[r].QuestionId };
                var cur = recent[r];
                while (cur.FollowsUpOn is { } parent && byId.TryGetValue(parent, out var p) && !chain.Contains(parent))
                {
                    chain.Add(parent);
                    if (!recentIds.Contains(parent) && !ancestorIds.Contains(parent))
                    {
                        ancestorIds.Add(parent);
                    }
                    cur = p;
                }
                if (r == recent.Count - 1 && chain.Count > 1)
                {
                    chain.Reverse();
                    thread = chain;
                }
            }

            // Related details: the chain root first, then nearer ancestors, then the most relevant other older exchanges.
            var candidateIds = new List<string>();
            if (ancestorIds.Count > 0)
            {
                string root = ancestorIds.MinBy(id => order[id])!;
                candidateIds.Add(root);
                candidateIds.AddRange(ancestorIds.Where(id => id != root));
            }
            if (!string.IsNullOrWhiteSpace(focusText))
            {
                candidateIds.AddRange(older
                    .Where(x => !candidateIds.Contains(x.QuestionId))
                    .Select(x => (x.QuestionId, Score: Relevance(focusText, x, replies[x.QuestionId])))
                    .Where(s => s.Score >= b.MinRelevance)
                    .OrderByDescending(s => s.Score)
                    .Take(b.MaxRelevant)
                    .Select(s => s.QuestionId));
            }
            var related = new List<QuestionExchange>();
            int relatedUsed = 0;
            foreach (var id in candidateIds)
            {
                var x = byId[id];
                int t = Cost(tokens, Clip(x.Question.Text, RelatedQuestionChars), Clip(x.DraftSummary, RelatedDraftChars), Clip(replies[id]?.Text, RelatedReplyChars));
                if (relatedUsed + t <= b.Related)
                {
                    related.Add(x);
                    relatedUsed += t;
                }
            }
            related.Sort((p, q) => order[p.QuestionId].CompareTo(order[q.QuestionId]));
            return new MemorySelection(log, omitted, recent, related, thread);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }

    /// <summary>A compact log entry shows what the user said, or the AI draft when no reply was captured.</summary>
    public static int LogEntryCost(ITokenCounter tokens, QuestionExchange x, PinnedExcerpt? reply)
    {
        bool hasReply = !string.IsNullOrWhiteSpace(reply?.Text);
        return Cost(tokens, Clip(x.Question.Text, LogQuestionChars), hasReply ? null : Clip(x.DraftSummary, LogDraftChars), hasReply ? Clip(reply!.Text, LogReplyChars) : null);
    }

    public static string? Clip(string? s, int max) => s is null || s.Length <= max ? s : s[..max] + "…";

    private static double Relevance(string focus, QuestionExchange x, PinnedExcerpt? reply) =>
        TextTools.Containment(focus, x.Question.Text + " " + (string.IsNullOrWhiteSpace(reply?.Text) ? x.DraftSummary : reply!.Text))
        + TextTools.Jaccard(focus, x.Question.Text);

    private QuestionExchange SetFormLocked(QuestionExchange x, LogForm form)
    {
        var updated = x with { LogForm = form };
        int i = _items.FindIndex(e => e.QuestionId == x.QuestionId);
        if (i >= 0)
        {
            _items[i] = updated;
        }
        return updated;
    }

    private static int Cost(ITokenCounter tokens, string? question, string? draft, string? reply) =>
        tokens.Count(question ?? string.Empty) + tokens.Count(draft ?? string.Empty) + tokens.Count(reply ?? string.Empty) + 40;
}
