using ProactiveAI.Core.Audio;

namespace ProactiveAI.Probe;

/// <summary>
/// Offline evaluation of the built-in echo canceller on the same synthetic audio as the feed test: TTS dialogue
/// on the loudspeaker, simulated laptop echo, and "my" speech over it. No device audio, no API calls.
/// </summary>
public static class AecEval
{
    public static async Task<int> RunAsync(Options o)
    {
        string scriptArg = o.Get("script") ?? "feed";
        var script = SpeechScript.Load(File.Exists(scriptArg) ? scriptArg : Path.Combine(AppContext.BaseDirectory, "Scripts", scriptArg + ".json"));
        var (sys, _) = await FeedTest.BuildSystemTrackAsync(script);
        var echo = FeedTest.SimulatedEcho(sys, o.Has("distort"));
        var mic = (short[])echo.Clone();
        var (talk, near) = await FeedTest.AddMySpeechAsync(script, mic);

        var t0 = TimeSpan.FromSeconds(10);
        var timeline = new AudioTimeline(TimeSpan.FromSeconds(300), 300L * PipelineAudio.BytesPerSecond);
        var p = new EchoCancellingPreprocessor(() => timeline);
        var output = new short[mic.Length];
        int packet = PipelineAudio.Samples(TimeSpan.FromMilliseconds(10));
        int sr = PipelineAudio.SampleRate;
        var perSecond = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < mic.Length; i += packet)
        {
            int n = Math.Min(packet, mic.Length - i);
            var t = t0 + PipelineAudio.Duration(i);
            timeline.Append(t, sys.AsSpan(i, n).ToArray());
            foreach (var piece in p.Process(t, mic.AsSpan(i, n).ToArray(), false))
            {
                int at = PipelineAudio.Samples(piece.Start - t0);
                piece.Samples.AsSpan(0, Math.Min(piece.Samples.Length, output.Length - at)).CopyTo(output.AsSpan(at));
            }
            if ((i + n) / sr != i / sr)
            {
                int s = i / sr;
                bool mine = talk.Any(x => x.Start < s + 1 && x.End > s);
                perSecond.Add($"  {s,3}s mic {Db(mic, s * sr, (s + 1) * sr),6:F1} out {Db(output, s * sr, (s + 1) * sr),6:F1} dB  " +
                              $"adapted={p.Canceller.Adapted,-5} erle={p.Canceller.EchoReductionDb,5:F1} resets={p.Canceller.DivergenceResets} fg={p.Canceller.ForegroundUpdates} bg={p.Canceller.BackgroundResets}{(mine ? "  <- my speech" : string.Empty)}");
            }
        }
        double cpu = sw.Elapsed.TotalSeconds / (mic.Length / (double)sr);

        Console.WriteLine($"AEC eval: script={script.Name} audio={mic.Length / (double)sr:F0}s distort={o.Has("distort")} cpu={cpu:P1} of one core (incl. timeline)");
        if (o.Has("verbose"))
        {
            perSecond.ForEach(Console.WriteLine);
        }

        // Echo-only reduction, power weighted, outside my speech (+/-0.3 s), where the echo is above the noise floor.
        bool Mine(int idx) => talk.Any(x => idx >= (x.Start - 0.3) * sr && idx < (x.End + 0.3) * sr);
        (double In, double Out) Sum(int from, int to)
        {
            double a = 0, b = 0;
            for (int k = from; k < Math.Min(to, mic.Length); k++)
            {
                if (Mine(k))
                {
                    continue;
                }
                a += (double)mic[k] * mic[k];
                b += (double)output[k] * output[k];
            }
            return (a, b);
        }
        void Report(string label, int from, int to)
        {
            var (a, b) = Sum(from, to);
            Console.WriteLine($"Echo-only reduction {label,-10}: {10 * Math.Log10((a + 1) / (b + 1)),5:F1} dB");
        }
        Report("0-5 s", 0, 5 * sr);
        Report("5-15 s", 5 * sr, 15 * sr);
        Report("15 s-end", 15 * sr, mic.Length);

        foreach (var x in talk)
        {
            int a = (int)(x.Start * sr), b = Math.Min(mic.Length, (int)(x.End * sr));
            double on = 0, nn = 0, ee = 0;
            for (int k = a; k < b; k++)
            {
                on += (double)output[k] * near[k];
                nn += (double)near[k] * near[k];
                double e = mic[k] - near[k];
                ee += e * e;
            }
            double g = on / nn;
            double rr = 0;
            for (int k = a; k < b; k++)
            {
                double r = output[k] - g * near[k];
                rr += r * r;
            }
            Console.WriteLine($"Double talk {x.Start,5:F1}-{x.End,5:F1}s: echo-to-me in {10 * Math.Log10(ee / nn),6:F1} dB â†’ residual-to-me out {10 * Math.Log10(rr / (g * g * nn)),6:F1} dB; my speech level {20 * Math.Log10(g),5:F1} dB");
        }
        Console.WriteLine($"Final: adapted={p.Canceller.Adapted} erle={p.Canceller.EchoReductionDb:F1} resets={p.Canceller.DivergenceResets} pathChanges={p.Canceller.PathChanges} foregroundUpdates={p.Canceller.ForegroundUpdates} backgroundResets={p.Canceller.BackgroundResets} heldOut={p.BlocksHeldOut}");
        return 0;
    }

    private static double Db(short[] s, int from, int to)
    {
        double sum = 0;
        to = Math.Min(to, s.Length);
        for (int i = from; i < to; i++)
        {
            double v = s[i] / 32768.0;
            sum += v * v;
        }
        return 10 * Math.Log10(sum / Math.Max(1, to - from) + 1e-12);
    }
}
