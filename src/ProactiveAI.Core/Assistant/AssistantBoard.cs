namespace ProactiveAI.Core.Assistant;

public enum CardKind
{
    Answer,
    Insight,
    Clarification,
    NoAction,
}

public enum CardState
{
    /// <summary>Waiting for the single reasoning slot.</summary>
    Queued,
    /// <summary>Waiting for request budget; not silently dropped.</summary>
    Limited,
    Generating,
    Complete,
    /// <summary>Stopped by pause/stop/reset, a newer question, or the user. Partial text is preserved.</summary>
    Interrupted,
    /// <summary>Completed output failed validation; partial text is shown as invalid.</summary>
    Invalid,
    Failed,
}

/// <summary>Immutable snapshot of an answer or insight card.</summary>
public sealed record AssistantCard
{
    public required string Id { get; init; }
    public required CardKind Kind { get; init; }
    public CardState State { get; init; }
    public long SessionId { get; init; }
    public string? QuestionId { get; init; }
    public int Revision { get; init; }
    public string? QuestionText { get; init; }
    public IReadOnlyList<string> QuestionSegmentIds { get; init; } = [];
    public bool Manual { get; init; }
    public bool SpeakerUncertain { get; init; }
    public string? Title { get; init; }
    public string? DirectPosition { get; init; }
    public string? SpokenAnswer { get; init; }
    public string? Suggestion { get; init; }
    public string? SuggestedQuestion { get; init; }
    public IReadOnlyList<string> SupportingPoints { get; init; } = [];
    public IReadOnlyList<string> AssumptionsAndLimits { get; init; } = [];
    public IReadOnlyList<FollowUp> LikelyFollowups { get; init; } = [];
    public IReadOnlyList<string> MissingFacts { get; init; } = [];
    /// <summary>Details about the user's own work the model supplied; shown so the user can check them before saying them.</summary>
    public IReadOnlyList<string> FilledInDetails { get; init; } = [];
    public IReadOnlyList<string> EvidenceSegmentIds { get; init; } = [];
    public IReadOnlyList<string> PreparationEvidenceIds { get; init; } = [];
    public string Category { get; init; } = "none";
    public string? DecisionNote { get; init; }
    public string? StatusDetail { get; init; }
    public bool Pinned { get; init; }
    public bool Dismissed { get; init; }
    public bool AskedAgain { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public DateTimeOffset? QuestionDetectedUtc { get; init; }
    public TimeSpan? FirstUsefulTextLatency { get; init; }
    public TimeSpan? CompleteLatency { get; init; }
    /// <summary>Session-clock times of the first useful answer text and of completion (latency evaluation against ground truth).</summary>
    public TimeSpan? FirstUsefulTextAt { get; init; }
    public TimeSpan? CompletedAt { get; init; }
    public string? Model { get; init; }
    public string? PromptVersion { get; init; }
    public int ContentVersion { get; init; }

    public bool IsTerminal => State is CardState.Complete or CardState.Interrupted or CardState.Invalid or CardState.Failed;

    public bool IsDraft => State is CardState.Generating or CardState.Interrupted or CardState.Invalid or CardState.Failed;
}

public enum CardChangeKind
{
    Added,
    Updated,
    Removed,
    Cleared,
}

public sealed record CardChange(CardChangeKind Kind, AssistantCard? Card);

/// <summary>Bounded in-memory history of answers and insights with pin/dismiss state.</summary>
public sealed class AssistantBoard
{
    public const int DefaultMaxCards = 100;

    private readonly object _gate = new();
    private readonly List<AssistantCard> _cards = new();
    private readonly int _max;
    private int _next;
    private int _trimmed;

    public AssistantBoard(int maxCards = DefaultMaxCards) => _max = maxCards;

    public event Action<CardChange>? Changed;

    public int TrimmedCount
    {
        get { lock (_gate) { return _trimmed; } }
    }

    public IReadOnlyList<AssistantCard> Cards
    {
        get { lock (_gate) { return _cards.ToList(); } }
    }

    public string NextId()
    {
        lock (_gate)
        {
            return $"card-{++_next}";
        }
    }

    public AssistantCard? Get(string id)
    {
        lock (_gate)
        {
            return _cards.FirstOrDefault(c => c.Id == id);
        }
    }

    public AssistantCard? FindByQuestion(string questionId)
    {
        lock (_gate)
        {
            return _cards.LastOrDefault(c => c.QuestionId == questionId);
        }
    }

    public void Add(AssistantCard card)
    {
        var changes = new List<CardChange> { new(CardChangeKind.Added, card) };
        lock (_gate)
        {
            _cards.Add(card);
            while (_cards.Count > _max)
            {
                int victim = _cards.FindIndex(c => !c.Pinned && c.IsTerminal);
                if (victim < 0)
                {
                    victim = _cards.FindIndex(c => !c.Pinned);
                }
                if (victim < 0)
                {
                    break;
                }
                var removed = _cards[victim];
                _cards.RemoveAt(victim);
                _trimmed++;
                changes.Add(new CardChange(CardChangeKind.Removed, removed));
            }
        }
        Raise(changes);
    }

    public AssistantCard? Update(string id, Func<AssistantCard, AssistantCard?> f)
    {
        AssistantCard? updated = null;
        lock (_gate)
        {
            int i = _cards.FindIndex(c => c.Id == id);
            if (i >= 0)
            {
                var next = f(_cards[i]);
                if (next is not null && !ReferenceEquals(next, _cards[i]))
                {
                    updated = next with { ContentVersion = _cards[i].ContentVersion + 1 };
                    _cards[i] = updated;
                }
            }
        }
        if (updated is not null)
        {
            Raise([new CardChange(CardChangeKind.Updated, updated)]);
        }
        return updated;
    }

    public void SetPinned(string id, bool pinned)
    {
        if (pinned)
        {
            // only one pinned reading target at a time
            foreach (var other in Cards.Where(c => c.Pinned && c.Id != id))
            {
                Update(other.Id, c => c with { Pinned = false });
            }
        }
        Update(id, c => c.Pinned == pinned ? null : c with { Pinned = pinned });
    }

    public void Dismiss(string id) => Update(id, c => c.Dismissed ? null : c with { Dismissed = true, Pinned = false });

    /// <summary>The answer the reading panel should show: the pinned one, else the newest non-dismissed answer.</summary>
    public AssistantCard? FocusAnswer()
    {
        lock (_gate)
        {
            return _cards.LastOrDefault(c => c.Pinned && c.Kind != CardKind.Insight)
                   ?? _cards.LastOrDefault(c => !c.Dismissed && c.Kind is CardKind.Answer or CardKind.Clarification or CardKind.NoAction);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _cards.Clear();
            _trimmed = 0;
        }
        Raise([new CardChange(CardChangeKind.Cleared, null)]);
    }

    private void Raise(List<CardChange> changes)
    {
        foreach (var c in changes)
        {
            Changed?.Invoke(c);
        }
    }
}
