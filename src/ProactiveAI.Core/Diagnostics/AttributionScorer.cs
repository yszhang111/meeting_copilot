using ProactiveAI.Core.Speakers;
using ProactiveAI.Core.Transcript;

namespace ProactiveAI.Core.Diagnostics;

/// <summary>A ground-truth utterance from a consented or synthetic multi-speaker fixture.</summary>
public sealed record GroundTruthTurn(string Speaker, TimeSpan Start, TimeSpan End, string Text);

public sealed record AttributionScore(
    TimeSpan Evaluable,
    TimeSpan Correct,
    TimeSpan Wrong,
    TimeSpan Unknown,
    double Accuracy,
    double Coverage,
    int IdentitySwitches,
    IReadOnlyDictionary<string, string> Mapping);

/// <summary>
/// Scores speaker attribution against ground truth by audio-interval overlap. Session labels are mapped
/// one-to-one onto true speakers by largest co-occurring duration. Unknown, pending and multiple-speaker
/// labels count as misses; coverage is reported separately.
/// </summary>
public static class AttributionScorer
{
    public static AttributionScore Score(IReadOnlyList<GroundTruthTurn> truth, IReadOnlyList<TranscriptSegment> segments)
    {
        var speech = segments.Where(s => s.Kind == SegmentKind.Speech && s.DuplicateOfSegmentId is null).ToList();
        var cooccur = new Dictionary<(string Label, string Truth), double>();
        var perTurn = new List<(GroundTruthTurn Turn, List<(string? Label, TimeSpan Overlap)> Hits)>();
        foreach (var turn in truth)
        {
            var hits = new List<(string?, TimeSpan)>();
            foreach (var seg in speech)
            {
                var o = SpeakerReconciler.Overlap(turn.Start, turn.End, seg.AudioStart, seg.AudioEnd);
                if (o <= TimeSpan.Zero)
                {
                    continue;
                }
                string? label = seg.Attribution.Status is AttributionStatus.Assigned or AttributionStatus.UserAssigned or AttributionStatus.SourceAlias
                    ? seg.Attribution.SpeakerId
                    : null;
                hits.Add((label, o));
                if (label is not null)
                {
                    cooccur[(label, turn.Speaker)] = cooccur.GetValueOrDefault((label, turn.Speaker)) + o.TotalSeconds;
                }
            }
            perTurn.Add((turn, hits));
        }

        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedTruth = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ((label, truthSpeaker), _) in cooccur.OrderByDescending(kv => kv.Value))
        {
            if (mapping.ContainsKey(label) || usedTruth.Contains(truthSpeaker))
            {
                continue;
            }
            mapping[label] = truthSpeaker;
            usedTruth.Add(truthSpeaker);
        }

        TimeSpan evaluable = TimeSpan.Zero, correct = TimeSpan.Zero, wrong = TimeSpan.Zero, unknown = TimeSpan.Zero;
        var lastLabelBySpeaker = new Dictionary<string, string>(StringComparer.Ordinal);
        int switches = 0;
        foreach (var (turn, hits) in perTurn)
        {
            var duration = turn.End - turn.Start;
            evaluable += duration;
            TimeSpan c = TimeSpan.Zero, w = TimeSpan.Zero;
            foreach (var (label, o) in hits)
            {
                if (label is null)
                {
                    continue;
                }
                if (mapping.TryGetValue(label, out var mapped) && mapped == turn.Speaker)
                {
                    c += o;
                }
                else
                {
                    w += o;
                }
            }
            // segment intervals include padding; never credit more than the turn itself
            if (c + w > duration)
            {
                double scale = duration.TotalSeconds / (c + w).TotalSeconds;
                c = TimeSpan.FromSeconds(c.TotalSeconds * scale);
                w = TimeSpan.FromSeconds(w.TotalSeconds * scale);
            }
            correct += c;
            wrong += w;
            unknown += duration - c - w;

            var dominant = hits.Where(h => h.Label is not null).GroupBy(h => h.Label!)
                .Select(g => (Label: g.Key, D: g.Sum(x => x.Overlap.TotalSeconds))).OrderByDescending(x => x.D).FirstOrDefault();
            if (dominant.Label is not null)
            {
                if (lastLabelBySpeaker.TryGetValue(turn.Speaker, out var prev) && prev != dominant.Label)
                {
                    switches++;
                }
                lastLabelBySpeaker[turn.Speaker] = dominant.Label;
            }
        }
        double accuracy = evaluable > TimeSpan.Zero ? correct / evaluable : 0;
        double coverage = evaluable > TimeSpan.Zero ? (correct + wrong) / evaluable : 0;
        return new AttributionScore(evaluable, correct, wrong, unknown, accuracy, coverage, switches, mapping);
    }
}
