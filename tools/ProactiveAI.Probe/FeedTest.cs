using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Assistant;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Diagnostics;
using ProactiveAI.Core.Session;
using ProactiveAI.Core.Settings;
using ProactiveAI.Core.Transcript;
using ProactiveAI.Infrastructure;

namespace ProactiveAI.Probe;

/// <summary>Capture source fed by the probe instead of a device (synthetic audio, no speakers or microphone).</summary>
internal sealed class FeedCaptureSource(CaptureRequest request) : ICaptureSource
{
    public AudioSourceKind Kind => request.Kind;

    public event Action<AudioChunk>? ChunkCaptured;

    public event Action<CaptureFault>? Faulted { add { } remove { } }

    public CaptureStartInfo Start() => new(request.DeviceId, "Synthetic feed", "24kHz mono", EchoControlStatus.NotApplicable, null);

    public void Push(short[] samples, long qpc100ns) => ChunkCaptured?.Invoke(new AudioChunk(samples, qpc100ns, false));

    public Task StopAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FeedCaptureFactory : ICaptureSourceFactory
{
    public Dictionary<AudioSourceKind, FeedCaptureSource> Latest { get; } = new();

    public ICaptureSource Create(CaptureRequest request)
    {
        var s = new FeedCaptureSource(request);
        lock (Latest)
        {
            Latest[request.Kind] = s;
        }
        return s;
    }
}

/// <summary>
/// Runs the production session pipeline (real Realtime transcription, diarization and reasoning) on synthetic
/// TTS audio delivered as real-time 10 ms packets. Optional loudspeaker-echo simulation on the microphone path.
/// No device audio is captured and nothing is played.
/// </summary>
public static class FeedTest
{
    public static async Task<int> RunAsync(Options o)
    {
        var factory = new FeedCaptureFactory();
        var services = new ServiceCollection();
        services.AddProactiveAI();
        services.AddSingleton<ICaptureSourceFactory>(factory);
        await using var sp = services.BuildServiceProvider();

        string scriptArg = o.Get("script") ?? "feed";
        var script = SpeechScript.Load(File.Exists(scriptArg) ? scriptArg : Path.Combine(AppContext.BaseDirectory, "Scripts", scriptArg + ".json"));
        bool echo = o.Has("echo") || o.Has("talk");
        bool talk = o.Has("talk");
        var echoMode = Enum.Parse<EchoCancellationMode>(o.Get("echo-mode") ?? "BuiltIn", true);
        var settings = sp.GetRequiredService<ISettingsStore>().Load() with
        {
            Scenario = Enum.Parse<Scenario>(script.Scenario ?? "Meeting", true),
            SystemAudioEnabled = true,
            MicrophoneUsage = MicrophoneUsage.Personal,
            EchoCancellation = echoMode,
            SaveSessionHistory = o.Has("save-history"), // synthetic runs stay out of the user's history
        };
        var session = sp.GetRequiredService<SessionCoordinator>();
        session.UpdateSettings(settings);
        if (echo)
        {
            await session.SetMicrophoneEnabledAsync(true);
        }

        var (sysTrack, placements) = await BuildSystemTrackAsync(script);
        short[]? micTrack = echo ? SimulatedEcho(sysTrack, o.Has("distort")) : null;

        // The user talks over the playback: their own speech is mixed into the microphone with the echo.
        var talkPlacements = talk && micTrack is not null
            ? (await AddMySpeechAsync(script, micTrack)).Placements
            : new List<(string Text, double Start, double End)>();

        var log = new StringBuilder();
        void Out(string s)
        {
            Console.WriteLine(s);
            log.AppendLine(s);
        }
        Out($"Feed test: script={script.Name} audio={sysTrack.Length / (double)PipelineAudio.SampleRate:F0}s echoOnMic={echo} talk={talk} echoMode={echoMode} scenario={settings.Scenario}");
        await session.StartAsync();
        if (session.State != SessionState.Listening)
        {
            Out("Session did not start: " + session.Snapshot.Message);
            return 2;
        }

        // Real-time delivery with media timestamps on the session clock.
        long origin = session.Clock.Now100ns() + TimeSpan.FromMilliseconds(200).Ticks;
        var sysSrc = factory.Latest[AudioSourceKind.System];
        factory.Latest.TryGetValue(AudioSourceKind.Microphone, out var micSrc);
        int packet = PipelineAudio.Samples(TimeSpan.FromMilliseconds(10));
        var clock = Stopwatch.StartNew();
        for (int i = 0, k = 0; i < sysTrack.Length; i += packet, k++)
        {
            var due = TimeSpan.FromMilliseconds(200 + k * 10);
            var wait = due - clock.Elapsed;
            if (wait > TimeSpan.FromMilliseconds(2))
            {
                await Task.Delay(wait);
            }
            long qpc = origin + PipelineAudio.Duration(i).Ticks;
            int n = Math.Min(packet, sysTrack.Length - i);
            sysSrc.Push(sysTrack.AsSpan(i, n).ToArray(), qpc);
            if (micTrack is not null && micSrc is not null)
            {
                micSrc.Push(micTrack.AsSpan(i, n).ToArray(), qpc);
            }
        }
        Out("Playback finished; waiting for transcription and speaker labels…");
        await Task.Delay(TimeSpan.FromSeconds(o.GetInt("tail", 25)));
        var snap = session.Snapshot;
        await session.StopAsync();

        var segs = session.Transcript.Snapshot();
        Out("\n# Transcript");
        foreach (var s in segs)
        {
            if (s.Kind != SegmentKind.Speech)
            {
                Out($"  ── {s.Note}");
                continue;
            }
            var spk = session.Speakers.Get(s.Attribution.SpeakerId);
            string label = s.Attribution.Status switch
            {
                AttributionStatus.Assigned or AttributionStatus.SourceAlias or AttributionStatus.UserAssigned => spk?.Name ?? "?",
                AttributionStatus.Multiple => $"multiple({spk?.Name})",
                _ => s.Attribution.Status.ToString().ToLowerInvariant(),
            };
            Out($"  [{s.AudioStart:mm\\:ss\\.f}-{s.AudioEnd:mm\\:ss\\.f}] {s.Source,-10} {label,-14} {(s.DuplicateOfSegmentId is null ? string.Empty : "[ECHO HIDDEN] ")}{s.Text}");
        }

        var origin0 = session.Clock.FromQpc100ns(origin);
        var truth = placements.Select(p => new GroundTruthTurn(p.Speaker, origin0 + TimeSpan.FromSeconds(p.Start), origin0 + TimeSpan.FromSeconds(p.End), string.Empty)).ToList();
        var score = AttributionScorer.Score(truth, segs.Where(s => s.Source == AudioSourceKind.System).ToList());
        Out("\n# Summary");
        Out($"System speakers found: {string.Join(", ", session.Speakers.All.Where(s => !s.IsSourceAlias).Select(s => s.Name + (s.IsProvisional ? "?" : string.Empty)))}");
        Out($"Attribution vs ground truth: accuracy={score.Accuracy:P1} (unknown counted as miss) coverage={score.Coverage:P1} evaluable={score.Evaluable.TotalSeconds:F0}s unknown={score.Unknown.TotalSeconds:F0}s wrong={score.Wrong.TotalSeconds:F0}s identitySwitches={score.IdentitySwitches}");
        Out($"Label mapping: {string.Join(", ", score.Mapping.Select(kv => $"{session.Speakers.Get(kv.Key)?.Name ?? kv.Key}→{kv.Value}"))}");
        var d = snap.System.Diarization;
        Out($"Diarization: requests={d?.Requests} failures={d?.Failures} covered={d?.Covered.TotalSeconds:F0}s skipped={d?.Skipped.TotalSeconds:F0}s health={d?.Health} {d?.Detail}");
        if (echo)
        {
            var micSegs = segs.Where(s => s.Source == AudioSourceKind.Microphone && s.Kind == SegmentKind.Speech).ToList();
            Out($"Microphone echo: {micSegs.Count(s => s.DuplicateOfSegmentId is not null)}/{micSegs.Count} microphone segments hidden as echo; echo control: {snap.Microphone.EchoControl} — {snap.Microphone.EchoDetail}");
            bool Overlaps(TranscriptSegment s, double a, double b) =>
                s.AudioStart < origin0 + TimeSpan.FromSeconds(b + 0.3) && s.AudioEnd > origin0 + TimeSpan.FromSeconds(a - 0.3);
            var leaks = micSegs.Where(s => s.DuplicateOfSegmentId is null && !talkPlacements.Any(p => Overlaps(s, p.Start, p.End))).ToList();
            Out($"Visible echo-only microphone captions (should be 0): {leaks.Count}");
            foreach (var p in talkPlacements)
            {
                var hits = micSegs.Where(s => Overlaps(s, p.Start, p.End)).ToList();
                Out($"My speech \"{p.Text}\" at {p.Start:F1}-{p.End:F1}s → " + (hits.Count == 0 ? "NOT TRANSCRIBED" : string.Join(" | ", hits.Select(h => (h.DuplicateOfSegmentId is null ? string.Empty : "[HIDDEN] ") + h.Text))));
            }
        }
        foreach (var l in session.LatencyReport())
        {
            Out($"Latency {l.Metric}: n={l.Samples} median={l.MedianSeconds?.ToString("F2") ?? "n/a"}s p90={l.P90Seconds?.ToString("F2") ?? "n/a"}s");
        }
        foreach (var card in session.Board.Cards)
        {
            Out($"Card {card.Kind}/{card.State} q=\"{card.QuestionText}\" firstText={card.FirstUsefulTextLatency?.TotalSeconds:F1}s complete={card.CompleteLatency?.TotalSeconds:F1}s spokenChars={card.SpokenAnswer?.Length ?? 0}");
        }
        // Against ground truth: from the end of the spoken question (last script line that ended before the answer text).
        var speechToFirst = new List<double>();
        var speechToComplete = new List<double>();
        foreach (var card in session.Board.Cards.Where(c => c.Kind == CardKind.Answer && c.FirstUsefulTextAt is not null))
        {
            var at = card.FirstUsefulTextAt!.Value;
            var line = truth.Where(t => t.End <= at).OrderByDescending(t => t.End).FirstOrDefault();
            if (line is null)
            {
                continue;
            }
            speechToFirst.Add((at - line.End).TotalSeconds);
            if (card.CompletedAt is { } done)
            {
                speechToComplete.Add((done - line.End).TotalSeconds);
            }
        }
        if (speechToFirst.Count > 0)
        {
            Out($"Speech end → first useful answer text (ground truth): {string.Join(", ", speechToFirst.Select(x => x.ToString("F2")))} s; median {speechToFirst.Order().ElementAt(speechToFirst.Count / 2):F2} s");
            Out($"Speech end → complete answer (ground truth): {string.Join(", ", speechToComplete.Select(x => x.ToString("F1")))} s");
        }
        if (o.Get("report") is { } path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(path, log.ToString());
        }
        return 0;
    }

    /// <summary>Script lines one after another (with their pauses) as one playback track, plus ground-truth placements.</summary>
    internal static async Task<(short[] Track, List<(string Speaker, double Start, double End)> Placements)> BuildSystemTrackAsync(SpeechScript script)
    {
        var lines = await ScriptPlayer.SynthesizeAsync(script);
        var system = new List<short>();
        var placements = new List<(string Speaker, double Start, double End)>();
        system.AddRange(new short[PipelineAudio.Samples(TimeSpan.FromSeconds(1))]);
        foreach (var (line, pcm) in lines)
        {
            double start = system.Count / (double)PipelineAudio.SampleRate;
            system.AddRange(pcm);
            // Ground truth ends at the last audible sample (TTS output carries trailing silence).
            int last = pcm.Length - 1;
            while (last > 0 && Math.Abs((int)pcm[last]) < 300)
            {
                last--;
            }
            placements.Add((line.Speaker, start, start + (last + 1) / (double)PipelineAudio.SampleRate));
            system.AddRange(new short[PipelineAudio.Samples(TimeSpan.FromMilliseconds(line.PauseAfterMs + line.WaitAfterMs))]);
        }
        system.AddRange(new short[PipelineAudio.Samples(TimeSpan.FromSeconds(2))]);
        return (system.ToArray(), placements);
    }

    /// <summary>Mixes three of "my" utterances into the microphone track over the playback (double talk).</summary>
    internal static async Task<(List<(string Text, double Start, double End)> Placements, short[] Near)> AddMySpeechAsync(SpeechScript script, short[] micTrack)
    {
        string[] mine =
        [
            "Sorry to interrupt, let me add one point about idempotency keys.",
            "I think we also need monitoring on the replay queue.",
            "Can we come back to the database question later?",
        ];
        var used = script.Voices.Values.Select(v => v.Voice).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string voice = ScriptPlayer.InstalledVoices().Where(v => v.Contains("(en-", StringComparison.OrdinalIgnoreCase))
            .Select(v => v.Split(" (")[0].Replace("Microsoft", string.Empty).Trim())
            .FirstOrDefault(v => !used.Contains(v)) ?? "David";
        var meScript = new SpeechScript("me", new() { ["Me"] = new ScriptVoice(voice, "high") }, mine.Select(t => new ScriptLine("Me", t)).ToList());
        var meLines = await ScriptPlayer.SynthesizeAsync(meScript);
        var near = new short[micTrack.Length];
        var placements = new List<(string Text, double Start, double End)>();
        double[] at = [0.3, 0.55, 0.8];
        for (int i = 0; i < meLines.Count; i++)
        {
            int offset = (int)(micTrack.Length * at[i]);
            var pcm = meLines[i].Pcm;
            for (int k = 0; k < pcm.Length && offset + k < micTrack.Length; k++)
            {
                near[offset + k] = (short)(pcm[k] * 0.8);
                micTrack[offset + k] = (short)Math.Clamp(micTrack[offset + k] + near[offset + k], short.MinValue, short.MaxValue);
            }
            placements.Add((mine[i], offset / (double)PipelineAudio.SampleRate, (offset + pcm.Length) / (double)PipelineAudio.SampleRate));
        }
        return (placements, near);
    }

    /// <summary>
    /// Loudspeaker echo as heard by a laptop microphone: delayed, attenuated, reflections, noise floor; optionally a
    /// small loudspeaker that saturates on loud passages (non-linear echo).
    /// </summary>
    internal static short[] SimulatedEcho(short[] playback, bool distort)
    {
        var rng = new Random(7);
        var speaker = distort
            ? playback.Select(v => (short)(Math.Tanh(2.5 * v / 32768.0) / 2.5 * 32768)).ToArray()
            : playback;
        var y = new short[playback.Length];
        (int Ms, double G)[] taps = [(90, 0.2), (125, 0.1), (200, 0.05)];
        for (int i = 0; i < y.Length; i++)
        {
            double v = (rng.NextDouble() * 2 - 1) * 40;
            foreach (var (ms, g) in taps)
            {
                int j = i - PipelineAudio.Samples(TimeSpan.FromMilliseconds(ms));
                if (j >= 0)
                {
                    v += speaker[j] * g;
                }
            }
            y[i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
        }
        return y;
    }
}
