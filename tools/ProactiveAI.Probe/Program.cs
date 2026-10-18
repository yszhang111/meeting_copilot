using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Assistant;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Context;
using ProactiveAI.Core.Diagnostics;
using ProactiveAI.Core.Session;
using ProactiveAI.Core.Settings;
using ProactiveAI.Core.Speakers;
using ProactiveAI.Core.Transcript;
using ProactiveAI.Infrastructure;
using ProactiveAI.Infrastructure.OpenAI;

namespace ProactiveAI.Probe;

/// <summary>
/// Headless verification tool. Exercises the production services against real devices and the
/// configured OpenAI account. It only listens when explicitly run with the session command.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }
        var opts = Options.Parse(args.Skip(1).ToArray());
        try
        {
            return args[0] switch
            {
                "voices" => Voices(),
                "models" => await ModelsAsync(opts),
                "session" => await SessionAsync(opts),
                "eval" => await EvalRunner.RunAsync(opts),
                "diarize-test" => await DiarizeTest.RunAsync(opts),
                "feed" => await FeedTest.RunAsync(opts),
                "aec-eval" => await AecEval.RunAsync(opts),
                "intent-test" => await IntentTest.RunAsync(opts),
                "latency" => await LatencyTest.RunAsync(opts),
                "delta-timing" => await DeltaTimingTest.RunAsync(opts),
                "device-check" => await DeviceCheckTest.RunAsync(opts),
                "history-sample" => await HistorySample.RunAsync(opts),
                "memory-test" => await MemoryTest.RunAsync(opts),
                "followup-test" => await FollowUpTest.RunAsync(opts),
                "resume-check" => await ResumeCheck.RunAsync(opts, args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList()),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed: {ex.GetType().Name}: {ex.Message}");
            return 3;
        }
    }

    private static int Help()
    {
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ProactiveAI.Probe <command> [options]
              voices                         List installed TTS voices
              models                         Check configured model ids and the realtime endpoint (makes API calls)
              session [options]              Run a real listening session (captures audio and uploads it to OpenAI)
                --seconds N                  Listen for N seconds when no script is given (default 30)
                --script NAME|PATH           Play a scripted TTS dialogue through the speakers (ground truth)
                --scenario meeting|interview|defense
                --mic                        Also enable the microphone (explicit)
                --shared-mic                 Diarize the microphone (shared microphone mode)
                --no-system                  Disable system audio capture
                --report PATH                Write a markdown report
                --tail N                     Seconds to keep listening after the script (default 25)
              eval [--filter TEXT] [--report PATH]
                                             Run suggestion-quality fixtures through the configured model
              diarize-test                   Diarize a synthetic two-voice TTS clip (makes API calls)
              feed [--echo] [--talk] [--distort] [--echo-mode builtin|windows|off] [--script NAME] [--report PATH]
                                             Run the production pipeline on synthetic TTS packets (makes API calls)
              aec-eval [--distort] [--script NAME]
                                             Offline echo-canceller evaluation on the feed audio (no API calls)
              intent-test [--model ID] [--effort none|low]
                                             AI question-intent check on labeled sample turns (makes API calls)
              latency [--q simple|hard] [--configs model:effort[:tier],...] [--runs N] [--vary] [--layout legacy] [--show]
                                             Answer latency breakdown with the production prompt (makes API calls)
              delta-timing [--voice NAME]    Live-caption timing vs end of speech for TTS questions (makes API calls)
              device-check [--no-mic]        Pre-session device check on the real devices (plays a short chime; no API calls)
                           [--restart [--cycles N] | --after-session | --race [--cycles N]]
                                             Restart / after-session / immediate start-stop stress of the real captures
              memory-test [--baseline] [--show]
                                             Session question memory through the answer model: follow-up links, facts from
                                             early answers, prompt-cache use (makes API calls; no audio)
              followup-test [--runs N] [--filter TEXT] [--parallel N]
                                             Accuracy of the follow-up link on labeled zh/en cases (makes API calls; no audio)
              resume-check FILE... [--quiet] Extract résumé text from PDF/.docx files like the app (no API calls)
              history-sample [--delete]      Write (or delete) one labeled sample session in the local history (UI checks)
            """);
    }

    private static int Voices()
    {
        foreach (var v in ScriptPlayer.InstalledVoices())
        {
            Console.WriteLine(v);
        }
        return 0;
    }

    private static async Task<int> ModelsAsync(Options o)
    {
        using var sp = Host.Build();
        var settings = sp.GetRequiredService<ISettingsStore>().Load();
        var checker = sp.GetRequiredService<IModelAccessChecker>();
        int failures = 0;
        foreach (var model in new[] { settings.TranscriptionModel, settings.DiarizationModel, settings.AnswerModel, settings.InsightModel, settings.IntentModel }.Distinct())
        {
            var r = await checker.CheckAsync(model, CancellationToken.None);
            Console.WriteLine($"{model,-28} {(r.Accessible ? "visible" : "NOT AVAILABLE")}  {r.Detail}");
            failures += r.Accessible ? 0 : 1;
        }
        var provider = sp.GetRequiredService<ITranscriptionProvider>();
        try
        {
            await using var stream = await provider.ConnectAsync(new TranscriptionStreamOptions(settings.TranscriptionModel, settings.TranscriptionLanguages, null, [], settings.TranscriptionDelay), CancellationToken.None);
            Console.WriteLine($"Realtime transcription session accepted at {RealtimeTranscriptionProvider.ConfirmedUrl}");
        }
        catch (ProviderException ex)
        {
            Console.WriteLine($"Realtime transcription session FAILED: {ex.Kind} {ex.Code} {ex.Message}");
            failures++;
        }
        return failures == 0 ? 0 : 2;
    }

    private static async Task<int> SessionAsync(Options o)
    {
        await using var sp = Host.Build();
        var store = sp.GetRequiredService<ISettingsStore>();
        var settings = store.Load();
        SpeechScript? script = null;
        if (o.Get("script") is { } scriptArg)
        {
            string path = File.Exists(scriptArg) ? scriptArg : Path.Combine(AppContext.BaseDirectory, "Scripts", scriptArg + ".json");
            script = SpeechScript.Load(path);
        }
        string scenario = o.Get("scenario") ?? script?.Scenario ?? settings.Scenario.ToString();
        settings = settings with
        {
            Scenario = Enum.Parse<Scenario>(scenario, ignoreCase: true),
            SystemAudioEnabled = !o.Has("no-system"),
            MicrophoneUsage = o.Has("shared-mic") ? MicrophoneUsage.Shared : MicrophoneUsage.Personal,
            SaveSessionHistory = o.Has("save-history"),
        };
        var coordinator = sp.GetRequiredService<SessionCoordinator>();
        coordinator.UpdateSettings(settings);
        if (script?.Preparation is { } prep)
        {
            foreach (var part in prep.Split("\n##", StringSplitOptions.RemoveEmptyEntries))
            {
                var lines = part.Trim().TrimStart('#').Trim().Split('\n', 2);
                if (lines.Length == 2 && Enum.TryParse<PrepField>(lines[0].Trim(), true, out var field))
                {
                    coordinator.Preparation.Set(field, lines[1].Trim());
                }
            }
        }
        if (o.Has("mic"))
        {
            await coordinator.SetMicrophoneEnabledAsync(true);
        }

        var log = new StringBuilder();
        void Out(string line)
        {
            Console.WriteLine(line);
            log.AppendLine(line);
        }
        string Label(TranscriptSegment s)
        {
            var sp2 = coordinator.Speakers.Get(s.Attribution.SpeakerId);
            return s.Attribution.Status switch
            {
                AttributionStatus.Pending => "…pending",
                AttributionStatus.Assigned or AttributionStatus.UserAssigned or AttributionStatus.SourceAlias => sp2?.Name ?? s.Attribution.SpeakerId ?? "?",
                AttributionStatus.Multiple => $"multiple({sp2?.Name})",
                _ => s.Attribution.Status.ToString().ToLowerInvariant(),
            };
        }
        coordinator.Transcript.Changed += c =>
        {
            if (c.Segment is not { } s)
            {
                return;
            }
            if (s.Kind != SegmentKind.Speech && c.Kind == TranscriptChangeKind.Added)
            {
                Out($"  ── {s.Note}");
            }
            else if (s.Kind == SegmentKind.Speech && s.IsFinal && c.Kind == TranscriptChangeKind.Updated)
            {
                Out($"  [{s.AudioStart:mm\\:ss}] {s.Source,-10} {Label(s),-14} v{s.Attribution.Version} {(s.DuplicateOfSegmentId is null ? string.Empty : "(echo) ")}{s.Text}");
            }
        };
        var reportedCards = new HashSet<string>();
        coordinator.Board.Changed += c =>
        {
            if (c.Card is not { } card || !card.IsTerminal || !reportedCards.Add(card.Id + card.Revision + card.State))
            {
                return;
            }
            Out($"\n  ==== {card.Kind} {card.State} {card.QuestionId} r{card.Revision} firstText={card.FirstUsefulTextLatency?.TotalSeconds:F1}s complete={card.CompleteLatency?.TotalSeconds:F1}s {card.StatusDetail}");
            if (card.QuestionText is not null)
            {
                Out($"  Q: {card.QuestionText}");
            }
            if (card.Title is not null)
            {
                Out($"  Title: {card.Title}");
            }
            if (card.DirectPosition is not null)
            {
                Out($"  Direct: {card.DirectPosition}");
            }
            if (card.SpokenAnswer is not null)
            {
                Out($"  Spoken ({card.SpokenAnswer.Length} chars): {card.SpokenAnswer}");
            }
            if (card.Suggestion is not null)
            {
                Out($"  Suggestion: {card.Suggestion}");
            }
            foreach (var p in card.SupportingPoints)
            {
                Out($"   • {p}");
            }
            foreach (var p in card.AssumptionsAndLimits)
            {
                Out($"   ◦ {p}");
            }
            foreach (var f in card.LikelyFollowups)
            {
                Out($"   ? {f.Question} → {f.ResponseDirection}");
            }
            foreach (var m in card.MissingFacts)
            {
                Out($"   ! missing: {m}");
            }
            foreach (var m in card.FilledInDetails)
            {
                Out($"   ~ AI filled in: {m}");
            }
            Out($"  evidence={string.Join(",", card.EvidenceSegmentIds)} prep={string.Join(",", card.PreparationEvidenceIds)}\n");
        };
        SessionState lastState = SessionState.Stopped;
        coordinator.StatusChanged += snap =>
        {
            if (snap.State != lastState)
            {
                lastState = snap.State;
                Out($"  >> state {snap.State} {snap.Message}");
            }
        };

        Out($"Starting session scenario={settings.Scenario} system={settings.SystemAudioEnabled} mic={o.Has("mic")} script={script?.Name ?? "-"}");
        await coordinator.StartAsync();
        if (coordinator.State != SessionState.Listening)
        {
            Out($"Session did not start: {coordinator.Snapshot.Message}");
            return 2;
        }
        var snap0 = coordinator.Snapshot;
        Out($"Listening. system={snap0.System.DeviceName} mic={snap0.Microphone.DeviceName} echo={snap0.Microphone.EchoControl} {snap0.Microphone.EchoDetail}");

        List<GroundTruthTurn>? truth = null;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        try
        {
            if (script is not null)
            {
                var lines = await ScriptPlayer.SynthesizeAsync(script);
                Out($"Synthesized {lines.Count} lines ({lines.Sum(l => l.Pcm.Length) / (double)PipelineAudio.SampleRate:F0}s of speech)");
                await Task.Delay(1500, cts.Token);
                truth = await ScriptPlayer.PlayAsync(lines, coordinator.Clock, l => Out("  ▶ " + l), cts.Token);
                await Task.Delay(TimeSpan.FromSeconds(o.GetInt("tail", 25)), cts.Token);
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(o.GetInt("seconds", 30)), cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Out("Interrupted by user");
        }
        var final = coordinator.Snapshot;
        await coordinator.StopAsync();

        Out("\n# Summary");
        var segs = coordinator.Transcript.Snapshot();
        Out($"Final speech segments: {segs.Count(s => s.Kind == SegmentKind.Speech && s.IsFinal)}; gaps: {segs.Count(s => s.Kind == SegmentKind.Gap)}; echo-suppressed: {segs.Count(s => s.DuplicateOfSegmentId is not null)}");
        Out($"Speakers: {string.Join(", ", coordinator.Speakers.All.Select(s => $"{s.Name}{(s.IsProvisional ? "?" : string.Empty)}"))}");
        foreach (var src in new[] { final.System, final.Microphone })
        {
            if (src.Metrics is { } m)
            {
                Out($"{src.Kind}: sent {m.SentMs / 1000.0:F1}s, dropped {m.DroppedMs}ms, overflows {m.Overflows}, turns {m.Turns}, drift {m.DriftPpm?.ToString("F0") ?? "n/a"} ppm; diarization {src.Diarization?.Requests} req / {src.Diarization?.Failures} fail, covered {src.Diarization?.Covered.TotalSeconds:F0}s, skipped {src.Diarization?.Skipped.TotalSeconds:F0}s");
            }
        }
        foreach (var l in coordinator.LatencyReport())
        {
            Out($"Latency {l.Metric}: n={l.Samples} median={l.MedianSeconds?.ToString("F2") ?? "n/a"}s p90={l.P90Seconds?.ToString("F2") ?? "n/a"}s");
        }
        var a = coordinator.Assistant.Status;
        Out($"Reasoning requests={a.TotalRequests} inputTokens={a.InputTokens} outputTokens={a.OutputTokens}");
        if (truth is not null)
        {
            var score = AttributionScorer.Score(truth, segs);
            Out($"Attribution vs ground truth: accuracy={score.Accuracy:P1} (unknown counted as miss) coverage={score.Coverage:P1} evaluable={score.Evaluable.TotalSeconds:F0}s correct={score.Correct.TotalSeconds:F0}s wrong={score.Wrong.TotalSeconds:F0}s unknown={score.Unknown.TotalSeconds:F0}s identitySwitches={score.IdentitySwitches}");
            Out($"Label mapping: {string.Join(", ", score.Mapping.Select(kv => $"{coordinator.Speakers.Get(kv.Key)?.Name ?? kv.Key}→{kv.Value}"))}");
        }
        if (o.Get("report") is { } reportPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, log.ToString());
            Console.WriteLine($"Report written to {reportPath}");
        }
        return 0;
    }
}

internal static class Host
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddProactiveAI();
        return services.BuildServiceProvider();
    }
}

public sealed class Options
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }
            string key = args[i][2..];
            string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
            o._values[key] = value;
        }
        return o;
    }

    public bool Has(string key) => _values.ContainsKey(key);

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public int GetInt(string key, int fallback) => int.TryParse(Get(key), out int v) ? v : fallback;
}
