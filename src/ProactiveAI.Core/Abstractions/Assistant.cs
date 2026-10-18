namespace ProactiveAI.Core.Abstractions;

/// <summary>A block of reusable prompt context. <see cref="CacheBreakpoint"/> marks the end of a prefix worth caching.</summary>
public sealed record ContextBlock(string Text, bool CacheBreakpoint = true);

/// <param name="Context">
/// Optional reusable context placed between <paramref name="Instructions"/> and <paramref name="Input"/>, most stable first.
/// When present, the instructions and the marked blocks end at explicit prompt-cache breakpoints (where the model supports
/// them) and <paramref name="Input"/> is not written to the cache.
/// </param>
public sealed record AssistantModelRequest(
    string Model,
    string Instructions,
    string Input,
    string SchemaName,
    string SchemaJson,
    int MaxOutputTokens,
    string? ReasoningEffort,
    string? Verbosity,
    string? ServiceTier = null,
    IReadOnlyList<ContextBlock>? Context = null);

public abstract record AssistantStreamUpdate;

public sealed record AssistantTextDelta(string Delta) : AssistantStreamUpdate;

/// <param name="ServiceTier">Processing tier reported by the service (e.g. "default", "priority").</param>
public sealed record AssistantCompleted(string Text, UsageInfo? Usage, string? ServiceTier = null) : AssistantStreamUpdate;

public sealed record AssistantIncomplete(string Reason, string Text, UsageInfo? Usage) : AssistantStreamUpdate;

public sealed record AssistantRefused(string Message) : AssistantStreamUpdate;

/// <summary>Structured-output reasoning model with streaming. Implementations throw <see cref="ProviderException"/> on failure.</summary>
public interface IAssistantModel
{
    IAsyncEnumerable<AssistantStreamUpdate> StreamAsync(AssistantModelRequest request, CancellationToken cancellationToken);

    /// <summary>Best-effort: prepares the network path so the next request starts without connection set-up. Never throws.</summary>
    Task WarmUpAsync(string model, CancellationToken cancellationToken) => Task.CompletedTask;
}

public interface ICredentialStore
{
    bool HasApiKey();

    /// <summary>Returns the key or null. Callers must never log or display it.</summary>
    string? ReadApiKey();

    void WriteApiKey(string apiKey);

    void DeleteApiKey();
}

public interface ITokenCounter
{
    int Count(string text);
}

public sealed record ModelCheckResult(string Model, bool Accessible, ProviderFailureKind Failure, string Detail);

public interface IModelAccessChecker
{
    Task<ModelCheckResult> CheckAsync(string model, CancellationToken cancellationToken);
}
