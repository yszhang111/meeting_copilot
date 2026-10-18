using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Assistant;
using ProactiveAI.Core.Audio;
using ProactiveAI.Core.Context;
using ProactiveAI.Core.Questions;
using ProactiveAI.Core.Session;
using ProactiveAI.Core.Settings;
using ProactiveAI.Core.Speakers;
using ProactiveAI.Core.Transcript;
using ProactiveAI.Infrastructure.OpenAI;

namespace ProactiveAI.Probe;

/// <summary>
/// Answer latency breakdown with the production prompt: connection set-up (cold/warm, HTTP/1.1 vs HTTP/2) and, per
/// model/effort, time to the first streamed token, to the first useful text (direct_position ≥ 20 chars), to the
/// spoken answer and to completion. Makes real API calls.
/// </summary>
public static class LatencyTest
{
    public static async Task<int> RunAsync(Options o)
    {
        using var sp = Host.Build();
        var settings = sp.GetRequiredService<ISettingsStore>().Load() with { Scenario = Scenario.Interview, AnswerLanguage = "auto" };
        var credentials = sp.GetRequiredService<ICredentialStore>();
        var prompt = BuildPrompt(settings, sp.GetRequiredService<ITokenCounter>(), o.Get("q") ?? "simple");
        Console.WriteLine($"Prompt input ~{prompt.InputTokens} tokens");

        Console.WriteLine("\n# Connection set-up (GET /v1/models/{id}, ms)");
        foreach (var version in new[] { HttpVersion.Version11, HttpVersion.Version20 })
        {
            using var http = NewClient(version);
            var conn = new OpenAIConnection(credentials, http);
            var times = new List<string>();
            for (int i = 0; i < 4; i++)
            {
                var sw = Stopwatch.StartNew();
                using var req = conn.CreateRequest(HttpMethod.Get, "models/" + settings.AnswerModel);
                using var resp = await http.SendAsync(req);
                times.Add($"{sw.ElapsedMilliseconds}{(i == 0 ? " (cold)" : string.Empty)} {resp.Version}");
            }
            Console.WriteLine($"HTTP/{version}: {string.Join(", ", times)}");
        }

        var configs = (o.Get("configs") ?? $"{settings.AnswerModel}:{settings.AnswerReasoningEffort},gpt-6-sol:none,gpt-6-sol:low,gpt-6-luna:low,gpt-6-luna:none")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.Split(':')).Select(p => (Model: p[0], Effort: p.Length > 1 ? p[1] : null, Tier: p.Length > 2 ? p[2] : null)).ToList();
        int runs = o.GetInt("runs", 2);
        int variant = 0;
        Console.WriteLine($"\n# Answer stream (warm HTTP/2 connection, {runs} runs each; ms from request start)");
        using var http2 = NewClient(HttpVersion.Version20);
        var conn2 = new OpenAIConnection(credentials, http2);
        var model = new OpenAIResponsesModel(conn2, Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAIResponsesModel>.Instance);
        using (var warm = conn2.CreateRequest(HttpMethod.Get, "models/" + settings.AnswerModel))
        {
            using var _ = await http2.SendAsync(warm);
        }
        foreach (var (name, effort, tier) in configs)
        {
            for (int r = 0; r < runs; r++)
            {
                if (o.Has("vary"))
                {
                    // A different question per run, like consecutive questions in a real session (identical prompts would hit the cache trivially).
                    prompt = BuildPrompt(settings, sp.GetRequiredService<ITokenCounter>(), o.Get("q") ?? "simple", VaryingQuestions[(variant++) % VaryingQuestions.Length]);
                }
                var request = o.Get("layout") == "legacy"
                    ? new AssistantModelRequest(name, prompt.Instructions, string.Concat(prompt.Context.Select(b => b.Text)) + prompt.Input, PromptLibrary.SchemaName, prompt.SchemaJson, 4000, effort, "medium", tier)
                    : new AssistantModelRequest(name, prompt.Instructions, prompt.Input, PromptLibrary.SchemaName, prompt.SchemaJson, 4000, effort, "medium", tier, prompt.Context);
                var reader = new PartialJsonReader();
                var sw = Stopwatch.StartNew();
                long first = -1, direct = -1, spoken = -1, done = -1;
                UsageInfo? usage = null;
                string? servedTier = null;
                string error = string.Empty;
                try
                {
                    await foreach (var u in model.StreamAsync(request, CancellationToken.None))
                    {
                        if (u is AssistantTextDelta d)
                        {
                            if (first < 0)
                            {
                                first = sw.ElapsedMilliseconds;
                            }
                            reader.Append(d.Delta);
                            var view = reader.Read();
                            if (direct < 0 && view.Strings.TryGetValue("direct_position", out var dp) && dp.Value.Length >= 20)
                            {
                                direct = sw.ElapsedMilliseconds;
                            }
                            if (spoken < 0 && view.Strings.TryGetValue("spoken_answer", out var sa) && sa.Value.Length >= 20)
                            {
                                spoken = sw.ElapsedMilliseconds;
                            }
                        }
                        else if (u is AssistantCompleted c)
                        {
                            done = sw.ElapsedMilliseconds;
                            usage = c.Usage;
                            servedTier = c.ServiceTier;
                        }
                        else if (u is AssistantIncomplete inc)
                        {
                            error = "incomplete " + inc.Reason;
                        }
                    }
                }
                catch (ProviderException ex)
                {
                    error = ex.Message;
                }
                var v = reader.Read();
                int spokenChars = v.Strings.TryGetValue("spoken_answer", out var s) ? s.Value.Length : 0;
                int directChars = v.Strings.TryGetValue("direct_position", out var dd) ? dd.Value.Length : 0;
                Console.WriteLine($"{name,-12} {effort ?? "-",-5} {servedTier ?? "-",-8} firstToken={first,5} direct20={direct,5} spoken20={spoken,5} complete={done,6}  " +
                                  $"in={usage?.InputTokens,5} cached={usage?.CachedInputTokens,5} cacheWrite={usage?.CacheWriteTokens,5} out={usage?.OutputTokens,5} reasoning={usage?.ReasoningTokens,4} directChars={directChars} spokenChars={spokenChars} {error}");
                if (o.Has("show") && r == 0)
                {
                    Console.WriteLine("   direct: " + (v.Strings.TryGetValue("direct_position", out var x) ? x.Value : string.Empty));
                }
            }
        }
        return 0;
    }

    private static HttpClient NewClient(Version version) => new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    {
        BaseAddress = new Uri(OpenAIConnection.ApiBase),
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestVersion = version,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };

    private static readonly string[] VaryingQuestions =
    [
        "那你说说，Redis 和本地缓存各自适合什么场景？",
        "如果缓存和数据库数据不一致了，你一般怎么处理？",
        "缓存穿透、击穿和雪崩分别是什么，怎么防？",
        "你们订单系统的缓存过期时间是怎么定的？",
        "为什么不直接把所有数据都放缓存里？",
        "如果让你重新设计这个缓存层，你会改什么？",
    ];

    private static BuiltPrompt BuildPrompt(AppSettings settings, ITokenCounter tokens, string kind, string? question = null)
    {
        var time = TimeProvider.System;
        var clock = new SessionClock(time, TimeSpan.FromSeconds(60));
        var store = new TranscriptStore(time);
        var speakers = new SpeakerRegistry();
        var history = new QuestionHistory();
        var prep = new PreparationNotes();
        var filter = new InsightFilter(time);
        Party Classify(TranscriptSegment s) => s.Attribution.Status == AttributionStatus.SourceAlias ? Party.Self : Party.Other;
        var prompts = new PromptBuilder(store, speakers, history, prep, filter, tokens, clock, Classify);
        var interviewer = speakers.CreateDiarized(AudioSourceKind.System, false);
        speakers.EnsureSelfAlias();
        (bool Self, string Text)[] lines =
        [
            (false, "你好，欢迎参加今天的面试，先简单介绍一下你自己吧。"),
            (true, "好的，我叫小张，做了三年后端开发，主要负责订单系统和支付相关的服务。"),
            (false, "嗯，好的。那我们聊点技术的。"),
            (false, question ?? (kind == "hard"
                ? "你们订单系统为什么选 Kafka 而不是 RabbitMQ？另外如果要保证下单消息不丢也不重复消费，你具体会怎么设计，消费端失败了怎么处理？"
                : "好，来个偏技术又不太刁钻的，如果让你解释什么是缓存给一个完全不懂技术的朋友，你会怎么用一句话讲清楚？不用标准答案，你自己的说法就行。")),
        ];
        var ids = new List<string>();
        double at = 10;
        int turn = 0;
        foreach (var (self, text) in lines)
        {
            var attribution = self
                ? new SpeakerAttribution(AttributionStatus.SourceAlias, SpeakerRegistry.SelfAliasId, [], 0, null)
                : new SpeakerAttribution(AttributionStatus.Assigned, interviewer.Id, [], 0, null);
            var end = at + Math.Max(1.5, text.Length / 5.0);
            var seg = store.ApplyFinal(new TurnKey(1, self ? AudioSourceKind.Microphone : AudioSourceKind.System, 1, ++turn), text,
                TimeSpan.FromSeconds(at), TimeSpan.FromSeconds(end), new AudioAttributionDefault(attribution), $"item-{turn}")!;
            ids.Add(seg.Id);
            at = end + 1;
        }
        var q = new QuestionCandidate("q-1", 1, lines[^1].Text, [ids[^1]], AudioSourceKind.System, interviewer.Id, false, false, null, false,
            TimeSpan.FromSeconds(at - 1), DateTimeOffset.UtcNow);
        return prompts.Build(new PromptRequest(settings.Scenario, settings.AnswerDepth, settings.AnswerLanguage, q, [], null));
    }
}
