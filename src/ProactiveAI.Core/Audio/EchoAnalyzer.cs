namespace ProactiveAI.Core.Audio;

public readonly record struct EchoCorrelation(double Correlation, TimeSpan Lag, double LeaderActivity, double FollowerActivity)
{
    public static EchoCorrelation None => new(0, TimeSpan.Zero, 0, 0);
}

/// <summary>
/// Result of explaining a microphone interval by the playback signal. <see cref="UnexplainedHops"/> counts
/// 10 ms hops where the microphone is clearly louder than the echo coupling would predict — i.e. a local
/// talker is present. <see cref="LocalBurstHops"/> is the densest 1 s of such hops: local speech arrives in
/// bursts, whereas residual-echo glitches are scattered.
/// </summary>
public readonly record struct EchoAssessment(double Correlation, TimeSpan Lag, int MicActiveHops, int UnexplainedHops, int PlaybackActiveHops, int LocalBurstHops)
{
    public static EchoAssessment None => new(0, TimeSpan.Zero, 0, 0, 0, 0);

    public double UnexplainedFraction => MicActiveHops == 0 ? 0 : UnexplainedHops / (double)MicActiveHops;

    public TimeSpan UnexplainedDuration => TimeSpan.FromMilliseconds(UnexplainedHops * 10);

    /// <summary>
    /// ≥150 ms of unexplained microphone energy within one second marks a local talker. Biased towards keeping
    /// speech: a missed echo shows a duplicate line, a false echo would hide what the user said.
    /// </summary>
    public bool HasLocalSpeech => LocalBurstHops >= 15 || UnexplainedFraction > 0.2;

    /// <summary>Timing + acoustic evidence that the microphone only carried loudspeaker echo of the playback.</summary>
    public bool IsEcho => MicActiveHops >= 20 && PlaybackActiveHops >= 10 && Correlation >= 0.65 && !HasLocalSpeech;
}

/// <summary>
/// Acoustic corroboration for cross-source duplicates. Compares short-time energy envelopes of
/// two sources (e.g. loopback playback vs. microphone) across a range of positive lags in which
/// the leader precedes the follower. Loudspeaker echo produces a strongly correlated, delayed
/// envelope; an independent talker does not. Text similarity alone is never sufficient.
/// </summary>
public static class EchoAnalyzer
{
    private const int HopSamples = PipelineAudio.SampleRate / 100; // 10 ms

    public static EchoCorrelation Correlate(short[]? leader, short[]? follower, TimeSpan maxLag)
    {
        if (leader is null || follower is null)
        {
            return EchoCorrelation.None;
        }
        var a = Envelope(leader);
        var b = Envelope(follower);
        int maxLagHops = (int)(maxLag.TotalMilliseconds / 10);
        if (a.Length < 20 || b.Length < 20)
        {
            return EchoCorrelation.None;
        }

        double best = 0;
        int bestLag = 0;
        for (int lag = 0; lag <= maxLagHops; lag++)
        {
            // follower[t] ~ leader[t - lag]
            int n = Math.Min(a.Length, b.Length - lag);
            if (n < 20)
            {
                break;
            }
            double c = Pearson(a.AsSpan(0, n), b.AsSpan(lag, n));
            if (c > best)
            {
                best = c;
                bestLag = lag;
            }
        }
        return new EchoCorrelation(best, TimeSpan.FromMilliseconds(bestLag * 10), Activity(a), Activity(b));
    }

    /// <summary>
    /// Explains a microphone interval by playback that starts <paramref name="lead"/> earlier (so the echo
    /// delay, up to <paramref name="lead"/>, can be searched). Echo is modelled as playback scaled by the
    /// strongest observed coupling plus a 200 ms reverberation hold; AEC can only lower the echo, so any
    /// microphone energy well above that prediction is attributed to a local talker.
    /// </summary>
    public static EchoAssessment Assess(short[]? mic, short[]? playbackWithLead, TimeSpan lead)
    {
        if (mic is null || playbackWithLead is null)
        {
            return EchoAssessment.None;
        }
        var m = Envelope(mic);
        var p = Envelope(playbackWithLead);
        int leadHops = (int)(lead.TotalMilliseconds / 10);
        if (m.Length < 20 || p.Length < m.Length + leadHops - 1)
        {
            return EchoAssessment.None;
        }

        // Echo delay: best correlation of mic[t] with playback[t - lag], lag in [0, lead].
        double best = -1;
        int bestLag = 0;
        for (int lag = 0; lag <= leadHops; lag++)
        {
            double c = Pearson(p.AsSpan(leadHops - lag, m.Length), m);
            if (c > best)
            {
                best = c;
                bestLag = lag;
            }
        }

        const int holdHops = 20;
        var shifted = new double[m.Length];
        for (int t = 0; t < m.Length; t++)
        {
            double max = -10;
            for (int k = 0; k <= holdHops; k++)
            {
                int idx = leadHops - bestLag + t - k;
                if (idx >= 0)
                {
                    max = Math.Max(max, p[idx]);
                }
            }
            shifted[t] = max;
        }

        double micFloor = Percentile(m, 0.1);
        double micActive = Math.Max(micFloor + 1.0, -6.0);   // ≥10 dB above the floor and above -60 dBFS
        const double playbackActive = -5.5;                   // -55 dBFS
        var couplings = new List<double>();
        int micActiveHops = 0, playbackActiveHops = 0;
        for (int t = 0; t < m.Length; t++)
        {
            double aligned = p[leadHops - bestLag + t];
            bool ma = m[t] > micActive;
            bool pa = shifted[t] > playbackActive;
            micActiveHops += ma ? 1 : 0;
            playbackActiveHops += pa ? 1 : 0;
            if (ma && aligned > playbackActive)
            {
                // Instantaneous coupling on the delay-aligned playback (not the hold), so word decays do not bias it.
                couplings.Add(m[t] - aligned);
            }
        }
        if (couplings.Count < 5)
        {
            // Nothing the playback could explain: every active microphone hop is local.
            return new EchoAssessment(Math.Max(0, best), TimeSpan.FromMilliseconds(bestLag * 10), micActiveHops, micActiveHops, playbackActiveHops, micActiveHops);
        }
        // Typical echo coupling (median, robust while double talk is the minority) plus a 6 dB allowance for how
        // much an AEC varies its residual echo; the reverberation hold covers decays.
        double coupling = Percentile(couplings.ToArray(), 0.5);
        var unexplainedAt = new bool[m.Length];
        int unexplained = 0;
        for (int t = 0; t < m.Length; t++)
        {
            if (m[t] > micActive && m[t] > shifted[t] + coupling + 0.6)
            {
                unexplainedAt[t] = true;
                unexplained++;
            }
        }
        // Local speech appears as a burst; AEC glitches are scattered. Densest 1 s of unexplained hops:
        int burst = 0, run = 0;
        for (int t = 0; t < m.Length; t++)
        {
            run += unexplainedAt[t] ? 1 : 0;
            if (t >= 100)
            {
                run -= unexplainedAt[t - 100] ? 1 : 0;
            }
            burst = Math.Max(burst, run);
        }
        return new EchoAssessment(Math.Max(0, best), TimeSpan.FromMilliseconds(bestLag * 10), micActiveHops, unexplained, playbackActiveHops, burst);
    }

    private static double Percentile(double[] values, double q)
    {
        if (values.Length == 0)
        {
            return 0;
        }
        var sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)(q * (sorted.Length - 1)), 0, sorted.Length - 1)];
    }

    internal static double[] Envelope(short[] samples)
    {
        int hops = samples.Length / HopSamples;
        var env = new double[hops];
        for (int h = 0; h < hops; h++)
        {
            double sum = 0;
            int off = h * HopSamples;
            for (int i = 0; i < HopSamples; i++)
            {
                double v = samples[off + i] / 32768.0;
                sum += v * v;
            }
            // log-compressed envelope is robust to the gain difference between playback and capture
            env[h] = Math.Log10(1e-7 + (sum / HopSamples));
        }
        return env;
    }

    private static double Activity(double[] env)
    {
        if (env.Length == 0)
        {
            return 0;
        }
        int active = 0;
        foreach (double e in env)
        {
            if (e > -5.0)
            {
                active++;
            }
        }
        return active / (double)env.Length;
    }

    private static double Pearson(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
    {
        double mx = 0, my = 0;
        for (int i = 0; i < x.Length; i++)
        {
            mx += x[i];
            my += y[i];
        }
        mx /= x.Length;
        my /= y.Length;
        double sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            sxy += dx * dy;
            sxx += dx * dx;
            syy += dy * dy;
        }
        if (sxx < 1e-9 || syy < 1e-9)
        {
            return 0;
        }
        return sxy / Math.Sqrt(sxx * syy);
    }
}
