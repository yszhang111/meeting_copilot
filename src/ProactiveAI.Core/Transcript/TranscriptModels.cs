using ProactiveAI.Core.Audio;

namespace ProactiveAI.Core.Transcript;

public enum SegmentKind
{
    Speech,
    Gap,
    Notice,
}

public enum AttributionStatus
{
    /// <summary>Waiting for diarization.</summary>
    Pending,
    /// <summary>Diarization assigned one speaker.</summary>
    Assigned,
    /// <summary>Overlapping or mixed speech from several speakers.</summary>
    Multiple,
    /// <summary>Diarization ran but could not attribute this span.</summary>
    Unknown,
    /// <summary>No diarization coverage (skipped window, paused, service failure).</summary>
    Unavailable,
    /// <summary>Explicit source alias, e.g. "Me (microphone)" for a personal microphone. Not verified identity.</summary>
    SourceAlias,
    /// <summary>The user corrected this assignment for the session.</summary>
    UserAssigned,
}

public sealed record SpeakerAttribution(
    AttributionStatus Status,
    string? SpeakerId,
    IReadOnlyList<string> OtherSpeakerIds,
    int Version,
    string? Note)
{
    public static SpeakerAttribution Pending { get; } = new(AttributionStatus.Pending, null, [], 0, null);

    public bool IsResolved => Status is AttributionStatus.Assigned or AttributionStatus.SourceAlias or AttributionStatus.UserAssigned;

    public bool IsUncertain => !IsResolved;
}

/// <summary>Immutable snapshot of one transcript entry. Updates replace the instance in the store.</summary>
public sealed record TranscriptSegment
{
    public required string Id { get; init; }
    public required SegmentKind Kind { get; init; }
    public required AudioSourceKind Source { get; init; }
    public required long SessionId { get; init; }
    public required int Generation { get; init; }
    public long LocalTurnId { get; init; }
    public string? ProviderItemId { get; init; }

    /// <summary>Session-clock interval of the audio that produced this text (from capture timing, not provider timestamps).</summary>
    public TimeSpan AudioStart { get; init; }
    public TimeSpan AudioEnd { get; init; }

    /// <summary>Local receipt timing (when text was received), distinct from audio timing.</summary>
    public DateTimeOffset FirstReceivedUtc { get; init; }
    public DateTimeOffset? FinalizedUtc { get; init; }

    public string Text { get; init; } = string.Empty;
    public bool IsFinal { get; init; }
    public long Sequence { get; init; }
    public int TextVersion { get; init; }
    public SpeakerAttribution Attribution { get; init; } = SpeakerAttribution.Pending;

    /// <summary>When set, this segment was judged a cross-source echo/duplicate of another segment.</summary>
    public string? DuplicateOfSegmentId { get; init; }

    public string? Note { get; init; }

    public bool IsAnalyzable => Kind == SegmentKind.Speech && IsFinal && DuplicateOfSegmentId is null && !string.IsNullOrWhiteSpace(Text);
}

public enum TranscriptChangeKind
{
    Added,
    Updated,
    Removed,
    Cleared,
}

public sealed record TranscriptChange(TranscriptChangeKind Kind, TranscriptSegment? Segment, int TrimmedCount = 0);
