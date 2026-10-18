using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Questions;
using ProactiveAI.Core.Transcript;
using ProactiveAI.Tests.Fakes;

namespace ProactiveAI.Tests;

public class EarlyQuestionTests
{
    private static readonly TurnKey Key1 = new(1, AudioSourceKind.System, 1, 1);

    private static QuestionTurnAssembler Assembler(Microsoft.Extensions.Time.Testing.FakeTimeProvider time) =>
        new(time, s => s.Source == AudioSourceKind.Microphone ? Party.Self : Party.Other);

    private static TranscriptSegment Provisional(TranscriptStore store, TurnKey key, string text, double start = 0, double end = 3) =>
        store.ApplyDelta(key, text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), AudioAttributionDefault.Pending)!;

    private static TranscriptSegment Final(TranscriptStore store, TurnKey key, string text, double start = 0, double end = 3.7) =>
        store.ApplyFinal(key, text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), AudioAttributionDefault.Pending, "item")!;

    [Fact]
    public void ProvisionalQuestionIsEmittedImmediatelyAndConfirmedByAnEqualFinal()
    {
        var time = Clock.Fake();
        var store = new TranscriptStore(time);
        var asm = Assembler(time);
        var p = Provisional(store, Key1, "Can you explain how a B-tree index works?");
        var c = Assert.Single(asm.OnEarlyQuestion(p));
        Assert.Equal([p.Id], c.SegmentIds);
        Assert.Empty(asm.OnEarlyQuestion(p)); // once per turn

        asm.OnFinalSegment(Final(store, Key1, "Can you explain how a B-tree index works ?"));
        time.Advance(TimeSpan.FromSeconds(1.1));
        Assert.Empty(asm.Tick()); // same words: the running answer stands
    }

    [Fact]
    public void FinalThatDiffersRegeneratesEvenForANonMaterialAddition()
    {
        var time = Clock.Fake();
        var store = new TranscriptStore(time);
        var asm = Assembler(time);
        var c = Assert.Single(asm.OnEarlyQuestion(Provisional(store, Key1, "Can you explain how caching works?")));
        // The speaker continued after the pause; "for example in a web app" is not a material addition by the rules.
        const string full = "Can you explain how caching works? For example in a web app.";
        asm.OnFinalSegment(Final(store, Key1, full));
        Assert.Empty(asm.Tick()); // debounce
        time.Advance(TimeSpan.FromSeconds(1.1));
        var rev = Assert.Single(asm.Tick());
        Assert.Equal(c.QuestionId, rev.QuestionId);
        Assert.Equal(2, rev.Revision);
        Assert.Equal(full, rev.Text);
    }

    [Theory]
    [InlineData("Can you explain how a B-tree index works")]          // no question mark yet (text still arriving)
    [InlineData("We shipped it last week, didn't we, right?")]      // rhetorical/tag
    [InlineData("He asked \"why not use Redis?\"")]                 // quoted
    public void NoEarlyCandidateWithoutAClearTerminalQuestion(string text)
    {
        var time = Clock.Fake();
        var store = new TranscriptStore(time);
        var asm = Assembler(time);
        Assert.Empty(asm.OnEarlyQuestion(Provisional(store, Key1, text)));
    }

    [Fact]
    public void UserSpeechAndFinalSegmentsAreNotEarlyCandidates()
    {
        var time = Clock.Fake();
        var store = new TranscriptStore(time);
        var asm = Assembler(time);
        var mic = Provisional(store, new TurnKey(1, AudioSourceKind.Microphone, 1, 2), "Could you repeat the question?");
        Assert.Empty(asm.OnEarlyQuestion(mic));
        var final = Final(store, Key1, "Can you explain caching?");
        Assert.Empty(asm.OnEarlyQuestion(final));
    }

    [Fact]
    public void TurnWithAQuestionAlreadyLeavesAdditionsToTheFinalPath()
    {
        var time = Clock.Fake();
        var store = new TranscriptStore(time);
        var asm = Assembler(time);
        asm.OnFinalSegment(Final(store, Key1, "Why did you choose PostgreSQL?", 0, 2));
        time.Advance(TimeSpan.FromSeconds(1.1));
        var q = Assert.Single(asm.Tick());
        var key2 = new TurnKey(1, AudioSourceKind.System, 1, 2);
        Assert.Empty(asm.OnEarlyQuestion(Provisional(store, key2, "And what would you change with one server?", 2.5, 5)));
        asm.OnFinalSegment(Final(store, key2, "And what would you change with one server?", 2.5, 5));
        time.Advance(TimeSpan.FromSeconds(1.1));
        var rev = Assert.Single(asm.Tick());
        Assert.Equal(q.QuestionId, rev.QuestionId);
    }

    [Fact]
    public void ContextSentenceBeforeTheQuestionIsIncluded()
    {
        var time = Clock.Fake();
        var store = new TranscriptStore(time);
        var asm = Assembler(time);
        var context = Final(store, Key1, "Let's talk about your payment service.", 0, 2);
        asm.OnFinalSegment(context);
        time.Advance(TimeSpan.FromSeconds(1.1));
        Assert.Empty(asm.Tick());
        var key2 = new TurnKey(1, AudioSourceKind.System, 1, 2);
        var c = Assert.Single(asm.OnEarlyQuestion(Provisional(store, key2, "How did you make retries idempotent?", 2.5, 5)));
        Assert.Equal(2, c.SegmentIds.Count);
        Assert.StartsWith("Let's talk about", c.Text);
    }
}
