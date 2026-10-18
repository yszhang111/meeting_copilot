using ProactiveAI.Core.Transcript;

namespace ProactiveAI.Core.Speakers;

/// <summary>Assigns a transcript segment to session speakers from diarized spans by audio-interval overlap.</summary>
public static class AttributionResolver
{
    public const double DominantShare = 0.7;
    public const double SecondaryShare = 0.15;
    public static readonly TimeSpan MinLabeledSpeech = TimeSpan.FromMilliseconds(250);

    public sealed record Resolution(SpeakerAttribution? Attribution, double Coverage);

    /// <param name="coveredUntil">Diarization has processed all audio before this time.</param>
    public static Resolution Resolve(TimeSpan segStart, TimeSpan segEnd, IReadOnlyList<AttributedSpan> spans, TimeSpan coveredUntil)
    {
        var duration = segEnd - segStart;
        if (duration < TimeSpan.FromMilliseconds(200))
        {
            duration = TimeSpan.FromMilliseconds(200);
        }
        var bySpeaker = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        TimeSpan total = TimeSpan.Zero;
        foreach (var span in spans)
        {
            var o = SpeakerReconciler.Overlap(segStart, segEnd, span.Start, span.End);
            if (o > TimeSpan.Zero)
            {
                bySpeaker[span.SpeakerId] = bySpeaker.GetValueOrDefault(span.SpeakerId) + o;
                total += o;
            }
        }
        double coverage = Math.Min(1.0, total.TotalSeconds / duration.TotalSeconds);
        bool fullyProcessed = segEnd <= coveredUntil;

        if (total < MinLabeledSpeech)
        {
            return fullyProcessed
                ? new Resolution(new SpeakerAttribution(AttributionStatus.Unknown, null, [], 0, "No speaker label for this audio"), coverage)
                : new Resolution(null, coverage);
        }
        if (!fullyProcessed && coverage < 0.5)
        {
            return new Resolution(null, coverage); // wait for the next window
        }

        var ordered = bySpeaker.OrderByDescending(kv => kv.Value).ToList();
        double share = ordered[0].Value.TotalSeconds / total.TotalSeconds;
        var others = ordered.Skip(1)
            .Where(kv => kv.Value.TotalSeconds / total.TotalSeconds >= SecondaryShare)
            .Select(kv => kv.Key)
            .ToList();
        var status = share >= DominantShare ? AttributionStatus.Assigned : AttributionStatus.Multiple;
        return new Resolution(new SpeakerAttribution(status, ordered[0].Key, others, 0, null), coverage);
    }
}
