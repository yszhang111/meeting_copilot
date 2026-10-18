namespace ProactiveAI.Core.Session;

/// <summary>Sliding one-minute request budget. Every attempt, including retries, consumes budget.</summary>
public sealed class RequestBudget
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _stamps = new();
    private readonly TimeProvider _time;
    private int _maxPerMinute;

    public RequestBudget(TimeProvider time, int maxPerMinute)
    {
        _time = time;
        _maxPerMinute = maxPerMinute;
    }

    public long TotalAcquired { get; private set; }

    public long TotalRejected { get; private set; }

    public int MaxPerMinute
    {
        get { lock (_gate) { return _maxPerMinute; } }
        set { lock (_gate) { _maxPerMinute = Math.Max(1, value); } }
    }

    public bool TryAcquire()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Prune(now);
            if (_stamps.Count >= _maxPerMinute)
            {
                TotalRejected++;
                return false;
            }
            _stamps.Enqueue(now);
            TotalAcquired++;
            return true;
        }
    }

    public int UsedInLastMinute
    {
        get
        {
            lock (_gate)
            {
                Prune(_time.GetUtcNow());
                return _stamps.Count;
            }
        }
    }

    /// <summary>Time until the next slot frees up, or zero if one is available.</summary>
    public TimeSpan NextAvailableIn
    {
        get
        {
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                Prune(now);
                if (_stamps.Count < _maxPerMinute)
                {
                    return TimeSpan.Zero;
                }
                var wait = _stamps.Peek().AddMinutes(1) - now;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _stamps.Clear();
        }
    }

    private void Prune(DateTimeOffset now)
    {
        while (_stamps.Count > 0 && now - _stamps.Peek() >= TimeSpan.FromMinutes(1))
        {
            _stamps.Dequeue();
        }
    }
}
