using ProactiveAI.Core.Audio;

namespace ProactiveAI.Core.Speakers;

/// <summary>
/// Temporary in-memory voice reference clips (2–10 s) used only to keep speaker labels stable within
/// a session. Bounded by count and bytes; cleared on stop, reset and exit. Never persisted.
/// </summary>
public sealed class ReferenceClipStore
{
    public static readonly TimeSpan MinClip = TimeSpan.FromSeconds(2.5);
    public static readonly TimeSpan MaxClip = TimeSpan.FromSeconds(8);

    private readonly object _gate = new();
    private readonly Dictionary<string, Clip> _clips = new(StringComparer.Ordinal);
    private readonly int _maxClips;
    private readonly long _maxBytes;

    public ReferenceClipStore(int maxClips = 8, long maxBytes = 8 * 8 * PipelineAudio.BytesPerSecond)
    {
        _maxClips = maxClips;
        _maxBytes = maxBytes;
    }

    public int Count
    {
        get { lock (_gate) { return _clips.Count; } }
    }

    public long Bytes
    {
        get { lock (_gate) { return _clips.Values.Sum(c => c.Pcm.Length * 2L); } }
    }

    public bool Has(string speakerId)
    {
        lock (_gate)
        {
            return _clips.ContainsKey(speakerId);
        }
    }

    /// <summary>Stores or improves a clip. Longer clean clips replace shorter ones.</summary>
    public bool Offer(string speakerId, short[] pcm, TimeSpan observedAt)
    {
        var duration = PipelineAudio.Duration(pcm.Length);
        if (duration < MinClip)
        {
            return false;
        }
        if (duration > MaxClip)
        {
            pcm = pcm[..PipelineAudio.Samples(MaxClip)];
        }
        lock (_gate)
        {
            if (_clips.TryGetValue(speakerId, out var existing) && existing.Pcm.Length >= pcm.Length)
            {
                _clips[speakerId] = existing with { LastActive = observedAt };
                return false;
            }
            _clips[speakerId] = new Clip(pcm, observedAt);
            while (_clips.Count > _maxClips || _clips.Values.Sum(c => c.Pcm.Length * 2L) > _maxBytes)
            {
                var oldest = _clips.MinBy(kv => kv.Value.LastActive);
                _clips.Remove(oldest.Key);
            }
            return true;
        }
    }

    public void Touch(string speakerId, TimeSpan at)
    {
        lock (_gate)
        {
            if (_clips.TryGetValue(speakerId, out var c) && at > c.LastActive)
            {
                _clips[speakerId] = c with { LastActive = at };
            }
        }
    }

    /// <summary>Most recently active clips among <paramref name="candidateSpeakerIds"/>, up to <paramref name="max"/>.</summary>
    public IReadOnlyList<(string SpeakerId, short[] Pcm)> Select(IEnumerable<string> candidateSpeakerIds, int max)
    {
        var set = candidateSpeakerIds.ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            return _clips.Where(kv => set.Contains(kv.Key))
                .OrderByDescending(kv => kv.Value.LastActive)
                .Take(max)
                .Select(kv => (kv.Key, kv.Value.Pcm))
                .ToList();
        }
    }

    public void Remove(string speakerId)
    {
        lock (_gate)
        {
            _clips.Remove(speakerId);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _clips.Clear();
        }
    }

    private sealed record Clip(short[] Pcm, TimeSpan LastActive);
}
