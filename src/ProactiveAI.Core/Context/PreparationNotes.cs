using System.Security.Cryptography;
using System.Text;
using ProactiveAI.Core.Abstractions;
using ProactiveAI.Core.Text;

namespace ProactiveAI.Core.Context;

public enum PrepField
{
    RoleOrTopic,
    Contribution,
    ProjectOrMethod,
    VerifiedResults,
    Limitations,
    Terminology,
    Other,
    Resume,
}

/// <param name="Resume">Set for excerpts of the selected résumé.</param>
public sealed record PrepExcerpt(string Id, PrepField Field, string Text, ResumeInfo? Resume = null);

/// <summary>
/// User-entered preparation notes (kept in memory only) and the résumé selected for the session. Excerpts receive
/// content-derived stable IDs so generated answers can cite them and stale citations are detected when notes change.
/// </summary>
public sealed class PreparationNotes
{
    public const int MaxCharsPerField = 8000;
    public const int MaxExcerptChars = 1200;
    /// <summary>Résumé text is cut into excerpts of about this size at line boundaries.</summary>
    public const int ResumeChunkChars = 600;

    private readonly object _gate = new();
    private readonly Dictionary<PrepField, string> _fields = new();
    private Resume? _resume;

    public event Action? Changed;

    public string Get(PrepField field)
    {
        lock (_gate)
        {
            return _fields.GetValueOrDefault(field, string.Empty);
        }
    }

    public Resume? Resume
    {
        get { lock (_gate) { return _resume; } }
    }

    public bool IsEmpty
    {
        get { lock (_gate) { return _fields.Values.All(string.IsNullOrWhiteSpace) && _resume is null; } }
    }

    /// <summary>Characters of the hand-written notes.</summary>
    public int TotalChars
    {
        get { lock (_gate) { return _fields.Values.Sum(v => v.Length); } }
    }

    /// <summary>Sets (or clears) the résumé used for this session.</summary>
    public void SetResume(Resume? resume)
    {
        lock (_gate)
        {
            if (_resume?.Info == resume?.Info && _resume?.Text == resume?.Text)
            {
                return;
            }
            _resume = resume;
        }
        Changed?.Invoke();
    }

    public void Set(PrepField field, string? text)
    {
        text ??= string.Empty;
        if (text.Length > MaxCharsPerField)
        {
            text = text[..MaxCharsPerField];
        }
        lock (_gate)
        {
            if (_fields.GetValueOrDefault(field, string.Empty) == text)
            {
                return;
            }
            _fields[field] = text;
        }
        Changed?.Invoke();
    }

    /// <summary>Clears the hand-written notes (the selected résumé stays).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _fields.Clear();
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<PrepExcerpt> Excerpts()
    {
        var result = new List<PrepExcerpt>();
        lock (_gate)
        {
            foreach (var (field, text) in _fields.OrderBy(kv => kv.Key))
            {
                int n = 0;
                foreach (var para in SplitParagraphs(text))
                {
                    n++;
                    result.Add(new PrepExcerpt($"prep-{Short(field)}-{n}-{Hash(para)}", field, para));
                }
            }
            if (_resume is { } r)
            {
                int n = 0;
                foreach (var chunk in Chunk(r.Text))
                {
                    n++;
                    result.Add(new PrepExcerpt($"cv-{n}-{Hash(chunk)}", PrepField.Resume, chunk, r.Info));
                }
            }
        }
        return result;
    }

    /// <summary>Packs lines into excerpts of about <see cref="ResumeChunkChars"/> characters, breaking at blank lines when possible.</summary>
    internal static IEnumerable<string> Chunk(string text)
    {
        var current = new System.Text.StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            bool blank = line.Length == 0;
            if (current.Length > 0 && (blank && current.Length >= ResumeChunkChars / 3 || current.Length + line.Length + 1 > ResumeChunkChars))
            {
                yield return current.ToString();
                current.Clear();
            }
            if (blank)
            {
                continue;
            }
            for (int i = 0; i < line.Length; i += MaxExcerptChars)
            {
                var part = line.Substring(i, Math.Min(MaxExcerptChars, line.Length - i));
                if (current.Length > 0 && current.Length + part.Length + 1 > ResumeChunkChars)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                if (current.Length > 0)
                {
                    current.Append('\n');
                }
                current.Append(part);
            }
        }
        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    internal static IEnumerable<string> SplitParagraphs(string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            for (int i = 0; i < line.Length; i += MaxExcerptChars)
            {
                yield return line.Substring(i, Math.Min(MaxExcerptChars, line.Length - i));
            }
        }
    }

    private static string Short(PrepField f) => f switch
    {
        PrepField.RoleOrTopic => "role",
        PrepField.Contribution => "contrib",
        PrepField.ProjectOrMethod => "method",
        PrepField.VerifiedResults => "results",
        PrepField.Limitations => "limits",
        PrepField.Terminology => "terms",
        _ => "other",
    };

    private static string Hash(string s) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..6];
}

public sealed record PrepSelection(IReadOnlyList<PrepExcerpt> Included, int OmittedCount, int Tokens);

public static class PreparationSelector
{
    /// <summary>Selects the most relevant excerpts within a token budget; role/topic is prioritized.</summary>
    public static PrepSelection Select(IReadOnlyList<PrepExcerpt> excerpts, string focusText, int maxTokens, ITokenCounter tokens)
    {
        var scored = excerpts
            .Select(e => (Excerpt: e, Score: Score(e, focusText)))
            .OrderByDescending(x => x.Score)
            .ToList();
        var included = new List<PrepExcerpt>();
        int used = 0;
        foreach (var (e, _) in scored)
        {
            int t = tokens.Count(e.Text) + 12;
            if (used + t > maxTokens)
            {
                continue;
            }
            included.Add(e);
            used += t;
        }
        // keep original order for readability
        var order = excerpts.Select((e, i) => (e.Id, i)).ToDictionary(x => x.Id, x => x.i);
        included.Sort((a, b) => order[a.Id].CompareTo(order[b.Id]));
        return new PrepSelection(included, excerpts.Count - included.Count, used);
    }

    private static double Score(PrepExcerpt e, string focus)
    {
        double relevance = TextTools.Containment(focus, e.Text) + TextTools.Jaccard(focus, e.Text);
        double fieldBoost = e.Field switch
        {
            PrepField.RoleOrTopic => 1.0,
            PrepField.Contribution => 0.3,
            PrepField.VerifiedResults => 0.3,
            PrepField.ProjectOrMethod => 0.25,
            PrepField.Limitations => 0.2,
            PrepField.Terminology => 0.1,
            PrepField.Resume => 0.3,
            _ => 0.05,
        };
        return relevance + fieldBoost;
    }
}
