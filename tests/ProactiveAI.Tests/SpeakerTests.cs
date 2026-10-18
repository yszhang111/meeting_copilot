using Microsoft.Extensions.Logging.Abstractions;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Diagnostics;
using ProactiveAI.Core.Session;
using ProactiveAI.Core.Speakers;
using ProactiveAI.Core.Transcript;
using ProactiveAI.Tests.Fakes;

namespace ProactiveAI.Tests;

public class SpeakerTests
{
    [Fact]
    public void ReferenceClipMatchMapsToTheSameSessionSpeaker()
    {
        var spans = new[] { new WindowSpan("spk-2", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(14), "x"), new WindowSpan("A", TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(18), "y") };
        var map = SpeakerReconciler.Reconcile(spans, new Dictionary<string, string> { ["spk-2"] = "spk-2" }, []);
        Assert.Equal(MappingEvidence.ReferenceClip, map.Single(m => m.RawLabel == "spk-2").Evidence);
        Assert.Equal("spk-2", map.Single(m => m.RawLabel == "spk-2").SpeakerId);
        Assert.Equal(MappingEvidence.New, map.Single(m => m.RawLabel == "A").Evidence);
    }

    [Fact]
    public void RequestLocalLabelsAreNotAssumedToBeTheSamePersonAcrossRequests()
    {
        // Earlier window labeled spk-1 at 0-5 s; this request's "A" speaks at 30 s with no overlap and no reference.
        var previous = new[] { new AttributedSpan("spk-1", TimeSpan.Zero, TimeSpan.FromSeconds(5)) };
        var spans = new[] { new WindowSpan("A", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(33), "hello") };
        var map = SpeakerReconciler.Reconcile(spans, new Dictionary<string, string>(), previous);
        Assert.Null(map.Single().SpeakerId);
    }

    [Fact]
    public void OverlapAgreementMapsLabelAndResolvesConflicts()
    {
        var previous = new[]
        {
            new AttributedSpan("spk-1", TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(18)),
            new AttributedSpan("spk-2", TimeSpan.FromSeconds(18.2), TimeSpan.FromSeconds(20)),
        };
        var spans = new[]
        {
            new WindowSpan("A", TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(18), "a"),
            new WindowSpan("B", TimeSpan.FromSeconds(18.2), TimeSpan.FromSeconds(20), "b"),
            new WindowSpan("C", TimeSpan.FromSeconds(15.5), TimeSpan.FromSeconds(16.0), "c"), // too little evidence
        };
        var map = SpeakerReconciler.Reconcile(spans, new Dictionary<string, string>(), previous).ToDictionary(m => m.RawLabel);
        Assert.Equal("spk-1", map["A"].SpeakerId);
        Assert.Equal("spk-2", map["B"].SpeakerId);
        Assert.Null(map["C"].SpeakerId);
    }

    [Fact]
    public void ResolverAssignsDominantMarksMultipleAndWaitsForCoverage()
    {
        var spans = new[]
        {
            new AttributedSpan("s1", TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(4)),
            new AttributedSpan("s2", TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6)),
        };
        var dominant = AttributionResolver.Resolve(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3.8), spans, TimeSpan.FromSeconds(10));
        Assert.Equal(AttributionStatus.Assigned, dominant.Attribution!.Status);
        Assert.Equal("s1", dominant.Attribution.SpeakerId);

        var mixed = AttributionResolver.Resolve(TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(5.5), spans, TimeSpan.FromSeconds(10));
        Assert.Equal(AttributionStatus.Multiple, mixed.Attribution!.Status);

        var notYet = AttributionResolver.Resolve(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(9), spans, TimeSpan.FromSeconds(6));
        Assert.Null(notYet.Attribution);

        var unknown = AttributionResolver.Resolve(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(9), spans, TimeSpan.FromSeconds(12));
        Assert.Equal(AttributionStatus.Unknown, unknown.Attribution!.Status);
    }

    [Fact]
    public void ReferenceClipsAreBoundedByCountAndBytes()
    {
        var store = new ReferenceClipStore(maxClips: 2, maxBytes: 12 * PipelineAudio.BytesPerSecond);
        Assert.False(store.Offer("a", new short[PipelineAudio.Samples(TimeSpan.FromSeconds(1))], TimeSpan.Zero)); // too short
        Assert.True(store.Offer("a", Signals.Speech(TimeSpan.FromSeconds(4), 1), TimeSpan.FromSeconds(1)));
        Assert.True(store.Offer("b", Signals.Speech(TimeSpan.FromSeconds(4), 2), TimeSpan.FromSeconds(2)));
        Assert.True(store.Offer("c", Signals.Speech(TimeSpan.FromSeconds(12), 3), TimeSpan.FromSeconds(3))); // capped at 8 s
        Assert.Equal(2, store.Count);
        Assert.False(store.Has("a"));
        Assert.True(store.Bytes <= 12 * PipelineAudio.BytesPerSecond);
        store.Clear();
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task DiarizationEngineKeepsLabelsAcrossWindowsAndRespectsUserCorrections()
    {
        var time = TimeProvider.System;
        var clock = new SessionClock(time, TimeSpan.FromSeconds(30)); // the 18 s of fixture audio ended well before "now"
        var timeline = new AudioTimeline(TimeSpan.FromSeconds(60), 60 * PipelineAudio.BytesPerSecond);
        var transcript = new TranscriptStore(time);
        var registry = new SpeakerRegistry();
        var refs = new ReferenceClipStore();
        var diarizer = new FakeDiarizer();
        var audio = Signals.Speech(TimeSpan.FromSeconds(18), 5, modulationHz: 0.3);
        // Ground truth: speaker X 0-6 s, Y 6-12 s, X again 12-18 s (returning speaker).
        string TruthAt(double t) => t < 6 ? "X" : t < 12 ? "Y" : "X";
        // Position of a PCM slice within the fixture audio, so the fake provider needs no shared state with the test.
        double Locate(short[] pcm)
        {
            int probe = Math.Min(64, pcm.Length);
            for (int i = 0; i + probe <= audio.Length; i++)
            {
                if (audio.AsSpan(i, probe).SequenceEqual(pcm.AsSpan(0, probe)))
                {
                    return i / (double)PipelineAudio.SampleRate;
                }
            }
            throw new InvalidOperationException("slice not found in fixture audio");
        }
        diarizer.Respond = req =>
        {
            // Simulates a provider that labels each request with fresh letters but honours known references by name.
            var spans = new List<DiarizedSpan>();
            double len = req.Pcm.Length / (double)PipelineAudio.SampleRate;
            double start = Locate(req.Pcm);
            var byTruth = new Dictionary<string, string>();
            int letter = 0;
            foreach (var r in req.References)
            {
                byTruth[TruthAt(Locate(r.Pcm))] = r.Name;
            }
            for (double t = 0; t < len; t += 3)
            {
                string truth = TruthAt(start + t);
                if (!byTruth.TryGetValue(truth, out var label))
                {
                    label = ((char)('A' + letter++)).ToString();
                    byTruth[truth] = label;
                }
                spans.Add(new DiarizedSpan(label, t, Math.Min(len, t + 3), "x"));
            }
            return new DiarizationResult(spans, null);
        };
        var options = new DiarizationOptions { Window = TimeSpan.FromSeconds(9), Overlap = TimeSpan.FromSeconds(3), MinWindow = TimeSpan.FromSeconds(3) };
        var engine = new DiarizationEngine(AudioSourceKind.System, 1, 1, timeline, diarizer, "m", [], registry, refs, transcript,
            new RequestBudget(time, 100), options, clock, NullLogger.Instance, () => true);

        // Feed 18 s of speech-like audio and segments.
        timeline.Append(TimeSpan.Zero, audio);
        engine.NoteTurn(TimeSpan.Zero, TimeSpan.FromSeconds(18), stillOpen: false);
        for (int i = 0; i < 6; i++)
        {
            transcript.ApplyFinal(new TurnKey(1, AudioSourceKind.System, 1, i), $"line {i}", TimeSpan.FromSeconds(i * 3 + 0.2), TimeSpan.FromSeconds(i * 3 + 2.8), AudioAttributionDefault.Pending, $"item{i}");
        }
        engine.Tick(); // later windows are chained by the engine itself
        await Wait.UntilAsync(() => transcript.Snapshot().All(s => s.Attribution.Status != AttributionStatus.Pending), because: "all labeled");

        var all = transcript.Snapshot();
        var first = all[0].Attribution.SpeakerId;
        var last = all[^1].Attribution.SpeakerId;
        Assert.Equal(first, last); // returning speaker keeps the same label across windows
        Assert.NotEqual(first, all[2].Attribution.SpeakerId);
        Assert.True(diarizer.Requests.Skip(1).Any(r => r.References.Count > 0), "later windows send reference clips");

        // A user correction is not overridden by later diarization.
        transcript.UpdateAttribution(all[0].Id, _ => new SpeakerAttribution(AttributionStatus.UserAssigned, "spk-x", [], 0, null));
        engine.OnSegmentFinalized(transcript.Get(all[0].Id)!);
        Assert.Equal(AttributionStatus.UserAssigned, transcript.Get(all[0].Id)!.Attribution.Status);

        await engine.StopAsync("stopped");
    }

    [Fact]
    public async Task StaleDiarizationResultAfterStopIsDiscardedAndPendingBecomesUnavailable()
    {
        var time = TimeProvider.System;
        var clock = new SessionClock(time);
        var timeline = new AudioTimeline(TimeSpan.FromSeconds(60), 60 * PipelineAudio.BytesPerSecond);
        var transcript = new TranscriptStore(time);
        var diarizer = new FakeDiarizer { Gate = new TaskCompletionSource(), Respond = _ => new DiarizationResult([new DiarizedSpan("A", 0, 5, "x")], null) };
        var engine = new DiarizationEngine(AudioSourceKind.System, 1, 1, timeline, diarizer, "m", [], new SpeakerRegistry(), new ReferenceClipStore(), transcript,
            new RequestBudget(time, 100), new DiarizationOptions { Window = TimeSpan.FromSeconds(6), Overlap = TimeSpan.FromSeconds(2) }, clock, NullLogger.Instance, () => true);
        timeline.Append(TimeSpan.Zero, Signals.Speech(TimeSpan.FromSeconds(6), 1));
        engine.NoteTurn(TimeSpan.Zero, TimeSpan.FromSeconds(6), false);
        var seg = transcript.ApplyFinal(new TurnKey(1, AudioSourceKind.System, 1, 1), "hello", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), AudioAttributionDefault.Pending, "i")!;
        engine.Tick();
        await Wait.UntilAsync(() => diarizer.Requests.Count == 1);
        await engine.StopAsync("paused");
        diarizer.Gate.TrySetResult();
        await Task.Delay(100);
        Assert.Equal(AttributionStatus.Unavailable, transcript.Get(seg.Id)!.Attribution.Status);
    }

    [Fact]
    public async Task DiarizationBudgetLimitIsReportedNotSilentlyDropped()
    {
        var time = TimeProvider.System;
        var budget = new RequestBudget(time, 1);
        Assert.True(budget.TryAcquire());
        var timeline = new AudioTimeline(TimeSpan.FromSeconds(60), 60 * PipelineAudio.BytesPerSecond);
        var engine = new DiarizationEngine(AudioSourceKind.System, 1, 1, timeline, new FakeDiarizer(), "m", [], new SpeakerRegistry(), new ReferenceClipStore(),
            new TranscriptStore(time), budget, new DiarizationOptions(), new SessionClock(time), NullLogger.Instance, () => true);
        timeline.Append(TimeSpan.Zero, Signals.Speech(TimeSpan.FromSeconds(20), 1));
        engine.NoteTurn(TimeSpan.Zero, TimeSpan.FromSeconds(20), false);
        engine.Tick();
        Assert.Equal(DiarizationHealth.Limited, engine.Status.Health);
        await engine.StopAsync("x");
    }

    [Fact]
    public void ScorerCountsUnknownAsMissAndDetectsIdentitySwitches()
    {
        var truth = new[]
        {
            new GroundTruthTurn("X", TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(4), "a"),
            new GroundTruthTurn("Y", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9), "b"),
            new GroundTruthTurn("X", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(14), "c"),
        };
        var store = new TranscriptStore(Clock.Fake());
        TranscriptSegment Seg(int i, double s, double e, string? speaker, AttributionStatus st)
        {
            var seg = store.ApplyFinal(new TurnKey(1, AudioSourceKind.System, 1, i), "t", TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(e), AudioAttributionDefault.Pending, null)!;
            return store.UpdateAttribution(seg.Id, _ => new SpeakerAttribution(st, speaker, [], 0, null)) ?? seg;
        }
        Seg(1, 0, 4, "s1", AttributionStatus.Assigned);
        Seg(2, 5, 9, null, AttributionStatus.Unknown);
        Seg(3, 10, 14, "s3", AttributionStatus.Assigned);
        var score = AttributionScorer.Score(truth, store.Snapshot());
        Assert.Equal(1 / 3.0, score.Accuracy, 2);
        Assert.Equal(2 / 3.0, score.Coverage, 2);
        Assert.Equal(1, score.IdentitySwitches);
    }
}
