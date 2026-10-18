using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Assistant;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Context;
using ProactiveAI.Core.Questions;
using ProactiveAI.Core.Settings;
using ProactiveAI.Core.Speakers;
using ProactiveAI.Core.Transcript;

namespace ProactiveAI.Probe;

public sealed record FixtureLine(string Speaker, string Text, double At, string? Role = null);

public sealed record FixtureExchange(string Question, string? Draft, string? UserSaid);

public sealed record FixtureExpect(
    List<string> Kinds,
    List<List<string>>? MustMention = null,
    List<string>? MustNotMatch = null,
    int MinSpokenChars = 0,
    bool? MissingFactsExpected = null,
    bool? PrepEvidenceExpected = null,
    bool? DetectQuestion = null,
    bool? FilledInExpected = null);

public sealed record QualityFixture(
    string Id,
    string Scenario,
    string Description,
    List<FixtureLine> Transcript,
    FixtureExpect Expect,
    Dictionary<string, string>? Preparation = null,
    List<FixtureExchange>? History = null,
    List<string>? RecentInsights = null,
    string Task = "question",
    string? Resume = null);

/// <summary>
/// Opt-in evaluation through the actual configured model. Deterministic checks (kinds, required
/// mentions, prohibited claims, missing facts) are reported per fixture together with the full output
/// for human review. Passing a small sample does not prove general quality.
/// </summary>
public static class EvalRunner
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<int> RunAsync(Options o)
    {
        await using var sp = Host.Build();
        var baseSettings = sp.GetRequiredService<ISettingsStore>().Load();
        var model = sp.GetRequiredService<IAssistantModel>();
        var tokens = sp.GetRequiredService<ITokenCounter>();
        var loggers = sp.GetRequiredService<ILoggerFactory>();
        string dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "quality");
        var fixtures = Directory.GetFiles(dir, "*.json").SelectMany(f => JsonSerializer.Deserialize<List<QualityFixture>>(File.ReadAllText(f), Json)!).ToList();
        if (o.Get("filter") is { } filter)
        {
            fixtures = fixtures.Where(f => f.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        var report = new StringBuilder();
        report.AppendLine($"# Suggestion-quality evaluation\n\nDate: {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}  ");
        report.AppendLine($"Answer model: `{baseSettings.AnswerModel}` (effort {baseSettings.AnswerReasoningEffort}); insight model: `{baseSettings.InsightModel}` (effort {baseSettings.InsightReasoningEffort}); prompt: `{PromptLibrary.Version}`\n");
        int passed = 0;
        foreach (var f in fixtures)
        {
            Console.WriteLine($"=== {f.Id}");
            var (ok, details, output) = await RunOneAsync(f, baseSettings, model, tokens, loggers);
            passed += ok ? 1 : 0;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {string.Join("; ", details)}");
            report.AppendLine($"## {f.Id} — {(ok ? "PASS" : "FAIL")}\n\n{f.Description}\n");
            foreach (var d in details)
            {
                report.AppendLine($"- {d}");
            }
            report.AppendLine("\n<details><summary>Output</summary>\n\n```text\n" + output + "\n```\n</details>\n");
        }
        report.Insert(0, string.Empty);
        report.AppendLine($"\n**Result: {passed}/{fixtures.Count} fixtures passed deterministic checks.**");
        Console.WriteLine($"\n{passed}/{fixtures.Count} passed");
        if (o.Get("report") is { } path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(path, report.ToString());
            Console.WriteLine($"Report written to {path}");
        }
        return passed == fixtures.Count ? 0 : 4;
    }

    private static async Task<(bool Ok, List<string> Details, string Output)> RunOneAsync(QualityFixture f, AppSettings baseSettings, IAssistantModel model, ITokenCounter tokens, ILoggerFactory loggers)
    {
        var settings = baseSettings with { Scenario = Enum.Parse<Scenario>(f.Scenario, true), AnswerLanguage = "auto" };
        var time = TimeProvider.System;
        double lastAt = f.Transcript.Count > 0 ? f.Transcript.Max(l => l.At) : 0;
        var clock = new SessionClock(time, TimeSpan.FromSeconds(lastAt + 6));
        var store = new TranscriptStore(time);
        var speakers = new SpeakerRegistry();
        var prep = new PreparationNotes();
        var history = new QuestionHistory();
        var board = new AssistantBoard();
        var filter = new InsightFilter(time);
        var byName = new Dictionary<string, SessionSpeaker>(StringComparer.Ordinal);
        Party Classify(TranscriptSegment s) => s.Attribution.Status == AttributionStatus.SourceAlias ? Party.Self : Party.Other;
        var prompts = new PromptBuilder(store, speakers, history, prep, filter, tokens, clock, Classify);
        var orchestrator = new AssistantOrchestrator(model, prompts, board, filter, history, store, clock, settings, loggers.CreateLogger("Eval"));
        orchestrator.BeginSession(1);

        foreach (var (k, v) in f.Preparation ?? new())
        {
            prep.Set(Enum.Parse<PrepField>(k, true), v);
        }
        if (f.Resume is { } resumeText)
        {
            prep.SetResume(new Resume(new ResumeInfo("fixture", "resume.pdf", "pdf", resumeText.Length, DateTimeOffset.UtcNow), resumeText));
        }
        foreach (var r in f.RecentInsights ?? [])
        {
            filter.RecordShown(r, null, null);
        }

        var ids = new List<string>();
        int turn = 0;
        foreach (var line in f.Transcript)
        {
            bool self = line.Speaker == "Me";
            var source = self ? AudioSourceKind.Microphone : AudioSourceKind.System;
            SpeakerAttribution attribution;
            if (self)
            {
                speakers.EnsureSelfAlias();
                attribution = new SpeakerAttribution(AttributionStatus.SourceAlias, SpeakerRegistry.SelfAliasId, [], 0, null);
            }
            else
            {
                if (!byName.TryGetValue(line.Speaker, out var spk))
                {
                    spk = speakers.CreateDiarized(AudioSourceKind.System, false);
                    byName[line.Speaker] = spk;
                }
                if (line.Role is { } role)
                {
                    speakers.SetRole(spk.Id, Enum.Parse<SpeakerRole>(role, true));
                }
                attribution = new SpeakerAttribution(AttributionStatus.Assigned, spk.Id, [], 0, null);
            }
            var start = TimeSpan.FromSeconds(line.At);
            var end = start + TimeSpan.FromSeconds(Math.Max(1.5, line.Text.Length / 14.0));
            var seg = store.ApplyFinal(new TurnKey(1, source, 1, ++turn), line.Text, start, end, new AudioAttributionDefault(attribution), $"item-{turn}");
            ids.Add(seg!.Id);
        }

        int q = 0;
        foreach (var x in f.History ?? [])
        {
            q++;
            history.Upsert(new QuestionExchange($"q-h{q}", 1, new PinnedExcerpt([], x.Question), x.Draft,
                x.UserSaid is null ? null : new PinnedExcerpt([], x.UserSaid), TimeSpan.FromSeconds(q)));
        }

        var details = new List<string>();
        bool ok = true;
        var segs = store.Snapshot();
        if (f.Expect.DetectQuestion is { } expectDetect)
        {
            // Deterministic stage-1 check on the trailing turn from the last non-user speaker.
            var tail = segs.Where(s => s.Attribution.Status != AttributionStatus.SourceAlias).TakeLast(2).ToList();
            var signal = QuestionHeuristics.Analyze(string.Join(" ", tail.Select(s => s.Text)), settings.Sensitivity);
            bool detected = signal.IsQuestion && !signal.LikelyRhetoricalOrQuoted;
            details.Add($"stage-1 detection={detected} (expected {expectDetect}) score={signal.Score:F2} [{string.Join(",", signal.Reasons)}]");
            ok &= detected == expectDetect;
        }

        if (f.Task == "meeting")
        {
            var triggers = segs.SelectMany(s => QuestionHeuristics.MeetingTriggers(s.Text, settings.Sensitivity)).Distinct().ToList();
            orchestrator.SubmitMeetingReview(triggers.Count > 0 ? triggers : [MeetingTrigger.Question], null);
        }
        else
        {
            var questionSegs = segs.Where(s => s.Attribution.Status != AttributionStatus.SourceAlias).TakeLast(f.Task == "question-last2" ? 2 : 1).ToList();
            orchestrator.SubmitQuestion(new QuestionCandidate($"q-{f.Id}", 1, string.Join(" ", questionSegs.Select(s => s.Text)),
                questionSegs.Select(s => s.Id).ToList(), AudioSourceKind.System, questionSegs[0].Attribution.SpeakerId, false, true, null, false,
                clock.Now - TimeSpan.FromSeconds(1), time.GetUtcNow()));
        }

        var started = DateTimeOffset.UtcNow;
        await Task.Delay(300);
        while (orchestrator.IsBusy && DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(150))
        {
            await Task.Delay(250);
        }
        var card = board.Cards.LastOrDefault();
        string kind = card is null ? "none" : card.Kind switch
        {
            CardKind.Answer => "answer",
            CardKind.Insight => "insight",
            CardKind.Clarification => "clarification",
            _ => "none",
        };
        var output = new StringBuilder();
        if (card is null)
        {
            output.AppendLine("(no card: model chose none or the result was filtered)");
        }
        else
        {
            output.AppendLine($"kind={card.Kind} state={card.State} {card.StatusDetail} firstText={card.FirstUsefulTextLatency?.TotalSeconds:F1}s complete={card.CompleteLatency?.TotalSeconds:F1}s");
            output.AppendLine($"title: {card.Title}");
            output.AppendLine($"direct: {card.DirectPosition}");
            output.AppendLine($"spoken: {card.SpokenAnswer}");
            output.AppendLine($"suggestion: {card.Suggestion} | ask: {card.SuggestedQuestion}");
            foreach (var p in card.SupportingPoints) { output.AppendLine($" • {p}"); }
            foreach (var p in card.AssumptionsAndLimits) { output.AppendLine($" ◦ {p}"); }
            foreach (var p in card.LikelyFollowups) { output.AppendLine($" ? {p.Question} → {p.ResponseDirection}"); }
            foreach (var p in card.MissingFacts) { output.AppendLine($" ! {p}"); }
            foreach (var p in card.FilledInDetails) { output.AppendLine($" ~ AI filled in: {p}"); }
            output.AppendLine($"evidence: {string.Join(",", card.EvidenceSegmentIds)} prep: {string.Join(",", card.PreparationEvidenceIds)} decision: {card.DecisionNote}");
        }
        string text = string.Join("\n", new[] { card?.Title, card?.DirectPosition, card?.SpokenAnswer, card?.Suggestion, card?.SuggestedQuestion }
            .Concat(card?.SupportingPoints ?? []).Concat(card?.AssumptionsAndLimits ?? []).Where(s => s is not null));

        if (card is not null && card.State != CardState.Complete)
        {
            ok = false;
            details.Add($"card state {card.State}: {card.StatusDetail}");
        }
        bool kindOk = f.Expect.Kinds.Contains(kind);
        details.Add($"kind={kind} (expected {string.Join("|", f.Expect.Kinds)})");
        ok &= kindOk;
        if (card is not null && kind == "answer")
        {
            int len = card.SpokenAnswer?.Length ?? 0;
            if (len < f.Expect.MinSpokenChars)
            {
                ok = false;
                details.Add($"spoken_answer too short: {len} < {f.Expect.MinSpokenChars}");
            }
            else
            {
                details.Add($"spoken_answer {len} chars");
            }
        }
        foreach (var group in f.Expect.MustMention ?? [])
        {
            bool hit = group.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
            ok &= hit;
            details.Add($"mentions any of [{string.Join(", ", group)}]: {hit}");
        }
        foreach (var pattern in f.Expect.MustNotMatch ?? [])
        {
            bool bad = Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);
            ok &= !bad;
            details.Add($"prohibited /{pattern}/: {(bad ? "PRESENT" : "absent")}");
        }
        if (f.Expect.MissingFactsExpected is { } mf && card is not null && kind == "answer")
        {
            bool has = card.MissingFacts.Count > 0;
            ok &= has == mf;
            details.Add($"missing_facts present={has} (expected {mf})");
        }
        if (f.Expect.FilledInExpected is { } fi && card is not null && kind == "answer")
        {
            bool has = card.FilledInDetails.Count > 0;
            ok &= has == fi;
            details.Add($"filled_in_details present={has} (expected {fi})");
        }
        if (f.Expect.PrepEvidenceExpected is { } pe && card is not null && kind == "answer")
        {
            bool has = card.PreparationEvidenceIds.Count > 0;
            ok &= has == pe;
            details.Add($"preparation evidence cited={has} (expected {pe})");
        }
        return (ok, details, output.ToString());
    }
}
