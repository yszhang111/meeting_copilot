using ProactiveAI.Core.Audio;
using ProactiveAI.Tests.Fakes;

namespace ProactiveAI.Tests;

public class EchoCancellingPreprocessorTests
{
    private const int B = EchoCanceller.Block;
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(5);

    private static AudioTimeline NewTimeline() => new(TimeSpan.FromSeconds(60), 60L * PipelineAudio.BytesPerSecond);

    private static List<AudioPiece> Feed(EchoCancellingPreprocessor p, short[] mic, TimeSpan start, int packet = 240, AudioTimeline? reference = null, short[]? playback = null, Random? jitter = null)
    {
        var pieces = new List<AudioPiece>();
        for (int i = 0; i < mic.Length; i += packet)
        {
            var t = start + PipelineAudio.Duration(i);
            int n = Math.Min(packet, mic.Length - i);
            if (reference is not null && playback is not null)
            {
                // Playback packets carry timestamps with sub-millisecond jitter, like real QPC positions.
                var rt = t + TimeSpan.FromTicks(jitter is null ? 0 : jitter.Next(-3000, 3000));
                reference.Append(rt, playback.AsSpan(i, n).ToArray());
            }
            var mt = t + TimeSpan.FromTicks(jitter is null ? 0 : jitter.Next(-3000, 3000));
            pieces.AddRange(p.Process(mt, mic.AsSpan(i, n).ToArray(), false));
        }
        return pieces;
    }

    private static short[] Join(IEnumerable<AudioPiece> pieces) => pieces.SelectMany(x => x.Samples).ToArray();

    [Fact]
    public void WithoutPlaybackAudioPassesThroughUnchangedAndContiguously()
    {
        var p = new EchoCancellingPreprocessor(() => null);
        var mic = Signals.Speech(TimeSpan.FromSeconds(2), 1);
        var pieces = Feed(p, mic, T0);
        var output = Join(pieces);
        Assert.Equal(mic.Length / B * B, output.Length);
        Assert.Equal(mic.Take(output.Length), output);
        Assert.True(pieces[0].Discontinuity);
        Assert.All(pieces.Skip(1), x => Assert.False(x.Discontinuity));
        var expected = T0;
        foreach (var piece in pieces)
        {
            Assert.True((piece.Start - expected).Duration() < TimeSpan.FromMilliseconds(0.1), $"piece at {piece.Start} expected {expected}");
            expected = piece.Start + PipelineAudio.Duration(piece.Samples.Length);
        }
    }

    [Fact]
    public void MicrophoneWaitsBrieflyForThePlaybackReference()
    {
        var timeline = NewTimeline();
        timeline.Append(T0 - TimeSpan.FromMilliseconds(100), Signals.Speech(TimeSpan.FromMilliseconds(100), 2)); // playback ends at T0
        var p = new EchoCancellingPreprocessor(() => timeline);
        // 50 ms of microphone audio after T0: the reference does not cover it yet and playback is not idle.
        Assert.Empty(Feed(p, Signals.Speech(TimeSpan.FromMilliseconds(50), 3), T0));
        // The reference catches up: held blocks are released.
        timeline.Append(T0, Signals.Speech(TimeSpan.FromMilliseconds(200), 4));
        var pieces = p.Process(T0 + TimeSpan.FromMilliseconds(50), new short[240], false);
        Assert.Equal(PipelineAudio.Samples(TimeSpan.FromMilliseconds(60)) / B * B, Join(pieces).Length);
        Assert.Equal(T0, pieces[0].Start);
        Assert.Equal(0, p.BlocksHeldOut);
        Assert.True(p.BlocksWithReference > 0);
    }

    [Fact]
    public void StalledReferenceDoesNotHoldTheMicrophoneLongerThanTheLimit()
    {
        var timeline = NewTimeline();
        timeline.Append(T0 - TimeSpan.FromMilliseconds(100), Signals.Speech(TimeSpan.FromMilliseconds(100), 2));
        var p = new EchoCancellingPreprocessor(() => timeline);
        var output = Join(Feed(p, Signals.Speech(TimeSpan.FromSeconds(1), 5), T0));
        // Everything except the last ~150 ms is released although the reference never caught up...
        Assert.InRange(output.Length, PipelineAudio.Samples(TimeSpan.FromMilliseconds(800)), PipelineAudio.Samples(TimeSpan.FromSeconds(1)));
        Assert.True(p.BlocksHeldOut > 0);
        // ...and once playback has been silent for a while, blocks are no longer held at all.
        var later = Feed(p, Signals.Speech(TimeSpan.FromSeconds(1), 6), T0 + TimeSpan.FromSeconds(1));
        Assert.True(Join(later).Length >= PipelineAudio.Samples(TimeSpan.FromSeconds(1)) - B);
    }

    [Fact]
    public void DiscontinuityReleasesHeldAudioAndRestartsTiming()
    {
        var timeline = NewTimeline();
        timeline.Append(T0 - TimeSpan.FromMilliseconds(100), Signals.Speech(TimeSpan.FromMilliseconds(100), 2));
        var p = new EchoCancellingPreprocessor(() => timeline);
        var held = Signals.Speech(TimeSpan.FromMilliseconds(40), 7);
        Assert.Empty(Feed(p, held, T0));
        var after = T0 + TimeSpan.FromSeconds(3);
        var pieces = p.Process(after, new short[240], false);
        var raw = Assert.Single(pieces);
        Assert.Equal(held, raw.Samples); // nothing lost, nothing altered
        Assert.Equal(T0, raw.Start);
        var next = Feed(p, new short[PipelineAudio.Samples(TimeSpan.FromMilliseconds(100))], after + TimeSpan.FromMilliseconds(10));
        Assert.True(next[0].Discontinuity);
        Assert.Equal(after, next[0].Start);
    }

    [Fact]
    public void EchoIsRemovedFromTheMicrophoneUsingThePlaybackTimeline()
    {
        var total = TimeSpan.FromSeconds(10);
        var playback = Signals.Speech(total, 8);
        var mic = Signals.RoomEcho(playback, 9, 0.25, 90, variableAec: false);
        var timeline = NewTimeline();
        var p = new EchoCancellingPreprocessor(() => timeline);
        var output = Join(Feed(p, mic, T0, 240, timeline, playback, new Random(10)));
        Assert.True(output.Length >= mic.Length - PipelineAudio.Samples(TimeSpan.FromMilliseconds(60)), $"released {output.Length} of {mic.Length}"); // tail waits for the reference lead
        double Db(short[] s, double a, double b)
        {
            int x = (int)(a * PipelineAudio.SampleRate), y = Math.Min(s.Length, (int)(b * PipelineAudio.SampleRate));
            double sum = 0;
            for (int i = x; i < y; i++)
            {
                sum += (s[i] / 32768.0) * (s[i] / 32768.0);
            }
            return 10 * Math.Log10(sum / (y - x) + 1e-12);
        }
        double reduction = Db(mic, 6, 9.5) - Db(output, 6, 9.5);
        Assert.True(reduction >= 20, $"echo reduction through the preprocessor {reduction:F1} dB");
        Assert.True(p.Canceller.Adapted);
        Assert.Contains("removing", p.Describe());
    }
}
