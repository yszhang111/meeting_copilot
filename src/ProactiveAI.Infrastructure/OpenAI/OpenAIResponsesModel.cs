using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ProactiveAI.Core.Abstractions;

namespace ProactiveAI.Infrastructure.OpenAI;

/// <summary>Streaming Responses API client with strict JSON-schema structured output.</summary>
public sealed class OpenAIResponsesModel : IAssistantModel
{
    private readonly OpenAIConnection _connection;
    private readonly ILogger _log;

    public OpenAIResponsesModel(OpenAIConnection connection, ILogger<OpenAIResponsesModel> log)
    {
        _connection = connection;
        _log = log;
    }

    public Task WarmUpAsync(string model, CancellationToken cancellationToken) => _connection.WarmUpAsync(model, cancellationToken);

    /// <summary>
    /// GPT-5.6 and later accept explicit prompt-cache breakpoints (<c>prompt_cache_options.mode = explicit</c>); earlier models only
    /// cache implicitly and would reject the options.
    /// </summary>
    public static bool SupportsExplicitPromptCaching(string model)
    {
        var m = System.Text.RegularExpressions.Regex.Match(model, @"^gpt-(\d+)(?:\.(\d+))?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return false;
        }
        int major = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        int minor = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        return major > 5 || (major == 5 && minor >= 6);
    }

    /// <summary>
    /// Request body. Without context: top-level instructions plus one user message (implicit caching). With context: the
    /// instructions as a developer message and each context block as a separate part, each ending at an explicit cache
    /// breakpoint, followed by the request-specific input that is not written to the cache.
    /// </summary>
    public static JsonObject BuildBody(AssistantModelRequest request, bool stream = true)
    {
        static JsonObject Text(string text, bool breakpoint)
        {
            var part = new JsonObject { ["type"] = "input_text", ["text"] = text };
            if (breakpoint)
            {
                part["prompt_cache_breakpoint"] = new JsonObject { ["mode"] = "explicit" };
            }
            return part;
        }

        var body = new JsonObject { ["model"] = request.Model };
        if (request.Context is null)
        {
            body["instructions"] = request.Instructions;
            body["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(Text(request.Input, false)) });
        }
        else
        {
            bool explicitCache = SupportsExplicitPromptCaching(request.Model);
            var input = new JsonArray(new JsonObject { ["role"] = "developer", ["content"] = new JsonArray(Text(request.Instructions, explicitCache)) });
            var parts = request.Context.Where(c => !string.IsNullOrWhiteSpace(c.Text)).Select(c => (JsonNode)Text(c.Text, explicitCache && c.CacheBreakpoint)).ToArray();
            if (parts.Length > 0)
            {
                input.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(parts) });
            }
            input.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(Text(request.Input, false)) });
            body["input"] = input;
            if (explicitCache)
            {
                body["prompt_cache_options"] = new JsonObject { ["mode"] = "explicit" };
            }
        }
        body["text"] = new JsonObject
        {
            ["format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["name"] = request.SchemaName,
                ["schema"] = JsonNode.Parse(request.SchemaJson),
                ["strict"] = true,
            },
        };
        body["max_output_tokens"] = request.MaxOutputTokens;
        body["store"] = false;
        body["stream"] = stream;
        if (!string.IsNullOrWhiteSpace(request.Verbosity))
        {
            body["text"]!["verbosity"] = request.Verbosity;
        }
        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
        {
            body["reasoning"] = new JsonObject { ["effort"] = request.ReasoningEffort };
        }
        if (!string.IsNullOrWhiteSpace(request.ServiceTier))
        {
            body["service_tier"] = request.ServiceTier;
        }
        return body;
    }

    public async IAsyncEnumerable<AssistantStreamUpdate> StreamAsync(AssistantModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = BuildBody(request);
        if (!string.IsNullOrWhiteSpace(_connection.SafetyIdentifier))
        {
            body["safety_identifier"] = _connection.SafetyIdentifier;
        }

        using var req = _connection.CreateRequest(HttpMethod.Post, "responses");
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await _connection.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException(ProviderFailureKind.Network, "network", $"Could not reach the AI service ({ex.HttpRequestError}).");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await OpenAIConnection.MapErrorAsync(response, cancellationToken).ConfigureAwait(false);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = new StringBuilder();
            var data = new StringBuilder();
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    throw new ProviderException(ProviderFailureKind.Network, "stream_interrupted", $"AI response stream interrupted ({ex.GetType().Name}).");
                }
                if (line is null)
                {
                    throw new ProviderException(ProviderFailureKind.Network, "stream_ended", "AI response stream ended before completion.");
                }
                if (line.Length == 0)
                {
                    if (data.Length == 0)
                    {
                        continue;
                    }
                    var payload = data.ToString();
                    data.Clear();
                    if (payload == "[DONE]")
                    {
                        yield break;
                    }
                    var update = Handle(payload, text);
                    if (update is not null)
                    {
                        yield return update;
                        if (update is AssistantCompleted or AssistantIncomplete or AssistantRefused)
                        {
                            yield break;
                        }
                    }
                    continue;
                }
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }
                    data.Append(line.AsSpan(5).TrimStart());
                }
            }
        }
    }

    internal static AssistantStreamUpdate? Handle(string payload, StringBuilder text)
    {
        JsonNode? ev;
        try
        {
            ev = JsonNode.Parse(payload);
        }
        catch (JsonException)
        {
            return null;
        }
        string? type = ev?["type"]?.GetValue<string>();
        switch (type)
        {
            case "response.output_text.delta":
                string delta = ev!["delta"]?.GetValue<string>() ?? string.Empty;
                text.Append(delta);
                return new AssistantTextDelta(delta);
            case "response.refusal.done":
                return new AssistantRefused(ev!["refusal"]?.GetValue<string>() ?? "The model refused this request.");
            case "response.completed":
                return new AssistantCompleted(text.ToString(), ParseUsage(ev!["response"]?["usage"]), ev["response"]?["service_tier"]?.GetValue<string>());
            case "response.incomplete":
                string reason = ev!["response"]?["incomplete_details"]?["reason"]?.GetValue<string>() ?? "incomplete";
                return new AssistantIncomplete(reason, text.ToString(), ParseUsage(ev["response"]?["usage"]));
            case "response.failed":
                var err = ev!["response"]?["error"];
                string? code = err?["code"]?.GetValue<string>();
                string? msg = err?["message"]?.GetValue<string>();
                var kind = code == "rate_limit_exceeded" ? ProviderFailureKind.RateLimited : ProviderFailureKind.ServerError;
                throw new ProviderException(kind, code ?? "failed", OpenAIConnection.Sanitize(kind, 500, code, msg));
            case "error":
                string? ecode = ev!["code"]?.GetValue<string>();
                string? emsg = ev["message"]?.GetValue<string>();
                var ekind = OpenAIConnection.Classify(ecode == "rate_limit_exceeded" ? 429 : 500, ecode, null, emsg);
                throw new ProviderException(ekind, ecode ?? "error", OpenAIConnection.Sanitize(ekind, 500, ecode, emsg));
            default:
                return null;
        }
    }

    private static UsageInfo? ParseUsage(JsonNode? u)
    {
        if (u is null)
        {
            return null;
        }
        return new UsageInfo(
            u["input_tokens"]?.GetValue<long>() ?? 0,
            u["output_tokens"]?.GetValue<long>() ?? 0,
            ReasoningTokens: u["output_tokens_details"]?["reasoning_tokens"]?.GetValue<long>(),
            CachedInputTokens: u["input_tokens_details"]?["cached_tokens"]?.GetValue<long>(),
            CacheWriteTokens: u["input_tokens_details"]?["cache_write_tokens"]?.GetValue<long>());
    }
}

/// <summary>Checks whether a model id is visible to the configured key (GET /v1/models/{id}).</summary>
public sealed class OpenAIModelAccessChecker(OpenAIConnection connection) : IModelAccessChecker
{
    public async Task<ModelCheckResult> CheckAsync(string model, CancellationToken cancellationToken)
    {
        try
        {
            using var req = connection.CreateRequest(HttpMethod.Get, "models/" + Uri.EscapeDataString(model));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await connection.Http.SendAsync(req, timeout.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                return new ModelCheckResult(model, true, ProviderFailureKind.None, "Model is visible to this key (call-time access is confirmed only by a real request).");
            }
            var ex = await OpenAIConnection.MapErrorAsync(resp, cancellationToken).ConfigureAwait(false);
            return new ModelCheckResult(model, false, ex.Kind, ex.Message);
        }
        catch (ProviderException ex)
        {
            return new ModelCheckResult(model, false, ex.Kind, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return new ModelCheckResult(model, false, ProviderFailureKind.Network, $"Network error ({ex.HttpRequestError}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ModelCheckResult(model, false, ProviderFailureKind.Network, "Timed out.");
        }
    }
}
