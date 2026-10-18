using ProactiveAI.Core.Audio;
using ProactiveAI.Tests.Fakes;

namespace ProactiveAI.Tests;

public class AudioTests
{
    [Fact]
    public void VadOpensTurnWithPreRollAndClosesAfterSilence()
    {
        var vad = new VoiceActivityDetector();
        var audio = Signals.Concat(Signals.Silence(TimeSpan.FromSeconds(1)), Signals.Speech(TimeSpan.FromSeconds(1.5), 1), Signals.Silence(TimeSpan.FromSeconds(1.2)));
        var actions = new List<VadAction>();
        int preRollFrames = 0;
        foreach (var f in Signals.Frames(audio))
        {
            var d = vad.Process(f, out var pre);
            if (d.Action != VadAction.Idle && d.Action != VadAction.Continue)
            {
                actions.Add(d.Action);
            }
            if (d.Action == VadAction.StartTurn)
            {
                preRollFrames = pre.Count;
            }
        }
        Assert.Equal([VadAction.StartTurn, VadAction.EndTurn], actions);
        Assert.InRange(preRollFrames, 10, 15);
    }

    [Fact]
    public void VadSplitsLongMonologueAndEndsOnDeliveryGap()
    {
        var vad = new VoiceActivityDetector(new VadOptions { SoftMaxTurn = TimeSpan.FromSeconds(3), HardMaxTurn = TimeSpan.FromSeconds(4) });
        int splits = 0;
        foreach (var f in Signals.Frames(Signals.Speech(TimeSpan.FromSeconds(9), 2, modulationHz: 0.1)))
        {
            if (vad.Process(f, out _).Action == VadAction.SplitTurn)
            {
                splits++;
            }
        }
        Assert.True(splits >= 2);
        Assert.True(vad.InTurn);
        Assert.True(vad.ShouldEndOnGap(TimeSpan.FromSeconds(1)));
        Assert.False(vad.InTurn);
    }

    [Fact]
    public void TimelineFillsGapsWithZerosAndIsBoundedByRetention()
    {
        var tl = new AudioTimeline(TimeSpan.FromSeconds(10), 10 * PipelineAudio.BytesPerSecond);
        var a = Signals.Speech(TimeSpan.FromSeconds(1), 3);
        tl.Append(TimeSpan.FromSeconds(0), a);
        tl.Append(TimeSpan.FromSeconds(3), a); // 2 s gap (loopback silence)
        var window = tl.Extract(TimeSpan.Zero, TimeSpan.FromSeconds(4))!;
        Assert.Equal(PipelineAudio.Samples(TimeSpan.FromSeconds(4)), window.Length);
        Assert.Equal(a[100], window[100]);
        Assert.Equal(0, window[PipelineAudio.Samples(TimeSpan.FromSeconds(2))]);
        Assert.Equal(a[100], window[PipelineAudio.Samples(TimeSpan.FromSeconds(3)) + 100]);

        for (int i = 4; i < 30; i++)
        {
            tl.Append(TimeSpan.FromSeconds(i), Signals.Speech(TimeSpan.FromSeconds(1), i));
        }
        Assert.True(tl.End - tl.Start <= TimeSpan.FromSeconds(11));
        Assert.True(tl.Bytes <= 11 * PipelineAudio.BytesPerSecond);
        tl.Clear();
        Assert.Equal(0, tl.Bytes);
    }

    [Fact]
    public void EchoAnalyzerFindsDelayedLoudspeakerCopyButNotAnIndependentTalker()
    {
        var playback = Signals.Speech(TimeSpan.FromSeconds(3), 7);
        var echo = Signals.DelayAndScale(playback, TimeSpan.FromMilliseconds(120), 0.2);
        var independent = Signals.Speech(TimeSpan.FromSeconds(3), 99);

        var corr = EchoAnalyzer.Correlate(playback, echo, TimeSpan.FromMilliseconds(500));
        Assert.True(corr.Correlation > 0.85, $"echo corr {corr.Correlation}");
        Assert.InRange(corr.Lag.TotalMilliseconds, 100, 140);
        Assert.True(EchoAnalyzer.Correlate(playback, independent, TimeSpan.FromMilliseconds(500)).Correlation < 0.5);
    }

    [Fact]
    public void IdenticalWordsWithoutAcousticEvidenceAreKept()
    {
        // Two people intentionally say the same words: text matches, audio does not correlate.
        var low = new EchoCorrelation(0.2, TimeSpan.Zero, 0.8, 0.8);
        Assert.Equal(DuplicateVerdict.KeepBoth, DuplicateJudge.Decide("we ship on monday", "we ship on Monday", low, low));
    }

    [Fact]
    public void EchoWithMatchingTextIsSuppressedOnTheMicrophoneSide()
    {
        var strong = new EchoCorrelation(0.92, TimeSpan.FromMilliseconds(120), 0.8, 0.7);
        var weak = new EchoCorrelation(0.3, TimeSpan.Zero, 0.7, 0.8);
        Assert.Equal(DuplicateVerdict.MicrophoneIsEcho, DuplicateJudge.Decide("we ship on monday", "okay we ship on Monday then", strong, weak));
        Assert.Equal(DuplicateVerdict.PlaybackIsMonitorEcho, DuplicateJudge.Decide("my answer is caching", "my answer is caching", weak, strong));
    }

    [Fact]
    public void DoubleTalkKeepsLocalSpeechWhenTextDiffers()
    {
        // Mic contains echo plus a different local sentence: acoustic correlation exists, text does not match.
        var strong = new EchoCorrelation(0.8, TimeSpan.FromMilliseconds(120), 0.8, 0.8);
        Assert.Equal(DuplicateVerdict.KeepBoth, DuplicateJudge.Decide("sorry can I add something about the budget", "the release date is next friday", strong, strong));
    }

    [Fact]
    public void WavEncoderWritesValidHeader()
    {
        var wav = WavEncoder.Encode(new short[] { 1, -1, 300 });
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(PipelineAudio.SampleRate, BitConverter.ToInt32(wav, 24));
        Assert.Equal(6, BitConverter.ToInt32(wav, 40));
        Assert.Equal(50, wav.Length);
    }

    [Fact]
    public void SessionClockMapsQpcToSessionTime()
    {
        var time = Clock.Fake();
        var clock = new SessionClock(time);
        long origin = clock.Now100ns();
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(5), clock.Now);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.FromQpc100ns(origin + TimeSpan.FromSeconds(2).Ticks));
    }
}
