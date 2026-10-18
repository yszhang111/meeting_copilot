using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Questions;
using ProactiveAI.Core.Settings;

namespace ProactiveAI.Probe;

/// <summary>
/// Measures when live-transcription text arrives relative to the end of speech: provisional deltas while the
/// trailing silence is still being sent, versus the final transcript after the commit that the client VAD issues
/// after 700 ms of silence. Uses TTS questions sent in real time like the production pipeline. Makes API calls.
/// </summary>
public static class DeltaTimingTest
{
    private static readonly string[] Questions =
    [
        "Thanks for that. So how would you design a rate limiter for a public API that has millions of users?",
        "Okay. Why did you choose PostgreSQL instead of MongoDB for the order service?",
        "Let's move to testing. How did you verify that the retry logic does not create duplicate payments?",
        "Interesting. If traffic grew ten times overnight, what would break first in your system?",
    ];

    public static async Task<int> RunAsync(Options o)
    {
        using var sp = Host.Build();
        var settings = sp.GetRequiredService<ISettingsStore>().Load();
        var provider = sp.GetRequiredService<ITranscriptionProvider>();
        var script = new SpeechScript("q", new() { ["I"] = new ScriptVoice(o.Get("voice") ?? "Zira") }, Questions.Select(q => new ScriptLine("I", q)).ToList());
        var lines = await ScriptPlayer.SynthesizeAsync(script);
        Console.WriteLine($"Delta timing: model={settings.TranscriptionModel} delay={o.Get("delay") ?? settings.TranscriptionDelay}");
        Console.WriteLine("Times in ms relative to the end of speech (last loud sample). VAD commit is sent at +700.");

        int batch = PipelineAudio.Samples(TimeSpan.FromMilliseconds(100));
        foreach (var (line, pcm) in lines)
        {
            await using var stream = await provider.ConnectAsync(
                new TranscriptionStreamOptions(settings.TranscriptionModel, settings.TranscriptionLanguages, null, [], o.Get("delay") ?? settings.TranscriptionDelay), CancellationToken.None);
            int last = pcm.Length - 1;
            while (last > 0 && Math.Abs((int)pcm[last]) < 300)
            {
                last--;
            }
            var speech = pcm.AsSpan(0, last + 1).ToArray();
            var audio = new List<short>();
            audio.AddRange(new short[PipelineAudio.Samples(TimeSpan.FromMilliseconds(300))]); // VAD pre-roll
            audio.AddRange(speech);
            int speechEndSample = audio.Count;
            audio.AddRange(new short[PipelineAudio.Samples(TimeSpan.FromMilliseconds(700))]); // trailing silence until EndTurn

            var clock = Stopwatch.StartNew();
            double speechEndMs = PipelineAudio.Duration(speechEndSample).TotalMilliseconds;
            var deltas = new List<(double At, string Text)>();
            var text = new StringBuilder();
            double? completedAt = null;
            string? final = null;
            var reader = Task.Run(async () =>
            {
                await foreach (var ev in stream.ReadEventsAsync(CancellationToken.None))
                {
                    double at = clock.Elapsed.TotalMilliseconds - speechEndMs;
                    switch (ev)
                    {
                        case TranscriptionDelta d:
                            lock (deltas)
                            {
                                text.Append(d.Delta);
                                deltas.Add((at, text.ToString()));
                            }
                            break;
                        case TranscriptionCompleted c:
                            completedAt = at;
                            final = c.Transcript;
                            return;
                    }
                }
            });

            var arr = audio.ToArray();
            for (int i = 0; i < arr.Length; i += batch)
            {
                var due = PipelineAudio.Duration(i + batch);
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait);
                }
                await stream.AppendAsync(arr.AsMemory(i, Math.Min(batch, arr.Length - i)), CancellationToken.None);
            }
            double commitAt = clock.Elapsed.TotalMilliseconds - speechEndMs;
            await stream.CommitAsync("c1", CancellationToken.None);
            await Task.WhenAny(reader, Task.Delay(8000));

            List<(double At, string Text)> snap;
            lock (deltas)
            {
                snap = deltas.ToList();
            }
            string TextAt(double ms) => snap.LastOrDefault(d => d.At <= ms).Text ?? string.Empty;
            double? questionMarkAt = snap.FirstOrDefault(d => d.Text.TrimEnd().EndsWith('?') || d.Text.TrimEnd().EndsWith('？')).At is var q && snap.Any(d => d.Text.TrimEnd().EndsWith('?') || d.Text.TrimEnd().EndsWith('？')) ? q : null;
            double? confidentAt = snap.FirstOrDefault(d => QuestionHeuristics.AnalyzeTurn(d.Text).IsQuestion).At is var c2 && snap.Any(d => QuestionHeuristics.AnalyzeTurn(d.Text).IsQuestion) ? c2 : null;
            double? completeTextAt = final is null ? null : snap.FirstOrDefault(d => Norm(d.Text) == Norm(final)).At is var f && snap.Any(d => Norm(d.Text) == Norm(final)) ? f : null;
            Console.WriteLine($"\n\"{line.Text}\"");
            Console.WriteLine($"  first delta {snap.FirstOrDefault().At,7:F0}  text ends with '?' {Fmt(questionMarkAt)}  heuristics say question {Fmt(confidentAt)}  provisional == final {Fmt(completeTextAt)}");
            Console.WriteLine($"  commit sent {commitAt,7:F0}  final transcript {Fmt(completedAt)}  deltas={snap.Count}  provisional==final: {(final is not null && snap.Count > 0 && Norm(snap[^1].Text) == Norm(final))}");
            foreach (var ms in new[] { 0, 300, 500, 700 })
            {
                Console.WriteLine($"  text at +{ms,3} ms: …{Tail(TextAt(ms), 60)}");
            }
        }
        return 0;
    }

    private static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string Fmt(double? v) => v is { } x ? $"{x,7:F0}" : "    n/a";

    private static string Tail(string s, int n) => s.Length <= n ? s : s[^n..];
}
