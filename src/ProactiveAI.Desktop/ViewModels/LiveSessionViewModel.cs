using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Assistant;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Context;
using ProactiveAI.Core.Session;
using ProactiveAI.Core.Settings;
using ProactiveAI.Core.Speakers;
using ProactiveAI.Core.Transcript;
using ProactiveAI.Desktop.Services;

namespace ProactiveAI.Desktop.ViewModels;

public sealed partial class LiveSessionViewModel : ObservableObject
{
    private static readonly TimeSpan ReadingHold = TimeSpan.FromSeconds(8);

    private readonly SessionCoordinator _session;
    private readonly ISettingsStore _store;
    private readonly ICredentialStore _credentials;
    private readonly UiDispatcher _ui;
    private readonly AppNavigator _nav;
    private readonly ConcurrentQueue<TranscriptChange> _transcriptChanges = new();
    private readonly ConcurrentQueue<CardChange> _cardChanges = new();
    private readonly Dictionary<string, SegmentViewModel> _segmentsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CardViewModel> _cardsById = new(StringComparer.Ordinal);
    private readonly DispatcherQueueTimer _timer;
    private SessionSnapshot? _pendingSnapshot;
    private int _speakersDirty;
    private string? _viewedCardId;
    private DateTimeOffset _lastReadingActivity = DateTimeOffset.MinValue;
    private bool _suppressSourceEvents;

    public LiveSessionViewModel(SessionCoordinator session, ISettingsStore store, ICredentialStore credentials, UiDispatcher ui, AppNavigator nav)
    {
        _session = session;
        _store = store;
        _credentials = credentials;
        _ui = ui;
        _nav = nav;

        var s = session.Settings;
        _suppressSourceEvents = true;
        SystemEnabled = s.SystemAudioEnabled;
        ScenarioIndex = (int)s.Scenario;
        _suppressSourceEvents = false;
        LoadPreparation();

        session.Transcript.Changed += c => _transcriptChanges.Enqueue(c);
        session.Board.Changed += c => _cardChanges.Enqueue(c);
        session.Speakers.Changed += () => Interlocked.Exchange(ref _speakersDirty, 1);
        session.StatusChanged += snap => Volatile.Write(ref _pendingSnapshot, snap);
        session.Assistant.FocusRequested += id => _ui.Post(() => FocusCard(id, fromUser: false));
        session.ArchiveSaved += id => _ui.Post(() =>
        {
            SavedSessionId = id;
            OnPropertyChanged(nameof(SavedVisibility));
        });

        _timer = ui.Queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) => Pump();
        _timer.Start();
        ApplySnapshot(session.Snapshot);
        if (s.MicrophoneEnabled)
        {
            _ = SetMicrophoneEnabledAsync(true); // on by default (remembered setting)
        }
    }

    /// <summary>History id of the last saved session (shown as "saved to history" once stopped).</summary>
    public string? SavedSessionId { get; private set; }

    public Visibility SavedVisibility =>
        State == SessionState.Stopped && SavedSessionId is not null && SavedSessionId == _session.ArchiveId ? Visibility.Visible : Visibility.Collapsed;

    public SessionCoordinator Session => _session;

    public ObservableCollection<SegmentViewModel> Segments { get; } = new();

    public ObservableCollection<CardViewModel> History { get; } = new();

    public ObservableCollection<CardViewModel> Insights { get; } = new();

    public ObservableCollection<SpeakerViewModel> Speakers { get; } = new();

    /// <summary>A new segment was appended at the end (the view auto-scrolls only when the user is at the bottom).</summary>
    public event Action? SegmentsAppended;

    /// <summary>Request the view to bring evidence segments into view.</summary>
    public event Action<IReadOnlyList<string>>? EvidenceRequested;

    /// <summary>The focused answer card changed to a different card (the reading panel resets to the top).</summary>
    public event Action? FocusedCardReplaced;

    /// <summary>A new insight card appeared (meeting mode overlay).</summary>
    public event Action<CardViewModel>? InsightArrived;

    // ------------------------------------------------------------------ state

    [ObservableProperty] public partial SessionState State { get; set; }
    /// <summary>The page was opened from New session and listening has not started yet: only then is Start enabled.</summary>
    [ObservableProperty] public partial bool AwaitingStart { get; set; }
    [ObservableProperty] public partial string StateText { get; set; } = "Stopped";
    [ObservableProperty] public partial string DurationText { get; set; } = "00:00";
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial InfoBarSeverity MessageSeverity { get; set; }
    [ObservableProperty] public partial bool HasMessage { get; set; }
    [ObservableProperty] public partial bool CanRetry { get; set; }
    [ObservableProperty] public partial bool HasApiKey { get; set; }

    [ObservableProperty] public partial bool SystemEnabled { get; set; }
    [ObservableProperty] public partial bool MicrophoneEnabled { get; set; }
    [ObservableProperty] public partial bool MicrophoneMuted { get; set; }
    [ObservableProperty] public partial double SystemLevel { get; set; }
    [ObservableProperty] public partial double MicLevel { get; set; }
    [ObservableProperty] public partial string SystemStatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial string MicStatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial string TranscriptionStatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial string DiarizationStatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial string AssistantStatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial string EchoStatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool SystemSpeaking { get; set; }
    [ObservableProperty] public partial bool MicSpeaking { get; set; }

    [ObservableProperty] public partial int ScenarioIndex { get; set; }
    [ObservableProperty] public partial string? TrimNotice { get; set; }
    [ObservableProperty] public partial bool HasSelection { get; set; }

    [ObservableProperty] public partial CardViewModel? FocusedCard { get; set; }
    [ObservableProperty] public partial bool NewerAnswerAvailable { get; set; }

    [ObservableProperty] public partial string PrepRole { get; set; } = string.Empty;
    [ObservableProperty] public partial string PrepContribution { get; set; } = string.Empty;
    [ObservableProperty] public partial string PrepMethod { get; set; } = string.Empty;
    [ObservableProperty] public partial string PrepResults { get; set; } = string.Empty;
    [ObservableProperty] public partial string PrepLimitations { get; set; } = string.Empty;
    [ObservableProperty] public partial string PrepTerminology { get; set; } = string.Empty;
    [ObservableProperty] public partial string PrepSummary { get; set; } = string.Empty;

    public bool IsAnswerMode => ScenarioIndex != (int)Scenario.Meeting;

    /// <summary>The résumé is used in interview mode only.</summary>
    public Visibility InterviewVisibility => ScenarioIndex == (int)Scenario.Interview ? Visibility.Visible : Visibility.Collapsed;

    public bool IsActive => State is SessionState.Listening or SessionState.Starting or SessionState.Reconnecting;

    public bool CanStart => AwaitingStart && !IsSessionInProgress;

    /// <summary>A session is running, paused, starting/stopping, or failed but can still be resumed.</summary>
    public bool IsSessionInProgress => !(State == SessionState.Stopped || (State == SessionState.Error && !CanResumeAfterError));

    /// <summary>Back to Home is disabled while a session is in progress (stop it first).</summary>
    public bool CanGoHome => !IsSessionInProgress;

    public bool CanResume => State == SessionState.Paused || (State == SessionState.Error && CanResumeAfterError);

    public bool CanPause => State is SessionState.Listening or SessionState.Reconnecting;

    public bool CanStop => State is SessionState.Listening or SessionState.Paused or SessionState.Reconnecting or SessionState.Starting or SessionState.Error;

    public bool CanReset => State is SessionState.Stopped or SessionState.Paused or SessionState.Error || Segments.Count > 0;

    public bool IsEmptyTranscript => Segments.Count == 0;

    public Visibility EmptyTranscriptVisibility => Segments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StartVisibility => CanResume ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ResumeVisibility => CanResume ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MicControlsVisibility => MicrophoneEnabled ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FocusedCardVisibility => FocusedCard is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility NoFocusedCardVisibility => FocusedCard is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NewerAnswerVisibility => NewerAnswerAvailable ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AnswerModeVisibility => IsAnswerMode ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MeetingModeVisibility => IsAnswerMode ? Visibility.Collapsed : Visibility.Visible;

    public Visibility TrimNoticeVisibility => string.IsNullOrEmpty(TrimNotice) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility EchoVisibility => string.IsNullOrEmpty(EchoStatusText) ? Visibility.Collapsed : Visibility.Visible;

    partial void OnEchoStatusTextChanged(string value) => OnPropertyChanged(nameof(EchoVisibility));

    public string EmptyTranscriptText => State switch
    {
        SessionState.Listening => "Listening… captions appear here when someone speaks.",
        SessionState.Paused => "Paused. Nothing is being captured.",
        SessionState.Starting => "Starting…",
        _ when !HasApiKey => "Add your OpenAI API key in Settings to get started.",
        _ when AwaitingStart => "Ready. Select Start to begin listening.",
        _ => "No session is running. Go to Home to start a new session or to review past sessions.",
    };

    private bool CanResumeAfterError => _session.Snapshot.SessionId != 0 && State == SessionState.Error && CanRetry;

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private async Task StartAsync()
    {
        ResetFocus();
        await Task.Run(_session.StartAsync);
    }

    [RelayCommand]
    private Task PauseAsync() => Task.Run(() => _session.PauseAsync());

    [RelayCommand]
    private Task ResumeAsync() => Task.Run(_session.ResumeAsync);

    [RelayCommand]
    private Task StopAsync() => Task.Run(_session.StopAsync);

    [RelayCommand]
    private async Task ResetAsync()
    {
        await Task.Run(_session.ResetAsync);
        ResetFocus();
    }

    [RelayCommand]
    private Task RetryAsync() => CanResumeAfterError ? ResumeAsync() : StartAsync();

    /// <summary>
    /// Called by New session's Continue: clears the previous session from this page (it is already in history) and
    /// waits for Start. Listening begins only when the user selects Start.
    /// </summary>
    public async Task PrepareNewSessionAsync()
    {
        await Task.Run(_session.ResetAsync); // stops and saves a leftover session first; clears transcript, answers and timer
        ResetFocus();
        AwaitingStart = true;
    }

    [RelayCommand]
    private void GoHome() => _nav.Navigate(AppPage.Home);

    [RelayCommand]
    private void DismissMessage() => HasMessage = false;

    public void AnswerSelection(IEnumerable<SegmentViewModel> selected)
    {
        var ids = selected.Where(s => s.IsSpeech && s.IsFinal).Select(s => s.Id).ToList();
        if (ids.Count == 0)
        {
            return;
        }
        _viewedCardId = null;
        _lastReadingActivity = DateTimeOffset.MinValue;
        _session.AnswerSelection(ids);
    }

    [RelayCommand]
    private void TogglePin(CardViewModel? card)
    {
        if (card is null)
        {
            return;
        }
        _session.Board.SetPinned(card.Id, !card.IsPinned);
        if (!card.IsPinned)
        {
            _viewedCardId = card.Id;
        }
    }

    [RelayCommand]
    private void Dismiss(CardViewModel? card)
    {
        if (card is null)
        {
            return;
        }
        _session.Board.Dismiss(card.Id);
        if (card.IsInsight)
        {
            _session.Filter.RecordDismissed(card.Model.Title, card.Model.Suggestion, card.Model.SuggestedQuestion);
        }
        if (_viewedCardId == card.Id)
        {
            _viewedCardId = null;
        }
        RecomputeFocus(userInitiated: true);
    }

    [RelayCommand]
    private void Cancel(CardViewModel? card)
    {
        if (card is not null)
        {
            _session.Assistant.Cancel(card.Id);
        }
    }

    [RelayCommand]
    private void Regenerate(CardViewModel? card)
    {
        if (card is null)
        {
            return;
        }
        _viewedCardId = card.Id;
        _session.Assistant.Regenerate(card.Id);
    }

    [RelayCommand]
    private void Copy(CardViewModel? card)
    {
        if (card is null)
        {
            return;
        }
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(card.PlainText);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    [RelayCommand]
    private void ShowEvidence(CardViewModel? card)
    {
        if (card is null)
        {
            return;
        }
        var ids = card.Model.QuestionSegmentIds.Concat(card.Model.EvidenceSegmentIds).Distinct().ToList();
        foreach (var vm in Segments)
        {
            vm.IsHighlighted = ids.Contains(vm.Id);
        }
        EvidenceRequested?.Invoke(ids);
    }

    [RelayCommand]
    private void ViewCard(CardViewModel? card)
    {
        if (card is not null)
        {
            FocusCard(card.Id, fromUser: true);
        }
    }

    [RelayCommand]
    private void ShowNewest()
    {
        var pinned = _session.Board.Cards.FirstOrDefault(c => c.Pinned);
        if (pinned is not null)
        {
            _session.Board.SetPinned(pinned.Id, false);
        }
        _viewedCardId = null;
        _lastReadingActivity = DateTimeOffset.MinValue;
        RecomputeFocus(userInitiated: true);
    }

    [RelayCommand]
    private void ClearPreparation()
    {
        _session.Preparation.Clear();
        LoadPreparation();
    }

    public async Task SetMicrophoneEnabledAsync(bool enabled) => await Task.Run(() => _session.SetMicrophoneEnabledAsync(enabled));

    public void RenameSpeaker(string speakerId, string? name) => _session.RenameSpeaker(speakerId, name);

    public void SetSpeakerRole(string speakerId, SpeakerRole role) => _session.SetSpeakerRole(speakerId, role);

    public void AssignSegment(string segmentId, string speakerId) => _session.CorrectSegmentSpeaker(segmentId, speakerId);

    /// <summary>The user scrolled or interacted with the answer panel; hold automatic replacement briefly.</summary>
    public void NotifyReadingActivity() => _lastReadingActivity = DateTimeOffset.UtcNow;

    public void RefreshCredentialState()
    {
        HasApiKey = _credentials.HasApiKey();
        OnPropertyChanged(nameof(EmptyTranscriptText));
    }

    // ------------------------------------------------------------------ property hooks

    partial void OnScenarioIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsAnswerMode));
        OnPropertyChanged(nameof(AnswerModeVisibility));
        OnPropertyChanged(nameof(MeetingModeVisibility));
        OnPropertyChanged(nameof(InterviewVisibility));
        if (_suppressSourceEvents || value < 0)
        {
            return;
        }
        var s = _session.Settings with { Scenario = (Scenario)value };
        _session.UpdateSettings(s);
        _store.Save(s);
    }

    partial void OnSystemEnabledChanged(bool value)
    {
        if (_suppressSourceEvents)
        {
            return;
        }
        _ = Task.Run(() => _session.SetSystemAudioEnabledAsync(value)).ContinueWith(_ => _ui.Post(() => _store.Save(_session.Settings)));
    }

    partial void OnMicrophoneEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(MicControlsVisibility));
        if (!_suppressSourceEvents)
        {
            _ = SetMicrophoneEnabledAsync(value);
            var s = _session.Settings with { MicrophoneEnabled = value }; // remembered for the next run
            _session.UpdateSettings(s);
            _store.Save(s);
        }
    }

    partial void OnMicrophoneMutedChanged(bool value)
    {
        if (!_suppressSourceEvents)
        {
            _ = Task.Run(() => _session.SetMicrophoneMutedAsync(value));
        }
    }

    partial void OnStateChanged(SessionState value)
    {
        if (value is SessionState.Listening or SessionState.Paused or SessionState.Reconnecting)
        {
            AwaitingStart = false; // the session has started; Start stays disabled until the next New session
        }
        foreach (var name in new[]
                 {
                     nameof(CanStart), nameof(CanResume), nameof(CanPause), nameof(CanStop), nameof(CanReset), nameof(IsActive),
                     nameof(StartVisibility), nameof(ResumeVisibility), nameof(EmptyTranscriptText), nameof(SavedVisibility),
                     nameof(IsSessionInProgress), nameof(CanGoHome),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    partial void OnAwaitingStartChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(EmptyTranscriptText));
    }

    partial void OnCanRetryChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(StartVisibility));
        OnPropertyChanged(nameof(ResumeVisibility));
        OnPropertyChanged(nameof(IsSessionInProgress));
        OnPropertyChanged(nameof(CanGoHome));
    }

    partial void OnFocusedCardChanged(CardViewModel? value)
    {
        OnPropertyChanged(nameof(FocusedCardVisibility));
        OnPropertyChanged(nameof(NoFocusedCardVisibility));
    }

    partial void OnNewerAnswerAvailableChanged(bool value) => OnPropertyChanged(nameof(NewerAnswerVisibility));

    partial void OnTrimNoticeChanged(string? value) => OnPropertyChanged(nameof(TrimNoticeVisibility));

    partial void OnHasApiKeyChanged(bool value) => OnPropertyChanged(nameof(EmptyTranscriptText));

    partial void OnPrepRoleChanged(string value) => SetPrep(PrepField.RoleOrTopic, value);
    partial void OnPrepContributionChanged(string value) => SetPrep(PrepField.Contribution, value);
    partial void OnPrepMethodChanged(string value) => SetPrep(PrepField.ProjectOrMethod, value);
    partial void OnPrepResultsChanged(string value) => SetPrep(PrepField.VerifiedResults, value);
    partial void OnPrepLimitationsChanged(string value) => SetPrep(PrepField.Limitations, value);
    partial void OnPrepTerminologyChanged(string value) => SetPrep(PrepField.Terminology, value);

    private void SetPrep(PrepField field, string value)
    {
        if (_suppressSourceEvents)
        {
            return;
        }
        _session.Preparation.Set(field, value);
        UpdatePrepSummary();
    }

    private void LoadPreparation()
    {
        _suppressSourceEvents = true;
        var p = _session.Preparation;
        PrepRole = p.Get(PrepField.RoleOrTopic);
        PrepContribution = p.Get(PrepField.Contribution);
        PrepMethod = p.Get(PrepField.ProjectOrMethod);
        PrepResults = p.Get(PrepField.VerifiedResults);
        PrepLimitations = p.Get(PrepField.Limitations);
        PrepTerminology = p.Get(PrepField.Terminology);
        _suppressSourceEvents = false;
        UpdatePrepSummary();
    }

    private void UpdatePrepSummary()
    {
        var p = _session.Preparation;
        int chars = p.TotalChars;
        PrepSummary = chars == 0
            ? "No preparation notes. Notes are kept in memory only and cleared when the app exits."
            : $"{chars:N0} characters in {p.Excerpts().Count(e => e.Resume is null)} lines · in memory only · sent to OpenAI with each answer request (the most relevant parts if notes and résumé together exceed about 10,000 tokens).";
    }

    // ------------------------------------------------------------------ UI pump

    private void Pump()
    {
        bool appended = false;
        int processed = 0;
        while (processed < 400 && _transcriptChanges.TryDequeue(out var change))
        {
            processed++;
            appended |= ApplyTranscriptChange(change);
        }
        if (Interlocked.Exchange(ref _speakersDirty, 0) == 1)
        {
            SyncSpeakers();
        }
        bool cardsChanged = false;
        var latestUpdates = new Dictionary<string, CardChange>(StringComparer.Ordinal);
        while (_cardChanges.TryDequeue(out var cardChange))
        {
            // Coalesce streaming updates: only the newest snapshot of each card is rendered per tick.
            if (cardChange.Kind == CardChangeKind.Updated && cardChange.Card is { } updatedCard && _cardsById.ContainsKey(updatedCard.Id))
            {
                latestUpdates[updatedCard.Id] = cardChange;
                cardsChanged = true;
                continue;
            }
            foreach (var pending in latestUpdates.Values)
            {
                ApplyCardChange(pending);
            }
            latestUpdates.Clear();
            ApplyCardChange(cardChange);
            cardsChanged = true;
        }
        foreach (var pending in latestUpdates.Values)
        {
            ApplyCardChange(pending);
        }
        if (cardsChanged)
        {
            RecomputeFocus(userInitiated: false);
        }
        if (Interlocked.Exchange(ref _pendingSnapshot, null) is { } snap)
        {
            ApplySnapshot(snap);
        }
        else if (State == SessionState.Listening)
        {
            DurationText = Format(_session.Snapshot.Elapsed);
        }
        if (processed > 0)
        {
            OnPropertyChanged(nameof(EmptyTranscriptVisibility));
            OnPropertyChanged(nameof(IsEmptyTranscript));
            OnPropertyChanged(nameof(CanReset));
            int trimmed = _session.Transcript.TrimmedCount;
            TrimNotice = trimmed > 0 ? $"{trimmed:N0} older transcript entries were removed from view (in-memory limit {TranscriptStore.DefaultMaxSegments:N0})." : null;
        }
        if (appended)
        {
            SegmentsAppended?.Invoke();
        }
    }

    private bool ApplyTranscriptChange(TranscriptChange change)
    {
        switch (change.Kind)
        {
            case TranscriptChangeKind.Cleared:
                Segments.Clear();
                _segmentsById.Clear();
                return false;
            case TranscriptChangeKind.Removed when change.Segment is { } removed:
                if (_segmentsById.Remove(removed.Id, out var vmr))
                {
                    Segments.Remove(vmr);
                }
                return false;
            case TranscriptChangeKind.Updated when change.Segment is { } updated:
                if (_segmentsById.TryGetValue(updated.Id, out var vmu))
                {
                    vmu.Update(updated, _session.Speakers);
                    return false;
                }
                return Insert(updated);
            case TranscriptChangeKind.Added when change.Segment is { } added:
                if (_segmentsById.ContainsKey(added.Id))
                {
                    return false;
                }
                return Insert(added);
        }
        return false;
    }

    private bool Insert(TranscriptSegment seg)
    {
        var vm = new SegmentViewModel(seg, _session.Speakers);
        _segmentsById[seg.Id] = vm;
        int i = Segments.Count;
        while (i > 0 && Compare(Segments[i - 1].Model, seg) > 0)
        {
            i--;
        }
        Segments.Insert(i, vm);
        return i == Segments.Count - 1;
    }

    private static int Compare(TranscriptSegment a, TranscriptSegment b)
    {
        int c = a.AudioStart.CompareTo(b.AudioStart);
        return c != 0 ? c : a.Sequence.CompareTo(b.Sequence);
    }

    private void SyncSpeakers()
    {
        var all = _session.Speakers.All;
        var existing = Speakers.ToDictionary(s => s.Id);
        Speakers.Clear();
        foreach (var s in all)
        {
            if (existing.TryGetValue(s.Id, out var vm))
            {
                vm.Update(s);
                Speakers.Add(vm);
            }
            else
            {
                Speakers.Add(new SpeakerViewModel(s));
            }
        }
        foreach (var seg in Segments)
        {
            seg.RefreshSpeaker(_session.Speakers);
        }
    }

    private void ApplyCardChange(CardChange change)
    {
        switch (change.Kind)
        {
            case CardChangeKind.Cleared:
                History.Clear();
                Insights.Clear();
                _cardsById.Clear();
                FocusedCard = null;
                break;
            case CardChangeKind.Removed when change.Card is { } removed:
                if (_cardsById.Remove(removed.Id, out var vmr))
                {
                    History.Remove(vmr);
                    Insights.Remove(vmr);
                }
                break;
            case CardChangeKind.Added or CardChangeKind.Updated when change.Card is { } card:
                if (_cardsById.TryGetValue(card.Id, out var vm))
                {
                    vm.Update(card);
                }
                else
                {
                    vm = new CardViewModel(card);
                    _cardsById[card.Id] = vm;
                    if (card.Kind == CardKind.Insight)
                    {
                        Insights.Insert(0, vm);
                        InsightArrived?.Invoke(vm);
                    }
                    else
                    {
                        History.Insert(0, vm);
                    }
                }
                if (vm.IsInsight && card.Dismissed)
                {
                    Insights.Remove(vm);
                }
                break;
        }
    }

    private void FocusCard(string id, bool fromUser)
    {
        if (!_cardsById.ContainsKey(id))
        {
            return;
        }
        if (!fromUser && (_session.Board.Cards.Any(c => c.Pinned && c.Id != id) || Holding()))
        {
            NewerAnswerAvailable = true;
            return;
        }
        _viewedCardId = id;
        SetFocused(_cardsById[id]);
    }

    private void RecomputeFocus(bool userInitiated)
    {
        var target = _session.Board.FocusAnswer();
        CardViewModel? next = target is null ? null : _cardsById.GetValueOrDefault(target.Id);
        if (_viewedCardId is not null && _cardsById.TryGetValue(_viewedCardId, out var viewed) && !viewed.IsDismissed && target is not { Pinned: true })
        {
            NewerAnswerAvailable = next is not null && next != viewed && next.Model.CreatedUtc > viewed.Model.CreatedUtc;
            SetFocused(viewed);
            return;
        }
        if (!userInitiated && FocusedCard is not null && next is not null && next != FocusedCard && !FocusedCard.IsDismissed && Holding())
        {
            NewerAnswerAvailable = true; // the user is reading; do not replace the visible answer
            FocusedCard.Update(FocusedCard.Model);
            return;
        }
        var newest = _session.Board.Cards.LastOrDefault(c => !c.Dismissed && c.Kind != CardKind.Insight);
        NewerAnswerAvailable = target is { Pinned: true } && newest is not null && newest.Id != target.Id && newest.CreatedUtc > target.CreatedUtc;
        SetFocused(next);
    }

    private void SetFocused(CardViewModel? card)
    {
        if (!ReferenceEquals(FocusedCard, card))
        {
            FocusedCard = card;
            FocusedCardReplaced?.Invoke();
        }
    }

    private bool Holding() => DateTimeOffset.UtcNow - _lastReadingActivity < ReadingHold;

    private void ResetFocus()
    {
        _viewedCardId = null;
        _lastReadingActivity = DateTimeOffset.MinValue;
        NewerAnswerAvailable = false;
    }

    private void ApplySnapshot(SessionSnapshot snap)
    {
        State = snap.State;
        StateText = snap.State switch
        {
            SessionState.Stopped => "Stopped",
            SessionState.Starting => "Starting…",
            SessionState.Listening => "Listening",
            SessionState.Paused => "Paused",
            SessionState.Reconnecting => "Reconnecting…",
            SessionState.Stopping => "Stopping…",
            SessionState.Error => "Error",
            _ => snap.State.ToString(),
        };
        DurationText = Format(snap.Elapsed);
        CanRetry = snap.CanRetry;
        HasApiKey = _credentials.HasApiKey();
        if (!string.IsNullOrEmpty(snap.Message))
        {
            if (snap.Message != Message || !HasMessage)
            {
                Message = snap.Message;
                HasMessage = true;
            }
            MessageSeverity = snap.State switch
            {
                SessionState.Error => InfoBarSeverity.Error,
                SessionState.Reconnecting or SessionState.Paused => InfoBarSeverity.Warning,
                _ => InfoBarSeverity.Informational,
            };
        }
        else if (snap.State is SessionState.Listening or SessionState.Stopped)
        {
            HasMessage = false;
            Message = null;
        }

        _suppressSourceEvents = true;
        MicrophoneEnabled = snap.Microphone.Enabled;
        MicrophoneMuted = snap.Microphone.Muted;
        SystemEnabled = snap.System.Enabled;
        _suppressSourceEvents = false;

        SystemLevel = Level(snap.System);
        MicLevel = Level(snap.Microphone);
        SystemSpeaking = snap.System.Active && snap.System.Speaking;
        MicSpeaking = snap.Microphone.Active && snap.Microphone.Speaking;
        SystemStatusText = SourceText(snap.System, "System audio");
        MicStatusText = SourceText(snap.Microphone, "Microphone");
        EchoStatusText = snap.Microphone.Active && snap.System.Enabled
            ? snap.Microphone.EchoControl switch
            {
                EchoControlStatus.BuiltInCanceller => snap.Microphone.EchoDetail ?? "Built-in echo canceller active",
                EchoControlStatus.ReferenceEndpointSet => "Echo cancellation: Windows AEC with playback reference",
                EchoControlStatus.CommunicationsModeOnly => "Echo cancellation: device processing only — headphones recommended",
                EchoControlStatus.Unavailable => "Echo cancellation unavailable — use headphones",
                _ => string.Empty,
            }
            : string.Empty;

        var active = new[] { snap.System, snap.Microphone }.Where(s => s.Active || s.Transcription != TranscriptionHealth.Off).ToList();
        TranscriptionStatusText = active.Count == 0 ? "Transcription: off" : "Transcription: " + string.Join(", ", active.Select(s => s.Transcription switch
        {
            TranscriptionHealth.Connected => "connected",
            TranscriptionHealth.Connecting => "connecting",
            TranscriptionHealth.Reconnecting => "reconnecting",
            TranscriptionHealth.Failed => "failed",
            _ => "off",
        }).Distinct());

        var diar = new[] { snap.System.Diarization, snap.Microphone.Diarization }.Where(d => d is not null).Cast<DiarizationStatus>().ToList();
        DiarizationStatusText = diar.Count == 0
            ? (snap.Microphone.Active && !snap.System.Active ? "Speaker labels: personal microphone alias" : "Speaker labels: off")
            : "Speaker labels: " + string.Join(", ", diar.Select(d => d.Health switch
            {
                DiarizationHealth.Idle or DiarizationHealth.Working => $"active ({d.Requests} windows{(d.LastLabelLatency is { } l ? $", ~{l.TotalSeconds:F0}s delay" : string.Empty)})",
                DiarizationHealth.Limited => "delayed (budget)",
                DiarizationHealth.Degraded => "degraded — " + (d.Detail ?? "service error"),
                _ => "stopped",
            }));

        var a = snap.Assistant;
        AssistantStatusText = "AI analysis: " + a.Health switch
        {
            AssistantHealth.Generating => "generating",
            AssistantHealth.Limited => $"rate limited ({a.RequestsLastMinute}/{a.Budget} per min)",
            AssistantHealth.Degraded => "degraded — " + (a.Detail ?? "service error"),
            _ => $"ready ({a.RequestsLastMinute}/{a.Budget} per min)",
        };
    }

    private static double Level(SourceStatus s) => s.Active ? Math.Clamp((s.LevelDbfs + 60) / 60 * 100, 0, 100) : 0;

    private static string SourceText(SourceStatus s, string label)
    {
        if (!s.Enabled)
        {
            return $"{label}: off";
        }
        if (s.Muted)
        {
            return $"{label}: muted";
        }
        if (s.Missing)
        {
            return $"{label}: device missing";
        }
        if (!s.Active)
        {
            return $"{label}: ready";
        }
        return $"{label}: {(s.Speaking ? "speech" : "listening")} · {s.DeviceName}";
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
}
