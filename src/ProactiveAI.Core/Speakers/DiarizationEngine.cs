using Microsoft.Extensions.Logging;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Session;
using ProactiveAI.Core.Transcript;

namespace ProactiveAI.Core.Speakers;

public sealed record DiarizationOptions
{
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan Overlap { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxBacklog { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan MinWindow { get; init; } = TimeSpan.FromSeconds(4);
    public TimeSpan FlushAfterSilence { get; init; } = TimeSpan.FromSeconds(2.0);
    public TimeSpan MinSpeech { get; init; } = TimeSpan.FromSeconds(1);
    public int MaxReferences { get; init; } = 4;
    public TimeSpan SpanRetention { get; init; } = TimeSpan.FromSeconds(180);
    public TimeSpan Hop => Window - Overlap;
}

public enum DiarizationHealth
{
    Idle,
    Working,
    Limited,
    Degraded,
    Stopped,
}

public sealed record DiarizationStatus(
    AudioSourceKind Source,
    DiarizationHealth Health,
    string? Detail,
    int Requests,
    int Failures,
    TimeSpan Covered,
    TimeSpan Skipped,
    TimeSpan? LastLabelLatency);

/// <summary>
/// Secondary speaker-attribution path for one source generation: submits bounded overlapping audio
/// windows from memory to a diarization endpoint, reconciles request-local labels to session
/// speakers, and revises segment attribution in place. At most one request in flight; the next
/// window is recomputed (replacing any older pending window) when it completes.
/// </summary>
public sealed class DiarizationEngine
{
    private readonly object _gate = new();
    private readonly AudioSourceKind _source;
    private readonly long _sessionId;
    private readonly int _generation;
    private readonly AudioTimeline _audio;
    private readonly ISpeakerDiarizer _diarizer;
    private readonly string _model;
    private readonly IReadOnlyList<string> _languages;
    private readonly SpeakerRegistry _registry;
    private readonly ReferenceClipStore _references;
    private readonly TranscriptStore _transcript;
    private readonly RequestBudget _budget;
    private readonly DiarizationOptions _o;
    private readonly SessionClock _clock;
    private readonly ILogger _log;
    private readonly Func<bool> _isCurrent;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<(TimeSpan Start, TimeSpan End)> _speech = new();
    private readonly List<AttributedSpan> _spans = new();
    private readonly Dictionary<string, double> _segmentCoverage = new(StringComparer.Ordinal);
    private readonly List<TimeSpan> _latencies = new();

    private TimeSpan? _lastWindowEnd;
    private TimeSpan _coveredUntil = TimeSpan.MinValue;
    private TimeSpan? _lastSpeechEnd;
    private bool _turnOpen;
    private Task? _inFlight;
    private DateTimeOffset _retryNotBefore;
    private int _consecutiveFailures;
    private bool _stopped;
    private bool _disabled;
    private int _requests;
    private int _failures;
    private TimeSpan _covered;
    private TimeSpan _skipped;
    private DiarizationHealth _health = DiarizationHealth.Idle;
    private string? _detail;

    public DiarizationEngine(
        AudioSourceKind source,
        long sessionId,
        int generation,
        AudioTimeline audio,
        ISpeakerDiarizer diarizer,
        string model,
        IReadOnlyList<string> languages,
        SpeakerRegistry registry,
        ReferenceClipStore references,
        TranscriptStore transcript,
        RequestBudget budget,
        DiarizationOptions options,
        SessionClock clock,
        ILogger log,
        Func<bool> isCurrent)
    {
        _source = source;
        _sessionId = sessionId;
        _generation = generation;
        _audio = audio;
        _diarizer = diarizer;
        _model = model;
        _languages = languages;
        _registry = registry;
        _references = references;
        _transcript = transcript;
        _budget = budget;
        _o = options;
        _clock = clock;
        _log = log;
        _isCurrent = isCurrent;
    }

    public event Action<DiarizationStatus>? StatusChanged;

    public IReadOnlyList<TimeSpan> LabelLatencies
    {
        get { lock (_gate) { return _latencies.ToList(); } }
    }

    public DiarizationStatus Status
    {
        get { lock (_gate) { return StatusLocked(); } }
    }

    /// <summary>Records a locally detected speech turn (open turns extend while speech continues).</summary>
    public void NoteTurn(TimeSpan start, TimeSpan end, bool stillOpen)
    {
        lock (_gate)
        {
            if (_speech.Count > 0 && _speech[^1].Start == start)
            {
                _speech[^1] = (start, end);
            }
            else
            {
                _speech.Add((start, end));
            }
            _turnOpen = stillOpen;
            if (!stillOpen)
            {
                _lastSpeechEnd = end;
            }
            var horizon = end - _o.SpanRetention;
            _speech.RemoveAll(s => s.End < horizon);
        }
    }

    /// <summary>Called periodically; starts a request when a window is due and budget allows.</summary>
    public void Tick()
    {
        Window? window;
        IReadOnlyList<(string SpeakerId, short[] Pcm)> refs;
        short[]? pcm;
        lock (_gate)
        {
            if (_stopped || _disabled || _inFlight is not null || _clock.TimeProvider.GetUtcNow() < _retryNotBefore)
            {
                return;
            }
            window = ComputeWindowLocked(_clock.Now);
            if (window is null)
            {
                return;
            }
            if (!_budget.TryAcquire())
            {
                SetHealthLocked(DiarizationHealth.Limited, "Speaker-label request budget reached; labels delayed");
                return;
            }
            if (window.Skipped > TimeSpan.Zero)
            {
                _skipped += window.Skipped;
            }
            pcm = _audio.Extract(window.Start, window.End);
            if (pcm is null)
            {
                _lastWindowEnd = window.End;
                return;
            }
            refs = _references.Select(_registry.ForSource(_source).Select(s => s.Id), _o.MaxReferences);
            _requests++;
            SetHealthLocked(DiarizationHealth.Working, null);
            var request = new DiarizationRequest(_model, pcm, refs.Select(r => new SpeakerReference(r.SpeakerId, r.Pcm)).ToList(), _languages);
            var refMap = refs.ToDictionary(r => r.SpeakerId, r => r.SpeakerId, StringComparer.Ordinal);
            var w = window;
            _inFlight = Task.Run(() => RunAsync(w, pcm, request, refMap, _cts.Token));
        }
        RaiseStatus();
    }

    /// <summary>Resolves a newly finalized segment from spans already diarized.</summary>
    public void OnSegmentFinalized(TranscriptSegment segment)
    {
        if (segment.Source != _source || segment.SessionId != _sessionId || segment.Generation != _generation || segment.Kind != SegmentKind.Speech)
        {
            return;
        }
        List<AttributedSpan> spans;
        TimeSpan covered;
        lock (_gate)
        {
            spans = _spans.ToList();
            covered = _coveredUntil;
        }
        ApplyResolution(segment, spans, covered);
    }

    public async Task StopAsync(string reason)
    {
        Task? inflight;
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }
            _stopped = true;
            inflight = _inFlight;
            SetHealthLocked(DiarizationHealth.Stopped, reason);
        }
        _cts.Cancel();
        if (inflight is not null)
        {
            try
            {
                await inflight.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
                // cancelled or timed out; results are rejected by the stopped flag
            }
        }
        foreach (var seg in _transcript.Query(s => s.Source == _source && s.SessionId == _sessionId && s.Generation == _generation && s.Attribution.Status == AttributionStatus.Pending))
        {
            _transcript.UpdateAttribution(seg.Id, _ => new SpeakerAttribution(AttributionStatus.Unavailable, null, [], 0, reason));
        }
        lock (_gate)
        {
            _spans.Clear();
            _speech.Clear();
        }
        _cts.Dispose();
        RaiseStatus();
    }

    private Window? ComputeWindowLocked(TimeSpan now)
    {
        if (_audio.Start is not { } audioStart || _audio.End is not { } audioEnd)
        {
            return null;
        }
        // Windows advance in order over unprocessed audio so nothing is skipped silently.
        var from = Max(_lastWindowEnd ?? audioStart, audioStart);
        bool regular = audioEnd - from >= _o.Hop;
        bool flush = _lastSpeechEnd is { } se && se > from && !_turnOpen && now - se >= _o.FlushAfterSilence;
        if (!regular && !flush)
        {
            return null;
        }
        var skipped = TimeSpan.Zero;
        if (audioEnd - from > _o.MaxBacklog)
        {
            // Too far behind (e.g. budget-limited): jump ahead and record the uncovered span.
            var resume = audioEnd - _o.Window;
            skipped = resume - from;
            from = resume;
            _lastWindowEnd = null;
        }
        var start = _lastWindowEnd is { } le ? Max(le - _o.Overlap, audioStart) : from;
        var end = Min(audioEnd, start + _o.Window);
        if (!regular && _lastSpeechEnd is { } speechEnd)
        {
            end = Min(end, speechEnd + TimeSpan.FromMilliseconds(300));
        }
        if (end - start < _o.MinWindow)
        {
            start = Max(audioStart, end - _o.MinWindow);
        }
        if (end - start < TimeSpan.FromSeconds(1.5))
        {
            return null;
        }
        var speech = SpeechWithinLocked(start, end);
        if (speech < _o.MinSpeech)
        {
            _lastWindowEnd = end; // nothing worth labeling
            _coveredUntil = Max(_coveredUntil, end);
            return null;
        }
        return new Window(start, end, skipped);
    }

    private TimeSpan SpeechWithinLocked(TimeSpan start, TimeSpan end)
    {
        TimeSpan total = TimeSpan.Zero;
        foreach (var s in _speech)
        {
            total += SpeakerReconciler.Overlap(start, end, s.Start, s.End);
        }
        return total;
    }

    private async Task RunAsync(Window window, short[] pcm, DiarizationRequest request, IReadOnlyDictionary<string, string> refMap, CancellationToken ct)
    {
        DiarizationResult? result = null;
        ProviderException? failure = null;
        var started = _clock.Now;
        try
        {
            result = await _diarizer.DiarizeAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ProviderException ex)
        {
            failure = ex;
        }
        catch (Exception ex)
        {
            failure = new ProviderException(ProviderFailureKind.Unknown, "unexpected", ex.GetType().Name);
        }

        lock (_gate)
        {
            _inFlight = null;
            if (_stopped || ct.IsCancellationRequested || !_isCurrent())
            {
                return;
            }
            if (failure is not null)
            {
                _failures++;
                _consecutiveFailures++;
                if (failure.Kind.RequiresUserAction())
                {
                    _disabled = true;
                    SetHealthLocked(DiarizationHealth.Degraded, $"Speaker labels unavailable ({failure.Kind}): {failure.Message}");
                }
                else
                {
                    var backoff = TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, _consecutiveFailures - 1)));
                    _retryNotBefore = _clock.TimeProvider.GetUtcNow() + (failure.RetryAfter is { } ra && ra > backoff ? ra : backoff);
                    SetHealthLocked(DiarizationHealth.Degraded, $"Speaker-label request failed ({failure.Kind}); retrying");
                }
                _log.LogWarning("Diarization failed source={Source} kind={Kind} code={Code}", _source, failure.Kind, failure.Code);
            }
        }

        if (result is not null && failure is null)
        {
            Apply(window, pcm, result, refMap);
            _log.LogInformation("Diarization window source={Source} len={Len:F1}s spans={Spans} refs={Refs} took={Took:F1}s",
                _source, (window.End - window.Start).TotalSeconds, result.Spans.Count, refMap.Count, (_clock.Now - started).TotalSeconds);
        }
        RaiseStatus();
        if (!_stopped)
        {
            Tick();
        }
    }

    private void Apply(Window window, short[] pcm, DiarizationResult result, IReadOnlyDictionary<string, string> refMap)
    {
        var length = window.End - window.Start;
        var spans = result.Spans
            .Where(s => s.EndSeconds > s.StartSeconds && !string.IsNullOrWhiteSpace(s.RawSpeaker))
            .Select(s => new WindowSpan(
                s.RawSpeaker.Trim(),
                window.Start + TimeSpan.FromSeconds(Math.Clamp(s.StartSeconds, 0, length.TotalSeconds)),
                window.Start + TimeSpan.FromSeconds(Math.Clamp(s.EndSeconds, 0, length.TotalSeconds)),
                s.Text))
            .Where(s => s.End > s.Start)
            .ToList();

        List<AttributedSpan> allSpans;
        TimeSpan covered;
        lock (_gate)
        {
            if (_stopped || !_isCurrent())
            {
                return;
            }
            var previous = _spans.Where(p => p.End > window.Start).ToList();
            var mappings = SpeakerReconciler.Reconcile(spans, refMap, previous);
            int knownForSource = _registry.ForSource(_source).Count;
            var labelToSpeaker = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var m in mappings)
            {
                if (m.SpeakerId is { } id)
                {
                    labelToSpeaker[m.RawLabel] = id;
                }
                else
                {
                    // A new label may be a returning speaker whose reference was not sent (provider accepts at most 4).
                    bool provisional = knownForSource > refMap.Count;
                    labelToSpeaker[m.RawLabel] = _registry.CreateDiarized(_source, provisional).Id;
                }
            }

            // Replace earlier attribution inside this window's non-overlap region with the fresh result.
            _spans.RemoveAll(p => p.Start >= window.Start + _o.Overlap && p.End <= window.End);
            foreach (var s in spans)
            {
                _spans.Add(new AttributedSpan(labelToSpeaker[s.RawLabel], s.Start, s.End));
            }
            var horizon = window.End - _o.SpanRetention;
            _spans.RemoveAll(p => p.End < horizon);
            _covered += _lastWindowEnd is { } le && le > window.Start ? window.End - le : length;
            _lastWindowEnd = window.End;
            _coveredUntil = Max(_coveredUntil, window.End);
            _consecutiveFailures = 0;
            SetHealthLocked(DiarizationHealth.Idle, null);

            HarvestReferences(window, pcm, spans, labelToSpeaker);
            allSpans = _spans.ToList();
            covered = _coveredUntil;
        }

        foreach (var seg in _transcript.Query(s => s.Source == _source && s.SessionId == _sessionId && s.Generation == _generation
                     && s.Kind == SegmentKind.Speech && s.IsFinal && s.AudioEnd > window.Start - _o.Overlap && s.AudioStart < window.End))
        {
            ApplyResolution(seg, allSpans, covered);
        }
    }

    private void ApplyResolution(TranscriptSegment segment, IReadOnlyList<AttributedSpan> spans, TimeSpan coveredUntil)
    {
        if (segment.Attribution.Status is AttributionStatus.UserAssigned or AttributionStatus.SourceAlias)
        {
            return;
        }
        var res = AttributionResolver.Resolve(segment.AudioStart, segment.AudioEnd, spans, coveredUntil);
        if (res.Attribution is null)
        {
            return;
        }
        bool wasPending;
        lock (_gate)
        {
            double prev = _segmentCoverage.GetValueOrDefault(segment.Id, -1);
            if (segment.Attribution.Status is AttributionStatus.Assigned or AttributionStatus.Multiple && res.Coverage + 0.05 < prev)
            {
                return; // keep the better-supported earlier assignment
            }
            _segmentCoverage[segment.Id] = res.Coverage;
            if (_segmentCoverage.Count > 4000)
            {
                _segmentCoverage.Clear();
            }
            wasPending = segment.Attribution.Status == AttributionStatus.Pending;
        }
        var updated = _transcript.UpdateAttribution(segment.Id, current =>
        {
            if (current.Attribution.Status is AttributionStatus.UserAssigned or AttributionStatus.SourceAlias)
            {
                return null;
            }
            var next = res.Attribution with { Version = current.Attribution.Version };
            return next.Status == current.Attribution.Status && next.SpeakerId == current.Attribution.SpeakerId
                   && next.OtherSpeakerIds.SequenceEqual(current.Attribution.OtherSpeakerIds) ? null : next;
        });
        if (updated is not null && wasPending)
        {
            var latency = _clock.Now - segment.AudioEnd;
            lock (_gate)
            {
                _latencies.Add(latency);
                if (_latencies.Count > 500)
                {
                    _latencies.RemoveAt(0);
                }
            }
        }
        if (updated?.Attribution.SpeakerId is { } sid)
        {
            _references.Touch(sid, segment.AudioEnd);
        }
    }

    private void HarvestReferences(Window window, short[] pcm, List<WindowSpan> spans, Dictionary<string, string> labelToSpeaker)
    {
        var guard = TimeSpan.FromMilliseconds(250);
        foreach (var group in spans.GroupBy(s => labelToSpeaker[s.RawLabel]))
        {
            var clean = group
                .Where(s => s.Duration >= ReferenceClipStore.MinClip)
                .Where(s => !spans.Any(o => labelToSpeaker[o.RawLabel] != group.Key
                                            && SpeakerReconciler.Overlap(s.Start - guard, s.End + guard, o.Start, o.End) > TimeSpan.Zero))
                .OrderByDescending(s => s.Duration)
                .FirstOrDefault();
            if (clean is null)
            {
                continue;
            }
            int from = PipelineAudio.Samples(clean.Start - window.Start + TimeSpan.FromMilliseconds(100));
            int len = PipelineAudio.Samples(Min(clean.Duration - TimeSpan.FromMilliseconds(200), ReferenceClipStore.MaxClip));
            if (from < 0 || len <= 0 || from + len > pcm.Length)
            {
                continue;
            }
            _references.Offer(group.Key, pcm.AsSpan(from, len).ToArray(), clean.End);
        }
    }

    private void SetHealthLocked(DiarizationHealth health, string? detail)
    {
        _health = health;
        _detail = detail;
    }

    private DiarizationStatus StatusLocked() =>
        new(_source, _health, _detail, _requests, _failures, _covered, _skipped, _latencies.Count > 0 ? _latencies[^1] : null);

    private void RaiseStatus()
    {
        DiarizationStatus status;
        lock (_gate)
        {
            status = StatusLocked();
        }
        StatusChanged?.Invoke(status);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private sealed record Window(TimeSpan Start, TimeSpan End, TimeSpan Skipped);
}
