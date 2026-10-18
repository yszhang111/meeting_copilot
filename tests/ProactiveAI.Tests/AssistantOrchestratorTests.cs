using Microsoft.Extensions.Logging.Abstractions;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Assistant;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Context;
using ProactiveAI.Core.Questions;
using ProactiveAI.Core.Settings;
using ProactiveAI.Core.Speakers;
using ProactiveAI.Core.Transcript;
using ProactiveAI.Tests.Fakes;

namespace ProactiveAI.Tests;

public class AssistantOrchestratorTests
{
    private sealed class Harness
    {
        public TimeProvider Time = TimeProvider.System;
        public TranscriptStore Store;
        public AssistantBoard Board = new();
        public QuestionHistory History = new();
        public InsightFilter Filter;
        public FakeAssistantModel Model = new();
        public AssistantOrchestrator Orchestrator;
        public SessionClock Clock;
        private int _turn;

        public Harness(AppSettings? settings = null)
        {
            Store = new TranscriptStore(Time);
            Filter = new InsightFilter(Time);
            Clock = new SessionClock(Time, TimeSpan.FromSeconds(100));
            var prompts = new PromptBuilder(Store, new SpeakerRegistry(), History, new PreparationNotes(), Filter, new CharTokenCounter(), Clock,
                s => s.Source == AudioSourceKind.Microphone ? Party.Self : Party.Other);
            Orchestrator = new AssistantOrchestrator(Model, prompts, Board, Filter, History, Store, Clock, settings ?? new AppSettings(), NullLogger.Instance);
            Orchestrator.BeginSession(1);
        }

        public TranscriptSegment Say(string text, AudioSourceKind src = AudioSourceKind.System)
        {
            _turn++;
            return Store.ApplyFinal(new TurnKey(1, src, 1, _turn), text, TimeSpan.FromSeconds(_turn * 3), TimeSpan.FromSeconds(_turn * 3 + 2), AudioAttributionDefault.Pending, null)!;
        }

        public QuestionCandidate Question(string id, int rev, params TranscriptSegment[] segs) =>
            new(id, rev, string.Join(" ", segs.Select(s => s.Text)), segs.Select(s => s.Id).ToList(), AudioSourceKind.System, null, true, false, null, false,
                Clock.Now - TimeSpan.FromSeconds(1), Time.GetUtcNow());

        public AssistantCard Card(string questionId) => Board.Cards.Last(c => c.QuestionId == questionId);
    }

    [Fact]
    public async Task StreamsPartialTextThenCompletesWithValidatedAnswer()
    {
        var h = new Harness();
        var seg = h.Say("How does a hash map handle collisions?");
        h.Model.Respond = _ => Json.Answer("q-1", 1, [seg.Id], spoken: new string('x', 300));
        h.Model.Gate = new TaskCompletionSource();
        h.Model.ChunkSize = 20;
        h.Orchestrator.SubmitQuestion(h.Question("q-1", 1, seg));
        await Wait.UntilAsync(() => h.Card("q-1").DirectPosition is not null, because: "partial direct position");
        Assert.Equal(CardState.Generating, h.Card("q-1").State);
        Assert.True(h.Card("q-1").IsDraft);
        h.Model.Gate.SetResult();
        await Wait.UntilAsync(() => h.Card("q-1").State == CardState.Complete);
        var card = h.Card("q-1");
        Assert.Equal(300, card.SpokenAnswer!.Length);
        Assert.NotNull(card.FirstUsefulTextLatency);
        Assert.NotNull(card.CompleteLatency);
        Assert.Equal("Direct position answering the question.", h.History.Find("q-1")!.DraftSummary);
        Assert.Null(h.History.Find("q-1")!.UserResponse); // the draft is not promoted to what the user said
    }

    [Fact]
    public async Task DetailsTheModelFilledInAreShownWithTheAnswer()
    {
        var h = new Harness();
        var s1 = h.Say("How did you measure the latency improvement?");
        h.Model.ChunkSize = 15;
        h.Model.Respond = _ => Json.Answer("q-1", 1, [s1.Id], filledIn: ["Replayed one week of production traffic", "Compared p99 before and after"]);
        h.Orchestrator.SubmitQuestion(h.Question("q-1", 1, s1));
        await Wait.UntilAsync(() => h.Card("q-1").State == CardState.Complete);
        Assert.Equal(["Replayed one week of production traffic", "Compared p99 before and after"], h.Card("q-1").FilledInDetails);
    }

    [Fact]
    public async Task FollowUpLinkFromTheAnswerIsShownAsAChainInLaterRequests()
    {
        var h = new Harness();
        var s1 = h.Say("Tell me about your caching project.");
        h.Model.Respond = _ => Json.Answer("q-1", 1, [s1.Id]);
        h.Orchestrator.SubmitQuestion(h.Question("q-1", 1, s1) with { EndedAt = s1.AudioEnd });
        await Wait.UntilAsync(() => h.Card("q-1").State == CardState.Complete);
        var mine = h.Say("We cut p99 latency from 800 ms to 120 ms with Redis.", AudioSourceKind.Microphone);

        var s2 = h.Say("Why Redis and not a local cache?");
        h.Model.Respond = _ => Json.Answer("q-2", 1, [s2.Id], followsUpOn: "q-1");
        h.Orchestrator.SubmitQuestion(h.Question("q-2", 1, s2) with { EndedAt = s2.AudioEnd });
        await Wait.UntilAsync(() => h.Card("q-2").State == CardState.Complete);
        Assert.Equal("q-1", h.History.Find("q-2")!.FollowsUpOn);
        Assert.Equal([mine.Id], h.History.Find("q-1")!.UserResponse!.SegmentIds); // pinned when the next question arrived

        var s3 = h.Say("And how do you keep it consistent?");
        h.Model.Respond = _ => Json.Answer("q-3", 1, [s3.Id], followsUpOn: "q-9"); // unknown ids are ignored
        h.Orchestrator.SubmitQuestion(h.Question("q-3", 1, s3) with { EndedAt = s3.AudioEnd });
        await Wait.UntilAsync(() => h.Card("q-3").State == CardState.Complete);
        var request = h.Model.Requests[^1];
        Assert.Contains("Follow-up chain ending at the latest exchange: q-1 → q-2", request.Input);
        Assert.Contains("We cut p99 latency from 800 ms to 120 ms", request.Input);
        Assert.Null(h.History.Find("q-3")!.FollowsUpOn);
        Assert.Contains(request.Context!, b => b.Text.Contains("<preparation>"));
    }

    [Fact]
    public async Task FollowUpLinkStreamedBeforeAnInterruptionIsKept()
    {
        var h = new Harness();
        var s1 = h.Say("Explain your thesis method.");
        h.Model.Respond = _ => Json.Answer("q-1", 1, [s1.Id]);
        h.Orchestrator.SubmitQuestion(h.Question("q-1", 1, s1));
        await Wait.UntilAsync(() => h.Card("q-1").State == CardState.Complete);

        var s2 = h.Say("Why that baseline?");
        h.Model.Gate = new TaskCompletionSource();
        h.Model.ChunkSize = 10;
        h.Model.Respond = r => r.Input.Contains("question_id=\"q-3\"")
            ? Json.Answer("q-3", 1, [s2.Id])
            : Json.Answer("q-2", 1, [s2.Id], spoken: new string('x', 800), followsUpOn: "q-1");
        h.Orchestrator.SubmitQuestion(h.Question("q-2", 1, s2));
        await Wait.UntilAsync(() => h.History.Find("q-2")?.FollowsUpOn == "q-1", because: "link streams before the answer text");
        var s3 = h.Say("Different topic: what are your future plans?");
        h.Orchestrator.SubmitQuestion(h.Question("q-3", 1, s3));
        h.Model.Gate.SetResult();
        await Wait.UntilAsync(() => h.Card("q-3").State == CardState.Complete);
        Assert.Equal(CardState.Interrupted, h.Card("q-2").State);
        Assert.Equal("q-1", h.History.Find("q-2")!.FollowsUpOn);
        Assert.Same(PinnedExcerpt.NoReply, h.History.Find("q-2")!.UserResponse); // no reply captured: pinned as such
    }

    [Fact]
    public async Task NewQuestionPreemptsAndStaleStreamCannotOverwrite()
    {
        var h = new Harness();
        var s1 = h.Say("Explain B-trees?");
        var s2 = h.Say("Different question: how does TCP congestion control work?");
        h.Model.Gate = new TaskCompletionSource();
        string oldSpoken = "OLD " + new string('o', 600);
        h.Model.Respond = r => r.Input.Contains("q-2") ? Json.Answer("q-2", 1, [s2.Id]) : Json.Answer("q-1", 1, [s1.Id], spoken: oldSpoken);
        h.Orchestrator.SubmitQuestion(h.Question("q-1", 1, s1));
        await Wait.UntilAsync(() => h.Model.Requests.Count == 1);
        h.Orchestrator.SubmitQuestion(h.Question("q-2", 1, s2));
        h.Model.Gate.SetResult();
        await Wait.UntilAsync(() => h.Card("q-2").State == CardState.Complete);
        await Task.Delay(100);
        var old = h.Card("q-1");
        Assert.Equal(CardState.Interrupted, old.State); // partial text stays visible but marked incomplete
        Assert.True((old.SpokenAnswer?.Length ?? 0) < oldSpoken.Length, "stale stream must not complete the old card");
    }

    [Fact]
    public async Task RevisionSupersedesTheSameCardAndHaltRejectsLateResults()
    {
        var h = new Harness();
        var s1 = h.Say("Why PostgreSQL?");
        var s2 = h.Say("Assume a single server.");
        h.Model.Gate = new TaskCompletionSource();
        h.Model.Respond = r => r.Input.Contains("revision=\"2\"") ? Json.Answer("q-1", 2, [s1.Id, s2.Id], spoken: "REV2") : Json.Answer("q-1", 1, [s1.Id], spoken: "REV1");
        h.Orchestrator.SubmitQuestion(h.Question("q-1", 1, s1));
        await Wait.UntilAsync(() => h.Model.Requests.Count == 1);
        h.Orchestrator.SubmitQuestion(h.Question("q-1", 2, s1, s2));
        h.Model.Gate.SetResult();
        await Wait.UntilAsync(() => h.Card("q-1").State == CardState.Complete);
        Assert.Single(h.Board.Cards);
        Assert.Equal("REV2", h.Card("q-1").SpokenAnswer);

        // Pause/stop: an in-flight answer is interrupted and its late output never lands.
        var s3 = h.Say("Next: what is idempotency?");
        h.Model.Gate = new TaskCompletionSource();
        string late = "LATE " + new string('l', 600);
        h.Model.Respond = _ => Json.Answer("q-3", 1, [s3.Id], spoken: late);
        h.Orchestrator.SubmitQuestion(h.Question("q-3", 1, s3));
        await Wait.UntilAsync(() => h.Model.Requests.Count == 3);
        await h.Orchestrator.HaltAsync("Paused");
        h.Model.Gate.SetResult();
        await Task.Delay(150);
        Assert.Equal(CardState.Interrupted, h.Card("q-3").State);
        Assert.True((h.Card("q-3").SpokenAnswer?.Length ?? 0) < late.Length);
    }

    [Fact]
    public async Task BudgetLimitShowsLimitedStateAndRetriesCountAgainstBudget()
    {
        var h = new Harness(new AppSettings { Scenario = Scenario.Interview, InterviewMaxRequestsPerMinute = 2 });
        var s1 = h.Say("Question one?");
        h.Model.FailAttempt = n => n == 1 ? new ProviderException(ProviderFailureKind.ServerError, "server_error", "boom") : null;
        h.Model.Respond = r => Json.Answer("q-1", 1, [s1.Id]);
        h.Orchestrator.SubmitQuestion(h.Question("q-1", 1, s1));
        await Wait.UntilAsync(() => h.Card("q-1").State == CardState.Complete, because: "retry succeeds");
        Assert.Equal(2, h.Model.Requests.Count); // the retry consumed the second slot

        var s2 = h.Say("Question two?");
        h.Orchestrator.SubmitQuestion(h.Question("q-2", 1, s2));
        await Wait.UntilAsync(() => h.Card("q-2").State == CardState.Limited);
        Assert.Contains("budget", h.Card("q-2").StatusDetail);
        Assert.Equal(AssistantHealth.Limited, h.Orchestrator.Status.Health);
        Assert.Equal(2, h.Model.Requests.Count);
    }

    [Fact]
    public async Task AuthenticationFailureIsNotRetriedAndCaptionsAreUnaffected()
    {
        var h = new Harness();
        var s1 = h.Say("Question?");
        h.Model.FailAttempt = _ => new ProviderException(ProviderFailureKind.Authentication, "invalid_api_key", "bad key");
        h.Orchestrator.SubmitQuestion(h.Question("q-1", 1, s1));
        await Wait.UntilAsync(() => h.Card("q-1").State == CardState.Failed);
        Assert.Single(h.Model.Requests);
        Assert.Equal(AssistantHealth.Degraded, h.Orchestrator.Status.Health);
        h.Say("captions keep working");
        Assert.Equal(2, h.Store.Count);
    }

    [Fact]
    public async Task InvalidEvidenceMarksAnswerInvalidButKeepsPartialText()
    {
        var h = new Harness();
        var s1 = h.Say("Question?");
        h.Model.Respond = _ => Json.Answer("q-1", 1, ["seg-does-not-exist"], spoken: "Some text the user saw");
        h.Orchestrator.SubmitQuestion(h.Question("q-1", 1, s1));
        await Wait.UntilAsync(() => h.Card("q-1").State == CardState.Invalid);
        Assert.Equal("Some text the user saw", h.Card("q-1").SpokenAnswer);
        Assert.Contains("validation", h.Card("q-1").StatusDetail);
    }

    [Fact]
    public async Task RepeatedQuestionRedisplaysExistingAnswerWithoutNewRequest()
    {
        var h = new Harness();
        var s1 = h.Say("What is the limitation?");
        h.Model.Respond = _ => Json.Answer("q-1", 1, [s1.Id]);
        h.Orchestrator.SubmitQuestion(h.Question("q-1", 1, s1));
        await Wait.UntilAsync(() => h.Card("q-1").State == CardState.Complete);
        string? focused = null;
        h.Orchestrator.FocusRequested += id => focused = id;
        h.Orchestrator.SubmitQuestion(h.Question("q-2", 1, h.Say("What is the limitation again?")) with { RepeatOfQuestionId = "q-1" });
        Assert.Single(h.Model.Requests);
        Assert.Equal(h.Card("q-1").Id, focused);
        Assert.True(h.Card("q-1").AskedAgain);
    }

    [Fact]
    public async Task FollowUpWithinMeetingCooldownIsStillAnsweredInInterviewMode()
    {
        var h = new Harness(new AppSettings { Scenario = Scenario.Interview, MeetingCooldownSeconds = 300 });
        var s1 = h.Say("Why a transformer?");
        var s2 = h.Say("Why not a CNN then?");
        h.Model.Respond = r => r.Input.Contains("q-2") ? Json.Answer("q-2", 1, [s2.Id]) : Json.Answer("q-1", 1, [s1.Id]);
        h.Orchestrator.SubmitQuestion(h.Question("q-1", 1, s1));
        await Wait.UntilAsync(() => h.Card("q-1").State == CardState.Complete);
        h.Orchestrator.SubmitQuestion(h.Question("q-2", 1, s2));
        await Wait.UntilAsync(() => h.Card("q-2").State == CardState.Complete);
        Assert.Contains("Why a transformer?", h.Model.Requests[1].Input); // prior question carried as context
    }

    [Fact]
    public async Task MeetingReviewNeverDisplacesForegroundQuestionAndRespectsFilter()
    {
        var h = new Harness(new AppSettings { Scenario = Scenario.Meeting });
        var s1 = h.Say("Should we use Kafka or RabbitMQ?");
        h.Model.Gate = new TaskCompletionSource();
        h.Model.Respond = r => r.Input.Contains("q-1") ? Json.Answer("q-1", 1, [s1.Id]) : Json.Insight([s1.Id]);
        h.Orchestrator.SubmitQuestion(h.Question("q-1", 1, s1));
        await Wait.UntilAsync(() => h.Model.Requests.Count == 1);
        h.Orchestrator.SubmitMeetingReview([MeetingTrigger.Alternatives], null);
        h.Model.Gate.SetResult();
        await Wait.UntilAsync(() => h.Card("q-1").State == CardState.Complete);
        await Task.Delay(100);
        Assert.Single(h.Model.Requests);

        h.Model.Gate = null;
        h.Orchestrator.SubmitMeetingReview([MeetingTrigger.Alternatives], null);
        await Wait.UntilAsync(() => h.Board.Cards.Any(c => c.Kind == CardKind.Insight));
        h.Orchestrator.SubmitMeetingReview([MeetingTrigger.Alternatives], null);
        await Wait.UntilAsync(() => !h.Orchestrator.IsBusy);
        await Task.Delay(100);
        Assert.Single(h.Board.Cards, c => c.Kind == CardKind.Insight); // equivalent insight suppressed
    }
}
