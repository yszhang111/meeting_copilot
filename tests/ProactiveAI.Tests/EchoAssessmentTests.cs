using ProactiveAI.Core.Audio;
using ProactiveAI.Tests.Fakes;

namespace ProactiveAI.Tests;

/// <summary>Acoustic echo assessment: microphone interval explained by the playback signal.</summary>
public class EchoAssessmentTests
{
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(500);

    /// <summary>Playback aligned so that index 0 of the microphone equals Lead into the playback array.</summary>
    private static EchoAssessment Assess(short[] playback, short[] mic) =>
        EchoAnalyzer.Assess(mic, Signals.Concat(Signals.Silence(Lead), playback), Lead);

    [Theory]
    [InlineData(1, 0.12, 80)]
    [InlineData(2, 0.05, 40)]
    [InlineData(3, 0.25, 150)]
    [InlineData(4, 0.12, 250)]
    public void LoudspeakerEchoOnlyIsRecognizedDespiteVariableAec(int seed, double gain, int delayMs)
    {
        var playback = Signals.Speech(TimeSpan.FromSeconds(4), seed);
        var mic = Signals.RoomEcho(playback, seed + 10, gain, delayMs);
        var a = Assess(playback, mic);
        Assert.True(a.IsEcho, $"corr={a.Correlation:F2} lag={a.Lag.TotalMilliseconds} unexplained={a.UnexplainedFraction:P0} active={a.MicActiveHops}");
        Assert.InRange(a.Lag.TotalMilliseconds, delayMs - 20, delayMs + 60);
    }

    [Fact]
    public void DoubleTalkKeepsLocalSpeech()
    {
        var total = TimeSpan.FromSeconds(4);
        var playback = Signals.Speech(total, 21);
        var local = Signals.Place(Signals.Scale(Signals.Speech(TimeSpan.FromSeconds(2), 77), 0.6), TimeSpan.FromSeconds(1), total);
        var mic = Signals.Mix(Signals.RoomEcho(playback, 22), local);
        var a = Assess(playback, mic);
        Assert.False(a.IsEcho, $"corr={a.Correlation:F2} unexplained={a.UnexplainedFraction:P0}");
    }

    [Fact]
    public void QuieterDoubleTalkIsStillKept()
    {
        var total = TimeSpan.FromSeconds(4);
        var playback = Signals.Speech(total, 31);
        var local = Signals.Place(Signals.Scale(Signals.Speech(TimeSpan.FromSeconds(1.5), 78), 0.3), TimeSpan.FromSeconds(2), total);
        var mic = Signals.Mix(Signals.RoomEcho(playback, 32), local);
        var a = Assess(playback, mic);
        Assert.False(a.IsEcho, $"corr={a.Correlation:F2} unexplained={a.UnexplainedFraction:P0}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void ShortInterjectionInsideLongEchoIsKept(int seed)
    {
        // The user says ~0.8 s while 12 s of remote speech plays from the loudspeaker; the microphone turn
        // contains both. Hiding it would discard what the user said.
        var total = TimeSpan.FromSeconds(12);
        var playback = Signals.Speech(total, seed + 100);
        var interjection = Signals.Place(Signals.Scale(Signals.Speech(TimeSpan.FromSeconds(0.8), seed + 70), 0.3), TimeSpan.FromSeconds(6), total);
        var a = Assess(playback, Signals.Mix(Signals.RoomEcho(playback, seed + 110), interjection));
        Assert.False(a.IsEcho, $"corr={a.Correlation:F2} burst={a.LocalBurstHops * 10}ms unexplained={a.UnexplainedFraction:P0}");
        Assert.True(Assess(playback, Signals.RoomEcho(playback, seed + 110)).IsEcho, "the same echo without the interjection is hidden");
    }

    [Fact]
    public void LocalSpeechBetweenPlaybackPhrasesIsKept()
    {
        var total = TimeSpan.FromSeconds(4);
        var playback = Signals.Place(Signals.Speech(TimeSpan.FromSeconds(2), 41), TimeSpan.Zero, total);
        var local = Signals.Place(Signals.Scale(Signals.Speech(TimeSpan.FromSeconds(1.3), 79), 0.4), TimeSpan.FromSeconds(2.5), total);
        var mic = Signals.Mix(Signals.RoomEcho(playback, 42), local);
        Assert.False(Assess(playback, mic).IsEcho);
    }

    [Fact]
    public void LocalSpeechWithoutPlaybackIsKept()
    {
        var total = TimeSpan.FromSeconds(3);
        var mic = Signals.Speech(total, 51);
        Assert.False(Assess(Signals.Silence(total), mic).IsEcho);
        Assert.False(Assess(Signals.Speech(total, 52), mic).IsEcho); // unrelated playback at the same time
    }
}
