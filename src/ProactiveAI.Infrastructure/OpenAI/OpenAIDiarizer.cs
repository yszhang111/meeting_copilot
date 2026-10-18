using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Audio;

namespace ProactiveAI.Infrastructure.OpenAI;

/// <summary>
/// Speaker diarization through POST /v1/audio/transcriptions with response_format=diarized_json.
/// The window is encoded as WAV in memory; nothing is written to disk. Up to four known-speaker
/// reference clips (2–10 s) are supplied as data URLs to keep labels stable across windows.
/// </summary>
public sealed class OpenAIDiarizer : ISpeakerDiarizer
{
    public const int MaxReferences = 4;
    private readonly OpenAIConnection _connection;
    private readonly ILogger _log;

    public OpenAIDiarizer(OpenAIConnection connection, ILogger<OpenAIDiarizer> log)
    {
        _connection = connection;
        _log = log;
    }

    public async Task<DiarizationResult> DiarizeAsync(DiarizationRequest request, CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        var wav = new ByteArrayContent(WavEncoder.Encode(request.Pcm));
        wav.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(wav, "file", "window.wav");
        content.Add(new StringContent(request.Model), "model");
        content.Add(new StringContent("diarized_json"), "response_format");
        content.Add(new StringContent("auto"), "chunking_strategy");
        foreach (var r in request.References.Take(MaxReferences))
        {
            content.Add(new StringContent(r.Name), "known_speaker_names[]");
            content.Add(new StringContent("data:audio/wav;base64," + Convert.ToBase64String(WavEncoder.Encode(r.Pcm))), "known_speaker_references[]");
        }

        using var req = _connection.CreateRequest(HttpMethod.Post, "audio/transcriptions");
        req.Content = content;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        HttpResponseMessage response;
        try
        {
            response = await _connection.Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException(ProviderFailureKind.Network, "network", $"Speaker-label request failed ({ex.HttpRequestError}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException(ProviderFailureKind.Network, "timeout", "Speaker-label request timed out.");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await OpenAIConnection.MapErrorAsync(response, cancellationToken).ConfigureAwait(false);
            }
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var result = Parse(json);
            if (result.Spans.Count == 0)
            {
                LogEmptyResponse(json, request.Pcm);
            }
            return result;
        }
    }

    private void LogEmptyResponse(string json, short[] pcm)
    {
        // Structure and sizes only; transcript text is never logged.
        int segments = 0, withSpeaker = 0, textLength = 0;
        double duration = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in segs.EnumerateArray())
                {
                    segments++;
                    withSpeaker += s.TryGetProperty("speaker", out var sp) && sp.ValueKind == JsonValueKind.String ? 1 : 0;
                }
            }
            textLength = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()!.Length : 0;
            duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
        }
        catch (JsonException)
        {
        }
        _log.LogWarning("Diarization returned no spans: segments={Segments} withSpeaker={WithSpeaker} textChars={Text} duration={Duration:F1}s windowLevel={Level:F0}dBFS",
            segments, withSpeaker, textLength, duration, Core.Audio.VoiceActivityDetector.LevelDbfs(pcm));
    }

    public static DiarizationResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var spans = new List<DiarizedSpan>();
        if (root.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in segs.EnumerateArray())
            {
                string? speaker = s.TryGetProperty("speaker", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null;
                if (speaker is null || !s.TryGetProperty("start", out var st) || !s.TryGetProperty("end", out var en))
                {
                    continue;
                }
                string text = s.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() ?? string.Empty : string.Empty;
                spans.Add(new DiarizedSpan(speaker, st.GetDouble(), en.GetDouble(), text));
            }
        }
        UsageInfo? usage = null;
        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            usage = RealtimeTranscriptionStream.ParseUsage(System.Text.Json.Nodes.JsonNode.Parse(u.GetRawText()));
        }
        return new DiarizationResult(spans, usage);
    }
}
