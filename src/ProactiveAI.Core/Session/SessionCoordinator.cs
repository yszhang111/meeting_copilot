using Microsoft.Extensions.Logging;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Assistant;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Context;
using ProactiveAI.Core.History;
using ProactiveAI.Core.Questions;
using ProactiveAI.Core.Settings;
using ProactiveAI.Core.Speakers;
using ProactiveAI.Core.Text;
using ProactiveAI.Core.Transcript;

namespace ProactiveAI.Core.Session;

public sealed class SessionStartException(string message) : Exception(message);

/// <summary>
/// Owns the session lifecycle. Transitions are serialized; every asynchronous result is tagged with
/// session, source and stream generation and rejected when stale. Capture runs off the UI thread and
/// communicates through bounded queues; analysis failures never stop captions.
/// </summary>
public sealed class SessionCoordinator : ISourcePipelineSink, IAsyncDisposable
{
    public static readonly TimeSpan TimelineRetention = TimeSpan.FromSeconds(60);
    public const int MaxReconnectAttempts = 5;
    public const string EchoNote = "Microphone picked up the system audio (loudspeaker echo) — hidden from analysis";
    private static readonly TimeSpan EchoSearchLead = TimeSpan.FromMilliseconds(500);

    private readonly Dictionary<string, DateTimeOffset> _echoChecked = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly object _gate = new();
    private readonly ICaptureSourceFactory _captureFactory;
    private readonly IAudioDeviceCatalog _devices;
    private readonly ITranscriptionProvider _transcription;
    private readonly ISpeakerDiarizer _diarizer;
    private readonly ICredentialStore _credentials;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly Random _jitter = new();
    private readonly Dictionary<AudioSourceKind, SourceRuntime> _sources = new()
    {
        [AudioSourceKind.System] = new SourceRuntime(AudioSourceKind.System),
        [AudioSourceKind.Microphone] = new SourceRuntime(AudioSourceKind.Microphone),
    };
    private readonly List<TimeSpan> _firstCaptionLatency = new();
    private readonly List<TimeSpan> _finalCaptionLatency = new();
    private readonly RequestBudget _diarizationBudget;
    private readonly ITimer _ticker;

    private AppSettings _settings;
    private SessionState _state = SessionState.Stopped;
    private string? _message;
    private bool _canRetry;
    private long _sessionId;
    private long _sessionCounter;
    private DateTimeOffset? _startedUtc;
    private TimeSpan _accumulated;
    private bool _microphoneEnabled;
    private bool _microphoneMuted;
    private DateTimeOffset _lastMeetingReview;
    private string? _lastReviewedSegmentId;
    private readonly IQuestionIntentClassifier? _intentClassifier;
    private readonly IAssistantModel _assistantModel;
    private readonly HashSet<TurnKey> _quietTurns = new();
    private DateTimeOffset _lastWarmUp = DateTimeOffset.MinValue;
    private readonly RequestBudget _intentBudget;
    private long _intentChecks;
    private long _intentInputTokens;
    private long _intentOutputTokens;
    private DateTimeOffset _lastStatusRaise;
    private DateTimeOffset _lastMetricsLog;
    private int _tickBusy;
    private bool _disposed;
    private string? _archiveId;
    private DateTimeOffset _archiveStartedUtc;
    private DateTimeOffset _lastArchiveSave;
    private int _archiveDirty;
    private int _archiveSaving;
    private readonly object _archiveGate = new();

    public SessionCoordinator(
        ICaptureSourceFactory captureFactory,
        IAudioDeviceCatalog devices,
        ITranscriptionProvider transcription,
        ISpeakerDiarizer diarizer,
        IAssistantModel assistantModel,
        ICredentialStore credentials,
        ITokenCounter tokens,
        TimeProvider time,
        AppSettings settings,
        ILoggerFactory loggers,
        IQuestionIntentClassifier? intentClassifier = null,
        ISessionArchive? archive = null)
    {
        _captureFactory = captureFactory;
        _devices = devices;
        _transcription = transcription;
        _diarizer = diarizer;
        _credentials = credentials;
        _loggers = loggers;
        _intentClassifier = intentClassifier;
        _assistantModel = assistantModel;
        Archive = archive;
        _log = loggers.CreateLogger<SessionCoordinator>();
        _settings = settings.Normalize();
        Clock = new SessionClock(time);
        Transcript = new TranscriptStore(time);
        Speakers = new SpeakerRegistry();
        References = new ReferenceClipStore();
        Preparation = new PreparationNotes();
        History = new QuestionHistory();
        Board = new AssistantBoard();
        Filter = new InsightFilter(time, FilterOptions(_settings));
        Assembler = new QuestionTurnAssembler(time, Classify, AssemblerOptions(_settings), loggers.CreateLogger<QuestionTurnAssembler>());
        Prompts = new PromptBuilder(Transcript, Speakers, History, Preparation, Filter, tokens, Clock, Classify);
        Assistant = new AssistantOrchestrator(assistantModel, Prompts, Board, Filter, History, Transcript, Clock, _settings,
            loggers.CreateLogger<AssistantOrchestrator>());
        _diarizationBudget = new RequestBudget(time, _settings.DiarizationMaxRequestsPerMinute);
        _intentBudget = new RequestBudget(time, _settings.IntentMaxRequestsPerMinute);
        Assistant.StatusChanged += _ => RaiseStatus(force: true);
        Transcript.Changed += _ => Volatile.Write(ref _archiveDirty, 1);
        Board.Changed += _ => Volatile.Write(ref _archiveDirty, 1);
        _devices.DeviceChanged += OnDeviceChanged;
        _ticker = time.CreateTimer(_ => Tick(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    public SessionClock Clock { get; }

    /// <summary>Local session history (text only). Null when not configured.</summary>
    public ISessionArchive? Archive { get; }

    /// <summary>History id of the current (or last) session; null before the first start and after a reset.</summary>
    public string? ArchiveId
    {
        get { lock (_gate) { return _archiveId; } }
    }

    /// <summary>Interval of the in-session autosave to history (crash safety).</summary>
    public TimeSpan ArchiveAutosaveInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Raised (background thread) after the current session was written to history; the argument is the id.</summary>
    public event Action<string>? ArchiveSaved;
    public TranscriptStore Transcript { get; }
    public SpeakerRegistry Speakers { get; }
    public ReferenceClipStore References { get; }
    public PreparationNotes Preparation { get; }
    public QuestionHistory History { get; }
    public AssistantBoard Board { get; }
    public InsightFilter Filter { get; }
    public QuestionTurnAssembler Assembler { get; }
    public PromptBuilder Prompts { get; }
    public AssistantOrchestrator Assistant { get; }

    /// <summary>Raised (on a background thread) whenever the state or a source status changes.</summary>
    public event Action<SessionSnapshot>? StatusChanged;

    public AppSettings Settings
    {
        get { lock (_gate) { return _settings; } }
    }

    public SessionState State
    {
        get { lock (_gate) { return _state; } }
    }

    public bool MicrophoneEnabled
    {
        get { lock (_gate) { return _microphoneEnabled; } }
    }

    public bool MicrophoneMuted
    {
        get { lock (_gate) { return _microphoneMuted; } }
    }

    public SessionSnapshot Snapshot
    {
        get { lock (_gate) { return SnapshotLocked(); } }
    }

    public void UpdateSettings(AppSettings settings)
    {
        settings = settings.Normalize();
        lock (_gate)
        {
            _settings = settings;
            _diarizationBudget.MaxPerMinute = settings.DiarizationMaxRequestsPerMinute;
            _intentBudget.MaxPerMinute = settings.IntentMaxRequestsPerMinute;
        }
        Filter.Options = FilterOptions(settings);
        Assembler.Options = AssemblerOptions(settings);
        Assistant.UpdateSettings(settings);
        RaiseStatus(force: true);
    }

    // ------------------------------------------------------------------ commands

    public async Task StartAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state is not (SessionState.Stopped or SessionState.Error))
            {
                return; // concurrent or repeated Start never opens duplicate sessions
            }
            AppSettings s;
            bool mic;
            lock (_gate)
            {
                s = _settings;
                mic = _microphoneEnabled;
            }
            if (!_credentials.HasApiKey())
            {
                SetState(SessionState.Error, "Add your OpenAI API key in Settings before starting.", canRetry: false);
                return;
            }
            if (!s.SystemAudioEnabled && !mic)
            {
                SetState(SessionState.Error, "Enable system audio and/or the microphone before starting.", canRetry: false);
                return;
            }

            // A new session clears prior captured content (already in history); preparation notes are kept.
            SaveArchive(final: true);
            await ClearSessionContentAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _sessionId = ++_sessionCounter;
                _startedUtc = Clock.TimeProvider.GetUtcNow();
                _accumulated = TimeSpan.Zero;
                _archiveId = Guid.NewGuid().ToString("N");
                _archiveStartedUtc = _startedUtc.Value;
                _lastArchiveSave = _startedUtc.Value;
                foreach (var src in _sources.Values)
                {
                    src.Generation = 0;
                    src.Missing = false;
                }
            }
            Volatile.Write(ref _archiveDirty, 0);
            Clock.Restart();
            Assistant.BeginSession(_sessionId);
            SetState(SessionState.Starting, "Connecting…");
            _log.LogInformation("Session {Session} starting system={System} mic={Mic} scenario={Scenario}", _sessionId, s.SystemAudioEnabled, mic, s.Scenario);
            lock (_gate)
            {
                _lastWarmUp = DateTimeOffset.MinValue;
                _quietTurns.Clear();
            }
            WarmUpConnection(); // in parallel with the transcription connections

            try
            {
                await StartEnabledSourcesAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await StopAllSourcesAsync("Start failed").ConfigureAwait(false);
                lock (_gate)
                {
                    _startedUtc = null;
                }
                SetState(SessionState.Error, DescribeStartFailure(ex), canRetry: ex is ProviderException pe && !pe.Kind.RequiresUserAction());
                return;
            }
            SetState(SessionState.Listening, null);
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task PauseAsync(string? reason = null)
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state is not (SessionState.Listening or SessionState.Reconnecting or SessionState.Starting))
            {
                return;
            }
            await PauseLockedAsync(reason ?? "Paused").ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task ResumeAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state is not (SessionState.Paused or SessionState.Error) || _sessionId == 0 || _startedUtc is null)
            {
                return;
            }
            if (!_credentials.HasApiKey())
            {
                SetState(SessionState.Error, "Add your OpenAI API key in Settings before resuming.", canRetry: false);
                return;
            }
            SetState(SessionState.Starting, "Resuming…");
            try
            {
                await StartEnabledSourcesAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await StopAllSourcesAsync("Resume failed").ConfigureAwait(false);
                SetState(SessionState.Error, DescribeStartFailure(ex), canRetry: true);
                return;
            }
            AddGapMarkers("Resumed — audio while paused was not captured");
            lock (_gate)
            {
                _startedUtc = Clock.TimeProvider.GetUtcNow();
            }
            SetState(SessionState.Listening, null);
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task StopAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>Stops, then clears captured content, answers, speakers and suppression state. Preparation is kept.</summary>
    public async Task ResetAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopLockedAsync().ConfigureAwait(false);
            await ClearSessionContentAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _sessionId = 0;
                _startedUtc = null;
                _accumulated = TimeSpan.Zero;
                _message = null;
                _canRetry = false;
                _archiveId = null; // the stopped session stays in history; the cleared view starts nothing new
            }
            Volatile.Write(ref _archiveDirty, 0);
            SetState(SessionState.Stopped, null);
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>Explicitly enables/disables the microphone for this app run (never restored from settings).</summary>
    public async Task SetMicrophoneEnabledAsync(bool enabled)
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _microphoneEnabled = enabled;
                if (enabled)
                {
                    _microphoneMuted = false;
                }
            }
            await ApplyMicrophoneChangeLockedAsync(enabled ? "Microphone enabled" : "Microphone disabled").ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task SetMicrophoneMutedAsync(bool muted)
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _microphoneMuted = muted;
            }
            await ApplyMicrophoneChangeLockedAsync(muted ? "Microphone muted" : "Microphone unmuted").ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task SetSystemAudioEnabledAsync(bool enabled)
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _settings = _settings with { SystemAudioEnabled = enabled };
            }
            if (_state == SessionState.Listening)
            {
                var src = _sources[AudioSourceKind.System];
                if (enabled && src.Pipeline is null)
                {
                    try
                    {
                        await StartSourceAsync(src, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await StopSourceAsync(src, "System audio failed").ConfigureAwait(false);
                        PostNotice($"System audio could not start: {DescribeStartFailure(ex)}");
                    }
                }
                else if (!enabled && src.Pipeline is not null)
                {
                    await StopSourceAsync(src, "System audio disabled").ConfigureAwait(false);
                    if (!_microphoneEnabled || _microphoneMuted)
                    {
                        await PauseLockedAsync("No audio source is enabled").ConfigureAwait(false);
                    }
                }
            }
            RaiseStatus(force: true);
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>System suspend: pause so that sleep/resume can never bypass the user's paused/stopped intent.</summary>
    public Task OnSystemSuspendAsync() => PauseAsync("Paused because the computer went to sleep");

    public void AnswerSelection(IReadOnlyList<string> segmentIds)
    {
        var segs = segmentIds.Select(Transcript.Get).Where(s => s is { Kind: SegmentKind.Speech }).Cast<TranscriptSegment>().ToList();
        if (segs.Count == 0 || _sessionId == 0)
        {
            return;
        }
        Assistant.SubmitQuestion(Assembler.Manual(segs));
    }

    public void RenameSpeaker(string speakerId, string? name) => Speakers.Rename(speakerId, name);

    public void SetSpeakerRole(string speakerId, SpeakerRole role) => Speakers.SetRole(speakerId, role);

    /// <summary>User correction of one segment's speaker for this session. Not overridden by later diarization.</summary>
    public void CorrectSegmentSpeaker(string segmentId, string speakerId)
    {
        if (speakerId == SpeakerRegistry.SelfAliasId)
        {
            Speakers.EnsureSelfAlias();
        }
        Transcript.UpdateAttribution(segmentId, _ => new SpeakerAttribution(AttributionStatus.UserAssigned, speakerId, [], 0, "Corrected by user"));
    }

    public IReadOnlyList<LatencySummary> LatencyReport()
    {
        List<TimeSpan> first, final, labels = new();
        lock (_gate)
        {
            first = _firstCaptionLatency.ToList();
            final = _finalCaptionLatency.ToList();
            foreach (var s in _sources.Values)
            {
                if (s.Diarizer is not null)
                {
                    labels.AddRange(s.Diarizer.LabelLatencies);
                }
            }
            labels.AddRange(_sources.Values.SelectMany(s => s.PastLabelLatencies));
        }
        var cards = Board.Cards;
        return
        [
            LatencyMath.Summarize("audio turn start → first caption text", first),
            LatencyMath.Summarize("utterance end → final caption", final),
            LatencyMath.Summarize("utterance end → speaker label", labels),
            LatencyMath.Summarize("question end → first useful answer text", cards.Where(c => c.FirstUsefulTextLatency is not null).Select(c => c.FirstUsefulTextLatency!.Value).ToList()),
            LatencyMath.Summarize("question end → complete answer", cards.Where(c => c.Kind == CardKind.Answer && c.CompleteLatency is not null).Select(c => c.CompleteLatency!.Value).ToList()),
        ];
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _ticker.DisposeAsync().ConfigureAwait(false);
        _devices.DeviceChanged -= OnDeviceChanged;
        await StopAsync().ConfigureAwait(false);
        References.Clear();
        Preparation.Clear();
        _transition.Dispose();
    }

    // ------------------------------------------------------------------ lifecycle internals

    private async Task PauseLockedAsync(string reason)
    {
        await StopAllSourcesAsync(reason).ConfigureAwait(false);
        await Assistant.HaltAsync(reason).ConfigureAwait(false);
        Assembler.DropOpenTurn();
        FreezeElapsed();
        SaveArchive(final: false);
        SetState(SessionState.Paused, reason == "Paused" ? null : reason);
    }

    private async Task StopLockedAsync()
    {
        if (_state is SessionState.Stopped)
        {
            return;
        }
        SetState(SessionState.Stopping, null);
        await StopAllSourcesAsync("Stopped").ConfigureAwait(false);
        await Assistant.HaltAsync("Stopped").ConfigureAwait(false);
        Assembler.DropOpenTurn();
        References.Clear(); // temporary voice references never outlive a stopped session
        FreezeElapsed();
        SaveArchive(final: true);
        SetState(SessionState.Stopped, null);
        _log.LogInformation("Session {Session} stopped", _sessionId);
    }

    /// <summary>
    /// Writes the current session (text and assistant output only) to history. Skipped when history is off, when nothing
    /// was said or answered, or when nothing changed since the last save. Never throws.
    /// </summary>
    private void SaveArchive(bool final)
    {
        var archive = Archive;
        string? id;
        DateTimeOffset started;
        TimeSpan elapsed;
        AppSettings s;
        lock (_gate)
        {
            id = _archiveId;
            started = _archiveStartedUtc;
            elapsed = SnapshotLocked().Elapsed;
            s = _settings;
        }
        if (archive is null || id is null || !s.SaveSessionHistory)
        {
            return;
        }
        lock (_archiveGate)
        {
            if (Interlocked.Exchange(ref _archiveDirty, 0) == 0)
            {
                return;
            }
            var segments = Transcript.Snapshot();
            var cards = Board.Cards;
            if (!SessionRecordBuilder.HasContent(segments, cards))
            {
                return;
            }
            var now = Clock.TimeProvider.GetUtcNow();
            var record = SessionRecordBuilder.Build(id, started, final ? now : null, elapsed, s.Scenario, segments, cards, Speakers);
            try
            {
                archive.Save(record);
                lock (_gate)
                {
                    _lastArchiveSave = now;
                }
                _log.LogInformation("Session saved to history: lines={Lines} answers={Answers} final={Final}", record.Lines.Count, record.Summary.AnswerCount, final);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _archiveDirty, 1); // retry at the next save point
                _log.LogWarning("Saving session history failed: {Type}", ex.GetType().Name);
                return;
            }
        }
        ArchiveSaved?.Invoke(id);
    }

    private void AutosaveArchive()
    {
        DateTimeOffset last;
        lock (_gate)
        {
            last = _lastArchiveSave;
        }
        if (Volatile.Read(ref _archiveDirty) == 0 || Clock.TimeProvider.GetUtcNow() - last < ArchiveAutosaveInterval
            || Interlocked.Exchange(ref _archiveSaving, 1) == 1)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                SaveArchive(final: false);
            }
            finally
            {
                Volatile.Write(ref _archiveSaving, 0);
            }
        });
    }

    private async Task ApplyMicrophoneChangeLockedAsync(string reason)
    {
        var mic = _sources[AudioSourceKind.Microphone];
        bool want;
        lock (_gate)
        {
            want = _microphoneEnabled && !_microphoneMuted;
        }
        if (_state == SessionState.Listening)
        {
            if (!want && mic.Pipeline is not null)
            {
                await StopSourceAsync(mic, reason).ConfigureAwait(false);
                if (!_settings.SystemAudioEnabled)
                {
                    await PauseLockedAsync("No audio source is enabled").ConfigureAwait(false);
                }
            }
            else if (want && mic.Pipeline is null)
            {
                try
                {
                    await StartSourceAsync(mic, CancellationToken.None).ConfigureAwait(false);
                    AddGapMarker(mic, "Microphone started");
                }
                catch (Exception ex)
                {
                    await StopSourceAsync(mic, "Microphone failed").ConfigureAwait(false);
                    mic.Missing = ex is CaptureStartFailure { Kind: CaptureFaultKind.DeviceLost };
                    PostNotice("Microphone could not start: " + DescribeStartFailure(ex));
                }
            }
        }
        RaiseStatus(force: true);
    }

    private async Task StartEnabledSourcesAsync(CancellationToken ct)
    {
        bool system, mic;
        lock (_gate)
        {
            system = _settings.SystemAudioEnabled;
            mic = _microphoneEnabled && !_microphoneMuted;
        }
        var tasks = new List<Task>();
        if (system)
        {
            tasks.Add(StartSourceAsync(_sources[AudioSourceKind.System], ct));
        }
        if (mic)
        {
            tasks.Add(StartSourceAsync(_sources[AudioSourceKind.Microphone], ct));
        }
        if (tasks.Count == 0)
        {
            throw new SessionStartException("No audio source is enabled.");
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task StartSourceAsync(SourceRuntime src, CancellationToken ct)
    {
        AppSettings s;
        lock (_gate)
        {
            s = _settings;
        }
        // Resolve the endpoint now: an explicit selection or the current default.
        string? configured = src.Kind == AudioSourceKind.System ? s.PlaybackDeviceId : s.MicrophoneDeviceId;
        var device = configured is not null ? _devices.Find(src.Kind, configured) : _devices.GetDefault(src.Kind);
        if (device is null)
        {
            src.Missing = true;
            throw new CaptureStartFailure(CaptureFaultKind.DeviceLost,
                src.Kind == AudioSourceKind.System ? "The selected playback device is not available." : "The selected microphone is not available.");
        }
        src.Missing = false;
        src.DeviceId = device.Id;
        src.DeviceName = device.Name;
        src.FollowsDefault = configured is null;
        int generation;
        lock (_gate)
        {
            generation = ++src.Generation;
        }
        src.Transcription = TranscriptionHealth.Connecting;
        RaiseStatus(force: true);

        var options = new TranscriptionStreamOptions(s.TranscriptionModel, s.TranscriptionLanguages, null, BuildKeywords(), s.TranscriptionDelay);
        ITranscriptionStream stream;
        try
        {
            stream = await _transcription.ConnectAsync(options, ct).ConfigureAwait(false);
        }
        catch
        {
            src.Transcription = TranscriptionHealth.Failed;
            throw;
        }

        src.Timeline = new AudioTimeline(TimelineRetention, (long)TimelineRetention.TotalSeconds * PipelineAudio.BytesPerSecond + PipelineAudio.BytesPerSecond);
        // Built-in echo cancellation needs system playback as its reference; without system capture the
        // microphone falls back to Windows communications processing.
        var echoMode = s.EchoCancellation;
        if (src.Kind == AudioSourceKind.Microphone && echoMode == EchoCancellationMode.BuiltIn && !s.SystemAudioEnabled)
        {
            echoMode = EchoCancellationMode.Windows;
        }
        EchoCancellingPreprocessor? canceller = src.Kind == AudioSourceKind.Microphone && echoMode == EchoCancellationMode.BuiltIn
            ? new EchoCancellingPreprocessor(() => _sources[AudioSourceKind.System].Timeline)
            : null;
        // Echo checks on caption segments compare the raw microphone with the playback: a partially cancelled
        // echo no longer resembles the playback closely enough to be recognized.
        src.RawTimeline = canceller is null ? null
            : new AudioTimeline(TimelineRetention, (long)TimelineRetention.TotalSeconds * PipelineAudio.BytesPerSecond + PipelineAudio.BytesPerSecond);
        var pipeline = new SourcePipeline(src.Kind, _sessionId, generation, stream, src.Timeline, Clock, this,
            new SourcePipelineOptions(), _loggers.CreateLogger($"Pipeline.{src.Kind}"), canceller, src.RawTimeline);
        bool diarize = src.Kind == AudioSourceKind.System || s.MicrophoneUsage == MicrophoneUsage.Shared;
        DiarizationEngine? diarizer = null;
        if (diarize)
        {
            long session = _sessionId;
            diarizer = new DiarizationEngine(src.Kind, session, generation, src.Timeline, _diarizer, s.DiarizationModel, s.TranscriptionLanguages,
                Speakers, References, Transcript, _diarizationBudget, new DiarizationOptions(), Clock, _loggers.CreateLogger($"Diarization.{src.Kind}"),
                () => IsCurrent(src.Kind, session, generation));
            diarizer.StatusChanged += _ => RaiseStatus();
        }
        else if (src.Kind == AudioSourceKind.Microphone)
        {
            Speakers.EnsureSelfAlias();
        }

        string? reference = null;
        bool windowsProcessing = src.Kind == AudioSourceKind.Microphone && echoMode == EchoCancellationMode.Windows;
        if (windowsProcessing)
        {
            reference = s.PlaybackDeviceId ?? _devices.GetDefault(AudioSourceKind.System)?.Id;
        }
        // The built-in canceller needs the raw microphone: a second (Windows) canceller in front of it would
        // make the echo path non-linear.
        var capture = _captureFactory.Create(new CaptureRequest(src.Kind, device.Id, reference, windowsProcessing));
        capture.ChunkCaptured += pipeline.Enqueue;
        capture.Faulted += fault => _ = Task.Run(() => OnCaptureFaultAsync(src.Kind, generation, fault));

        lock (_gate)
        {
            src.Pipeline = pipeline;
            src.Diarizer = diarizer;
            src.Capture = capture;
            src.Canceller = canceller;
        }
        pipeline.Start();
        try
        {
            var info = await Task.Run(capture.Start, ct).ConfigureAwait(false);
            src.EchoControl = canceller is not null ? EchoControlStatus.BuiltInCanceller : info.EchoControl;
            src.EchoDetail = canceller is not null ? null : info.EchoControlDetail;
            src.DeviceName = info.DeviceName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            src.Transcription = TranscriptionHealth.Off;
            throw new CaptureStartFailure(ex is ICaptureFaultSource f ? f.FaultKind : CaptureFaultKind.Failed, ex.Message, ex);
        }
        src.Transcription = TranscriptionHealth.Connected;
        _log.LogInformation("Source {Kind} generation {Gen} started (echo={Echo})", src.Kind, generation, src.EchoControl);
        RaiseStatus(force: true);
    }

    private async Task StopAllSourcesAsync(string reason)
    {
        AssessOpenMicrophoneSegments(); // while both audio timelines still exist
        await Task.WhenAll(_sources.Values.Select(s => StopSourceAsync(s, reason))).ConfigureAwait(false);
    }

    /// <summary>Stops capture and transmission for one source and invalidates its generation's pending results.</summary>
    private async Task StopSourceAsync(SourceRuntime src, string reason)
    {
        AssessOpenMicrophoneSegments();
        ICaptureSource? capture;
        SourcePipeline? pipeline;
        DiarizationEngine? diarizer;
        int generation;
        EchoCancellingPreprocessor? canceller;
        lock (_gate)
        {
            capture = src.Capture;
            pipeline = src.Pipeline;
            diarizer = src.Diarizer;
            canceller = src.Canceller;
            generation = src.Generation;
            src.Capture = null;
            src.Pipeline = null;
            src.Diarizer = null;
            src.Canceller = null;
            src.Generation++; // any late callback for the old generation is now stale
            src.Speaking = false;
            src.LevelDbfs = -120;
        }
        if (capture is not null)
        {
            try
            {
                await capture.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Capture stop failed: {Type}", ex.GetType().Name);
            }
        }
        if (pipeline is not null)
        {
            src.LastMetrics = pipeline.Metrics;
            var m = src.LastMetrics;
            _log.LogInformation("Source {Kind} stopped ({Reason}): sentMs={Sent} droppedMs={Dropped} turns={Turns} overflows={Overflows}",
                src.Kind, reason, m.SentMs, m.DroppedMs, m.Turns, m.Overflows);
            if (canceller is not null)
            {
                LogCanceller(canceller);
            }
            await pipeline.DisposeAsync().ConfigureAwait(false);
        }
        if (diarizer is not null)
        {
            src.PastLabelLatencies.AddRange(diarizer.LabelLatencies);
            if (src.PastLabelLatencies.Count > 1000)
            {
                src.PastLabelLatencies.RemoveRange(0, src.PastLabelLatencies.Count - 1000);
            }
            await diarizer.StopAsync($"Not labeled: {reason.ToLowerInvariant()}").ConfigureAwait(false);
        }
        if (_sessionId != 0)
        {
            Transcript.AbandonProvisional(_sessionId, src.Kind, generation, $"Interrupted ({reason.ToLowerInvariant()})");
        }
        src.Timeline?.Clear(); // unsent audio and diarization windows are discarded
        src.RawTimeline?.Clear();
        src.Transcription = TranscriptionHealth.Off;
        RaiseStatus(force: true);
    }

    private async Task ClearSessionContentAsync()
    {
        await Assistant.HaltAsync("Reset").ConfigureAwait(false);
        Transcript.Clear();
        Speakers.Clear();
        References.Clear();
        History.Clear();
        Board.Clear();
        Filter.Reset();
        Assembler.Reset();
        lock (_gate)
        {
            _firstCaptionLatency.Clear();
            _finalCaptionLatency.Clear();
            _echoChecked.Clear();
            _lastReviewedSegmentId = null;
            foreach (var s in _sources.Values)
            {
                s.PastLabelLatencies.Clear();
                s.TurnFirstDelta.Clear();
            }
        }
    }

    // ------------------------------------------------------------------ reconnect / faults

    private async Task ReconnectAsync(AudioSourceKind failedSource, int generation, ProviderFailureKind kind, string detail)
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state != SessionState.Listening || !IsCurrent(failedSource, _sessionId, generation))
            {
                return;
            }
            _log.LogWarning("Transcription stream failed source={Source} kind={Kind}", failedSource, kind);
            await StopAllSourcesAsync("Connection lost").ConfigureAwait(false); // suspend capture, clear unsent audio
            if (kind.RequiresUserAction())
            {
                FreezeElapsed();
                SetState(SessionState.Error, $"Transcription stopped: {detail}", canRetry: kind is not ProviderFailureKind.Authentication);
                return;
            }
            SetState(SessionState.Reconnecting, "Connection lost — reconnecting…");
        }
        finally
        {
            _transition.Release();
        }

        for (int attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
        {
            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt - 1))) + TimeSpan.FromMilliseconds(_jitter.Next(0, 750));
            await Task.Delay(delay, Clock.TimeProvider).ConfigureAwait(false);
            await _transition.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_state != SessionState.Reconnecting)
                {
                    return; // the user paused, stopped or reset in the meantime
                }
                SetState(SessionState.Reconnecting, $"Reconnecting (attempt {attempt}/{MaxReconnectAttempts})…");
                try
                {
                    await StartEnabledSourcesAsync(CancellationToken.None).ConfigureAwait(false);
                    AddGapMarkers("Reconnected — audio during the outage was not captured");
                    SetState(SessionState.Listening, null);
                    return;
                }
                catch (Exception ex)
                {
                    await StopAllSourcesAsync("Reconnect failed").ConfigureAwait(false);
                    if (ex is ProviderException pe && pe.Kind.RequiresUserAction())
                    {
                        FreezeElapsed();
                        SetState(SessionState.Error, DescribeStartFailure(ex), canRetry: false);
                        return;
                    }
                    if (ex is CaptureStartFailure)
                    {
                        FreezeElapsed();
                        SetState(SessionState.Paused, DescribeStartFailure(ex));
                        return;
                    }
                }
            }
            finally
            {
                _transition.Release();
            }
        }
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state == SessionState.Reconnecting)
            {
                FreezeElapsed();
                SetState(SessionState.Error, "Could not reconnect to the transcription service. Check the network and select Retry.", canRetry: true);
            }
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task OnCaptureFaultAsync(AudioSourceKind kind, int generation, CaptureFault fault)
    {
        if (!IsCurrent(kind, _sessionId, generation))
        {
            return;
        }
        _sources[kind].Missing = fault.Kind == CaptureFaultKind.DeviceLost;
        // Never present a partial conversation as fully captured: pause everything and ask the user.
        await PauseAsync($"{fault.Message}. Select a source and resume.").ConfigureAwait(false);
    }

    private void OnDeviceChanged(object? sender, AudioDeviceChange change)
    {
        foreach (var src in _sources.Values)
        {
            bool affected;
            lock (_gate)
            {
                if (src.Pipeline is null)
                {
                    continue;
                }
                affected = change.Kind switch
                {
                    AudioDeviceChangeKind.Removed or AudioDeviceChangeKind.StateChanged => change.DeviceId == src.DeviceId,
                    AudioDeviceChangeKind.DefaultChanged => src.FollowsDefault && change.Flow == src.Kind && change.DeviceId != src.DeviceId,
                    _ => false,
                };
            }
            if (!affected)
            {
                continue;
            }
            string what = src.Kind == AudioSourceKind.System ? "Playback device" : "Microphone";
            string message = change.Kind == AudioDeviceChangeKind.DefaultChanged
                ? $"{what}: the Windows default device changed. Resume to continue with the new default."
                : $"{what} was disconnected. Select a source and resume.";
            src.Missing = change.Kind != AudioDeviceChangeKind.DefaultChanged;
            _ = Task.Run(() => PauseAsync(message));
        }
    }

    // ------------------------------------------------------------------ pipeline sink

    void ISourcePipelineSink.OnTurnOpened(SourcePipeline pipeline, TurnKey key, TimeSpan start)
    {
        if (!IsCurrent(pipeline))
        {
            return;
        }
        Diarizer(pipeline.Source)?.NoteTurn(start, start, stillOpen: true);
        // Someone else started talking: a question may follow within seconds, so make sure a connection is warm.
        if (pipeline.Source == AudioSourceKind.System || Settings.MicrophoneUsage == MicrophoneUsage.Shared)
        {
            WarmUpConnection();
        }
    }

    void ISourcePipelineSink.OnTurnPause(SourcePipeline pipeline, TurnKey key, bool paused)
    {
        if (!IsCurrent(pipeline) || pipeline.Source != AudioSourceKind.System)
        {
            return;
        }
        lock (_gate)
        {
            if (paused)
            {
                _quietTurns.Add(key);
            }
            else
            {
                _quietTurns.Remove(key);
            }
        }
        if (paused)
        {
            TryEarlyQuestion(key);
        }
    }

    /// <summary>
    /// Early question start: when the other side has paused (or the turn was committed) and the provisional caption already
    /// ends with a question mark, the answer starts without waiting ~0.8 s for the final transcript plus the debounce.
    /// Live measurement: provisional text equalled the final transcript 0.36–0.55 s after speech ended; the final
    /// arrived after 1.3 s.
    /// </summary>
    private void TryEarlyQuestion(TurnKey key)
    {
        bool quiet;
        lock (_gate)
        {
            quiet = _quietTurns.Contains(key) && _state == SessionState.Listening && key.SessionId == _sessionId;
        }
        if (!quiet || Settings.Scenario == Scenario.Meeting || Transcript.GetByTurn(key) is not { IsFinal: false } provisional)
        {
            return;
        }
        foreach (var candidate in Assembler.OnEarlyQuestion(provisional))
        {
            _log.LogInformation("Question candidate {Id} rev {Rev} repeat={Repeat} (early, provisional caption)", candidate.QuestionId, candidate.Revision, candidate.RepeatOfQuestionId is not null);
            Assistant.SubmitQuestion(candidate);
        }
    }

    private static readonly TimeSpan WarmUpInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Best-effort, throttled request that opens or refreshes a pooled connection to the model service, so the answer
    /// request after a question does not pay connection set-up (1.5-2 s measured on a slow network path).
    /// </summary>
    private void WarmUpConnection()
    {
        var now = Clock.TimeProvider.GetUtcNow();
        string model;
        lock (_gate)
        {
            if (now - _lastWarmUp < WarmUpInterval)
            {
                return;
            }
            _lastWarmUp = now;
            model = _settings.AnswerModel;
        }
        _ = Task.Run(() => _assistantModel.WarmUpAsync(model, CancellationToken.None));
    }

    void ISourcePipelineSink.OnTurnClosed(SourcePipeline pipeline, TurnKey key, TimeSpan start, TimeSpan end, bool committed)
    {
        if (!IsCurrent(pipeline))
        {
            return;
        }
        Diarizer(pipeline.Source)?.NoteTurn(start, end, stillOpen: false);
        if (pipeline.Source == AudioSourceKind.System)
        {
            lock (_gate)
            {
                if (committed)
                {
                    _quietTurns.Add(key); // speech ended; text may still be arriving until the final transcript
                }
                else
                {
                    _quietTurns.Remove(key);
                }
            }
            if (committed)
            {
                TryEarlyQuestion(key);
            }
        }
    }

    void ISourcePipelineSink.OnTranscriptDelta(SourcePipeline pipeline, TurnKey key, string delta, TimeSpan start, TimeSpan end)
    {
        if (!IsCurrent(pipeline))
        {
            return;
        }
        var src = _sources[pipeline.Source];
        lock (_gate)
        {
            if (src.TurnFirstDelta.Add(key.TurnId))
            {
                Record(_firstCaptionLatency, Clock.Now - start);
                if (src.TurnFirstDelta.Count > 2000)
                {
                    src.TurnFirstDelta.Clear();
                }
            }
        }
        Transcript.ApplyDelta(key, delta, start, end, DefaultAttribution(pipeline.Source));
        if (pipeline.Source == AudioSourceKind.Microphone && Transcript.GetByTurn(key) is { } provisional)
        {
            AssessMicrophoneEcho(provisional, force: false);
        }
        TryEarlyQuestion(key);
    }

    void ISourcePipelineSink.OnTranscriptFinal(SourcePipeline pipeline, TurnKey key, string text, TimeSpan start, TimeSpan end, string itemId, UsageInfo? usage)
    {
        if (!IsCurrent(pipeline))
        {
            return;
        }
        lock (_gate)
        {
            Record(_finalCaptionLatency, Clock.Now - end);
            _quietTurns.Remove(key);
        }
        var seg = Transcript.ApplyFinal(key, text, start, end, DefaultAttribution(pipeline.Source), itemId);
        if (seg is null)
        {
            return;
        }
        if (seg.Source == AudioSourceKind.Microphone)
        {
            AssessMicrophoneEcho(Transcript.Get(seg.Id) ?? seg, force: true);
        }
        CheckCrossSourceDuplicate(Transcript.Get(seg.Id) ?? seg);
        Diarizer(pipeline.Source)?.OnSegmentFinalized(seg);
        var current = Transcript.Get(seg.Id) ?? seg;
        if (Settings.Scenario != Scenario.Meeting && current.DuplicateOfSegmentId is null)
        {
            Assembler.OnFinalSegment(current);
            // Evaluate right when the debounce elapses instead of at the next 250 ms tick.
            var due = Assembler.Options.Debounce + TimeSpan.FromMilliseconds(20);
            _ = Task.Delay(due, Clock.TimeProvider).ContinueWith(_ => Tick(), TaskScheduler.Default);
        }
    }

    void ISourcePipelineSink.OnTranscriptItemFailed(SourcePipeline pipeline, TurnKey key, string reason)
    {
        lock (_gate)
        {
            _quietTurns.Remove(key);
        }
        if (IsCurrent(pipeline))
        {
            Transcript.FailTurn(key, reason);
        }
    }

    void ISourcePipelineSink.OnAudioGap(SourcePipeline pipeline, TimeSpan at, TimeSpan dropped, string reason)
    {
        if (IsCurrent(pipeline))
        {
            Transcript.AddMarker(SegmentKind.Gap, pipeline.Source, _sessionId, pipeline.Generation, at, $"{reason} ({dropped.TotalSeconds:F1}s)");
        }
    }

    void ISourcePipelineSink.OnLevel(SourcePipeline pipeline, double levelDbfs, bool speech)
    {
        if (!IsCurrent(pipeline))
        {
            return;
        }
        var src = _sources[pipeline.Source];
        src.LevelDbfs = levelDbfs;
        src.Speaking = speech;
        RaiseStatus();
    }

    void ISourcePipelineSink.OnStreamFailed(SourcePipeline pipeline, ProviderFailureKind kind, string detail)
    {
        if (!IsCurrent(pipeline))
        {
            return;
        }
        _sources[pipeline.Source].Transcription = TranscriptionHealth.Reconnecting;
        _ = Task.Run(() => ReconnectAsync(pipeline.Source, pipeline.Generation, kind, detail));
    }

    // ------------------------------------------------------------------ analysis helpers

    private void Tick()
    {
        if (_disposed || Interlocked.Exchange(ref _tickBusy, 1) == 1)
        {
            return;
        }
        try
        {
            SessionState state;
            AppSettings s;
            lock (_gate)
            {
                state = _state;
                s = _settings;
            }
            if (state == SessionState.Listening)
            {
                foreach (var src in _sources.Values)
                {
                    Diarizer(src.Kind)?.Tick();
                }
                if (s.Scenario != Scenario.Meeting)
                {
                    foreach (var candidate in Assembler.Tick())
                    {
                        _log.LogInformation("Question candidate {Id} rev {Rev} repeat={Repeat}", candidate.QuestionId, candidate.Revision, candidate.RepeatOfQuestionId is not null);
                        Assistant.SubmitQuestion(candidate);
                    }
                    foreach (var check in Assembler.TakeIntentChecks())
                    {
                        DispatchIntentCheck(check, s);
                    }
                }
                else
                {
                    MeetingReviewTick(s);
                }
            }
            Assistant.Tick();
            if (state is SessionState.Listening or SessionState.Paused or SessionState.Reconnecting)
            {
                AutosaveArchive();
            }
            var now = Clock.TimeProvider.GetUtcNow();
            if (now - _lastMetricsLog > TimeSpan.FromSeconds(60) && state == SessionState.Listening)
            {
                _lastMetricsLog = now;
                LogMetrics();
            }
            RaiseStatus();
        }
        catch (Exception ex)
        {
            _log.LogError("Tick failed: {Type}", ex.GetType().Name);
        }
        finally
        {
            Volatile.Write(ref _tickBusy, 0);
        }
    }

    private void MeetingReviewTick(AppSettings s)
    {
        var now = Clock.TimeProvider.GetUtcNow();
        if (now - _lastMeetingReview < TimeSpan.FromSeconds(s.MeetingAnalysisIntervalSeconds))
        {
            return;
        }
        _lastMeetingReview = now;
        var all = Transcript.Snapshot().Where(x => x.IsAnalyzable).ToList();
        int startIndex = _lastReviewedSegmentId is null ? 0 : all.FindIndex(x => x.Id == _lastReviewedSegmentId) + 1;
        if (startIndex < 0 || startIndex >= all.Count)
        {
            return; // no new finalized text
        }
        var fresh = all.Skip(startIndex).ToList();
        var triggers = fresh.SelectMany(x => QuestionHeuristics.MeetingTriggers(x.Text, s.Sensitivity)).Distinct().ToList();
        string? since = _lastReviewedSegmentId;
        _lastReviewedSegmentId = fresh[^1].Id;
        if (triggers.Count == 0)
        {
            // Hybrid detection: the local rules found nothing; let the intent classifier judge what others said.
            var others = fresh.Where(x => Classify(x) == Party.Other).ToList();
            string text = string.Join(" ", others.Select(x => x.Text.Trim()));
            if (s.AiQuestionDetection && _intentClassifier is not null && text.Count(char.IsLetterOrDigit) >= 6)
            {
                long session = _sessionId;
                var request = new IntentRequest(s.Scenario, text, PrecedingContext(others[0].Id), s.IntentModel, s.IntentReasoningEffort);
                RunIntentCheck(request, "meeting", verdict =>
                {
                    if (verdict is { IsQuestion: true } && IsListening(session))
                    {
                        _log.LogInformation("Meeting review requested by AI intent check");
                        Assistant.SubmitMeetingReview([MeetingTrigger.Question], since);
                    }
                });
            }
            return;
        }
        Assistant.SubmitMeetingReview(triggers, since);
    }

    private static readonly TimeSpan IntentTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Interview/defense: classify a turn the local rules were unsure about; a positive verdict emits the question.</summary>
    private void DispatchIntentCheck(IntentCheck check, AppSettings s)
    {
        long session = _sessionId;
        var request = new IntentRequest(s.Scenario, check.Text, PrecedingContext(check.SegmentIds[0]), s.IntentModel, s.IntentReasoningEffort);
        bool started = RunIntentCheck(request, $"turn {check.Id}", verdict =>
        {
            foreach (var candidate in Assembler.ResolveIntent(check, verdict))
            {
                if (!IsListening(session))
                {
                    return;
                }
                _log.LogInformation("Question candidate {Id} rev {Rev} repeat={Repeat} (AI intent check)", candidate.QuestionId, candidate.Revision, candidate.RepeatOfQuestionId is not null);
                Assistant.SubmitQuestion(candidate);
            }
        });
        if (!started)
        {
            Assembler.ResolveIntent(check, null);
        }
    }

    /// <summary>
    /// Runs one intent classification in the background (budgeted, with timeout). Failures fall back to the local
    /// decision (no question). Returns false when the request budget is exhausted.
    /// </summary>
    private bool RunIntentCheck(IntentRequest request, string label, Action<IntentVerdict?> onDone)
    {
        var classifier = _intentClassifier;
        if (classifier is null)
        {
            return false;
        }
        if (!_intentBudget.TryAcquire())
        {
            _log.LogInformation("AI intent check ({Label}) skipped: {Max}/min budget reached; local rules decide", label, _intentBudget.MaxPerMinute);
            return false;
        }
        _ = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            IntentVerdict? verdict = null;
            try
            {
                using var cts = new CancellationTokenSource(IntentTimeout);
                verdict = await classifier.ClassifyAsync(request, cts.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _intentChecks);
                Interlocked.Add(ref _intentInputTokens, verdict.Usage?.InputTokens ?? 0);
                Interlocked.Add(ref _intentOutputTokens, verdict.Usage?.OutputTokens ?? 0);
                _log.LogInformation("AI intent check ({Label}): question={Question} confidence={Confidence:F2} tookMs={Ms}",
                    label, verdict.IsQuestion, verdict.Confidence, sw.ElapsedMilliseconds);
            }
            catch (ProviderException ex)
            {
                _log.LogWarning("AI intent check ({Label}) failed: {Kind} {Code}; local rules decide", label, ex.Kind, ex.Code);
            }
            catch (OperationCanceledException)
            {
                _log.LogWarning("AI intent check ({Label}) timed out after {Ms} ms; local rules decide", label, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _log.LogWarning("AI intent check ({Label}) failed: {Type}; local rules decide", label, ex.GetType().Name);
            }
            try
            {
                onDone(verdict);
            }
            catch (Exception ex)
            {
                _log.LogWarning("AI intent result handling failed: {Type}", ex.GetType().Name);
            }
        });
        return true;
    }

    private bool IsListening(long session)
    {
        lock (_gate)
        {
            return _state == SessionState.Listening && _sessionId == session;
        }
    }

    /// <summary>Up to three analyzable lines before the given segment, labeled by party, for the intent classifier.</summary>
    private string? PrecedingContext(string segmentId)
    {
        var all = Transcript.Snapshot().Where(x => x.IsAnalyzable || x.Id == segmentId).ToList();
        int index = all.FindIndex(x => x.Id == segmentId);
        if (index <= 0)
        {
            return null;
        }
        var lines = all.Take(index).Where(x => x.IsAnalyzable).TakeLast(3)
            .Select(x => (Classify(x) == Party.Self ? "User: " : "Other: ") + x.Text.Trim());
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Timing + acoustic echo assessment of a microphone segment against the system playback timeline. Runs on
    /// provisional captions, on finalization and before stopping, so echo is hidden without waiting for both
    /// transcripts. A mark is withdrawn when later audio shows a local talker (double talk).
    /// </summary>
    private bool AssessMicrophoneEcho(TranscriptSegment seg, bool force)
    {
        if (seg.Source != AudioSourceKind.Microphone || seg.Kind != SegmentKind.Speech || seg.Attribution.Status == AttributionStatus.UserAssigned
            || seg.AudioEnd - seg.AudioStart < TimeSpan.FromMilliseconds(300))
        {
            return false;
        }
        AudioTimeline? sysTimeline, micTimeline;
        lock (_gate)
        {
            if (_sources[AudioSourceKind.System].Pipeline is null)
            {
                return false; // no playback reference: echo cannot be identified (headphones recommended)
            }
            sysTimeline = _sources[AudioSourceKind.System].Timeline;
            micTimeline = _sources[AudioSourceKind.Microphone].RawTimeline ?? _sources[AudioSourceKind.Microphone].Timeline;
            var now = Clock.TimeProvider.GetUtcNow();
            if (!force && _echoChecked.TryGetValue(seg.Id, out var last) && now - last < TimeSpan.FromMilliseconds(700))
            {
                return seg.DuplicateOfSegmentId is not null;
            }
            if (_echoChecked.Count > 2000)
            {
                _echoChecked.Clear();
            }
            _echoChecked[seg.Id] = now;
        }
        if (sysTimeline is null || micTimeline is null)
        {
            return false;
        }
        var assessment = EchoAnalyzer.Assess(micTimeline.Extract(seg.AudioStart, seg.AudioEnd),
            sysTimeline.Extract(seg.AudioStart - EchoSearchLead, seg.AudioEnd), EchoSearchLead);
        bool marked = seg.DuplicateOfSegmentId is not null;
        if (assessment.IsEcho && !marked)
        {
            var counterpart = Transcript.Query(x => x.Source == AudioSourceKind.System && x.Kind == SegmentKind.Speech && x.SessionId == seg.SessionId
                                                    && x.AudioStart < seg.AudioEnd && x.AudioEnd > seg.AudioStart)
                .MaxBy(x => SpeakerReconciler.Overlap(x.AudioStart, x.AudioEnd, seg.AudioStart, seg.AudioEnd));
            Transcript.MarkDuplicate(seg.Id, counterpart?.Id ?? "system-audio", EchoNote);
            _log.LogInformation("Microphone echo hidden corr={Corr:F2} lagMs={Lag:F0} unexplained={Unexplained:P0} burstMs={Burst} final={Final}",
                assessment.Correlation, assessment.Lag.TotalMilliseconds, assessment.UnexplainedFraction, assessment.LocalBurstHops * 10, seg.IsFinal);
        }
        else if (!assessment.IsEcho && marked && seg.Note == EchoNote && assessment.MicActiveHops >= 20 && assessment.HasLocalSpeech)
        {
            Transcript.ClearDuplicate(seg.Id);
            _log.LogInformation("Microphone echo mark withdrawn: local speech detected (unexplained={Unexplained:P0})", assessment.UnexplainedFraction);
        }
        return assessment.IsEcho;
    }

    private void AssessOpenMicrophoneSegments()
    {
        long session = _sessionId;
        foreach (var seg in Transcript.Query(s => s.SessionId == session && s.Source == AudioSourceKind.Microphone && s.Kind == SegmentKind.Speech && !s.IsFinal))
        {
            AssessMicrophoneEcho(seg, force: true);
        }
    }

    /// <summary>
    /// Conservative cross-source duplicate suppression: requires overlapping timing, similar text AND
    /// acoustic correlation with the expected lead/lag direction. Text similarity alone never suppresses.
    /// </summary>
    private void CheckCrossSourceDuplicate(TranscriptSegment seg)
    {
        var sys = _sources[AudioSourceKind.System];
        var mic = _sources[AudioSourceKind.Microphone];
        if (sys.Pipeline is null || mic.Pipeline is null || sys.Timeline is null || mic.Timeline is null || seg.DuplicateOfSegmentId is not null)
        {
            return;
        }
        var other = seg.Source == AudioSourceKind.System ? AudioSourceKind.Microphone : AudioSourceKind.System;
        var tolerance = TimeSpan.FromMilliseconds(600);
        var counterparts = Transcript.Query(x => x.Kind == SegmentKind.Speech && x.IsFinal && x.Source == other && x.SessionId == seg.SessionId
                                                 && x.DuplicateOfSegmentId is null && x.AudioStart <= seg.AudioEnd + tolerance && x.AudioEnd >= seg.AudioStart - tolerance);
        foreach (var c in counterparts)
        {
            var sysSeg = seg.Source == AudioSourceKind.System ? seg : c;
            var micSeg = seg.Source == AudioSourceKind.Microphone ? seg : c;
            if (TextTools.Containment(micSeg.Text, sysSeg.Text) < DuplicateJudge.MinContainment
                && TextTools.Containment(sysSeg.Text, micSeg.Text) < DuplicateJudge.MinContainment)
            {
                continue;
            }
            var from = Min(sysSeg.AudioStart, micSeg.AudioStart);
            var to = Max(sysSeg.AudioEnd, micSeg.AudioEnd);
            var sysAudio = sys.Timeline.Extract(from, to);
            var micAudio = (mic.RawTimeline ?? mic.Timeline).Extract(from, to);
            var playbackLeads = EchoAnalyzer.Correlate(sysAudio, micAudio, TimeSpan.FromMilliseconds(500));
            var micLeads = EchoAnalyzer.Correlate(micAudio, sysAudio, TimeSpan.FromMilliseconds(500));
            var verdict = DuplicateJudge.Decide(micSeg.Text, sysSeg.Text, playbackLeads, micLeads);
            _log.LogInformation("Duplicate check corrPlaybackLeads={A:F2} corrMicLeads={B:F2} verdict={Verdict}", playbackLeads.Correlation, micLeads.Correlation, verdict);
            if (verdict == DuplicateVerdict.MicrophoneIsEcho)
            {
                Transcript.MarkDuplicate(micSeg.Id, sysSeg.Id, "Loudspeaker echo of system audio (hidden from analysis)");
            }
            else if (verdict == DuplicateVerdict.PlaybackIsMonitorEcho)
            {
                Transcript.MarkDuplicate(sysSeg.Id, micSeg.Id, "Microphone monitoring echo in playback (hidden from analysis)");
            }
        }
    }

    private Party Classify(TranscriptSegment seg)
    {
        var a = seg.Attribution;
        if (a.Status == AttributionStatus.SourceAlias || a.SpeakerId == SpeakerRegistry.SelfAliasId)
        {
            return Party.Self;
        }
        if (Speakers.Get(a.SpeakerId) is { Role: SpeakerRole.Me })
        {
            return Party.Self;
        }
        if (seg.Source == AudioSourceKind.Microphone && Settings.MicrophoneUsage == MicrophoneUsage.Personal)
        {
            return Party.Self;
        }
        return Party.Other;
    }

    private AudioAttributionDefault DefaultAttribution(AudioSourceKind source)
    {
        if (source == AudioSourceKind.Microphone && Settings.MicrophoneUsage == MicrophoneUsage.Personal)
        {
            return new AudioAttributionDefault(new SpeakerAttribution(AttributionStatus.SourceAlias, SpeakerRegistry.SelfAliasId, [], 0, "Personal microphone alias"));
        }
        return AudioAttributionDefault.Pending;
    }

    private IReadOnlyList<string> BuildKeywords()
    {
        var terms = Preparation.Get(PrepField.Terminology);
        return terms.Split(['\n', ',', ';', '，', '；', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length is > 1 and <= 60 && !t.Contains('<') && !t.Contains('>'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();
    }

    // ------------------------------------------------------------------ state helpers

    private bool IsCurrent(SourcePipeline pipeline) => IsCurrent(pipeline.Source, pipeline.SessionId, pipeline.Generation);

    private bool IsCurrent(AudioSourceKind kind, long sessionId, int generation)
    {
        lock (_gate)
        {
            var src = _sources[kind];
            return sessionId == _sessionId && src.Pipeline is not null && src.Generation == generation
                   && _state is SessionState.Listening or SessionState.Starting;
        }
    }

    private DiarizationEngine? Diarizer(AudioSourceKind kind)
    {
        lock (_gate)
        {
            return _sources[kind].Diarizer;
        }
    }

    private void AddGapMarkers(string note)
    {
        foreach (var src in _sources.Values.Where(s => s.Pipeline is not null))
        {
            AddGapMarker(src, note);
        }
    }

    private void AddGapMarker(SourceRuntime src, string note)
    {
        if (_sessionId != 0)
        {
            Transcript.AddMarker(SegmentKind.Gap, src.Kind, _sessionId, src.Generation, Clock.Now, note);
        }
    }

    private void FreezeElapsed()
    {
        lock (_gate)
        {
            if (_startedUtc is { } started && _state is SessionState.Listening or SessionState.Reconnecting or SessionState.Starting or SessionState.Stopping)
            {
                _accumulated += Clock.TimeProvider.GetUtcNow() - started;
                _startedUtc = Clock.TimeProvider.GetUtcNow();
            }
        }
    }

    private void SetState(SessionState state, string? message, bool canRetry = false)
    {
        lock (_gate)
        {
            if (state == SessionState.Listening && _state != SessionState.Listening)
            {
                _startedUtc = Clock.TimeProvider.GetUtcNow();
            }
            _state = state;
            _message = message;
            _canRetry = canRetry;
        }
        _log.LogInformation("State -> {State}", state);
        RaiseStatus(force: true);
    }

    private void PostNotice(string message)
    {
        lock (_gate)
        {
            _message = message;
        }
        RaiseStatus(force: true);
    }

    private void RaiseStatus(bool force = false)
    {
        SessionSnapshot snap;
        lock (_gate)
        {
            var now = Clock.TimeProvider.GetUtcNow();
            if (!force && now - _lastStatusRaise < TimeSpan.FromMilliseconds(150))
            {
                return;
            }
            _lastStatusRaise = now;
            snap = SnapshotLocked();
        }
        StatusChanged?.Invoke(snap);
    }

    private SessionSnapshot SnapshotLocked()
    {
        var elapsed = _accumulated;
        if (_state == SessionState.Listening && _startedUtc is { } started)
        {
            elapsed += Clock.TimeProvider.GetUtcNow() - started;
        }
        return new SessionSnapshot(_state, _message, _canRetry, _sessionId, elapsed,
            SourceStatusLocked(_sources[AudioSourceKind.System], _settings.SystemAudioEnabled, false),
            SourceStatusLocked(_sources[AudioSourceKind.Microphone], _microphoneEnabled, _microphoneMuted),
            Assistant.Status);
    }

    private static SourceStatus SourceStatusLocked(SourceRuntime s, bool enabled, bool muted) =>
        new(s.Kind, enabled, muted, s.Pipeline is not null, s.Missing, s.DeviceName, s.LevelDbfs, s.Speaking, s.Transcription,
            s.Diarizer?.Status, s.EchoControl, s.Canceller?.Describe() ?? s.EchoDetail, s.Generation, s.Pipeline?.Metrics ?? s.LastMetrics);

    private void LogCanceller(EchoCancellingPreprocessor c) =>
        _log.LogInformation("Echo canceller blocks={Blocks} withReference={Ref} heldOut={Held} adapted={Adapted} erleDb={Erle:F1}",
            c.Blocks, c.BlocksWithReference, c.BlocksHeldOut, c.Canceller.Adapted, c.Canceller.EchoReductionDb);

    private void LogMetrics()
    {
        if (_intentClassifier is not null)
        {
            _log.LogInformation("AI intent checks={Checks} lastMinute={Used}/{Max} rejectedByBudget={Rejected} inputTokens={In} outputTokens={Out}",
                Interlocked.Read(ref _intentChecks), _intentBudget.UsedInLastMinute, _intentBudget.MaxPerMinute, _intentBudget.TotalRejected,
                Interlocked.Read(ref _intentInputTokens), Interlocked.Read(ref _intentOutputTokens));
        }
        foreach (var s in _sources.Values.Where(s => s.Pipeline is not null))
        {
            var m = s.Pipeline!.Metrics;
            var d = s.Diarizer?.Status;
            _log.LogInformation("Metrics source={Source} queuedMs={Q} droppedMs={D} sentMs={S} turns={T} overflows={O} driftPpm={Drift} diarReq={DR} diarFail={DF} coveredS={C:F0} skippedS={K:F0} timelineKB={KB}",
                s.Kind, m.QueuedMs, m.DroppedMs, m.SentMs, m.Turns, m.Overflows, m.DriftPpm?.ToString("F0") ?? "n/a", d?.Requests, d?.Failures,
                d?.Covered.TotalSeconds, d?.Skipped.TotalSeconds, (s.Timeline?.Bytes ?? 0) / 1024);
            if (s.Canceller is { } c)
            {
                LogCanceller(c);
            }
        }
        foreach (var l in LatencyReport())
        {
            _log.LogInformation("Latency {Metric}: n={N} median={Median} p90={P90}", l.Metric, l.Samples, l.MedianSeconds?.ToString("F2") ?? "n/a", l.P90Seconds?.ToString("F2") ?? "n/a");
        }
        var a = Assistant.Status;
        _log.LogInformation("Assistant requests={Total} lastMinute={Used}/{Budget} inputTokens={In} outputTokens={Out} refClips={Refs} refKB={RefKB}",
            a.TotalRequests, a.RequestsLastMinute, a.Budget, a.InputTokens, a.OutputTokens, References.Count, References.Bytes / 1024);
    }

    private static string DescribeStartFailure(Exception ex) => ex switch
    {
        ProviderException pe => pe.Message,
        CaptureStartFailure cf => cf.Message,
        SessionStartException se => se.Message,
        _ => $"Could not start ({ex.GetType().Name}).",
    };

    private static void Record(List<TimeSpan> list, TimeSpan value)
    {
        list.Add(value);
        if (list.Count > 1000)
        {
            list.RemoveAt(0);
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static InsightFilterOptions FilterOptions(AppSettings s) => new()
    {
        Cooldown = TimeSpan.FromSeconds(s.MeetingCooldownSeconds),
        MaxPerMinute = s.MeetingMaxNotificationsPerMinute,
        SuppressionWindow = TimeSpan.FromMinutes(s.MeetingSuppressionMinutes),
    };

    private QuestionAssemblerOptions AssemblerOptions(AppSettings s) => new()
    {
        Debounce = TimeSpan.FromMilliseconds(s.QuestionDebounceMilliseconds),
        Sensitivity = s.Sensitivity,
        AiIntent = s.AiQuestionDetection && _intentClassifier is not null,
    };

    private sealed class SourceRuntime(AudioSourceKind kind)
    {
        public AudioSourceKind Kind { get; } = kind;
        public int Generation { get; set; }
        public string? DeviceId { get; set; }
        public string? DeviceName { get; set; }
        public bool FollowsDefault { get; set; }
        public bool Missing { get; set; }
        public ICaptureSource? Capture { get; set; }
        public SourcePipeline? Pipeline { get; set; }
        public DiarizationEngine? Diarizer { get; set; }
        public AudioTimeline? Timeline { get; set; }
        /// <summary>Unprocessed microphone audio (only while the built-in echo canceller runs), for echo checks.</summary>
        public AudioTimeline? RawTimeline { get; set; }
        public TranscriptionHealth Transcription { get; set; }
        public EchoControlStatus EchoControl { get; set; } = EchoControlStatus.NotApplicable;
        public string? EchoDetail { get; set; }
        public EchoCancellingPreprocessor? Canceller { get; set; }
        public double LevelDbfs { get; set; } = -120;
        public bool Speaking { get; set; }
        public PipelineMetrics? LastMetrics { get; set; }
        public List<TimeSpan> PastLabelLatencies { get; } = new();
        public HashSet<long> TurnFirstDelta { get; } = new();
    }
}

/// <summary>Capture start failure with a classified cause, raised by the coordinator.</summary>
public sealed class CaptureStartFailure(CaptureFaultKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public CaptureFaultKind Kind { get; } = kind;
}

/// <summary>Implemented by capture exceptions that carry a classified fault kind.</summary>
public interface ICaptureFaultSource
{
    CaptureFaultKind FaultKind { get; }
}
