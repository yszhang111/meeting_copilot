using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Infrastructure.Logging;

namespace ProactiveAI.Infrastructure.OpenAI;

/// <summary>Shared configuration, authentication and sanitized error mapping for OpenAI requests.</summary>
public sealed class OpenAIConnection
{
    public const string ApiBase = "https://api.openai.com/v1/";

    private readonly ICredentialStore _credentials;

    public OpenAIConnection(ICredentialStore credentials, HttpClient? http = null)
    {
        _credentials = credentials;
        Http = http ?? new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // Connection set-up (TCP + TLS) measured at 1.5-2 s on the user's network: keep idle connections long.
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
        })
        {
            BaseAddress = new Uri(ApiBase),
            Timeout = Timeout.InfiniteTimeSpan,
            // HTTP/2 multiplexes requests on one warm connection where the network path supports it.
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    public HttpClient Http { get; }

    /// <summary>
    /// Opens (or keeps alive) a pooled connection with a cheap metadata request so a following model request skips
    /// connection set-up. Never throws.
    /// </summary>
    public async Task WarmUpAsync(string model, CancellationToken cancellationToken)
    {
        try
        {
            if (!_credentials.HasApiKey())
            {
                return;
            }
            using var req = CreateRequest(HttpMethod.Get, "models/" + Uri.EscapeDataString(model));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await Http.SendAsync(req, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ProviderException)
        {
            // best effort
        }
    }

    /// <summary>Hashed, non-identifying per-install identifier for abuse attribution (no PII).</summary>
    public string? SafetyIdentifier { get; set; }

    public string RequireKey()
    {
        var key = _credentials.ReadApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ProviderException(ProviderFailureKind.Authentication, "missing_api_key", "No OpenAI API key is configured.");
        }
        return key;
    }

    public HttpRequestMessage CreateRequest(HttpMethod method, string relativeUrl)
    {
        var req = new HttpRequestMessage(method, relativeUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RequireKey());
        req.Headers.UserAgent.ParseAdd("MeetingCopilot/0.1");
        return req;
    }

    public static async Task<ProviderException> MapErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // ignore body read failures
        }
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
                               ?? (response.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
        return MapError((int)response.StatusCode, body, retryAfter);
    }

    public static ProviderException MapError(int status, string? body, TimeSpan? retryAfter = null)
    {
        string? code = null, type = null, message = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                {
                    code = GetString(err, "code");
                    type = GetString(err, "type");
                    message = GetString(err, "message");
                }
            }
            catch (JsonException)
            {
            }
        }
        var kind = Classify(status, code, type, message);
        string sanitized = Sanitize(kind, status, code, message);
        return new ProviderException(kind, code ?? type ?? $"http_{status}", sanitized, status, retryAfter);
    }

    public static ProviderFailureKind Classify(int status, string? code, string? type, string? message)
    {
        string c = (code ?? string.Empty).ToLowerInvariant();
        string t = (type ?? string.Empty).ToLowerInvariant();
        string m = (message ?? string.Empty).ToLowerInvariant();
        if (status == 401 || c.Contains("invalid_api_key") || c == "missing_api_key")
        {
            return ProviderFailureKind.Authentication;
        }
        if (c is "insufficient_quota" or "credit_balance_exhausted" || c.EndsWith("_spend_limit_exceeded", StringComparison.Ordinal)
            || c.EndsWith("_usage_limit_exceeded", StringComparison.Ordinal) || t == "insufficient_quota")
        {
            return ProviderFailureKind.QuotaExceeded;
        }
        if (c == "model_not_found" || status == 404 || (status == 400 && m.Contains("model") && (m.Contains("not found") || m.Contains("does not exist") || m.Contains("not supported") || m.Contains("invalid model"))))
        {
            return ProviderFailureKind.InvalidModel;
        }
        if (status == 403)
        {
            return ProviderFailureKind.PermissionDenied;
        }
        if (status == 429)
        {
            return ProviderFailureKind.RateLimited;
        }
        if (status is >= 500 and < 600)
        {
            return ProviderFailureKind.ServerError;
        }
        if (status is >= 400 and < 500)
        {
            return ProviderFailureKind.InvalidRequest;
        }
        return ProviderFailureKind.Unknown;
    }

    public static string Sanitize(ProviderFailureKind kind, int status, string? code, string? message)
    {
        string head = kind switch
        {
            ProviderFailureKind.Authentication => "The API key was rejected. Check or replace it in Settings.",
            ProviderFailureKind.QuotaExceeded => "The OpenAI account has no remaining quota or reached a spend limit.",
            ProviderFailureKind.InvalidModel => "The configured model is not available to this API key.",
            ProviderFailureKind.PermissionDenied => "This API key is not permitted to use the requested resource.",
            ProviderFailureKind.RateLimited => "OpenAI rate limit reached.",
            ProviderFailureKind.ServerError => "OpenAI service error.",
            ProviderFailureKind.InvalidRequest => "The request was rejected.",
            _ => "Request failed.",
        };
        string detail = message is null ? string.Empty : " " + BoundedFileLoggerProvider.Redact(message.Length > 220 ? message[..220] + "…" : message);
        return $"{head} (HTTP {status}{(code is null ? string.Empty : ", " + code)}){detail}";
    }

    public static bool IsTransient(ProviderFailureKind kind) =>
        kind is ProviderFailureKind.Network or ProviderFailureKind.ServerError or ProviderFailureKind.RateLimited or ProviderFailureKind.Unknown;

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
