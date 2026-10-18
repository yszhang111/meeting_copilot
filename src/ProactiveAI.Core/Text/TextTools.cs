using System.Globalization;
using System.Text;

namespace ProactiveAI.Core.Text;

/// <summary>Language-agnostic normalization and similarity for English and CJK text.</summary>
public static class TextTools
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char raw in text.Normalize(NormalizationForm.FormKC))
        {
            char c = char.ToLowerInvariant(raw);
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            bool isWord = char.IsLetterOrDigit(c) || cat == UnicodeCategory.OtherLetter;
            if (isWord)
            {
                if (space && sb.Length > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(c);
                space = false;
            }
            else
            {
                space = true;
            }
        }
        return sb.ToString();
    }

    public static bool IsCjk(char c) =>
        (c >= '\u4E00' && c <= '\u9FFF') || (c >= '\u3400' && c <= '\u4DBF') ||
        (c >= '\u3040' && c <= '\u30FF') || (c >= '\uAC00' && c <= '\uD7AF') ||
        (c >= '\uF900' && c <= '\uFAFF');

    public static bool ContainsCjk(string? text) => text is not null && text.Any(IsCjk);

    /// <summary>Tokens: lowercase words for alphabetic scripts and character bigrams for CJK runs.</summary>
    public static HashSet<string> Tokens(string? text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        string n = Normalize(text);
        int i = 0;
        while (i < n.Length)
        {
            char c = n[i];
            if (c == ' ')
            {
                i++;
                continue;
            }
            if (IsCjk(c))
            {
                int start = i;
                while (i < n.Length && IsCjk(n[i]))
                {
                    i++;
                }
                var run = n.AsSpan(start, i - start);
                if (run.Length == 1)
                {
                    set.Add(run.ToString());
                }
                for (int k = 0; k + 1 < run.Length; k++)
                {
                    set.Add(run.Slice(k, 2).ToString());
                }
            }
            else
            {
                int start = i;
                while (i < n.Length && n[i] != ' ' && !IsCjk(n[i]))
                {
                    i++;
                }
                string word = n[start..i];
                if (word.Length > 1 && !StopWords.Contains(word))
                {
                    set.Add(word);
                }
            }
        }
        return set;
    }

    public static double Jaccard(string? a, string? b)
    {
        var ta = Tokens(a);
        var tb = Tokens(b);
        if (ta.Count == 0 && tb.Count == 0)
        {
            return 1;
        }
        if (ta.Count == 0 || tb.Count == 0)
        {
            return 0;
        }
        int inter = ta.Count(tb.Contains);
        return inter / (double)(ta.Count + tb.Count - inter);
    }

    /// <summary>Fraction of tokens in <paramref name="a"/> that also appear in <paramref name="b"/>.</summary>
    public static double Containment(string? a, string? b)
    {
        var ta = Tokens(a);
        if (ta.Count == 0)
        {
            return 0;
        }
        var tb = Tokens(b);
        return ta.Count(tb.Contains) / (double)ta.Count;
    }

    public static string Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text ?? string.Empty;
        }
        return string.Concat(text.AsSpan(0, Math.Max(0, maxChars - 1)), "…");
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "is", "are", "was", "were",
        "be", "it", "this", "that", "with", "as", "at", "by", "we", "you", "i", "so", "do", "does",
        "um", "uh", "like", "just", "okay", "ok", "yeah",
    };
}
