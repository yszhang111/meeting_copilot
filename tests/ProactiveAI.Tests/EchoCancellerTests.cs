using ProactiveAI.Core.Audio;
using ProactiveAI.Tests.Fakes;

namespace ProactiveAI.Tests;

public class EchoCancellerTests
{
    private static short[] Run(EchoCanceller aec, short[] mic, short[] reference)
    {
        var output = new short[mic.Length];
        int B = EchoCanceller.Block;
        for (int i = 0; i + B <= mic.Length; i += B)
        {
            aec.Process(mic.AsSpan(i, B), reference.AsSpan(i, B), output.AsSpan(i, B));
        }
        return output;
    }

    private static double PowerDb(short[] s, TimeSpan from, TimeSpan to)
    {
        int a = PipelineAudio.Samples(from), b = Math.Min(s.Length, PipelineAudio.Samples(to));
        double sum = 0;
        for (int i = a; i < b; i++)
        {
            double v = s[i] / 32768.0;
            sum += v * v;
        }
        return 10 * Math.Log10(sum / Math.Max(1, b - a) + 1e-12);
    }

    private static double Correlation(short[] a, short[] b, TimeSpan from, TimeSpan to)
    {
        int s = PipelineAudio.Samples(from), e = Math.Min(Math.Min(a.Length, b.Length), PipelineAudio.Samples(to));
        double sab = 0, saa = 0, sbb = 0;
        for (int i = s; i < e; i++)
        {
            sab += (double)a[i] * b[i];
            saa += (double)a[i] * a[i];
            sbb += (double)b[i] * b[i];
        }
        return sab / Math.Sqrt(saa * sbb + 1e-9);
    }

    [Theory]
    [InlineData(1, 0.2, 90)]
    [InlineData(2, 0.5, 60)]
    [InlineData(3, 0.1, 180)]
    public void LinearRoomEchoIsReducedAfterConvergence(int seed, double gain, int delayMs)
    {
        var total = TimeSpan.FromSeconds(10);
        var far = Signals.Speech(total, seed);
        var mic = Signals.RoomEcho(far, seed + 1, gain, delayMs, variableAec: false);
        var aec = new EchoCanceller();
        var output = Run(aec, mic, far);
        double before = PowerDb(mic, TimeSpan.FromSeconds(6), total);
        double after = PowerDb(output, TimeSpan.FromSeconds(6), total);
        Assert.True(before - after >= 20, $"echo reduction {before - after:F1} dB (ERLE estimate {aec.EchoReductionDb:F1} dB)");
        Assert.True(aec.Adapted);
    }

    [Fact]
    public void LocalSpeechDuringPlaybackIsPreserved()
    {
        var total = TimeSpan.FromSeconds(12);
        var far = Signals.Speech(total, 5);
        var echo = Signals.RoomEcho(far, 6, 0.3, 90, variableAec: false);
        var near = Signals.Place(Signals.Scale(Signals.Speech(TimeSpan.FromSeconds(3), 7), 0.5), TimeSpan.FromSeconds(7), total);
        var mic = Signals.Mix(echo, near);
        var output = Run(new EchoCanceller(), mic, far);
        var window = (TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(10));
        double nearDb = PowerDb(near, window.Item1, window.Item2);
        double outDb = PowerDb(output, window.Item1, window.Item2);
        Assert.InRange(outDb - nearDb, -3, 3); // local speech level kept, echo mostly gone
        Assert.True(Correlation(output, near, window.Item1, window.Item2) > 0.9, "output resembles the local talker");
    }

    [Fact]
    public void WithoutPlaybackTheMicrophoneIsUntouched()
    {
        var total = TimeSpan.FromSeconds(3);
        var near = Signals.Speech(total, 9);
        var output = Run(new EchoCanceller(), near, Signals.Silence(total));
        Assert.True(Correlation(output, near, TimeSpan.Zero, total) > 0.999);
    }

    [Fact]
    public void PauseInPlaybackKeepsTheLearntEchoPath()
    {
        // Playback stops abruptly at full level (as TTS or a muted call does), pauses, then resumes.
        var rng = new Random(30);
        var loudTail = Enumerable.Range(0, PipelineAudio.Samples(TimeSpan.FromMilliseconds(300))).Select(_ => (short)((rng.NextDouble() * 2 - 1) * 9000)).ToArray();
        var far = Signals.Concat(Signals.Speech(TimeSpan.FromSeconds(6), 31), loudTail, Signals.Silence(TimeSpan.FromSeconds(3)), Signals.Speech(TimeSpan.FromSeconds(3), 32));
        var mic = Signals.RoomEcho(far, 33, 0.6, 90, variableAec: false); // microphone noise floor continues during the pause
        var aec = new EchoCanceller();
        var output = Run(aec, mic, far);
        Assert.Equal(0, aec.DivergenceResets);
        Assert.True(aec.Adapted);
        double reduction = PowerDb(mic, TimeSpan.FromSeconds(9.3), TimeSpan.FromSeconds(10.3)) - PowerDb(output, TimeSpan.FromSeconds(9.3), TimeSpan.FromSeconds(10.3));
        Assert.True(reduction >= 15, $"reduction right after playback resumes {reduction:F1} dB");
    }

    [Fact]
    public void EchoPathChangeIsTracked()
    {
        var total = TimeSpan.FromSeconds(16);
        var far = Signals.Speech(total, 11);
        var first = Signals.RoomEcho(far, 12, 0.2, 80, variableAec: false);
        var second = Signals.RoomEcho(far, 13, 0.35, 150, variableAec: false);
        int half = PipelineAudio.Samples(TimeSpan.FromSeconds(8));
        var mic = first.Take(half).Concat(second.Skip(half)).ToArray();
        var output = Run(new EchoCanceller(), mic, far);
        double reduction = PowerDb(mic, TimeSpan.FromSeconds(13), total) - PowerDb(output, TimeSpan.FromSeconds(13), total);
        Assert.True(reduction >= 15, $"reduction after path change {reduction:F1} dB");
    }
}
