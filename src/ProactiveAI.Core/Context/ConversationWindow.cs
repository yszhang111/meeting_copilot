using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Transcript;

namespace ProactiveAI.Core.Context;

public sealed record ContextSelection(IReadOnlyList<TranscriptSegment> Segments, int Tokens, int DroppedForAge, int DroppedForSize);

/// <summary>Bounded recent-conversation window: expires text by age and by a hard token cap.</summary>
public static class ConversationWindow
{
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(120);
    public const int DefaultMaxTokens = 6000;

    /// <param name="segments">Candidate segments ordered by audio time.</param>
    /// <param name="now">Current session time.</param>
    public static ContextSelection Select(IReadOnlyList<TranscriptSegment> segments, TimeSpan now, TimeSpan maxAge, int maxTokens, ITokenCounter tokens)
    {
        var picked = new List<TranscriptSegment>();
        int used = 0, droppedAge = 0, droppedSize = 0;
        for (int i = segments.Count - 1; i >= 0; i--)
        {
            var s = segments[i];
            if (!s.IsAnalyzable)
            {
                continue;
            }
            if (now - s.AudioEnd > maxAge)
            {
                droppedAge++;
                continue;
            }
            int t = tokens.Count(s.Text) + 16; // per-line label overhead
            if (used + t > maxTokens)
            {
                droppedSize++;
                continue;
            }
            picked.Add(s);
            used += t;
        }
        picked.Reverse();
        return new ContextSelection(picked, used, droppedAge, droppedSize);
    }
}
