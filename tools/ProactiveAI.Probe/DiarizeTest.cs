using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Settings;
using ProactiveAI.Infrastructure.OpenAI;

namespace ProactiveAI.Probe;

/// <summary>
/// Calls the diarization endpoint with synthetic two-voice TTS audio (no user audio) and prints the raw
/// response structure, then the parsed spans, to verify the response contract.
/// </summary>
public static class DiarizeTest
{
    public static async Task<int> RunAsync(Options o)
    {
        await using var sp = Host.Build();
        var settings = sp.GetRequiredService<ISettingsStore>().Load();
        var connection = sp.GetRequiredService<OpenAIConnection>();
        var script = new SpeechScript("diarize-test",
            new() { ["A"] = new ScriptVoice("Zira"), ["B"] = new ScriptVoice("David") },
            [
                new ScriptLine("A", "Good morning. Can you walk me through the architecture of your last project?"),
                new ScriptLine("B", "Sure. It was an event driven system with three services and a shared message queue."),
                new ScriptLine("A", "Why did you choose a message queue instead of direct calls?"),
                new ScriptLine("B", "Mainly to decouple the services and absorb traffic spikes during peak hours."),
            ]);
        var lines = await ScriptPlayer.SynthesizeAsync(script);
        var pcm = new List<short>();
        var truth = new List<string>();
        foreach (var (line, samples) in lines)
        {
            double start = pcm.Count / (double)PipelineAudio.SampleRate;
            pcm.AddRange(samples);
            truth.Add($"{line.Speaker} {start:F1}-{pcm.Count / (double)PipelineAudio.SampleRate:F1}s");
            pcm.AddRange(new short[PipelineAudio.Samples(TimeSpan.FromMilliseconds(600))]);
        }
        Console.WriteLine($"Synthetic audio {pcm.Count / (double)PipelineAudio.SampleRate:F1}s; truth: {string.Join(" | ", truth)}");

        using var content = new MultipartFormDataContent();
        var wav = new ByteArrayContent(WavEncoder.Encode(pcm.ToArray()));
        wav.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(wav, "file", "window.wav");
        content.Add(new StringContent(settings.DiarizationModel), "model");
        content.Add(new StringContent("diarized_json"), "response_format");
        content.Add(new StringContent("auto"), "chunking_strategy");
        using var req = connection.CreateRequest(HttpMethod.Post, "audio/transcriptions");
        req.Content = content;
        using var resp = await connection.Http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        Console.WriteLine($"HTTP {(int)resp.StatusCode}");
        Console.WriteLine(body.Length > 4000 ? body[..4000] + "…" : body);

        if (resp.IsSuccessStatusCode)
        {
            var parsed = OpenAIDiarizer.Parse(body);
            Console.WriteLine($"\nParsed spans: {parsed.Spans.Count}");
            foreach (var s in parsed.Spans)
            {
                Console.WriteLine($"  {s.RawSpeaker,-8} {s.StartSeconds,6:F2}-{s.EndSeconds,6:F2}  {s.Text}");
            }
        }
        return resp.IsSuccessStatusCode ? 0 : 2;
    }
}
