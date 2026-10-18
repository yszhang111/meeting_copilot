namespace ProactiveAI.Core.Abstractions;

public sealed record TranscriptionStreamOptions(
    string Model,
    IReadOnlyList<string> Languages,
    string? Prompt,
    IReadOnlyList<string> Keywords,
    string? Delay);

/// <summary>A live transcription connection for one source generation.</summary>
public interface ITranscriptionStream : IAsyncDisposable
{
    /// <summary>Appends pipeline PCM (24 kHz mono 16-bit).</summary>
    ValueTask AppendAsync(ReadOnlyMemory<short> pcm, CancellationToken cancellationToken);

    ValueTask CommitAsync(string clientEventId, CancellationToken cancellationToken);

    ValueTask ClearAsync(CancellationToken cancellationToken);

    /// <summary>Provider events in arrival order. Completes when the connection closes.</summary>
    IAsyncEnumerable<TranscriptionEvent> ReadEventsAsync(CancellationToken cancellationToken);
}

public interface ITranscriptionProvider
{
    Task<ITranscriptionStream> ConnectAsync(TranscriptionStreamOptions options, CancellationToken cancellationToken);
}

public abstract record TranscriptionEvent;

public sealed record TranscriptionReady : TranscriptionEvent;

public sealed record TranscriptionCommitted(string ItemId, string? PreviousItemId) : TranscriptionEvent;

public sealed record TranscriptionDelta(string ItemId, string Delta) : TranscriptionEvent;

public sealed record TranscriptionCompleted(string ItemId, string Transcript, UsageInfo? Usage) : TranscriptionEvent;

public sealed record TranscriptionItemFailed(string ItemId, string Code, string SanitizedMessage) : TranscriptionEvent;

/// <summary>A provider error. When <see cref="Fatal"/> the connection is unusable. <see cref="ClientEventId"/> identifies a rejected client command.</summary>
public sealed record TranscriptionError(ProviderFailureKind Kind, string Code, string SanitizedMessage, bool Fatal, string? ClientEventId = null) : TranscriptionEvent;

public sealed record TranscriptionClosed(ProviderFailureKind Kind, string? Detail) : TranscriptionEvent;

public enum ProviderFailureKind
{
    None,
    Network,
    Authentication,
    InvalidModel,
    PermissionDenied,
    RateLimited,
    QuotaExceeded,
    InvalidRequest,
    ServerError,
    SessionExpired,
    Cancelled,
    Unknown,
}

public static class ProviderFailureKindExtensions
{
    /// <summary>Failures that a retry cannot fix without user action.</summary>
    public static bool RequiresUserAction(this ProviderFailureKind kind) => kind is
        ProviderFailureKind.Authentication or ProviderFailureKind.InvalidModel or
        ProviderFailureKind.PermissionDenied or ProviderFailureKind.QuotaExceeded or
        ProviderFailureKind.InvalidRequest;
}

public sealed record UsageInfo(long InputTokens, long OutputTokens, long? AudioInputTokens = null, long? ReasoningTokens = null, long? CachedInputTokens = null, double? AudioSeconds = null, long? CacheWriteTokens = null);

public sealed class ProviderException : Exception
{
    public ProviderException(ProviderFailureKind kind, string code, string sanitizedMessage, int? httpStatus = null, TimeSpan? retryAfter = null)
        : base(sanitizedMessage)
    {
        Kind = kind;
        Code = code;
        HttpStatus = httpStatus;
        RetryAfter = retryAfter;
    }

    public ProviderFailureKind Kind { get; }

    public string Code { get; }

    public int? HttpStatus { get; }

    public TimeSpan? RetryAfter { get; }
}
