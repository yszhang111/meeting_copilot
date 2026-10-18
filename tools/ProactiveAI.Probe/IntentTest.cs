using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ProactiveAI.Core.Questions;
using ProactiveAI.Core.Settings;

namespace ProactiveAI.Probe;

/// <summary>
/// Live check of the AI question-intent classifier on labeled synthetic turns (no audio). Prints local heuristic score,
/// AI verdict, confidence and latency, and the accuracy on the cases the local rules miss.
/// </summary>
public static class IntentTest
{
    private static readonly (string Text, bool IsQuestion)[] Cases =
    [
        ("好，来个偏技术又不太刁钻的，如果让你解释什么是缓存给一个完全不懂技术的朋友，你会怎么用一句话讲清楚？不用标准答案，你自己的说法就行。", true),
        ("我想听听你在上一个项目里做缓存设计的思路", true),
        ("说说你对一致性哈希的理解", true),
        ("你可以展开讲讲当时为什么放弃了微服务", true),
        ("那第二个方案呢", true),
        ("I'd love to hear how you approached the migration", true),
        ("Tell me about a time you disagreed with your manager", true),
        ("walk me through your thesis results on the second dataset", true),
        ("好的，我们进入下一个部分", false),
        ("嗯，这个回答挺完整的，谢谢", false),
        ("请大家把摄像头打开一下", false),
        ("我们公司主要用 Redis 做缓存，规模大概几十台机器", false),
        ("OK, that makes sense. Let's move on.", false),
        ("I think we're running a bit late today", false),
        ("他刚才问为什么不用Redis，我觉得这个问题挺好", false),
        ("对吧", false),
    ];

    public static async Task<int> RunAsync(Options o)
    {
        using var sp = Host.Build();
        var settings = sp.GetRequiredService<ISettingsStore>().Load();
        var classifier = sp.GetRequiredService<IQuestionIntentClassifier>();
        string model = o.Get("model") ?? settings.IntentModel;
        string effort = o.Get("effort") ?? settings.IntentReasoningEffort;
        Console.WriteLine($"Intent test: model={model} effort={effort} cases={Cases.Length}");
        int correct = 0, missedLocally = 0, recoveredByAi = 0, errors = 0;
        var latencies = new List<double>();
        long inTokens = 0, outTokens = 0;
        foreach (var (text, expected) in Cases)
        {
            var local = QuestionHeuristics.AnalyzeTurn(text);
            bool localYes = local.IsQuestion && !local.LikelyRhetoricalOrQuoted;
            string verdictText;
            bool final;
            if (localYes)
            {
                final = true;
                verdictText = "local (no AI call)";
            }
            else
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var v = await classifier.ClassifyAsync(new IntentRequest(Scenario.Interview, text, null, model, effort), cts.Token);
                    latencies.Add(sw.Elapsed.TotalMilliseconds);
                    inTokens += v.Usage?.InputTokens ?? 0;
                    outTokens += v.Usage?.OutputTokens ?? 0;
                    final = v.IsQuestion;
                    verdictText = $"AI {(v.IsQuestion ? "question" : "no")} conf={v.Confidence:F2} {sw.ElapsedMilliseconds} ms";
                }
                catch (Exception ex)
                {
                    errors++;
                    final = false;
                    verdictText = $"AI error {ex.GetType().Name}: {ex.Message}";
                }
                if (expected)
                {
                    missedLocally++;
                    recoveredByAi += final ? 1 : 0;
                }
            }
            correct += final == expected ? 1 : 0;
            Console.WriteLine($"{(final == expected ? "OK  " : "FAIL")} expected={(expected ? "Q" : "-")} local={local.Score:F2} {verdictText}  {text}");
        }
        latencies.Sort();
        string Pct(double p) => latencies.Count == 0 ? "n/a" : latencies[Math.Min(latencies.Count - 1, (int)(p * latencies.Count))].ToString("F0");
        Console.WriteLine($"\nAccuracy {correct}/{Cases.Length}; questions missed by local rules {missedLocally}, recovered by AI {recoveredByAi}; errors {errors}");
        Console.WriteLine($"AI latency ms: median {Pct(0.5)} p90 {Pct(0.9)} max {(latencies.Count == 0 ? "n/a" : latencies[^1].ToString("F0"))}; tokens in {inTokens} out {outTokens}");
        return correct == Cases.Length ? 0 : 4;
    }
}
