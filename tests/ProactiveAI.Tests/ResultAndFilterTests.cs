using ProactiveAI.Core.Assistant;
using ProactiveAI.Core.Session;
using ProactiveAI.Tests.Fakes;

namespace ProactiveAI.Tests;

public class ResultAndFilterTests
{
    private static ValidationContext Ctx(string? q = "q-1", int? rev = 1, string[]? transcript = null, string[]? prep = null) =>
        new(q, rev, (transcript ?? ["seg-1", "seg-2"]).ToHashSet(), (prep ?? ["prep-results-1-abc"]).ToHashSet(), true);

    [Fact]
    public void ValidAnswerParsesAndValidates()
    {
        var parsed = AssistantResultParser.Parse(Json.Answer("q-1", 1, ["seg-1"], prep: ["prep-results-1-abc"]));
        Assert.True(parsed.Ok, string.Join(";", parsed.Errors));
        Assert.True(AssistantResultValidator.Validate(parsed.Result!, Ctx()).Valid);
    }

    [Fact]
    public void MalformedOrWrongTypedOutputIsRejected()
    {
        Assert.False(AssistantResultParser.Parse("{\"response_kind\":\"answer\"").Ok);
        Assert.False(AssistantResultParser.Parse(Json.Build("essay", true)).Ok);
        Assert.False(AssistantResultParser.Parse("{\"response_kind\":\"none\",\"should_notify\":\"yes\"}").Ok);
    }

    [Fact]
    public void EvidenceIsCheckedSeparatelyForTranscriptAndPreparation()
    {
        // A preparation id cited as transcript evidence (and vice versa) fails.
        var r1 = AssistantResultParser.Parse(Json.Answer("q-1", 1, ["prep-results-1-abc"])).Result!;
        Assert.False(AssistantResultValidator.Validate(r1, Ctx()).Valid);
        var r2 = AssistantResultParser.Parse(Json.Answer("q-1", 1, ["seg-1"], prep: ["seg-2"])).Result!;
        Assert.False(AssistantResultValidator.Validate(r2, Ctx()).Valid);
        var r3 = AssistantResultParser.Parse(Json.Answer("q-1", 1, ["seg-99"])).Result!;
        Assert.Contains(AssistantResultValidator.Validate(r3, Ctx()).Errors, e => e.Contains("seg-99"));
    }

    [Fact]
    public void RevisionMismatchAndNotifyingNoneAreRejected()
    {
        var stale = AssistantResultParser.Parse(Json.Answer("q-1", 1, ["seg-1"])).Result!;
        Assert.False(AssistantResultValidator.Validate(stale, Ctx(rev: 2)).Valid);
        var none = AssistantResultParser.Parse(Json.Build("none", true)).Result!;
        Assert.False(AssistantResultValidator.Validate(none, Ctx()).Valid);
        var insight = AssistantResultParser.Parse(Json.Build("insight", true, title: "T", suggestion: "S", category: "risk")).Result!;
        Assert.Contains(AssistantResultValidator.Validate(insight, Ctx()).Errors, e => e.Contains("evidence"));
    }

    [Fact]
    public void LengthLimitsAreEnforced()
    {
        var r = AssistantResultParser.Parse(Json.Answer("q-1", 1, ["seg-1"], spoken: new string('a', ResultLimits.SpokenAnswer + 1))).Result!;
        Assert.Contains(AssistantResultValidator.Validate(r, Ctx()).Errors, e => e.Contains("spoken_answer"));
    }

    [Fact]
    public void PartialReaderExtractsIncompleteStringsWithEscapes()
    {
        string json = "{\"response_kind\":\"answer\",\"should_notify\":true,\"question_id\":\"q-1\",\"question_revision\":1,\"title\":null," +
                      "\"direct_position\":\"Use \\\"chaining\\\".\",\"spoken_answer\":\"First paragraph.\\n\\nSecond par";
        var view = PartialJsonReader.Parse(json);
        Assert.True(view.Strings["direct_position"].Complete);
        Assert.Equal("Use \"chaining\".", view.Strings["direct_position"].Value);
        Assert.False(view.Strings["spoken_answer"].Complete);
        Assert.Equal("First paragraph.\n\nSecond par", view.Strings["spoken_answer"].Value);

        var arrays = PartialJsonReader.Parse("{\"supporting_points\":[\"a\",\"b\",\"c");
        Assert.Equal(["a", "b"], arrays.StringArrays["supporting_points"]);
    }

    [Fact]
    public void InsightFilterEnforcesCooldownRateDuplicatesAndDismissals()
    {
        var time = Clock.Fake();
        var f = new InsightFilter(time, new InsightFilterOptions { Cooldown = TimeSpan.FromSeconds(10), MaxPerMinute = 2 });
        Assert.Equal(FilterDecision.Show, f.Evaluate("Clarify ordering", "Global or per-key ordering is unresolved", null));
        f.RecordShown("Clarify ordering", "Global or per-key ordering is unresolved", null);
        Assert.Equal(FilterDecision.Duplicate, f.Evaluate("Clarify the ordering", "Global or per key ordering is still unresolved", null));
        Assert.Equal(FilterDecision.Cooldown, f.Evaluate("Rollback plan", "No rollback plan agreed", null));
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(FilterDecision.Show, f.Evaluate("Rollback plan", "No rollback plan agreed", null));
        f.RecordShown("Rollback plan", "No rollback plan agreed", null);
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(FilterDecision.RateLimited, f.Evaluate("Budget owner", "The budget owner was not named", null));
        f.RecordDismissed("Budget owner", "The budget owner was not named", null);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(FilterDecision.PreviouslyDismissed, f.Evaluate("Budget owner", "The budget owner was not named", null));
    }

    [Fact]
    public void DistinctFollowUpOnSameTopicIsNotTreatedAsDuplicate()
    {
        var f = new InsightFilter(Clock.Fake());
        f.RecordShown("Clarify Kafka ordering", "Is ordering needed globally or per key?", null);
        Assert.NotEqual(FilterDecision.Duplicate, f.Evaluate("Kafka retention", "How long must events be retained for replay?", null));
    }

    [Fact]
    public void RequestBudgetIsASlidingWindow()
    {
        var time = Clock.Fake();
        var b = new RequestBudget(time, 2);
        Assert.True(b.TryAcquire());
        Assert.True(b.TryAcquire());
        Assert.False(b.TryAcquire());
        Assert.True(b.NextAvailableIn > TimeSpan.Zero);
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.True(b.TryAcquire());
    }

    [Fact]
    public void BoardKeepsPinnedCardAsFocusAndBoundsHistory()
    {
        var board = new AssistantBoard(maxCards: 3);
        AssistantCard Card(string id) => new() { Id = id, Kind = CardKind.Answer, State = CardState.Complete, CreatedUtc = DateTimeOffset.UtcNow };
        board.Add(Card("a"));
        board.SetPinned("a", true);
        board.Add(Card("b"));
        Assert.Equal("a", board.FocusAnswer()!.Id);
        board.Add(Card("c"));
        board.Add(Card("d"));
        Assert.Equal(3, board.Cards.Count);
        Assert.Contains(board.Cards, c => c.Id == "a"); // pinned survives trimming
        board.SetPinned("a", false);
        Assert.Equal("d", board.FocusAnswer()!.Id);
    }
}
