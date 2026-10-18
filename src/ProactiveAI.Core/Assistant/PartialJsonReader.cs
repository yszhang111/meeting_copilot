using System.Text;

namespace ProactiveAI.Core.Assistant;

/// <summary>
/// Tolerant incremental reader for streamed structured output. Extracts the (possibly incomplete)
/// values of top-level string properties and the completed items of top-level string arrays so the
/// UI can render readable paragraphs before the JSON is complete. It never shows raw JSON.
/// </summary>
public sealed class PartialJsonReader
{
    private readonly StringBuilder _buffer = new();

    public int Length => _buffer.Length;

    public string Text => _buffer.ToString();

    public void Append(string delta) => _buffer.Append(delta);

    public void Reset() => _buffer.Clear();

    public PartialView Read() => Parse(_buffer.ToString());

    public sealed record PartialView(IReadOnlyDictionary<string, PartialString> Strings, IReadOnlyDictionary<string, IReadOnlyList<string>> StringArrays);

    public sealed record PartialString(string Value, bool Complete);

    public static PartialView Parse(string json)
    {
        var strings = new Dictionary<string, PartialString>(StringComparer.Ordinal);
        var arrays = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        int i = 0;
        SkipWs(json, ref i);
        if (i >= json.Length || json[i] != '{')
        {
            return new PartialView(strings, arrays);
        }
        i++;
        while (i < json.Length)
        {
            SkipWs(json, ref i);
            if (i >= json.Length)
            {
                break;
            }
            if (json[i] == ',')
            {
                i++;
                continue;
            }
            if (json[i] == '}')
            {
                break;
            }
            if (json[i] != '"')
            {
                break;
            }
            var (key, keyComplete) = ReadString(json, ref i);
            if (!keyComplete)
            {
                break;
            }
            SkipWs(json, ref i);
            if (i >= json.Length || json[i] != ':')
            {
                break;
            }
            i++;
            SkipWs(json, ref i);
            if (i >= json.Length)
            {
                break;
            }
            char c = json[i];
            if (c == '"')
            {
                var (value, complete) = ReadString(json, ref i);
                strings[key] = new PartialString(value, complete);
                if (!complete)
                {
                    break;
                }
            }
            else if (c == '[')
            {
                var items = new List<string>();
                bool done = ReadStringArray(json, ref i, items);
                arrays[key] = items;
                if (!done)
                {
                    break;
                }
            }
            else
            {
                if (!SkipValue(json, ref i))
                {
                    break;
                }
            }
        }
        return new PartialView(strings, arrays);
    }

    private static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i]))
        {
            i++;
        }
    }

    private static (string Value, bool Complete) ReadString(string s, ref int i)
    {
        var sb = new StringBuilder();
        i++; // opening quote
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '"')
            {
                i++;
                return (sb.ToString(), true);
            }
            if (c == '\\')
            {
                if (i + 1 >= s.Length)
                {
                    break;
                }
                char n = s[i + 1];
                switch (n)
                {
                    case 'n': sb.Append('\n'); i += 2; break;
                    case 't': sb.Append('\t'); i += 2; break;
                    case 'r': i += 2; break;
                    case 'b': i += 2; break;
                    case 'f': i += 2; break;
                    case '/': sb.Append('/'); i += 2; break;
                    case '\\': sb.Append('\\'); i += 2; break;
                    case '"': sb.Append('"'); i += 2; break;
                    case 'u':
                        if (i + 5 >= s.Length)
                        {
                            return (sb.ToString(), false);
                        }
                        if (int.TryParse(s.AsSpan(i + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out int code))
                        {
                            sb.Append((char)code);
                        }
                        i += 6;
                        break;
                    default: i += 2; break;
                }
                continue;
            }
            sb.Append(c);
            i++;
        }
        return (sb.ToString(), false);
    }

    private static bool ReadStringArray(string s, ref int i, List<string> items)
    {
        i++; // [
        while (i < s.Length)
        {
            SkipWs(s, ref i);
            if (i >= s.Length)
            {
                return false;
            }
            char c = s[i];
            if (c == ']')
            {
                i++;
                return true;
            }
            if (c == ',')
            {
                i++;
                continue;
            }
            if (c == '"')
            {
                var (v, complete) = ReadString(s, ref i);
                if (!complete)
                {
                    return false;
                }
                items.Add(v);
            }
            else if (!SkipValue(s, ref i))
            {
                return false;
            }
        }
        return false;
    }

    private static bool SkipValue(string s, ref int i)
    {
        int depth = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '"')
            {
                var (_, complete) = ReadString(s, ref i);
                if (!complete)
                {
                    return false;
                }
                if (depth == 0)
                {
                    return true;
                }
                continue;
            }
            if (c is '{' or '[')
            {
                depth++;
            }
            else if (c is '}' or ']')
            {
                if (depth == 0)
                {
                    return true; // end of enclosing container; caller handles it
                }
                depth--;
                if (depth == 0)
                {
                    i++;
                    return true;
                }
            }
            else if (c == ',' && depth == 0)
            {
                return true;
            }
            i++;
        }
        return false;
    }
}
