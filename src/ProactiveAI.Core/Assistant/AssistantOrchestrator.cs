using Microsoft.Extensions.Logging;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Context;
using ProactiveAI.Core.Questions;
using ProactiveAI.Core.Session;
using ProactiveAI.Core.Settings;
using ProactiveAI.Core.Transcript;

namespace ProactiveAI.Core.Assistant;

public enum AssistantHealth
{
    Idle,
    Generating,
    Limited,
    Degraded,
}

public sealed record AssistantStatus(AssistantHealth Health, string? Detail, int RequestsLastMinute, int Budget, long TotalRequests, long InputTokens, long OutputTokens);

/// <summary>
/// Schedules reasoning requests. Invariants: at most one active request; at most one coalesced pending
/// job; foreground questions pre-empt optional meeting reviews; hard per-mode budgets (retries count);
/// streamed updates and completed results are applied only while the job is still current.
/// </summary>
public sealed class AssistantOrchestrator
{
    public const int MaxStreamChars = 60_000;
    public static readonly TimeSpan MeetingResultMaxAge = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly IAssistantModel _model;
    private readonly PromptBuilder _prompts;
    private readonly AssistantBoard _board;
    private readonly InsightFilter _filter;
    private readonly QuestionHistory _history;
    private readonly TranscriptStore _transcript;
    private readonly SessionClock _clock;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly RequestBudget _meetingBudget;
    private readonly RequestBudget _interviewBudget;
    private readonly Dictionary<string, QuestionCandidate> _candidates = new(StringComparer.Ordinal);
    private AppSettings _settings;
    private Job? _active;
    private CancellationTokenSource? _activeCts;
    private Task? _activeTask;
    private Job? _pending;
    private long _epoch;
    private long _sessionId;
    private AssistantHealth _health = AssistantHealth.Idle;
    private string? _healthDetail;
    private long _inputTokens;
    private long _outputTokens;

    public AssistantOrchestrator(IAssistantModel model, PromptBuilder prompts, AssistantBoard board, InsightFilter filter,
        QuestionHistory history, TranscriptStore transcript, SessionClock clock, AppSettings settings, ILogger log)
    {
        _model = model;
        _prompts = prompts;
        _board = board;
        _filter = filter;
        _history = history;
        _transcript = transcript;
        _clock = clock;
        _time = clock.TimeProvider;
        _log = log;
        _settings = settings;
        _meetingBudget = new RequestBudget(_time, settings.MeetingMaxRequestsPerMinute);
        _interviewBudget = new RequestBudget(_time, settings.InterviewMaxRequestsPerMinute);
    }

    public event Action<AssistantStatus>? StatusChanged;

    /// <summary>Raised when an existing card should be brought into view (e.g. a repeated question).</summary>
    public event Action<string>? FocusRequested;

    public AssistantStatus Status
    {
        get
        {
            lock (_gate)
            {
                return StatusLocked();
            }
        }
    }

    public bool IsBusy
    {
        get { lock (_gate) { return _active is not null; } }
    }

    public void UpdateSettings(AppSettings settings)
    {
        lock (_gate)
        {
            _settings = settings;
            _meetingBudget.MaxPerMinute = settings.MeetingMaxRequestsPerMinute;
            _interviewBudget.MaxPerMinute = settings.InterviewMaxRequestsPerMinute;
        }
    }

    public void BeginSession(long sessionId)
    {
        lock (_gate)
        {
            _sessionId = sessionId;
            _epoch++;
            _candidates.Clear();
            _healthDetail = null;
            _health = AssistantHealth.Idle;
        }
        RaiseStatus();
    }

    /// <summary>Invalidates in-flight and pending work (pause, stop, reset, mute of the asking source).</summary>
    public async Task HaltAsync(string reason)
    {
        Task? task;
        lock (_gate)
        {
            _epoch++;
            if (_pending is { Kind: JobKind.Question } p)
            {
                MarkInterrupted(p.CardId, reason);
            }
            _pending = null;
            if (_active is { } a)
            {
                if (a.Kind == JobKind.Question)
                {
                    MarkInterrupted(a.CardId, reason);
                }
                _activeCts?.Cancel();
            }
            task = _activeTask;
        }
        if (task is not null)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
                // the job observes cancellation; stale updates are rejected by epoch
            }
        }
        RaiseStatus();
    }

    public void SubmitQuestion(QuestionCandidate c)
    {
        lock (_gate)
        {
            if (c.RepeatOfQuestionId is { } repeatOf && _board.FindByQuestion(repeatOf) is { } existing
                && existing.State is CardState.Complete or CardState.Generating or CardState.Queued or CardState.Limited)
            {
                _board.Update(existing.Id, x => x with { AskedAgain = true, Dismissed = false, UpdatedUtc = _time.GetUtcNow() });
                FocusRequested?.Invoke(existing.Id);
                _log.LogInformation("Repeated question {Question} redisplays {Card}", c.QuestionId, existing.Id);
                return;
            }

            var now = _time.GetUtcNow();
            AssistantCard card;
            var sameQuestion = _board.FindByQuestion(c.QuestionId);
            if (sameQuestion is not null)
            {
                card = _board.Update(sameQuestion.Id, x => x with
                {
                    Revision = c.Revision,
                    QuestionText = c.Text,
                    QuestionSegmentIds = c.SegmentIds,
                    State = CardState.Queued,
                    StatusDetail = c.Revision > x.Revision ? "Question extended — regenerating" : null,
                    DirectPosition = null,
                    SpokenAnswer = null,
                    SupportingPoints = [],
                    AssumptionsAndLimits = [],
                    LikelyFollowups = [],
                    MissingFacts = [],
                    FilledInDetails = [],
                    UpdatedUtc = now,
                    QuestionDetectedUtc = c.DetectedAtUtc,
                    FirstUsefulTextLatency = null,
                    CompleteLatency = null,
                    FirstUsefulTextAt = null,
                    CompletedAt = null,
                }) ?? sameQuestion;
            }
            else
            {
                PinPreviousUserResponse();
                card = new AssistantCard
                {
                    Id = _board.NextId(),
                    Kind = CardKind.Answer,
                    State = CardState.Queued,
                    SessionId = _sessionId,
                    QuestionId = c.QuestionId,
                    Revision = c.Revision,
                    QuestionText = c.Text,
                    QuestionSegmentIds = c.SegmentIds,
                    Manual = c.Manual,
                    SpeakerUncertain = c.SpeakerUncertain,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                    QuestionDetectedUtc = c.DetectedAtUtc,
                    PromptVersion = PromptLibrary.Version,
                };
                _board.Add(card);
            }
            _candidates[card.Id] = c;
            _history.Upsert(new QuestionExchange(c.QuestionId, c.Revision, new PinnedExcerpt(c.SegmentIds, c.Text), null, null, c.EndedAt));

            var job = new Job(JobKind.Question, card.Id, _epoch, _sessionId, c, [], null, 0);
            if (_active is { } active)
            {
                if (active.Kind == JobKind.Question && active.CardId != card.Id)
                {
                    MarkInterrupted(active.CardId, "Superseded by a newer question");
                }
                _activeCts?.Cancel(); // foreground question takes precedence
            }
            if (_pending is { Kind: JobKind.Question } prev && prev.CardId != card.Id)
            {
                MarkInterrupted(prev.CardId, "Skipped: a newer question arrived");
            }
            _pending = job;
            TryDispatchLocked();
        }
        RaiseStatus();
    }

    public void SubmitMeetingReview(IReadOnlyList<MeetingTrigger> triggers, string? newSinceSegmentId)
    {
        lock (_gate)
        {
            if (_active is not null || _pending is { Kind: JobKind.Question })
            {
                return; // optional work never displaces foreground work
            }
            _pending = new Job(JobKind.MeetingReview, string.Empty, _epoch, _sessionId, null, triggers, newSinceSegmentId, 0);
            TryDispatchLocked();
        }
        RaiseStatus();
    }

    public void Regenerate(string cardId)
    {
        QuestionCandidate? c;
        lock (_gate)
        {
            _candidates.TryGetValue(cardId, out c);
        }
        if (c is null)
        {
            return;
        }
        _board.Update(cardId, x => x with { AskedAgain = false });
        SubmitQuestion(c with { RepeatOfQuestionId = null, DetectedAtUtc = _time.GetUtcNow() });
    }

    public void Cancel(string cardId)
    {
        lock (_gate)
        {
            if (_pending?.CardId == cardId)
            {
                _pending = null;
                MarkInterrupted(cardId, "Cancelled");
            }
            if (_active?.CardId == cardId)
            {
                MarkInterrupted(cardId, "Cancelled");
                _activeCts?.Cancel();
            }
        }
        RaiseStatus();
    }

    /// <summary>Retries a budget-limited pending job when budget frees up. Call periodically.</summary>
    public void Tick()
    {
        bool changed;
        lock (_gate)
        {
            changed = _pending is not null && _active is null;
            TryDispatchLocked();
        }
        if (changed)
        {
            RaiseStatus();
        }
    }

    private void TryDispatchLocked()
    {
        if (_active is not null || _pending is null)
        {
            return;
        }
        var job = _pending;
        if (job.Epoch != _epoch)
        {
            _pending = null;
            return;
        }
        var budget = _settings.Scenario == Scenario.Meeting ? _meetingBudget : _interviewBudget;
        if (!budget.TryAcquire())
        {
            if (job.Kind == JobKind.Question)
            {
                int wait = (int)Math.Ceiling(budget.NextAvailableIn.TotalSeconds);
                _board.Update(job.CardId, x => x.State == CardState.Limited && x.StatusDetail?.Contains($"{wait}s") == true ? null
                    : x with { State = CardState.Limited, StatusDetail = $"Request budget reached ({budget.MaxPerMinute}/min); waiting ~{wait}s" });
                _health = AssistantHealth.Limited;
                _healthDetail = "Request budget reached";
            }
            else
            {
                _pending = null; // optional meeting review is dropped, not queued
            }
            return;
        }
        _pending = null;
        _active = job;
        _activeCts = new CancellationTokenSource();
        _health = AssistantHealth.Generating;
        _healthDetail = null;
        var ct = _activeCts.Token;
        var settings = _settings;
        _activeTask = Task.Run(() => RunAsync(job, settings, ct));
    }

    private async Task RunAsync(Job job, AppSettings settings, CancellationToken ct)
    {
        bool isQuestion = job.Kind == JobKind.Question;
        string model = isQuestion ? settings.AnswerModel : settings.InsightModel;
        string effort = isQuestion ? settings.AnswerReasoningEffort : settings.InsightReasoningEffort;
        BuiltPrompt prompt;
        try
        {
            prompt = _prompts.Build(new PromptRequest(settings.Scenario, settings.AnswerDepth, settings.AnswerLanguage, job.Question, job.Triggers, job.NewSince));
        }
        catch (Exception ex)
        {
            _log.LogError("Prompt build failed: {Type}", ex.GetType().Name);
            Finish(job, failed: true, "Could not prepare the request");
            return;
        }
        if (isQuestion)
        {
            UpdateIfCurrent(job, c => c with { State = CardState.Generating, StatusDetail = prompt.Notes.Count > 0 ? string.Join("; ", prompt.Notes) : null, Model = model });
        }
        int maxTokens = isQuestion ? (settings.AnswerDepth == AnswerDepth.Detailed ? 6000 : 4000) : 1200;
        string? tier = isQuestion && settings.AnswerFastMode ? "fast" : null;
        var request = new AssistantModelRequest(model, prompt.Instructions, prompt.Input, PromptLibrary.SchemaName, prompt.SchemaJson,
            maxTokens, effort, isQuestion ? "medium" : "low", tier, prompt.Context);
        _log.LogInformation("Reasoning request kind={Kind} model={Model} tier={Tier} inputTokens~{Tokens} attempt={Attempt}", job.Kind, model, tier ?? "standard", prompt.InputTokens, job.Attempt);

        var reader = new PartialJsonReader();
        var started = _time.GetUtcNow();
        string? finalText = null;
        string? terminalProblem = null;
        bool linked = false;
        try
        {
            await foreach (var update in _model.StreamAsync(request, ct).ConfigureAwait(false))
            {
                if (!IsCurrent(job))
                {
                    _log.LogInformation("Discarding stale stream for {Card}", job.CardId);
                    return;
                }
                switch (update)
                {
                    case AssistantTextDelta d:
                        reader.Append(d.Delta);
                        if (reader.Length > MaxStreamChars)
                        {
                            terminalProblem = "Output exceeded the length limit and was stopped";
                            break;
                        }
                        if (isQuestion)
                        {
                            // Applied on every delta so a pause in the stream never hides text that already arrived;
                            // the UI coalesces updates per card.
                            var view = reader.Read();
                            ApplyPartial(job, view);
                            // The follow-up link streams early, so it is kept even if this answer is later interrupted.
                            if (!linked && view.Strings.TryGetValue("follows_up_on", out var link) && link.Complete)
                            {
                                linked = true;
                                LinkFollowUp(job, link.Value);
                            }
                        }
                        break;
                    case AssistantCompleted done:
                        finalText = done.Text.Length > 0 ? done.Text : reader.Text;
                        CountUsage(done.Usage);
                        if (tier is not null && done.ServiceTier is { } served && served != tier && served != "priority")
                        {
                            _log.LogInformation("Requested service tier {Requested} was served as {Served}", tier, served);
                        }
                        break;
                    case AssistantIncomplete inc:
                        CountUsage(inc.Usage);
                        terminalProblem = inc.Reason == "max_output_tokens" ? "Output reached the token limit before completing" : $"Response incomplete ({inc.Reason})";
                        break;
                    case AssistantRefused r:
                        terminalProblem = "The model declined to answer";
                        break;
                }
                if (terminalProblem is not null)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Finish(job, failed: false, null);
            return;
        }
        catch (ProviderException ex)
        {
            _log.LogWarning("Reasoning failed kind={Kind} code={Code}", ex.Kind, ex.Code);
            bool retry = isQuestion && job.Attempt == 0 && !ex.Kind.RequiresUserAction() && ex.Kind != ProviderFailureKind.Cancelled;
            if (retry)
            {
                var delay = ex.RetryAfter is { } ra && ra < TimeSpan.FromSeconds(10) ? ra : TimeSpan.FromSeconds(2);
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Finish(job, failed: false, null);
                    return;
                }
                lock (_gate)
                {
                    if (IsCurrentLocked(job) && _pending is null)
                    {
                        _pending = job with { Attempt = 1 };
                        UpdateIfCurrentLocked(job, c => c with { StatusDetail = "Retrying after a service error" });
                    }
                }
                Finish(job, failed: false, null, keepHealth: true);
                return;
            }
            lock (_gate)
            {
                _health = AssistantHealth.Degraded;
                _healthDetail = ex.Message;
            }
            if (isQuestion)
            {
                UpdateIfCurrent(job, c => c with { State = CardState.Failed, StatusDetail = ex.Message });
            }
            Finish(job, failed: true, null, keepHealth: true);
            return;
        }

        if (terminalProblem is not null || finalText is null)
        {
            if (isQuestion)
            {
                ApplyPartial(job, reader.Read());
                UpdateIfCurrent(job, c => c with { State = CardState.Invalid, StatusDetail = terminalProblem ?? "The response ended unexpectedly" });
            }
            Finish(job, failed: false, null);
            return;
        }

        ApplyFinal(job, prompt, finalText, started);
        Finish(job, failed: false, null);
    }

    private void ApplyPartial(Job job, PartialJsonReader.PartialView view)
    {
        string? Get(string key) => view.Strings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v.Value) ? v.Value : null;
        IReadOnlyList<string> Arr(string key) => view.StringArrays.TryGetValue(key, out var a) ? a : [];
        var direct = Get("direct_position");
        var spoken = Get("spoken_answer");
        if (spoken is { Length: > ResultLimits.SpokenAnswer })
        {
            spoken = spoken[..ResultLimits.SpokenAnswer];
        }
        UpdateIfCurrent(job, c =>
        {
            var next = c with
            {
                Title = Get("title") ?? c.Title,
                DirectPosition = direct ?? c.DirectPosition,
                SpokenAnswer = spoken ?? c.SpokenAnswer,
                Suggestion = Get("suggestion") ?? c.Suggestion,
                SupportingPoints = Arr("supporting_points") is { Count: > 0 } sp ? sp : c.SupportingPoints,
                AssumptionsAndLimits = Arr("assumptions_and_limits") is { Count: > 0 } al ? al : c.AssumptionsAndLimits,
                FilledInDetails = Arr("filled_in_details") is { Count: > 0 } fd ? fd : c.FilledInDetails,
                UpdatedUtc = _time.GetUtcNow(),
            };
            if (c.FirstUsefulTextLatency is null && ((direct?.Length ?? 0) >= 20 || (spoken?.Length ?? 0) >= 20) && job.Question is { } q)
            {
                next = next with { FirstUsefulTextLatency = _clock.Now - q.EndedAt, FirstUsefulTextAt = _clock.Now };
            }
            return next.DirectPosition == c.DirectPosition && next.SpokenAnswer == c.SpokenAnswer && next.Title == c.Title
                   && next.SupportingPoints.Count == c.SupportingPoints.Count && next.AssumptionsAndLimits.Count == c.AssumptionsAndLimits.Count
                   && next.FilledInDetails.Count == c.FilledInDetails.Count
                ? null : next;
        });
    }

    private void ApplyFinal(Job job, BuiltPrompt prompt, string text, DateTimeOffset started)
    {
        var parsed = AssistantResultParser.Parse(text);
        if (!parsed.Ok)
        {
            _log.LogWarning("Structured output rejected: {Errors}", string.Join("; ", parsed.Errors.Take(3)));
            if (job.Kind == JobKind.Question)
            {
                ApplyPartial(job, PartialJsonReader.Parse(text));
                UpdateIfCurrent(job, c => c with { State = CardState.Invalid, StatusDetail = "The response was malformed; partial text shown is unvalidated" });
            }
            return;
        }
        var r = parsed.Result!;
        var ctx = new ValidationContext(job.Question?.QuestionId, job.Question?.Revision, prompt.TranscriptIds, prompt.PreparationIds,
            job.Kind == JobKind.Question);
        var validation = AssistantResultValidator.Validate(r, ctx);
        if (!validation.Valid)
        {
            _log.LogWarning("Result failed validation: {Errors}", string.Join("; ", validation.Errors.Take(4)));
            if (job.Kind == JobKind.Question)
            {
                ApplyPartial(job, PartialJsonReader.Parse(text));
                UpdateIfCurrent(job, c => c with { State = CardState.Invalid, StatusDetail = "Answer failed validation: " + string.Join("; ", validation.Errors.Take(2)) });
            }
            return;
        }

        // Evidence must still belong to the retained conversation (not reset or trimmed).
        if (r.EvidenceSegmentIds.Concat(r.QuestionSegmentIds).Any(id => _transcript.Get(id) is null))
        {
            _log.LogInformation("Result evidence no longer retained; discarded");
            if (job.Kind == JobKind.Question)
            {
                UpdateIfCurrent(job, c => c with { State = CardState.Interrupted, StatusDetail = "Conversation was cleared before the answer completed" });
            }
            return;
        }

        if (job.Kind == JobKind.Question)
        {
            var kind = r.Kind switch
            {
                ResponseKind.Answer => CardKind.Answer,
                ResponseKind.Clarification => CardKind.Clarification,
                ResponseKind.Insight => CardKind.Insight,
                _ => CardKind.NoAction,
            };
            UpdateIfCurrent(job, c => Fill(c, r, kind) with
            {
                State = CardState.Complete,
                StatusDetail = null,
                CompleteLatency = job.Question is { } q ? _clock.Now - q.EndedAt : null,
                CompletedAt = _clock.Now,
            });
            if (r.Kind == ResponseKind.Answer && job.Question is { } question)
            {
                _history.Update(question.QuestionId, x => x with { DraftSummary = Clip(r.DirectPosition, 400) });
            }
            if (r.FollowsUpOn is not null)
            {
                LinkFollowUp(job, r.FollowsUpOn);
            }
            _log.LogInformation("Answer complete card={Card} kind={Kind} chars={Chars} prepRefs={Prep} followsUpOn={FollowsUpOn}", job.CardId, r.Kind, r.SpokenAnswer?.Length ?? 0, r.PreparationEvidenceIds.Count, r.FollowsUpOn ?? "-");
            return;
        }

        // Meeting review
        if (!r.ShouldNotify || r.Kind == ResponseKind.None)
        {
            _log.LogInformation("Meeting review: no intervention ({Note})", Clip(r.DecisionNote, 80));
            return;
        }
        if (_time.GetUtcNow() - started > MeetingResultMaxAge)
        {
            _log.LogInformation("Meeting result obsolete after {Seconds:F0}s; discarded", (_time.GetUtcNow() - started).TotalSeconds);
            return;
        }
        var decision = _filter.Evaluate(r.Title, r.Suggestion ?? r.DirectPosition, r.SuggestedQuestion);
        if (decision != FilterDecision.Show)
        {
            _log.LogInformation("Meeting result suppressed: {Decision}", decision);
            return;
        }
        lock (_gate)
        {
            if (!IsCurrentLocked(job))
            {
                return;
            }
            var now = _time.GetUtcNow();
            var kind = r.Kind switch
            {
                ResponseKind.Answer => CardKind.Answer,
                ResponseKind.Clarification => CardKind.Clarification,
                _ => CardKind.Insight,
            };
            var card = Fill(new AssistantCard
            {
                Id = _board.NextId(),
                Kind = kind,
                State = CardState.Complete,
                SessionId = _sessionId,
                CreatedUtc = now,
                UpdatedUtc = now,
                Model = _settings.InsightModel,
                PromptVersion = PromptLibrary.Version,
            }, r, kind);
            _board.Add(card);
            _filter.RecordShown(r.Title, r.Suggestion ?? r.DirectPosition, r.SuggestedQuestion);
        }
    }

    private static AssistantCard Fill(AssistantCard c, AssistantResult r, CardKind kind) => c with
    {
        Kind = kind,
        Title = r.Title,
        DirectPosition = r.DirectPosition,
        SpokenAnswer = r.SpokenAnswer,
        Suggestion = r.Suggestion,
        SuggestedQuestion = r.SuggestedQuestion,
        SupportingPoints = r.SupportingPoints,
        AssumptionsAndLimits = r.AssumptionsAndLimits,
        LikelyFollowups = r.LikelyFollowups,
        MissingFacts = r.MissingFacts,
        FilledInDetails = r.FilledInDetails,
        EvidenceSegmentIds = r.EvidenceSegmentIds,
        PreparationEvidenceIds = r.PreparationEvidenceIds,
        QuestionSegmentIds = r.QuestionSegmentIds.Count > 0 ? r.QuestionSegmentIds : c.QuestionSegmentIds,
        QuestionText = c.QuestionText ?? r.QuestionText,
        Category = r.Category,
        DecisionNote = r.DecisionNote,
    };

    private void Finish(Job job, bool failed, string? detail, bool keepHealth = false)
    {
        lock (_gate)
        {
            if (failed && detail is not null && job.Kind == JobKind.Question)
            {
                UpdateIfCurrentLocked(job, c => c with { State = CardState.Failed, StatusDetail = detail });
            }
            if (_active == job)
            {
                _active = null;
                _activeCts?.Dispose();
                _activeCts = null;
                if (!keepHealth)
                {
                    _health = AssistantHealth.Idle;
                    _healthDetail = null;
                }
            }
            TryDispatchLocked();
        }
        RaiseStatus();
    }

    private void PinPreviousUserResponse()
    {
        var last = _history.Items.LastOrDefault();
        if (last is not null && last.UserResponse is null)
        {
            // Pinned once (also when the user said nothing) so the session log renders the same text from now on.
            var response = _prompts.ComputeUserResponse(last) ?? PinnedExcerpt.NoReply;
            _history.Update(last.QuestionId, x => x with { UserResponse = response });
        }
    }

    private void LinkFollowUp(Job job, string? earlierQuestionId)
    {
        if (job.Question is { } q && IsCurrent(job) && _history.LinkFollowUp(q.QuestionId, earlierQuestionId?.Trim()))
        {
            _log.LogInformation("Question {Question} follows up on {Earlier}", q.QuestionId, earlierQuestionId);
        }
    }

    private void MarkInterrupted(string cardId, string reason) =>
        _board.Update(cardId, c => c.State is CardState.Generating or CardState.Queued or CardState.Limited
            ? c with { State = CardState.Interrupted, StatusDetail = reason, UpdatedUtc = _time.GetUtcNow() }
            : null);

    private bool IsCurrent(Job job)
    {
        lock (_gate)
        {
            return IsCurrentLocked(job);
        }
    }

    private bool IsCurrentLocked(Job job) => job.Epoch == _epoch && job.SessionId == _sessionId && ReferenceEquals(_active, job);

    private void UpdateIfCurrent(Job job, Func<AssistantCard, AssistantCard?> f)
    {
        lock (_gate)
        {
            UpdateIfCurrentLocked(job, f);
        }
    }

    private void UpdateIfCurrentLocked(Job job, Func<AssistantCard, AssistantCard?> f)
    {
        if (!IsCurrentLocked(job) && !(job.Epoch == _epoch && ReferenceEquals(_pending, job)))
        {
            return;
        }
        _board.Update(job.CardId, c => c.Revision == (job.Question?.Revision ?? c.Revision) ? f(c) : null);
    }

    private void CountUsage(UsageInfo? usage)
    {
        if (usage is null)
        {
            return;
        }
        Interlocked.Add(ref _inputTokens, usage.InputTokens);
        Interlocked.Add(ref _outputTokens, usage.OutputTokens);
    }

    private AssistantStatus StatusLocked()
    {
        var budget = _settings.Scenario == Scenario.Meeting ? _meetingBudget : _interviewBudget;
        return new AssistantStatus(_health, _healthDetail, budget.UsedInLastMinute, budget.MaxPerMinute,
            _meetingBudget.TotalAcquired + _interviewBudget.TotalAcquired, Interlocked.Read(ref _inputTokens), Interlocked.Read(ref _outputTokens));
    }

    private void RaiseStatus()
    {
        AssistantStatus s;
        lock (_gate)
        {
            s = StatusLocked();
        }
        StatusChanged?.Invoke(s);
    }

    private static string? Clip(string? s, int max) => s is null || s.Length <= max ? s : s[..max] + "…";

    private enum JobKind
    {
        Question,
        MeetingReview,
    }

    private sealed record Job(
        JobKind Kind,
        string CardId,
        long Epoch,
        long SessionId,
        QuestionCandidate? Question,
        IReadOnlyList<MeetingTrigger> Triggers,
        string? NewSince,
        int Attempt);
}
