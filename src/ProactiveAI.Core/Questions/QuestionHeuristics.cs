using System.Text.RegularExpressions;

namespace ProactiveAI.Core.Questions;

public sealed record QuestionSignal(double Score, bool IsQuestion, bool LikelyRhetoricalOrQuoted, bool MultiPart, IReadOnlyList<string> Reasons);

public enum MeetingTrigger
{
    Question,
    Alternatives,
    UnresolvedRequirement,
    ConsequentialClaim,
}

/// <summary>
/// Inexpensive Stage-1 heuristics for English and Chinese. They identify candidates only and never
/// establish factual correctness.
/// </summary>
public static partial class QuestionHeuristics
{
    public static double ThresholdFor(int sensitivity) => sensitivity switch
    {
        <= 1 => 0.75,
        2 => 0.55,
        _ => 0.4,
    };

    public static QuestionSignal Analyze(string text, int sensitivity = 2)
    {
        var reasons = new List<string>();
        double score = 0;
        var t = text.Trim();
        if (t.Length == 0)
        {
            return new QuestionSignal(0, false, false, false, reasons);
        }

        if (TagOnly().IsMatch(t))
        {
            return new QuestionSignal(0.05, false, true, false, ["tag-question"]);
        }
        if (QuestionMark().IsMatch(t)) { score += 0.6; reasons.Add("question-mark"); }
        if (EnInterrogativeStart().IsMatch(t)) { score += 0.35; reasons.Add("en-interrogative"); }
        if (EnImperative().IsMatch(t)) { score += 0.6; reasons.Add("en-imperative"); }
        if (EnRequest().IsMatch(t)) { score += 0.45; reasons.Add("en-request"); }
        if (ZhParticleEnd().IsMatch(t)) { score += 0.4; reasons.Add("zh-particle"); }
        if (ZhInterrogative().IsMatch(t)) { score += 0.35; reasons.Add("zh-interrogative"); }
        if (ZhRequest().IsMatch(t)) { score += 0.6; reasons.Add("zh-request"); }

        int marks = QuestionMarks().Matches(t).Count;
        bool multi = marks >= 2 || MultiPart().IsMatch(t);
        if (multi) { score += 0.1; reasons.Add("multi-part"); }

        bool rhetorical = Rhetorical().IsMatch(t);
        if (rhetorical) { score -= 0.45; reasons.Add("rhetorical-or-quoted"); }

        int contentChars = t.Count(char.IsLetterOrDigit);
        if (contentChars < 6) { score -= 0.3; reasons.Add("very-short"); }

        score = Math.Clamp(score, 0, 1.5);
        return new QuestionSignal(score, score >= ThresholdFor(sensitivity), rhetorical, multi, reasons);
    }

    /// <summary>
    /// Turn-level analysis: the whole text and each sentence. A question is often followed by a remark
    /// ("…讲清楚？不用标准答案，你自己的说法就行。", "…in one sentence? No standard answer needed.").
    /// Returns the strongest non-rhetorical question signal, otherwise the whole-text signal.
    /// </summary>
    public static QuestionSignal AnalyzeTurn(string text, int sensitivity = 2)
    {
        var whole = Analyze(text, sensitivity);
        if (whole.IsQuestion && !whole.LikelyRhetoricalOrQuoted)
        {
            return whole;
        }
        QuestionSignal? best = null;
        foreach (var sentence in Sentences(text))
        {
            var s = Analyze(sentence, sensitivity);
            if (s.IsQuestion && !s.LikelyRhetoricalOrQuoted && (best is null || s.Score > best.Score))
            {
                best = s;
            }
        }
        return best is null ? whole : best with { MultiPart = best.MultiPart || whole.MultiPart };
    }

    /// <summary>Splits at sentence terminators, keeping a closing quote or bracket with its sentence.</summary>
    public static IReadOnlyList<string> Sentences(string text) =>
        SentenceBreak().Split(text).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    /// <summary>True when the text ends with a question mark (optionally followed by a closing quote or bracket).</summary>
    public static bool EndsWithQuestionMark(string text) => QuestionMark().IsMatch(text);

    public static IReadOnlyList<MeetingTrigger> MeetingTriggers(string text, int sensitivity = 2)
    {
        var list = new List<MeetingTrigger>();
        var q = AnalyzeTurn(text, sensitivity);
        if (q.IsQuestion && !q.LikelyRhetoricalOrQuoted)
        {
            list.Add(MeetingTrigger.Question);
        }
        if (Alternatives().IsMatch(text))
        {
            list.Add(MeetingTrigger.Alternatives);
        }
        if (Unresolved().IsMatch(text))
        {
            list.Add(MeetingTrigger.UnresolvedRequirement);
        }
        if (sensitivity >= 2 && Claim().IsMatch(text))
        {
            list.Add(MeetingTrigger.ConsequentialClaim);
        }
        return list;
    }

    /// <summary>Whether appended text materially changes a question (new subquestion, constraint or correction).</summary>
    public static bool IsMaterialAddition(string addedText, int sensitivity = 2)
    {
        if (string.IsNullOrWhiteSpace(addedText))
        {
            return false;
        }
        return Analyze(addedText, sensitivity).Score >= 0.35 || Constraint().IsMatch(addedText);
    }

    [GeneratedRegex(@"^\s*(right|ok(ay)?|yeah|you know|isn'?t it|correct|对吧|是吧|好吗|对不对|是不是|行吗|好不好)\s*[?？]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TagOnly();

    [GeneratedRegex(@"(?<=[。！？!?；;…][""”’)）」』]?)(?![""”’)）」』])\s*|(?<=\.[""”’)]?)(?![""”’)])\s+")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"[?？]\s*[""”’)）]?\s*$")]
    private static partial Regex QuestionMark();

    [GeneratedRegex(@"[?？]")]
    private static partial Regex QuestionMarks();

    [GeneratedRegex(@"^\s*(so\s+|and\s+|but\s+|okay\s+,?\s*)?(what|why|how|when|where|which|who|whom|whose|can|could|would|will|do|does|did|is|are|was|were|have|has|should|shall|may|might)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EnInterrogativeStart();

    [GeneratedRegex(@"^\s*(okay,?\s*|so,?\s*|now,?\s*|and\s+)?(please\s+)?(explain|describe|compare|contrast|justify|walk (me|us) through|tell (me|us)( more)? about|elaborate|talk (me|us) through|give (me|us) an example|defend|prove|derive|motivate|convince (me|us)|summarize|discuss)\b|\b(can|could|would|will) you (please )?(explain|describe|compare|walk|tell|elaborate|talk|give|justify|clarify|summarize|discuss|defend)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EnImperative();

    [GeneratedRegex(@"\b(how would you|what would you|why did you|why do you|why not|what('s| is) the difference|pros and cons|trade-?offs? (between|of)|what happens if|how do you (know|ensure|handle|measure|prove|validate)|if you (had|have|were) to (explain|describe|design|choose|pick|summarize|teach))\b", RegexOptions.IgnoreCase)]
    private static partial Regex EnRequest();

    [GeneratedRegex(@"[吗呢么嘛][？?。!！\s]*$")]
    private static partial Regex ZhParticleEnd();

    [GeneratedRegex(@"什么|为什么|为何|怎么|怎样|如何|哪(个|些|种|里|儿)|多少|几(个|种|次|年)|是否|能否|能不能|可不可以|会不会|有没有|是不是|对不对|行不行|凭什么")]
    private static partial Regex ZhInterrogative();

    [GeneratedRegex(@"请(你)?(简单)?(解释|介绍|说明|谈谈|讲讲|说说|比较|对比|阐述|论证|举例|描述|分析|证明|回答)|(解释|介绍|说明|讲|谈|说|比较|对比|分析|展开)一下|你(是)?(怎么|如何)(看|理解|证明|保证|处理|评估|验证|设计|选择)|你(会|要|能|可以|打算|准备)(怎么|如何)|如果(让|要|请)你(来)?(解释|介绍|讲|说|设计|选|做|实现|评价|评估|给|回答|处理)|用一句话(讲|说|解释|概括|总结|介绍|描述)|为什么(选择|选用|采用|不用|不选|没有)|有(什么|哪些)(区别|优势|优点|缺点|不足|局限|贡献|创新|难点|挑战)|局限性|创新点|主要贡献|依据是什么|如何验证")]
    private static partial Regex ZhRequest();

    [GeneratedRegex(@"(\b(and also|as well as|secondly|second(ly)?,|another question|follow[- ]up)\b)|另外|其次|还有一个问题|第二|同时.*[？?]", RegexOptions.IgnoreCase)]
    private static partial Regex MultiPart();

    [GeneratedRegex(@"(\b(he|she|they|someone|people) (just |also |once )?(asked|asks|said|says|wondered)\b)|(""[^""]*\?"")|(“[^”]*[?？]”)|(^\s*(who|what|why) (doesn'?t|does not|wouldn'?t|would not|isn'?t|is not|can'?t)\b)|(,\s*(right|isn'?t it|you know)\s*\?\s*$)|(他|她|他们|她们|有人|别人)[^，。？！,.?!\s]{0,4}问|所谓|难道|岂不是|谁不知道|谁不(喜欢|想)", RegexOptions.IgnoreCase)]
    private static partial Regex Rhetorical();

    [GeneratedRegex(@"(\b(or should we|either\b.*\bor|option (a|b|1|2|one|two)|alternatively|versus|vs\.?|instead of)\b)|还是说|还是用|还是选|或者|方案[一二三ABC12]|两种方案|选哪个|哪种更好", RegexOptions.IgnoreCase)]
    private static partial Regex Alternatives();

    [GeneratedRegex(@"(\b(not sure|unclear|tbd|to be decided|we need to decide|depends on|open question|haven'?t decided|no consensus)\b)|不确定|还没定|待定|需要确认|取决于|还没想好|有争议|没有结论", RegexOptions.IgnoreCase)]
    private static partial Regex Unresolved();

    [GeneratedRegex(@"(\b(always|never|guarantee[sd]?|definitely|impossible|100 ?%|zero (risk|downtime))\b)|(\d+(\.\d+)?\s?(%|ms|x|倍|毫秒|秒))|必须|一定|保证|绝对|肯定不会|不可能", RegexOptions.IgnoreCase)]
    private static partial Regex Claim();

    [GeneratedRegex(@"(\b(assume|assuming|suppose|given that|but what if|what if|constraint|limited to|must|only|without)\b)|假设|如果|假如|前提|但是|不过|限制|只能|必须|不允许|换成", RegexOptions.IgnoreCase)]
    private static partial Regex Constraint();
}
