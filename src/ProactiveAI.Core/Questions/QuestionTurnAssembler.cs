using Microsoft.Extensions.Logging;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Text;
using ProactiveAI.Core.Transcript;

namespace ProactiveAI.Core.Questions;

public enum Party
{
    /// <summary>The user (personal microphone alias or a speaker the user marked as "Me").</summary>
    Self,
    /// <summary>Anyone else, including speakers whose label is still pending.</summary>
    Other,
}

public sealed record QuestionCandidate(
    string QuestionId,
    int Revision,
    string Text,
    IReadOnlyList<string> SegmentIds,
    AudioSourceKind Source,
    string? SpeakerId,
    bool SpeakerUncertain,
    bool MultiPart,
    string? RepeatOfQuestionId,
    bool Manual,
    TimeSpan EndedAt,
    DateTimeOffset DetectedAtUtc);

public sealed record QuestionAssemblerOptions
{
    public TimeSpan Debounce { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ContinuationGap { get; init; } = TimeSpan.FromSeconds(4);
    public int Sensitivity { get; init; } = 2;
    public TimeSpan RepeatWindow { get; init; } = TimeSpan.FromMinutes(5);
    public double RepeatSimilarity { get; init; } = 0.75;
    /// <summary>When the local heuristics are not confident, ask an AI intent check (see <see cref="QuestionTurnAssembler.TakeIntentChecks"/>).</summary>
    public bool AiIntent { get; init; }
    /// <summary>Turns with fewer letters/digits than this ("好的。", "Okay.") are not worth an intent check.</summary>
    public int MinIntentChars { get; init; } = 6;
}

/// <summary>A turn the local heuristics were unsure about, waiting for an AI intent verdict.</summary>
public sealed record IntentCheck(long Id, int Epoch, string Text, IReadOnlyList<string> SegmentIds, double LocalScore);

/// <summary>
/// Event-driven question-turn assembly for interview/defense mode. Adjacent finalized segments from
/// the same non-user party are combined; after a short debounce, a question candidate is emitted when the
/// local heuristics are confident. Otherwise (hybrid mode) the turn becomes an <see cref="IntentCheck"/> for an
/// AI classifier and a candidate is emitted only on a positive verdict for the unchanged turn.
/// Later material additions (subquestions, constraints) produce a new revision; trivial additions
/// or repeated events do not restart generation.
/// </summary>
public sealed class QuestionTurnAssembler
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Func<TranscriptSegment, Party> _classify;
    private readonly ILogger? _log;
    private QuestionAssemblerOptions _o;
    private readonly List<(QuestionCandidate Candidate, DateTimeOffset At)> _recent = new();
    private readonly Dictionary<long, (Turn Turn, int SegmentCount)> _pendingChecks = new();
    private readonly List<IntentCheck> _outbox = new();
    private Turn? _turn;
    private int _nextId;
    private long _nextCheckId;
    private int _epoch;

    public QuestionTurnAssembler(TimeProvider time, Func<TranscriptSegment, Party> classify, QuestionAssemblerOptions? options = null, ILogger? log = null)
    {
        _time = time;
        _classify = classify;
        _o = options ?? new QuestionAssemblerOptions();
        _log = log;
    }

    public QuestionAssemblerOptions Options
    {
        get { lock (_gate) { return _o; } }
        set { lock (_gate) { _o = value; } }
    }

    public void OnFinalSegment(TranscriptSegment segment)
    {
        if (!segment.IsAnalyzable)
        {
            return;
        }
        var party = _classify(segment);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (party == Party.Self)
            {
                _turn = null; // the user is responding; the interviewer's turn has ended
                return;
            }
            if (_turn is { } current && current.Segments.FindIndex(s => s.Id == segment.Id) is var index and >= 0)
            {
                // Final transcript of a segment already in the turn (repeated event, or the provisional text an early
                // question candidate was built from).
                var previous = current.Segments[index];
                current.Segments[index] = segment;
                current.LastAudioEnd = segment.AudioEnd > current.LastAudioEnd ? segment.AudioEnd : current.LastAudioEnd;
                if (current.EarlySegmentIds.Remove(segment.Id))
                {
                    if (SameWords(previous.Text, segment.Text))
                    {
                        current.EmittedTextLength = JoinText(current).Length;
                        _log?.LogInformation("Early question confirmed by the final transcript");
                    }
                    else
                    {
                        // The speaker continued after the pause or the transcript changed: answer the final text.
                        current.ForceRevision = true;
                        current.Evaluated = false;
                        current.LastAppendUtc = now;
                        current.DueUtc = now + _o.Debounce;
                        _log?.LogInformation("Early question differs from the final transcript; regenerating");
                    }
                }
                return;
            }
            string? speaker = segment.Attribution.IsResolved ? segment.Attribution.SpeakerId : null;
            bool continues = _turn is not null
                             && _turn.Source == segment.Source
                             && (speaker is null || _turn.SpeakerId is null || _turn.SpeakerId == speaker)
                             && segment.AudioStart - _turn.LastAudioEnd <= _o.ContinuationGap;
            if (!continues)
            {
                _turn = new Turn(segment.Source);
            }
            var turn = _turn!;
            turn.Segments.Add(segment);
            turn.SpeakerId ??= speaker;
            turn.SpeakerUncertain |= speaker is null;
            turn.LastAudioEnd = segment.AudioEnd > turn.LastAudioEnd ? segment.AudioEnd : turn.LastAudioEnd;
            turn.LastAppendUtc = now;
            turn.DueUtc = now + _o.Debounce;
            turn.Evaluated = false;
        }
    }

    /// <summary>
    /// Early path: the speaker paused (or the turn was committed) and the provisional transcript already ends with a
    /// question mark and reads as a question. Emits the candidate immediately instead of waiting for the final
    /// transcript and the debounce. When the final transcript later differs (the speaker continued, or words were
    /// corrected), a revision with the final text is emitted.
    /// </summary>
    public IReadOnlyList<QuestionCandidate> OnEarlyQuestion(TranscriptSegment provisional)
    {
        if (provisional.IsFinal || provisional.Kind != SegmentKind.Speech || provisional.DuplicateOfSegmentId is not null
            || string.IsNullOrWhiteSpace(provisional.Text) || !QuestionHeuristics.EndsWithQuestionMark(provisional.Text.Trim()))
        {
            return [];
        }
        if (_classify(provisional) != Party.Other)
        {
            return [];
        }
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            string? speaker = provisional.Attribution.IsResolved ? provisional.Attribution.SpeakerId : null;
            bool continues = _turn is not null
                             && _turn.Source == provisional.Source
                             && (speaker is null || _turn.SpeakerId is null || _turn.SpeakerId == speaker)
                             && provisional.AudioStart - _turn.LastAudioEnd <= _o.ContinuationGap;
            if (continues && (_turn!.Emitted is not null || _turn.Segments.Any(s => s.Id == provisional.Id)))
            {
                return []; // the turn already has a question (additions are handled on the final path) or was tried
            }
            var turn = continues ? _turn! : new Turn(provisional.Source);
            string text = string.Join(" ", turn.Segments.Select(s => s.Text.Trim()).Append(provisional.Text.Trim()));
            var signal = new[] { QuestionHeuristics.AnalyzeTurn(text, _o.Sensitivity), QuestionHeuristics.AnalyzeTurn(provisional.Text, _o.Sensitivity) }
                .Where(s => s.IsQuestion && !s.LikelyRhetoricalOrQuoted)
                .OrderByDescending(s => s.Score)
                .FirstOrDefault();
            if (signal is null)
            {
                return [];
            }
            _turn = turn;
            turn.Segments.Add(provisional);
            turn.EarlySegmentIds.Add(provisional.Id);
            turn.SpeakerId ??= speaker;
            turn.SpeakerUncertain |= speaker is null;
            turn.LastAudioEnd = provisional.AudioEnd > turn.LastAudioEnd ? provisional.AudioEnd : turn.LastAudioEnd;
            turn.LastAppendUtc = now;
            turn.Evaluated = true;
            var candidate = Build(turn, $"q-{++_nextId}", 1, text, signal.MultiPart, now);
            turn.Emitted = candidate;
            turn.EmittedTextLength = text.Length;
            _recent.Add((candidate, now));
            return [candidate];
        }
    }

    private static string JoinText(Turn turn) => string.Join(" ", turn.Segments.Select(s => s.Text.Trim()));

    /// <summary>Same words, ignoring punctuation, spacing and letter case.</summary>
    private static bool SameWords(string a, string b)
    {
        static string N(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return N(a) == N(b);
    }

    /// <summary>Returns candidates whose debounce elapsed. Call periodically.</summary>
    public IReadOnlyList<QuestionCandidate> Tick()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _recent.RemoveAll(r => now - r.At > _o.RepeatWindow);
            if (_turn is null)
            {
                return [];
            }
            if (now - _turn.LastAppendUtc > _o.ContinuationGap + _o.Debounce + TimeSpan.FromSeconds(4) && _turn.Evaluated)
            {
                _turn = null;
                return [];
            }
            if (_turn.Evaluated || now < _turn.DueUtc)
            {
                return [];
            }
            _turn.Evaluated = true;
            string text = string.Join(" ", _turn.Segments.Select(s => s.Text.Trim()));
            if (_turn.Emitted is null)
            {
                // The whole turn, any of its sentences or any of its last segments may carry the question (context
                // first and question last, or a question followed by a remark such as "no standard answer needed").
                var signals = new[] { QuestionHeuristics.AnalyzeTurn(text, _o.Sensitivity) }
                    .Concat(_turn.Segments.TakeLast(3).Select(s => QuestionHeuristics.AnalyzeTurn(s.Text, _o.Sensitivity)))
                    .ToList();
                var signal = signals
                    .Where(s => s.IsQuestion && !s.LikelyRhetoricalOrQuoted)
                    .OrderByDescending(s => s.Score)
                    .FirstOrDefault();
                if (signal is null)
                {
                    var top = signals.OrderByDescending(s => s.Score).First();
                    int contentChars = text.Count(char.IsLetterOrDigit);
                    if (_o.AiIntent && contentChars >= _o.MinIntentChars)
                    {
                        var check = new IntentCheck(++_nextCheckId, _epoch, text, _turn.Segments.Select(s => s.Id).ToList(), top.Score);
                        _pendingChecks[check.Id] = (_turn, _turn.Segments.Count);
                        _outbox.Add(check);
                        _log?.LogInformation("Question turn sent to AI intent check {Check}: segments={Segments} chars={Chars} localScore={Score:F2} reasons={Reasons}",
                            check.Id, _turn.Segments.Count, text.Length, top.Score, string.Join(",", top.Reasons));
                        return [];
                    }
                    _log?.LogInformation("Question turn not a candidate: segments={Segments} chars={Chars} score={Score:F2} threshold={Threshold:F2} reasons={Reasons}",
                        _turn.Segments.Count, text.Length, top.Score, QuestionHeuristics.ThresholdFor(_o.Sensitivity), string.Join(",", top.Reasons));
                    return [];
                }
                var candidate = Build(_turn, $"q-{++_nextId}", 1, text, signal.MultiPart, now);
                _turn.Emitted = candidate;
                _turn.EmittedTextLength = text.Length;
                _recent.Add((candidate, now));
                return [candidate];
            }
            string added = text.Length > _turn.EmittedTextLength ? text[_turn.EmittedTextLength..] : string.Empty;
            bool force = _turn.ForceRevision;
            _turn.ForceRevision = false;
            if (!force && !QuestionHeuristics.IsMaterialAddition(added, _o.Sensitivity))
            {
                return [];
            }
            var revised = Build(_turn, _turn.Emitted.QuestionId, _turn.Emitted.Revision + 1, text,
                _turn.Emitted.MultiPart || QuestionHeuristics.Analyze(added, _o.Sensitivity).IsQuestion, now) with { RepeatOfQuestionId = null };
            _turn.Emitted = revised;
            _turn.EmittedTextLength = text.Length;
            _recent.Add((revised, now));
            return [revised];
        }
    }

    /// <summary>Intent checks created since the last call (hybrid mode). The caller classifies them and calls <see cref="ResolveIntent"/>.</summary>
    public IReadOnlyList<IntentCheck> TakeIntentChecks()
    {
        lock (_gate)
        {
            if (_outbox.Count == 0)
            {
                return [];
            }
            var list = _outbox.ToList();
            _outbox.Clear();
            return list;
        }
    }

    /// <summary>
    /// Applies an AI verdict. A positive verdict emits a candidate for the checked turn — also when the user has started
    /// replying meanwhile — unless the turn grew since the check (it is re-evaluated with the new text), a candidate was
    /// already emitted for it, or the session was reset/paused. A null verdict (failure, budget) counts as "no".
    /// </summary>
    public IReadOnlyList<QuestionCandidate> ResolveIntent(IntentCheck check, IntentVerdict? verdict)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (check.Epoch != _epoch || !_pendingChecks.Remove(check.Id, out var entry))
            {
                return [];
            }
            var (turn, segmentCount) = entry;
            if (verdict is not { IsQuestion: true } || turn.Emitted is not null || turn.Segments.Count != segmentCount)
            {
                return [];
            }
            var candidate = Build(turn, $"q-{++_nextId}", 1, check.Text, QuestionHeuristics.AnalyzeTurn(check.Text, _o.Sensitivity).MultiPart, now);
            turn.Emitted = candidate;
            turn.EmittedTextLength = check.Text.Length;
            _recent.Add((candidate, now));
            return [candidate];
        }
    }

    /// <summary>Builds a candidate from user-selected transcript text ("Answer this question").</summary>
    public QuestionCandidate Manual(IReadOnlyList<TranscriptSegment> segments)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var ordered = segments.OrderBy(s => s.AudioStart).ToList();
            string text = string.Join(" ", ordered.Select(s => s.Text.Trim()));
            var first = ordered[0];
            var candidate = new QuestionCandidate($"q-{++_nextId}", 1, text, ordered.Select(s => s.Id).ToList(), first.Source,
                first.Attribution.IsResolved ? first.Attribution.SpeakerId : null, ordered.Any(s => !s.Attribution.IsResolved),
                QuestionHeuristics.Analyze(text, 3).MultiPart, null, true, ordered[^1].AudioEnd, now);
            _recent.Add((candidate, now));
            return candidate;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _turn = null;
            _recent.Clear();
            DropChecksLocked();
        }
    }

    /// <summary>Clears the in-progress turn without forgetting recent questions (pause/mute).</summary>
    public void DropOpenTurn()
    {
        lock (_gate)
        {
            _turn = null;
            DropChecksLocked();
        }
    }

    private void DropChecksLocked()
    {
        _epoch++;
        _pendingChecks.Clear();
        _outbox.Clear();
    }

    private QuestionCandidate Build(Turn turn, string id, int revision, string text, bool multiPart, DateTimeOffset now)
    {
        string? repeatOf = null;
        if (revision == 1)
        {
            var match = _recent
                .Where(r => r.Candidate.QuestionId != id)
                .Select(r => (r.Candidate, Sim: TextTools.Jaccard(r.Candidate.Text, text)))
                .Where(x => x.Sim >= _o.RepeatSimilarity)
                .OrderByDescending(x => x.Sim)
                .FirstOrDefault();
            repeatOf = match.Candidate?.QuestionId;
        }
        return new QuestionCandidate(id, revision, text, turn.Segments.Select(s => s.Id).ToList(), turn.Source, turn.SpeakerId,
            turn.SpeakerUncertain, multiPart, repeatOf, false, turn.LastAudioEnd, now);
    }

    private sealed class Turn(AudioSourceKind source)
    {
        public AudioSourceKind Source { get; } = source;
        public List<TranscriptSegment> Segments { get; } = new();
        public string? SpeakerId { get; set; }
        public bool SpeakerUncertain { get; set; }
        public TimeSpan LastAudioEnd { get; set; }
        public DateTimeOffset LastAppendUtc { get; set; }
        public DateTimeOffset DueUtc { get; set; }
        public bool Evaluated { get; set; }
        public QuestionCandidate? Emitted { get; set; }
        public int EmittedTextLength { get; set; }
        /// <summary>Segments whose provisional text was used by an early candidate and whose final text is pending.</summary>
        public HashSet<string> EarlySegmentIds { get; } = new(StringComparer.Ordinal);
        /// <summary>Emit a revision at the next evaluation even if the change is not a "material addition".</summary>
        public bool ForceRevision { get; set; }
    }
}
