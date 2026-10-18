using Microsoft.ML.Tokenizers;
using ProactiveAI.Core.Abstractions;

namespace ProactiveAI.Infrastructure.Text;

/// <summary>
/// Token counting with the o200k_base encoding used by current OpenAI text models. Counts are used
/// for input budgets; a conservative character-based bound is used if the tokenizer fails to load.
/// </summary>
public sealed class TiktokenTokenCounter : ITokenCounter
{
    private readonly Tokenizer? _tokenizer;

    public TiktokenTokenCounter()
    {
        try
        {
            _tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");
        }
        catch
        {
            _tokenizer = null;
        }
    }

    public bool UsesFallback => _tokenizer is null;

    public int Count(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        if (_tokenizer is not null)
        {
            return _tokenizer.CountTokens(text);
        }
        return ConservativeBound(text);
    }

    /// <summary>Upper bound: one token per CJK character, one per 3 other characters.</summary>
    public static int ConservativeBound(string text)
    {
        int cjk = 0;
        foreach (char c in text)
        {
            if (Core.Text.TextTools.IsCjk(c))
            {
                cjk++;
            }
        }
        return cjk + ((text.Length - cjk + 2) / 3);
    }
}
