using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Audio;
using ProactiveAI.Infrastructure.Logging;

namespace ProactiveAI.Infrastructure.OpenAI;

/// <summary>
/// Realtime transcription session over WebSocket (`session.type = "transcription"`). The docs do not
/// publish a literal URL for transcription-only sessions, so candidate endpoints are probed in order
/// and the first that accepts the session configuration is remembered.
/// </summary>
public sealed class RealtimeTranscriptionProvider : ITranscriptionProvider
{
    public static readonly string[] CandidateUrls =
    [
        "wss://api.openai.com/v1/realtime?intent=transcription",
        "wss://api.openai.com/v1/realtime",
    ];

    private static int s_preferredUrl = -1;
    private readonly OpenAIConnection _connection;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;

    public RealtimeTranscriptionProvider(OpenAIConnection connection, ILoggerFactory loggers)
    {
        _connection = connection;
        _loggers = loggers;
        _log = loggers.CreateLogger<RealtimeTranscriptionProvider>();
    }

    public static string? ConfirmedUrl => s_preferredUrl >= 0 ? CandidateUrls[s_preferredUrl] : null;

    public async Task<ITranscriptionStream> ConnectAsync(TranscriptionStreamOptions options, CancellationToken cancellationToken)
    {
        string key = _connection.RequireKey();
        var order = Enumerable.Range(0, CandidateUrls.Length).ToList();
        if (s_preferredUrl >= 0)
        {
            order.Remove(s_preferredUrl);
            order.Insert(0, s_preferredUrl);
        }
        ProviderException? last = null;
        foreach (int i in order)
        {
            try
            {
                var stream = await RealtimeTranscriptionStream.OpenAsync(new Uri(CandidateUrls[i]), key, options, _connection.SafetyIdentifier,
                    _loggers.CreateLogger<RealtimeTranscriptionStream>(), cancellationToken).ConfigureAwait(false);
                if (s_preferredUrl != i)
                {
                    _log.LogInformation("Realtime transcription endpoint accepted: candidate {Index}", i);
                }
                s_preferredUrl = i;
                return stream;
            }
            catch (ProviderException ex) when (ex.Kind is ProviderFailureKind.InvalidRequest or ProviderFailureKind.InvalidModel && ex.HttpStatus is 400 or 404 or null)
            {
                // try the next documented-shape candidate; auth/quota errors stop immediately
                last = ex;
                _log.LogInformation("Realtime endpoint candidate {Index} rejected: {Kind} {Code}", i, ex.Kind, ex.Code);
            }
        }
        throw last ?? new ProviderException(ProviderFailureKind.Unknown, "no_endpoint", "No realtime transcription endpoint accepted the session.");
    }
}

internal sealed class RealtimeTranscriptionStream : ITranscriptionStream
{
    private readonly ClientWebSocket _ws;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Channel<TranscriptionEvent> _events = Channel.CreateUnbounded<TranscriptionEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiver;
    private int _disposed;

    private RealtimeTranscriptionStream(ClientWebSocket ws, ILogger log)
    {
        _ws = ws;
        _log = log;
    }

    public static async Task<RealtimeTranscriptionStream> OpenAsync(Uri url, string apiKey, TranscriptionStreamOptions options, string? safetyId, ILogger log, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
        if (!string.IsNullOrEmpty(safetyId))
        {
            ws.Options.SetRequestHeader("OpenAI-Safety-Identifier", safetyId);
        }
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ws.Options.CollectHttpResponseDetails = true;
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await ws.ConnectAsync(url, connectTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            int status = (int)ws.HttpStatusCode;
            ws.Dispose();
            if (status >= 400)
            {
                throw OpenAIConnection.MapError(status, null);
            }
            throw new ProviderException(ProviderFailureKind.Network, "connect_failed", $"Could not connect to the transcription service ({ex.GetType().Name}).");
        }

        var stream = new RealtimeTranscriptionStream(ws, log);
        try
        {
            await stream.HandshakeAsync(options, ct).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        stream._receiver = Task.Run(() => stream.ReceiveLoopAsync(stream._cts.Token));
        return stream;
    }

    public async ValueTask AppendAsync(ReadOnlyMemory<short> pcm, CancellationToken cancellationToken)
    {
        var bytes = WavEncoder.ToPcmBytes(pcm.Span);
        var payload = $"{{\"type\":\"input_audio_buffer.append\",\"audio\":\"{Convert.ToBase64String(bytes)}\"}}";
        await SendAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask CommitAsync(string clientEventId, CancellationToken cancellationToken) =>
        SendAsync($"{{\"type\":\"input_audio_buffer.commit\",\"event_id\":{JsonSerializer.Serialize(clientEventId)}}}", cancellationToken);

    public ValueTask ClearAsync(CancellationToken cancellationToken) =>
        SendAsync("{\"type\":\"input_audio_buffer.clear\"}", cancellationToken);

    public async IAsyncEnumerable<TranscriptionEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_events.Reader.TryRead(out var ev))
            {
                yield return ev;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        _cts.Cancel();
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", t.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // best effort
        }
        if (_receiver is not null)
        {
            try
            {
                await _receiver.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        _ws.Abort();
        _ws.Dispose();
        _events.Writer.TryComplete();
        _cts.Dispose();
        _sendLock.Dispose();
    }

    private async Task HandshakeAsync(TranscriptionStreamOptions o, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        // first server event: session.created
        var first = await ReceiveJsonAsync(timeout.Token).ConfigureAwait(false);
        if (first is null)
        {
            throw new ProviderException(ProviderFailureKind.Network, "closed", "Transcription connection closed during setup.");
        }
        ThrowIfError(first);

        var transcription = new JsonObject { ["model"] = o.Model };
        if (!string.IsNullOrWhiteSpace(o.Prompt))
        {
            transcription["prompt"] = o.Prompt;
        }
        if (o.Keywords.Count > 0)
        {
            transcription["keywords"] = new JsonArray(o.Keywords.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
        }
        if (o.Languages.Count > 0)
        {
            transcription["languages"] = new JsonArray(o.Languages.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
        }
        if (!string.IsNullOrWhiteSpace(o.Delay))
        {
            transcription["delay"] = o.Delay;
        }
        var update = new JsonObject
        {
            ["type"] = "session.update",
            ["event_id"] = "session-config",
            ["session"] = new JsonObject
            {
                ["type"] = "transcription",
                ["audio"] = new JsonObject
                {
                    ["input"] = new JsonObject
                    {
                        ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = PipelineAudio.SampleRate },
                        ["transcription"] = transcription,
                        ["turn_detection"] = null,
                    },
                },
            },
        };
        await SendAsync(update.ToJsonString(), ct).ConfigureAwait(false);

        while (true)
        {
            var msg = await ReceiveJsonAsync(timeout.Token).ConfigureAwait(false);
            if (msg is null)
            {
                throw new ProviderException(ProviderFailureKind.InvalidRequest, "closed", "Transcription session was closed during configuration.");
            }
            ThrowIfError(msg);
            string? type = msg["type"]?.GetValue<string>();
            if (type is "session.updated" or "transcription_session.updated")
            {
                _log.LogInformation("Transcription session configured model={Model}", o.Model);
                return;
            }
        }
    }

    private static void ThrowIfError(JsonNode msg)
    {
        if (msg["type"]?.GetValue<string>() != "error")
        {
            return;
        }
        var err = msg["error"];
        string? code = err?["code"]?.GetValue<string>();
        string? type = err?["type"]?.GetValue<string>();
        string? message = err?["message"]?.GetValue<string>();
        var kind = OpenAIConnection.Classify(400, code, type, message);
        throw new ProviderException(kind, code ?? type ?? "error", OpenAIConnection.Sanitize(kind, 400, code, message), 400);
    }

    private async ValueTask SendAsync(string json, CancellationToken ct)
    {
        if (_disposed == 1 || _ws.State != WebSocketState.Open)
        {
            throw new ProviderException(ProviderFailureKind.Network, "not_open", "Transcription connection is not open.");
        }
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            throw new ProviderException(ProviderFailureKind.Network, "send_failed", $"Transcription connection lost ({ex.WebSocketErrorCode}).");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<JsonNode?> ReceiveJsonAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var result = await _ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _closeStatus = _ws.CloseStatus;
                    _closeDescription = _ws.CloseStatusDescription;
                    return null;
                }
                ms.Write(buffer, 0, result.Count);
                if (ms.Length > 4 * 1024 * 1024)
                {
                    throw new ProviderException(ProviderFailureKind.InvalidRequest, "message_too_large", "Oversized server message.");
                }
                if (result.EndOfMessage)
                {
                    break;
                }
            }
            return JsonNode.Parse(ms.ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private WebSocketCloseStatus? _closeStatus;
    private string? _closeDescription;

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                JsonNode? msg;
                try
                {
                    msg = await ReceiveJsonAsync(ct).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (msg is null)
                {
                    var kind = _closeStatus switch
                    {
                        WebSocketCloseStatus.PolicyViolation => ProviderFailureKind.Authentication,
                        WebSocketCloseStatus.NormalClosure => ProviderFailureKind.SessionExpired,
                        _ => ProviderFailureKind.Network,
                    };
                    string detail = $"closed ({_closeStatus}) {BoundedFileLoggerProvider.Redact(_closeDescription ?? string.Empty)}".Trim();
                    _events.Writer.TryWrite(new TranscriptionClosed(kind, detail));
                    return;
                }
                var ev = Map(msg);
                if (ev is not null)
                {
                    _events.Writer.TryWrite(ev);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _events.Writer.TryWrite(new TranscriptionClosed(ProviderFailureKind.Network, ex is WebSocketException w ? $"connection lost ({w.WebSocketErrorCode})" : ex.GetType().Name));
        }
        finally
        {
            _events.Writer.TryComplete();
        }
    }

    internal static TranscriptionEvent? Map(JsonNode msg)
    {
        string? type = msg["type"]?.GetValue<string>();
        switch (type)
        {
            case "input_audio_buffer.committed":
                return new TranscriptionCommitted(Str(msg, "item_id") ?? string.Empty, Str(msg, "previous_item_id"));
            case "conversation.item.input_audio_transcription.delta":
                return new TranscriptionDelta(Str(msg, "item_id") ?? string.Empty, Str(msg, "delta") ?? string.Empty);
            case "conversation.item.input_audio_transcription.completed":
                return new TranscriptionCompleted(Str(msg, "item_id") ?? string.Empty, Str(msg, "transcript") ?? string.Empty, ParseUsage(msg["usage"]));
            case "conversation.item.input_audio_transcription.failed":
                return new TranscriptionItemFailed(Str(msg, "item_id") ?? string.Empty, msg["error"]?["code"]?.GetValue<string>() ?? "failed",
                    BoundedFileLoggerProvider.Redact(msg["error"]?["message"]?.GetValue<string>() ?? "Transcription failed"));
            case "error":
                var err = msg["error"];
                string? code = err?["code"]?.GetValue<string>();
                string? etype = err?["type"]?.GetValue<string>();
                string? message = err?["message"]?.GetValue<string>();
                var kind = OpenAIConnection.Classify(400, code, etype, message);
                bool fatal = kind is ProviderFailureKind.Authentication or ProviderFailureKind.QuotaExceeded or ProviderFailureKind.PermissionDenied
                             || code is "session_expired";
                if (code is "session_expired")
                {
                    kind = ProviderFailureKind.SessionExpired;
                }
                return new TranscriptionError(kind, code ?? etype ?? "error", OpenAIConnection.Sanitize(kind, 400, code, message), fatal, err?["event_id"]?.GetValue<string>());
            case "session.created":
            case "session.updated":
                return new TranscriptionReady();
            default:
                return null;
        }
    }

    private static string? Str(JsonNode n, string name) => n[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static UsageInfo? ParseUsage(JsonNode? usage)
    {
        if (usage is null)
        {
            return null;
        }
        string? type = usage["type"]?.GetValue<string>();
        if (type == "duration")
        {
            return new UsageInfo(0, 0, AudioSeconds: usage["seconds"]?.GetValue<double>());
        }
        long input = usage["input_tokens"]?.GetValue<long>() ?? 0;
        long output = usage["output_tokens"]?.GetValue<long>() ?? 0;
        long? audio = usage["input_token_details"]?["audio_tokens"]?.GetValue<long>();
        return new UsageInfo(input, output, audio);
    }
}
