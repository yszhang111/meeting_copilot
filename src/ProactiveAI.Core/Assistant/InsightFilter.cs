using ProactiveAI.Core.Text;

namespace ProactiveAI.Core.Assistant;

public sealed record InsightFilterOptions
{
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxPerMinute { get; init; } = 2;
    public TimeSpan SuppressionWindow { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan DismissedWindow { get; init; } = TimeSpan.FromMinutes(10);
    public double EquivalenceThreshold { get; init; } = 0.6;
}

public enum FilterDecision
{
    Show,
    Cooldown,
    RateLimited,
    Duplicate,
    PreviouslyDismissed,
}

/// <summary>
/// Meeting-mode notification filter: cooldown, per-minute cap, and exact/normalized plus modest
/// token-overlap equivalence suppression. Best-effort; topic overlap alone does not suppress a
/// distinct question. Interview/defense answers are not subject to these limits.
/// </summary>
public sealed class InsightFilter
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly List<(string Key, string Text, DateTimeOffset At)> _shown = new();
    private readonly List<(string Key, string Text, DateTimeOffset At)> _dismissed = new();
    private InsightFilterOptions _o;

    public InsightFilter(TimeProvider time, InsightFilterOptions? options = null)
    {
        _time = time;
        _o = options ?? new InsightFilterOptions();
    }

    public InsightFilterOptions Options
    {
        get { lock (_gate) { return _o; } }
        set { lock (_gate) { _o = value; } }
    }

    /// <summary>Recently shown insight titles for the model's dedupe context.</summary>
    public IReadOnlyList<string> RecentTexts
    {
        get { lock (_gate) { return _shown.Select(s => s.Text).TakeLast(8).ToList(); } }
    }

    public FilterDecision Evaluate(string? title, string? body, string? suggestedQuestion)
    {
        var now = _time.GetUtcNow();
        string text = $"{title} {body} {suggestedQuestion}";
        string key = TextTools.Normalize(text);
        lock (_gate)
        {
            Prune(now);
            if (_dismissed.Any(d => d.Key == key || TextTools.Jaccard(d.Text, text) >= _o.EquivalenceThreshold))
            {
                return FilterDecision.PreviouslyDismissed;
            }
            if (_shown.Any(s => s.Key == key || TextTools.Jaccard(s.Text, text) >= _o.EquivalenceThreshold))
            {
                return FilterDecision.Duplicate;
            }
            if (_shown.Count > 0 && now - _shown[^1].At < _o.Cooldown)
            {
                return FilterDecision.Cooldown;
            }
            if (_shown.Count(s => now - s.At < TimeSpan.FromMinutes(1)) >= _o.MaxPerMinute)
            {
                return FilterDecision.RateLimited;
            }
            return FilterDecision.Show;
        }
    }

    public void RecordShown(string? title, string? body, string? suggestedQuestion)
    {
        string text = $"{title} {body} {suggestedQuestion}";
        lock (_gate)
        {
            _shown.Add((TextTools.Normalize(text), text, _time.GetUtcNow()));
        }
    }

    public void RecordDismissed(string? title, string? body, string? suggestedQuestion)
    {
        string text = $"{title} {body} {suggestedQuestion}";
        lock (_gate)
        {
            _dismissed.Add((TextTools.Normalize(text), text, _time.GetUtcNow()));
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _shown.Clear();
            _dismissed.Clear();
        }
    }

    private void Prune(DateTimeOffset now)
    {
        _shown.RemoveAll(s => now - s.At > _o.SuppressionWindow && now - s.At > TimeSpan.FromMinutes(1));
        _dismissed.RemoveAll(s => now - s.At > _o.DismissedWindow);
    }
}
