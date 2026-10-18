using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Assistant;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Context;
using ProactiveAI.Core.Questions;
using ProactiveAI.Core.Settings;
using ProactiveAI.Core.Speakers;
using ProactiveAI.Core.Transcript;

namespace ProactiveAI.Probe;

/// <summary>
/// Accuracy of the answer model's follow-up link (<c>follows_up_on</c>) on labeled zh/en cases, using the production prompt
/// (same PromptBuilder, budgets and request layout). Each request is stopped as soon as the link has streamed, so only a few
/// output tokens are generated. Makes real API calls; no audio.
/// </summary>
public static partial class FollowUpTest
{
    public sealed record HistoryItem(string Q, string Said, string? Follows = null, string? Draft = null);

    public sealed record Case(string Id, string Lang, string Category, string Current, string? Expect, List<string>? Accept = null,
        string? Base = null, List<HistoryItem>? History = null, string Scenario = "Interview");

    public sealed record CaseFile(Dictionary<string, List<HistoryItem>> Histories, List<Case> Cases);

    private sealed record Outcome(Case Case, int Run, string? Got, bool Valid, string? Kind, long Ms, int InputTokens, string? Error)
    {
        public bool Correct => Error is null && (Got == Case.Expect || (Got is not null && (Case.Accept?.Contains(Got) ?? false)));
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<int> RunAsync(Options o)
    {
        await using var sp = Host.Build();
        var settings = sp.GetRequiredService<ISettingsStore>().Load();
        var model = sp.GetRequiredService<IAssistantModel>();
        var tokens = sp.GetRequiredService<ITokenCounter>();
        var file = JsonSerializer.Deserialize<CaseFile>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "followup-cases.json")), Json)!;
        var cases = file.Cases.Where(c => o.Get("filter") is not { } f || c.Id.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        int runs = o.GetInt("runs", 1);
        Console.WriteLine($"Follow-up link test: {cases.Count} cases × {runs} run(s), model={settings.AnswerModel}/{settings.AnswerReasoningEffort}, prompt={PromptLibrary.Version}");

        using var gate = new SemaphoreSlim(o.GetInt("parallel", 4));
        var tasks = new List<Task<Outcome>>();
        for (int run = 1; run <= runs; run++)
        {
            foreach (var c in cases)
            {
                int r = run;
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        return await ClassifyAsync(c, r, file, settings, model, tokens);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
            }
        }
        var outcomes = (await Task.WhenAll(tasks)).OrderBy(x => cases.IndexOf(x.Case)).ThenBy(x => x.Run).ToList();

        Console.WriteLine($"\n{"case",-34} {"category",-18} {"expect",-7} {"got",-8} ok   kind      ms");
        foreach (var x in outcomes)
        {
            Console.WriteLine($"{x.Case.Id,-34} {x.Case.Category,-18} {x.Case.Expect ?? "null",-7} {(x.Error is not null ? "ERROR" : x.Got ?? "null") + (x.Valid ? string.Empty : "!"),-8} {(x.Correct ? "✓" : "✗"),-4} {x.Kind ?? "-",-9} {x.Ms,5}{(x.Error is null ? string.Empty : "  " + x.Error)}");
        }

        int total = outcomes.Count, correct = outcomes.Count(x => x.Correct);
        Console.WriteLine($"\nOverall: {correct}/{total} correct ({100.0 * correct / Math.Max(1, total):F0}%)");
        foreach (var g in outcomes.GroupBy(x => x.Case.Category))
        {
            Console.WriteLine($"  {g.Key,-18} {g.Count(x => x.Correct)}/{g.Count()}");
        }
        foreach (var g in outcomes.GroupBy(x => x.Case.Lang))
        {
            Console.WriteLine($"  lang {g.Key,-13} {g.Count(x => x.Correct)}/{g.Count()}");
        }
        var wrong = outcomes.Where(x => !x.Correct && x.Error is null).ToList();
        int missed = wrong.Count(x => x.Case.Expect is not null && x.Got is null);
        int falseLink = wrong.Count(x => x.Case.Expect is null && x.Got is not null);
        int wrongTarget = wrong.Count(x => x.Case.Expect is not null && x.Got is not null);
        Console.WriteLine($"Errors: missed link {missed}, link on a new topic {falseLink}, wrong earlier question {wrongTarget}, request errors {outcomes.Count(x => x.Error is not null)}, invalid ids {outcomes.Count(x => !x.Valid)}");
        if (runs > 1)
        {
            int stable = outcomes.GroupBy(x => x.Case.Id).Count(g => g.Select(x => x.Got ?? "null").Distinct().Count() == 1);
            Console.WriteLine($"Same link in every run: {stable}/{cases.Count} cases");
        }
        Console.WriteLine($"Input ≈{outcomes.Sum(x => x.InputTokens):N0} tokens (estimate); output stopped right after the link; median time to link {Median(outcomes.Select(x => x.Ms)):F0} ms");
        return 0;
    }

    private static async Task<Outcome> ClassifyAsync(Case c, int run, CaseFile file, AppSettings settings, IAssistantModel model, ITokenCounter tokens)
    {
        var items = new List<HistoryItem>();
        if (c.Base is { } b)
        {
            items.AddRange(file.Histories[b]);
        }
        items.AddRange(c.History ?? []);

        var manual = new ManualTime();
        var clock = new SessionClock(manual);
        var time = TimeProvider.System;
        var store = new TranscriptStore(time);
        var speakers = new SpeakerRegistry();
        var history = new QuestionHistory();
        var interviewer = speakers.CreateDiarized(AudioSourceKind.System, false);
        speakers.EnsureSelfAlias();
        var them = new AudioAttributionDefault(new SpeakerAttribution(AttributionStatus.Assigned, interviewer.Id, [], 0, null));
        var me = new AudioAttributionDefault(new SpeakerAttribution(AttributionStatus.SourceAlias, SpeakerRegistry.SelfAliasId, [], 0, null));
        int turn = 0;
        double t = 10;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            double qEnd = t + Math.Max(2, item.Q.Length / 4.5);
            var qs = store.ApplyFinal(new TurnKey(1, AudioSourceKind.System, 1, ++turn), item.Q, TimeSpan.FromSeconds(t), TimeSpan.FromSeconds(qEnd), them, $"i{turn}")!;
            double rStart = qEnd + 2, rEnd = rStart + Math.Max(2, item.Said.Length / 4.5);
            var rs = store.ApplyFinal(new TurnKey(1, AudioSourceKind.Microphone, 1, ++turn), item.Said, TimeSpan.FromSeconds(rStart), TimeSpan.FromSeconds(rEnd), me, $"i{turn}")!;
            history.Upsert(new QuestionExchange($"q-{i + 1}", 1, new PinnedExcerpt([qs.Id], item.Q), item.Draft, new PinnedExcerpt([rs.Id], item.Said), TimeSpan.FromSeconds(qEnd))
            {
                FollowsUpOn = item.Follows,
            });
            t = rEnd + 60;
        }
        double cEnd = t + Math.Max(2, c.Current.Length / 4.5);
        var cs = store.ApplyFinal(new TurnKey(1, AudioSourceKind.System, 1, ++turn), c.Current, TimeSpan.FromSeconds(t), TimeSpan.FromSeconds(cEnd), them, $"i{turn}")!;
        manual.Set(TimeSpan.FromSeconds(cEnd + 0.5));

        Party Classify(TranscriptSegment s) => s.Attribution.Status == AttributionStatus.SourceAlias ? Party.Self : Party.Other;
        var prompts = new PromptBuilder(store, speakers, history, new PreparationNotes(), new InsightFilter(time), tokens, clock, Classify);
        string qid = $"q-{items.Count + 1}";
        var candidate = new QuestionCandidate(qid, 1, c.Current, [cs.Id], AudioSourceKind.System, interviewer.Id, false, false, null, false, TimeSpan.FromSeconds(cEnd), DateTimeOffset.UtcNow);
        var built = prompts.Build(new PromptRequest(Enum.Parse<Scenario>(c.Scenario, true), AnswerDepth.Normal, "auto", candidate, [], null));
        var request = new AssistantModelRequest(settings.AnswerModel, built.Instructions, built.Input, PromptLibrary.SchemaName, built.SchemaJson, 4000,
            settings.AnswerReasoningEffort, "medium", null, built.Context);

        var text = new StringBuilder();
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string? got = null, kind = null;
        bool found = false;
        try
        {
            await foreach (var u in model.StreamAsync(request, cts.Token))
            {
                if (u is AssistantTextDelta d)
                {
                    text.Append(d.Delta);
                    kind ??= KindPattern().Match(text.ToString()) is { Success: true } k ? k.Groups[1].Value : null;
                    var m = LinkPattern().Match(text.ToString());
                    if (m.Success)
                    {
                        found = true;
                        got = m.Groups[1].Value == "null" ? null : JsonSerializer.Deserialize<string>(m.Groups[1].Value);
                        cts.Cancel(); // the rest of the answer is not needed
                        break;
                    }
                }
                else if (u is AssistantCompleted done)
                {
                    var parsed = AssistantResultParser.Parse(done.Text);
                    got = parsed.Result?.FollowsUpOn;
                    found = true;
                }
            }
        }
        catch (OperationCanceledException) when (found)
        {
        }
        catch (Exception ex)
        {
            return new Outcome(c, run, null, true, kind, sw.ElapsedMilliseconds, built.InputTokens, ex.GetType().Name + ": " + ex.Message);
        }
        if (!found)
        {
            return new Outcome(c, run, null, true, kind, sw.ElapsedMilliseconds, built.InputTokens, "no follows_up_on in output");
        }
        bool valid = got is null || Enumerable.Range(1, items.Count).Select(i => $"q-{i}").Contains(got);
        // The app ignores invalid ids, so they count as "no link".
        return new Outcome(c, run, valid ? got : null, valid, kind, sw.ElapsedMilliseconds, built.InputTokens, null);
    }

    private static double Median(IEnumerable<long> values)
    {
        var v = values.Order().ToList();
        return v.Count == 0 ? 0 : v[v.Count / 2];
    }

    [GeneratedRegex("\"follows_up_on\"\\s*:\\s*(null|\"(?:[^\"\\\\]|\\\\.)*\")")]
    private static partial Regex LinkPattern();

    [GeneratedRegex("\"response_kind\"\\s*:\\s*\"(\\w+)\"")]
    private static partial Regex KindPattern();

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public void Set(TimeSpan t) => Interlocked.Exchange(ref _ticks, t.Ticks);

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
}
