using System.Diagnostics;
using System.Runtime.CompilerServices;
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

/// <summary>
/// Session-memory check through the real answer model: a scripted interview with a five-level drill-down, unrelated
/// questions, then two questions that refer back to early answers. Reports the follow-up links the model recorded,
/// whether the late answers use facts the user stated many questions earlier, and per-request cache usage.
/// <c>--baseline</c> emulates the previous memory (last three exchanges only). Makes real API calls; no audio.
/// </summary>
public static class MemoryTest
{
    private sealed record Step(string Question, string Reply, string? ExpectLink, string[]? MustMention = null);

    private static readonly Step[] Script =
    [
        new("先介绍一下你最近做的项目吧。",
            "我最近负责的是电商订单系统的缓存改造，用 Redis 做了二级缓存，把下单接口的 P99 延迟从 800 毫秒降到了 120 毫秒，峰值 QPS 大概两万。", null),
        new("为什么选 Redis，不直接用本地缓存？",
            "因为我们线上有 40 个实例，本地缓存很难保证一致，所以本地只放少量热点商品，Redis 做主缓存。", "q-1"),
        new("那 Redis 和数据库的一致性你们怎么保证？",
            "先更新数据库再删缓存，删除失败的话通过消息队列重试。", "q-2"),
        new("如果消息队列也出问题了呢？",
            "我们给缓存设了 10 分钟过期时间兜底，最坏情况下最多不一致 10 分钟。", "q-3"),
        new("10 分钟不一致，对订单业务能接受吗？",
            "订单状态本身不走缓存，缓存的只是商品详情这类读多写少的数据，所以业务上可以接受。", "q-4"),
        new("好，换个话题。你怎么理解微服务的拆分粒度？",
            "我觉得按业务能力和团队边界拆，拆得太细会让调用链和运维成本变高。", null),
        new("你平时是怎么做 code review 的？",
            "我会先看设计和接口，再看边界条件和测试，小问题直接在评论里给建议。", null),
        new("讲讲你遇到过最难排查的一次线上故障。",
            "有一次连接池泄漏导致接口超时，最后是通过线程 dump 和连接池监控定位到一个没关闭的连接。", null),
        new("回到你一开始说的那个缓存改造，P99 具体是从多少降到多少的？你是怎么测出来的？",
            "", "q-1", ["800", "120"]),
        new("你前面说线上有那么多实例，那本地热点缓存是怎么失效的？",
            "", "q-2", ["40"]),
    ];

    public static async Task<int> RunAsync(Options o)
    {
        await using var sp = Host.Build();
        var settings = sp.GetRequiredService<ISettingsStore>().Load() with
        {
            Scenario = Scenario.Interview,
            AnswerLanguage = "auto",
            AnswerDepth = AnswerDepth.Normal,
            InterviewMaxRequestsPerMinute = 20, // questions follow each other faster than in a real interview
        };
        var model = new RecordingModel(sp.GetRequiredService<IAssistantModel>());
        var tokens = sp.GetRequiredService<ITokenCounter>();
        var loggers = sp.GetRequiredService<ILoggerFactory>();
        bool baseline = o.Has("baseline");

        var manual = new ManualTime();
        var clock = new SessionClock(manual);
        var time = TimeProvider.System;
        var store = new TranscriptStore(time);
        var speakers = new SpeakerRegistry();
        var history = new QuestionHistory();
        var prep = new PreparationNotes();
        var filter = new InsightFilter(time);
        var board = new AssistantBoard();
        Party Classify(TranscriptSegment s) => s.Attribution.Status == AttributionStatus.SourceAlias ? Party.Self : Party.Other;
        var prompts = new PromptBuilder(store, speakers, history, prep, filter, tokens, clock, Classify);
        if (baseline)
        {
            prompts.Budgets = prompts.Budgets with { Memory = new MemoryBudgets { RecentCount = 3, Recent = 2_000, Log = 0, LogTarget = 0, Related = 0 } };
        }
        var orchestrator = new AssistantOrchestrator(model, prompts, board, filter, history, store, clock, settings, loggers.CreateLogger("MemoryTest"));
        orchestrator.BeginSession(1);
        var interviewer = speakers.CreateDiarized(AudioSourceKind.System, false);
        speakers.EnsureSelfAlias();

        Console.WriteLine($"Memory test: model={settings.AnswerModel}/{settings.AnswerReasoningEffort} memory={(baseline ? "baseline (last 3 exchanges)" : "session")} prompt={PromptLibrary.Version}");
        Console.WriteLine($"{"q",-5} {"link",-6} {"expect",-6} {"in",6} {"cached",6} {"write",6} {"first",6}  mentions");
        double t = 5;
        int turn = 0, linksOk = 0, linksExpected = 0, mentionsOk = 0, mentionsExpected = 0;
        for (int i = 0; i < Script.Length; i++)
        {
            var step = Script[i];
            string qid = $"q-{i + 1}";
            double qEnd = t + Math.Max(2, step.Question.Length / 4.5);
            var seg = store.ApplyFinal(new TurnKey(1, AudioSourceKind.System, 1, ++turn), step.Question, TimeSpan.FromSeconds(t), TimeSpan.FromSeconds(qEnd),
                new AudioAttributionDefault(new SpeakerAttribution(AttributionStatus.Assigned, interviewer.Id, [], 0, null)), $"item-{turn}")!;
            manual.Set(TimeSpan.FromSeconds(qEnd + 0.5));
            int before = model.Calls.Count;
            orchestrator.SubmitQuestion(new QuestionCandidate(qid, 1, step.Question, [seg.Id], AudioSourceKind.System, interviewer.Id, false, false, null, false,
                TimeSpan.FromSeconds(qEnd), DateTimeOffset.UtcNow));
            var started = DateTimeOffset.UtcNow;
            await Task.Delay(300);
            while (orchestrator.IsBusy && DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(120))
            {
                await Task.Delay(200);
            }
            var card = board.Cards.LastOrDefault(c => c.QuestionId == qid);
            (TimeSpan FirstToken, UsageInfo? Usage)? call = model.Calls.Count > before ? model.Calls[^1] : null;
            string? link = history.Find(qid)?.FollowsUpOn;
            string mention = string.Empty;
            if (step.ExpectLink is not null)
            {
                linksExpected++;
                linksOk += link == step.ExpectLink ? 1 : 0;
            }
            else
            {
                linksExpected++;
                linksOk += link is null ? 1 : 0;
            }
            if (step.MustMention is { } must)
            {
                string text = $"{card?.DirectPosition}\n{card?.SpokenAnswer}";
                var found = must.Where(m => text.Contains(m, StringComparison.Ordinal)).ToList();
                mentionsExpected += must.Length;
                mentionsOk += found.Count;
                mention = $"{found.Count}/{must.Length} [{string.Join(",", must)}] state={card?.State}";
                if (o.Has("show"))
                {
                    mention += "\n      direct: " + card?.DirectPosition;
                    foreach (var d in card?.FilledInDetails ?? [])
                    {
                        mention += "\n      AI filled in: " + d;
                    }
                }
            }
            Console.WriteLine($"{qid,-5} {link ?? "-",-6} {step.ExpectLink ?? "-",-6} {call?.Usage?.InputTokens,6} {call?.Usage?.CachedInputTokens,6} {call?.Usage?.CacheWriteTokens,6} {call?.FirstToken.TotalSeconds,6:F2}  {mention}");

            t = qEnd + 2;
            if (step.Reply.Length > 0)
            {
                double rEnd = t + Math.Max(2, step.Reply.Length / 4.5);
                store.ApplyFinal(new TurnKey(1, AudioSourceKind.Microphone, 1, ++turn), step.Reply, TimeSpan.FromSeconds(t), TimeSpan.FromSeconds(rEnd),
                    new AudioAttributionDefault(new SpeakerAttribution(AttributionStatus.SourceAlias, SpeakerRegistry.SelfAliasId, [], 0, null)), $"item-{turn}");
                t = rEnd + 3;
            }
            t += 40; // the interviewer listens and thinks; older lines leave the 2-minute transcript window
            manual.Set(TimeSpan.FromSeconds(t));
        }
        long input = model.Calls.Sum(c => c.Usage?.InputTokens ?? 0), cached = model.Calls.Sum(c => c.Usage?.CachedInputTokens ?? 0), written = model.Calls.Sum(c => c.Usage?.CacheWriteTokens ?? 0);
        Console.WriteLine($"\nFollow-up links correct: {linksOk}/{linksExpected}; facts from earlier answers used: {mentionsOk}/{mentionsExpected}");
        Console.WriteLine($"Input tokens {input}: cached {cached} ({(input == 0 ? 0 : cached * 100.0 / input):F0}%), cache writes {written}; requests {model.Calls.Count}");
        return 0;
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public void Set(TimeSpan t) => Interlocked.Exchange(ref _ticks, t.Ticks);

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private sealed class RecordingModel(IAssistantModel inner) : IAssistantModel
    {
        public List<(TimeSpan FirstToken, UsageInfo? Usage)> Calls { get; } = new();

        public async IAsyncEnumerable<AssistantStreamUpdate> StreamAsync(AssistantModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            TimeSpan first = TimeSpan.Zero;
            await foreach (var u in inner.StreamAsync(request, cancellationToken))
            {
                if (first == TimeSpan.Zero && u is AssistantTextDelta)
                {
                    first = sw.Elapsed;
                }
                if (u is AssistantCompleted c)
                {
                    lock (Calls)
                    {
                        Calls.Add((first, c.Usage));
                    }
                }
                yield return u;
            }
        }

        public Task WarmUpAsync(string model, CancellationToken cancellationToken) => inner.WarmUpAsync(model, cancellationToken);
    }
}
