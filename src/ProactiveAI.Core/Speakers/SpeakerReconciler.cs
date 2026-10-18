namespace ProactiveAI.Core.Speakers;

/// <summary>A diarized span on the session timeline with a request-local raw label.</summary>
public sealed record WindowSpan(string RawLabel, TimeSpan Start, TimeSpan End, string Text)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>A span already attributed to a session speaker (from earlier windows).</summary>
public sealed record AttributedSpan(string SpeakerId, TimeSpan Start, TimeSpan End);

public enum MappingEvidence
{
    /// <summary>The provider matched a known-speaker reference clip we supplied.</summary>
    ReferenceClip,
    /// <summary>Agreement with an earlier window over the overlapping audio.</summary>
    WindowOverlap,
    /// <summary>No supporting evidence: a new session speaker.</summary>
    New,
}

public sealed record LabelMapping(string RawLabel, string? SpeakerId, MappingEvidence Evidence, double Confidence);

/// <summary>
/// Maps request-local diarization labels onto stable session speakers. Provider labels from separate
/// requests are never assumed to be the same person: continuity comes only from supplied reference
/// clips (acoustic evidence) or agreement over overlapping audio. Without evidence a new label is used.
/// </summary>
public static class SpeakerReconciler
{
    public static readonly TimeSpan MinOverlapEvidence = TimeSpan.FromSeconds(0.8);
    public const double MinOverlapAgreement = 0.6;

    public static IReadOnlyList<LabelMapping> Reconcile(
        IReadOnlyList<WindowSpan> spans,
        IReadOnlyDictionary<string, string> referenceNameToSpeaker,
        IReadOnlyList<AttributedSpan> previous)
    {
        var labels = spans.Select(s => s.RawLabel).Distinct(StringComparer.Ordinal).ToList();
        var results = new Dictionary<string, LabelMapping>(StringComparer.Ordinal);
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        // 1. Reference-clip matches (provider returned the known speaker name we supplied).
        foreach (var label in labels)
        {
            if (referenceNameToSpeaker.TryGetValue(label, out var speakerId) && claimed.Add(speakerId))
            {
                results[label] = new LabelMapping(label, speakerId, MappingEvidence.ReferenceClip, 0.9);
            }
        }

        // 2. Overlap agreement with previously attributed audio, strongest evidence first.
        var candidates = new List<(string Label, string SpeakerId, TimeSpan Agree, double Ratio)>();
        foreach (var label in labels.Where(l => !results.ContainsKey(l)))
        {
            var bySpeaker = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
            TimeSpan total = TimeSpan.Zero;
            foreach (var span in spans.Where(s => s.RawLabel == label))
            {
                foreach (var prev in previous)
                {
                    var o = Overlap(span.Start, span.End, prev.Start, prev.End);
                    if (o > TimeSpan.Zero)
                    {
                        bySpeaker[prev.SpeakerId] = bySpeaker.GetValueOrDefault(prev.SpeakerId) + o;
                        total += o;
                    }
                }
            }
            if (bySpeaker.Count == 0)
            {
                continue;
            }
            var best = bySpeaker.MaxBy(kv => kv.Value);
            double ratio = best.Value.TotalSeconds / Math.Max(1e-6, total.TotalSeconds);
            if (best.Value >= MinOverlapEvidence && ratio >= MinOverlapAgreement)
            {
                candidates.Add((label, best.Key, best.Value, ratio));
            }
        }
        foreach (var c in candidates.OrderByDescending(c => c.Agree))
        {
            if (results.ContainsKey(c.Label) || !claimed.Add(c.SpeakerId))
            {
                continue;
            }
            results[c.Label] = new LabelMapping(c.Label, c.SpeakerId, MappingEvidence.WindowOverlap, Math.Min(0.85, c.Ratio));
        }

        // 3. Everything else is a new session speaker (created by the caller).
        foreach (var label in labels.Where(l => !results.ContainsKey(l)))
        {
            results[label] = new LabelMapping(label, null, MappingEvidence.New, 0.5);
        }
        return labels.Select(l => results[l]).ToList();
    }

    public static TimeSpan Overlap(TimeSpan aStart, TimeSpan aEnd, TimeSpan bStart, TimeSpan bEnd)
    {
        var start = aStart > bStart ? aStart : bStart;
        var end = aEnd < bEnd ? aEnd : bEnd;
        return end > start ? end - start : TimeSpan.Zero;
    }
}
