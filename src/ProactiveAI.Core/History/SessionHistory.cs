using ProactiveAI.Core.Assistant;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Settings;
using ProactiveAI.Core.Speakers;
using ProactiveAI.Core.Transcript;

namespace ProactiveAI.Core.History;

/// <summary>List entry for a saved session (small; listed without loading the full record).</summary>
public sealed record SessionSummary(
    string Id,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    TimeSpan Duration,
    Scenario Scenario,
    string Title,
    int LineCount,
    int AnswerCount);

/// <summary>One transcript line as it was shown (text only; audio is never saved).</summary>
public sealed record HistoryLine(
    string Id,
    AudioSourceKind Source,
    TimeSpan Start,
    TimeSpan End,
    string Speaker,
    bool IsSelf,
    string Text,
    bool Final);

public sealed record HistoryCard(
    string Id,
    CardKind Kind,
    CardState State,
    string? QuestionText,
    string? Title,
    string? DirectPosition,
    string? SpokenAnswer,
    string? Suggestion,
    string? SuggestedQuestion,
    IReadOnlyList<string> SupportingPoints,
    IReadOnlyList<string> AssumptionsAndLimits,
    IReadOnlyList<FollowUp> LikelyFollowups,
    IReadOnlyList<string> MissingFacts,
    IReadOnlyList<string> QuestionSegmentIds,
    DateTimeOffset CreatedUtc,
    string? Model,
    IReadOnlyList<string>? FilledInDetails = null);

public sealed record SessionRecord(int Version, SessionSummary Summary, IReadOnlyList<HistoryLine> Lines, IReadOnlyList<HistoryCard> Cards)
{
    public const int CurrentVersion = 1;
}

/// <summary>
/// Persistent store of finished (or in-progress, autosaved) sessions: transcript text and assistant output only —
/// never audio. Implementations must protect the content at rest and never log it.
/// </summary>
public interface ISessionArchive
{
    /// <summary>Creates or replaces the record with the same id.</summary>
    void Save(SessionRecord record);

    /// <summary>Saved sessions, newest first. Unreadable entries are skipped.</summary>
    IReadOnlyList<SessionSummary> List();

    SessionRecord? Load(string id);

    bool Delete(string id);

    void DeleteAll();
}

/// <summary>Builds a <see cref="SessionRecord"/> from the live session state.</summary>
public static class SessionRecordBuilder
{
    public const int MaxTitleChars = 90;

    public static SessionRecord Build(
        string id,
        DateTimeOffset startedUtc,
        DateTimeOffset? endedUtc,
        TimeSpan duration,
        Scenario scenario,
        IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<AssistantCard> cards,
        SpeakerRegistry speakers)
    {
        var lines = segments
            .Where(IsLine)
            .OrderBy(s => s.AudioStart).ThenBy(s => s.Sequence)
            .Select(s =>
            {
                var (name, self) = SpeakerName(s, speakers);
                return new HistoryLine(s.Id, s.Source, s.AudioStart, s.AudioEnd, name, self, s.Text.Trim(), s.IsFinal);
            })
            .ToList();
        var kept = cards
            .Where(IsKeptCard)
            .OrderBy(c => c.CreatedUtc)
            .Select(c => new HistoryCard(c.Id, c.Kind, c.State, c.QuestionText, c.Title, c.DirectPosition, c.SpokenAnswer, c.Suggestion, c.SuggestedQuestion,
                c.SupportingPoints, c.AssumptionsAndLimits, c.LikelyFollowups, c.MissingFacts, c.QuestionSegmentIds, c.CreatedUtc, c.Model, c.FilledInDetails))
            .ToList();
        var summary = new SessionSummary(id, startedUtc, endedUtc, duration, scenario, Title(scenario, lines, kept), lines.Count,
            kept.Count(c => c.Kind == CardKind.Answer));
        return new SessionRecord(SessionRecord.CurrentVersion, summary, lines, kept);
    }

    /// <summary>True when there is anything worth keeping (a spoken line or an answer/insight with text).</summary>
    public static bool HasContent(IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<AssistantCard> cards) =>
        segments.Any(IsLine) || cards.Any(IsKeptCard);

    private static bool IsLine(TranscriptSegment s) =>
        s.Kind == SegmentKind.Speech && s.DuplicateOfSegmentId is null && !string.IsNullOrWhiteSpace(s.Text);

    private static bool IsKeptCard(AssistantCard c) =>
        c.Kind is CardKind.Answer or CardKind.Clarification or CardKind.Insight
        && (!string.IsNullOrWhiteSpace(c.DirectPosition) || !string.IsNullOrWhiteSpace(c.SpokenAnswer) || !string.IsNullOrWhiteSpace(c.Suggestion));

    /// <summary>First answered question, otherwise the first thing someone else said, otherwise the first line.</summary>
    private static string Title(Scenario scenario, IReadOnlyList<HistoryLine> lines, IReadOnlyList<HistoryCard> cards)
    {
        string? text = cards.FirstOrDefault(c => c.Kind == CardKind.Answer && !string.IsNullOrWhiteSpace(c.QuestionText))?.QuestionText
                       ?? lines.FirstOrDefault(l => !l.IsSelf)?.Text
                       ?? lines.FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return $"{scenario} session";
        }
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= MaxTitleChars ? text : text[..(MaxTitleChars - 1)].TrimEnd() + "…";
    }

    private static (string Name, bool Self) SpeakerName(TranscriptSegment s, SpeakerRegistry speakers)
    {
        var a = s.Attribution;
        var speaker = speakers.Get(a.SpeakerId);
        bool self = a.Status == AttributionStatus.SourceAlias || a.SpeakerId == SpeakerRegistry.SelfAliasId || speaker?.Role == SpeakerRole.Me;
        string name = a.Status switch
        {
            AttributionStatus.Assigned or AttributionStatus.UserAssigned => speaker?.Name ?? "Speaker",
            AttributionStatus.Multiple => $"{speaker?.Name ?? "Speaker"} +",
            AttributionStatus.SourceAlias => speaker?.Name ?? "Me (microphone)",
            _ => s.Source == AudioSourceKind.Microphone ? "Microphone" : "Unknown speaker",
        };
        return (name, self);
    }
}
