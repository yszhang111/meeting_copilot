using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Transcript;
using ProactiveAI.Tests.Fakes;

namespace ProactiveAI.Tests;

public class TranscriptStoreTests
{
    private static TurnKey Key(int turn, AudioSourceKind src = AudioSourceKind.System, int gen = 1) => new(1, src, gen, turn);

    [Fact]
    public void PartialDeltasMergeIntoOneSegmentAndFinalCompletesIt()
    {
        var store = new TranscriptStore(Clock.Fake());
        store.ApplyDelta(Key(1), "Hello", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), AudioAttributionDefault.Pending);
        store.ApplyDelta(Key(1), " world", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), AudioAttributionDefault.Pending);
        var final = store.ApplyFinal(Key(1), "Hello, world.", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), AudioAttributionDefault.Pending, "item_1");

        var all = store.Snapshot();
        Assert.Single(all);
        Assert.Equal("Hello, world.", all[0].Text);
        Assert.True(all[0].IsFinal);
        Assert.Equal(final!.Id, all[0].Id);
        Assert.Equal("item_1", all[0].ProviderItemId);
    }

    [Fact]
    public void DuplicateCompletionAndLateDeltaAreIgnored()
    {
        var store = new TranscriptStore(Clock.Fake());
        var changes = new List<TranscriptChange>();
        store.Changed += changes.Add;
        store.ApplyFinal(Key(1), "Final text", TimeSpan.Zero, TimeSpan.FromSeconds(1), AudioAttributionDefault.Pending, "a");
        int before = changes.Count;
        store.ApplyFinal(Key(1), "Final text", TimeSpan.Zero, TimeSpan.FromSeconds(1), AudioAttributionDefault.Pending, "a");
        store.ApplyDelta(Key(1), " extra", TimeSpan.Zero, TimeSpan.FromSeconds(1), AudioAttributionDefault.Pending);
        Assert.Equal(before, changes.Count);
        Assert.Equal("Final text", store.Snapshot().Single().Text);
    }

    [Fact]
    public void FinalWithoutDeltasCreatesSegmentAndEmptyFinalRemovesProvisional()
    {
        var store = new TranscriptStore(Clock.Fake());
        store.ApplyFinal(Key(1), "Only final", TimeSpan.Zero, TimeSpan.FromSeconds(1), AudioAttributionDefault.Pending, "a");
        store.ApplyDelta(Key(2), "um", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), AudioAttributionDefault.Pending);
        var removed = store.ApplyFinal(Key(2), "   ", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), AudioAttributionDefault.Pending, "b");
        Assert.Null(removed);
        Assert.Single(store.Snapshot());
    }

    [Fact]
    public void OutOfOrderCompletionsAreOrderedByAudioTimeNotArrival()
    {
        var store = new TranscriptStore(Clock.Fake());
        store.ApplyFinal(Key(2), "second", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6), AudioAttributionDefault.Pending, "b");
        store.ApplyFinal(Key(1), "first", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), AudioAttributionDefault.Pending, "a");
        store.ApplyFinal(Key(3, AudioSourceKind.Microphone), "mic in between", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), AudioAttributionDefault.Pending, "c");
        Assert.Equal(["first", "mic in between", "second"], store.Snapshot().Select(s => s.Text));
    }

    [Fact]
    public void StoreIsBoundedAndReportsTrimmedCount()
    {
        var store = new TranscriptStore(Clock.Fake(), maxSegments: 5);
        for (int i = 1; i <= 8; i++)
        {
            store.ApplyFinal(Key(i), $"t{i}", TimeSpan.FromSeconds(i), TimeSpan.FromSeconds(i + 0.5), AudioAttributionDefault.Pending, $"i{i}");
        }
        Assert.Equal(5, store.Count);
        Assert.Equal(3, store.TrimmedCount);
        Assert.Equal("t4", store.Snapshot()[0].Text);
    }

    [Fact]
    public void AbandonProvisionalFinalizesOnlyTheGivenGeneration()
    {
        var store = new TranscriptStore(Clock.Fake());
        store.ApplyDelta(Key(1, gen: 1), "partial old", TimeSpan.Zero, TimeSpan.FromSeconds(1), AudioAttributionDefault.Pending);
        store.ApplyDelta(Key(2, gen: 2), "partial new", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), AudioAttributionDefault.Pending);
        store.AbandonProvisional(1, AudioSourceKind.System, 1, "Interrupted");
        var segs = store.Snapshot();
        Assert.True(segs[0].IsFinal);
        Assert.Equal("Interrupted", segs[0].Note);
        Assert.False(segs[1].IsFinal);
    }

    [Fact]
    public void AttributionRevisionsUpdateInPlaceWithVersion()
    {
        var store = new TranscriptStore(Clock.Fake());
        var seg = store.ApplyFinal(Key(1), "text", TimeSpan.Zero, TimeSpan.FromSeconds(1), AudioAttributionDefault.Pending, "a")!;
        var updated = store.UpdateAttribution(seg.Id, _ => new SpeakerAttribution(AttributionStatus.Assigned, "spk-1", [], 0, null));
        Assert.Equal(1, updated!.Attribution.Version);
        Assert.Single(store.Snapshot());
        Assert.Null(store.UpdateAttribution(seg.Id, _ => new SpeakerAttribution(AttributionStatus.Assigned, "spk-1", [], 1, null)));
    }
}
