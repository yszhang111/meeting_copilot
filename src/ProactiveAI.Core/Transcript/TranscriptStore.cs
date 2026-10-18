using ProactiveAI.Core.Audio;

namespace ProactiveAI.Core.Transcript;

/// <summary>Identifies one locally detected audio turn; provider item ids are mapped onto it.</summary>
public readonly record struct TurnKey(long SessionId, AudioSourceKind Source, int Generation, long TurnId);

/// <summary>
/// Bounded, thread-safe in-memory transcript. Partial text merges into its segment; finalization
/// completes the same segment rather than adding a duplicate. Change notifications are raised
/// after the internal lock is released, on the calling thread.
/// </summary>
public sealed class TranscriptStore
{
    public const int DefaultMaxSegments = 2000;
    public const int MaxSegmentChars = 4000;

    private readonly object _gate = new();
    private readonly Dictionary<string, TranscriptSegment> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<TurnKey, string> _byTurn = new();
    private readonly LinkedList<string> _order = new(); // insertion order for trimming
    private readonly int _maxSegments;
    private readonly TimeProvider _time;
    private long _nextId;
    private long _sequence;
    private int _trimmedTotal;

    public TranscriptStore(TimeProvider time, int maxSegments = DefaultMaxSegments)
    {
        _time = time;
        _maxSegments = maxSegments;
    }

    public event Action<TranscriptChange>? Changed;

    public int TrimmedCount
    {
        get { lock (_gate) { return _trimmedTotal; } }
    }

    public int Count
    {
        get { lock (_gate) { return _byId.Count; } }
    }

    public TranscriptSegment? Get(string id)
    {
        lock (_gate)
        {
            return _byId.GetValueOrDefault(id);
        }
    }

    public TranscriptSegment? GetByTurn(TurnKey key)
    {
        lock (_gate)
        {
            return _byTurn.TryGetValue(key, out var id) ? _byId.GetValueOrDefault(id) : null;
        }
    }

    /// <summary>Segments ordered by audio time, then arrival sequence.</summary>
    public IReadOnlyList<TranscriptSegment> Snapshot()
    {
        lock (_gate)
        {
            return _byId.Values.OrderBy(s => s.AudioStart).ThenBy(s => s.Sequence).ToList();
        }
    }

    public TranscriptSegment ApplyDelta(TurnKey key, string delta, TimeSpan audioStart, TimeSpan audioEnd, AudioAttributionDefault attribution)
    {
        var changes = new List<TranscriptChange>(2);
        TranscriptSegment result;
        lock (_gate)
        {
            if (_byTurn.TryGetValue(key, out var id) && _byId.TryGetValue(id, out var existing))
            {
                if (existing.IsFinal)
                {
                    return existing; // late delta after finalization is ignored
                }
                result = existing with
                {
                    Text = Cap(existing.Text + delta),
                    TextVersion = existing.TextVersion + 1,
                    AudioEnd = audioEnd > existing.AudioEnd ? audioEnd : existing.AudioEnd,
                };
                _byId[id] = result;
                changes.Add(new TranscriptChange(TranscriptChangeKind.Updated, result));
            }
            else
            {
                result = CreateLocked(key, Cap(delta), false, audioStart, audioEnd, attribution, changes);
            }
        }
        Raise(changes);
        return result;
    }

    /// <summary>Completes a turn. Returns null when the final transcript is empty (the provisional segment is removed).</summary>
    public TranscriptSegment? ApplyFinal(TurnKey key, string transcript, TimeSpan audioStart, TimeSpan audioEnd, AudioAttributionDefault attribution, string? providerItemId)
    {
        var changes = new List<TranscriptChange>(2);
        TranscriptSegment? result;
        string text = Cap(transcript.Trim());
        lock (_gate)
        {
            if (_byTurn.TryGetValue(key, out var id) && _byId.TryGetValue(id, out var existing))
            {
                if (existing.IsFinal && existing.Text == text)
                {
                    return existing; // duplicate completion
                }
                if (text.Length == 0)
                {
                    RemoveLocked(id, changes);
                    _byTurn.Remove(key);
                    result = null;
                }
                else
                {
                    result = existing with
                    {
                        Text = text,
                        IsFinal = true,
                        FinalizedUtc = existing.FinalizedUtc ?? _time.GetUtcNow(),
                        TextVersion = existing.TextVersion + 1,
                        AudioStart = audioStart,
                        AudioEnd = audioEnd,
                        ProviderItemId = providerItemId ?? existing.ProviderItemId,
                    };
                    _byId[id] = result;
                    changes.Add(new TranscriptChange(TranscriptChangeKind.Updated, result));
                }
            }
            else if (text.Length == 0)
            {
                result = null;
            }
            else
            {
                result = CreateLocked(key, text, true, audioStart, audioEnd, attribution, changes, providerItemId);
            }
        }
        Raise(changes);
        return result;
    }

    /// <summary>Marks a provisional turn as failed (keeps partial text with a note) or removes it when empty.</summary>
    public void FailTurn(TurnKey key, string note)
    {
        var changes = new List<TranscriptChange>(1);
        lock (_gate)
        {
            if (!_byTurn.TryGetValue(key, out var id) || !_byId.TryGetValue(id, out var existing) || existing.IsFinal)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(existing.Text))
            {
                RemoveLocked(id, changes);
                _byTurn.Remove(key);
            }
            else
            {
                var updated = existing with { IsFinal = true, FinalizedUtc = _time.GetUtcNow(), Note = note, TextVersion = existing.TextVersion + 1 };
                _byId[id] = updated;
                changes.Add(new TranscriptChange(TranscriptChangeKind.Updated, updated));
            }
        }
        Raise(changes);
    }

    /// <summary>Finalizes every provisional segment of a source generation (pause/stop/reconnect). Returns affected count.</summary>
    public int AbandonProvisional(long sessionId, AudioSourceKind? source, int? generation, string note)
    {
        var changes = new List<TranscriptChange>();
        lock (_gate)
        {
            foreach (var (key, id) in _byTurn.ToList())
            {
                if (key.SessionId != sessionId || (source is { } s && key.Source != s) || (generation is { } g && key.Generation != g))
                {
                    continue;
                }
                if (!_byId.TryGetValue(id, out var seg) || seg.IsFinal)
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(seg.Text))
                {
                    RemoveLocked(id, changes);
                    _byTurn.Remove(key);
                }
                else
                {
                    var updated = seg with { IsFinal = true, FinalizedUtc = _time.GetUtcNow(), Note = note, TextVersion = seg.TextVersion + 1 };
                    _byId[id] = updated;
                    changes.Add(new TranscriptChange(TranscriptChangeKind.Updated, updated));
                }
            }
        }
        Raise(changes);
        return changes.Count;
    }

    public TranscriptSegment AddMarker(SegmentKind kind, AudioSourceKind source, long sessionId, int generation, TimeSpan at, string note)
    {
        var changes = new List<TranscriptChange>(2);
        TranscriptSegment seg;
        lock (_gate)
        {
            seg = new TranscriptSegment
            {
                Id = $"gap-{++_nextId}",
                Kind = kind,
                Source = source,
                SessionId = sessionId,
                Generation = generation,
                AudioStart = at,
                AudioEnd = at,
                FirstReceivedUtc = _time.GetUtcNow(),
                IsFinal = true,
                Sequence = ++_sequence,
                Note = note,
                Attribution = new SpeakerAttribution(AttributionStatus.Unavailable, null, [], 0, null),
            };
            AddLocked(seg, changes);
        }
        Raise(changes);
        return seg;
    }

    /// <summary>Atomically updates attribution. The updater returns null to leave it unchanged.</summary>
    public TranscriptSegment? UpdateAttribution(string segmentId, Func<TranscriptSegment, SpeakerAttribution?> updater)
    {
        var changes = new List<TranscriptChange>(1);
        TranscriptSegment? result = null;
        lock (_gate)
        {
            if (_byId.TryGetValue(segmentId, out var seg))
            {
                var next = updater(seg);
                if (next is not null && next != seg.Attribution)
                {
                    result = seg with { Attribution = next with { Version = seg.Attribution.Version + 1 } };
                    _byId[segmentId] = result;
                    changes.Add(new TranscriptChange(TranscriptChangeKind.Updated, result));
                }
            }
        }
        Raise(changes);
        return result;
    }

    public TranscriptSegment? MarkDuplicate(string segmentId, string duplicateOf, string note)
    {
        var changes = new List<TranscriptChange>(1);
        TranscriptSegment? result = null;
        lock (_gate)
        {
            if (_byId.TryGetValue(segmentId, out var seg) && seg.DuplicateOfSegmentId is null)
            {
                result = seg with { DuplicateOfSegmentId = duplicateOf, Note = note };
                _byId[segmentId] = result;
                changes.Add(new TranscriptChange(TranscriptChangeKind.Updated, result));
            }
        }
        Raise(changes);
        return result;
    }

    /// <summary>Clears an echo/duplicate mark when later evidence shows local speech (e.g. double talk).</summary>
    public TranscriptSegment? ClearDuplicate(string segmentId)
    {
        var changes = new List<TranscriptChange>(1);
        TranscriptSegment? result = null;
        lock (_gate)
        {
            if (_byId.TryGetValue(segmentId, out var seg) && seg.DuplicateOfSegmentId is not null)
            {
                result = seg with { DuplicateOfSegmentId = null, Note = null };
                _byId[segmentId] = result;
                changes.Add(new TranscriptChange(TranscriptChangeKind.Updated, result));
            }
        }
        Raise(changes);
        return result;
    }

    public IReadOnlyList<TranscriptSegment> Query(Func<TranscriptSegment, bool> predicate)
    {
        lock (_gate)
        {
            return _byId.Values.Where(predicate).OrderBy(s => s.AudioStart).ThenBy(s => s.Sequence).ToList();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _byId.Clear();
            _byTurn.Clear();
            _order.Clear();
            _trimmedTotal = 0;
        }
        Raise([new TranscriptChange(TranscriptChangeKind.Cleared, null)]);
    }

    private TranscriptSegment CreateLocked(TurnKey key, string text, bool isFinal, TimeSpan audioStart, TimeSpan audioEnd, AudioAttributionDefault attribution, List<TranscriptChange> changes, string? providerItemId = null)
    {
        var now = _time.GetUtcNow();
        var seg = new TranscriptSegment
        {
            Id = $"seg-{++_nextId}",
            Kind = SegmentKind.Speech,
            Source = key.Source,
            SessionId = key.SessionId,
            Generation = key.Generation,
            LocalTurnId = key.TurnId,
            ProviderItemId = providerItemId,
            AudioStart = audioStart,
            AudioEnd = audioEnd,
            FirstReceivedUtc = now,
            FinalizedUtc = isFinal ? now : null,
            Text = text,
            IsFinal = isFinal,
            Sequence = ++_sequence,
            TextVersion = 1,
            Attribution = attribution.Attribution,
        };
        _byTurn[key] = seg.Id;
        AddLocked(seg, changes);
        return seg;
    }

    private void AddLocked(TranscriptSegment seg, List<TranscriptChange> changes)
    {
        _byId[seg.Id] = seg;
        _order.AddLast(seg.Id);
        changes.Add(new TranscriptChange(TranscriptChangeKind.Added, seg));
        while (_byId.Count > _maxSegments && _order.First is { } first)
        {
            _order.RemoveFirst();
            if (_byId.Remove(first.Value, out var removed))
            {
                _trimmedTotal++;
                changes.Add(new TranscriptChange(TranscriptChangeKind.Removed, removed, _trimmedTotal));
                foreach (var kv in _byTurn.Where(kv => kv.Value == first.Value).ToList())
                {
                    _byTurn.Remove(kv.Key);
                }
            }
        }
    }

    private void RemoveLocked(string id, List<TranscriptChange> changes)
    {
        if (_byId.Remove(id, out var removed))
        {
            _order.Remove(id);
            changes.Add(new TranscriptChange(TranscriptChangeKind.Removed, removed));
        }
    }

    private static string Cap(string text) => text.Length <= MaxSegmentChars ? text : text[..MaxSegmentChars];

    private void Raise(List<TranscriptChange> changes)
    {
        var handler = Changed;
        if (handler is null)
        {
            return;
        }
        foreach (var change in changes)
        {
            handler(change);
        }
    }
}

/// <summary>Initial attribution assigned when a segment is created (pending diarization or a source alias).</summary>
public readonly record struct AudioAttributionDefault(SpeakerAttribution Attribution)
{
    public static AudioAttributionDefault Pending => new(SpeakerAttribution.Pending);
}
