using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Sanctuary.Ps3.Web;

/// <summary>
/// Percent-decoding and <c>application/x-www-form-urlencoded</c> parsing with the behaviour of Python's
/// <c>urllib.parse.unquote</c> and <c>parse_qs</c>, which the reference server uses for request bodies and query strings.
/// </summary>
public static class FormBody
{
    /// <summary>
    /// Decodes <c>%XX</c> escapes as UTF-8 (a run of escapes that is not valid UTF-8 gives U+FFFD for each bad byte); a
    /// <c>%</c> that is not followed by two hex digits stays as written.
    /// </summary>
    public static string PercentDecode(string text)
    {
        if (!text.Contains('%', StringComparison.Ordinal))
            return text;

        var result = new StringBuilder(text.Length);
        var pending = new MemoryStream();

        void Flush()
        {
            if (pending.Length == 0)
                return;

            result.Append(Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length));
            pending.SetLength(0);
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '%' && i + 2 < text.Length && IsHex(text[i + 1]) && IsHex(text[i + 2]))
            {
                pending.WriteByte((byte)((HexValue(text[i + 1]) << 4) | HexValue(text[i + 2])));
                i += 2;
            }
            else
            {
                Flush();
                result.Append(text[i]);
            }
        }

        Flush();

        return result.ToString();
    }

    /// <summary>
    /// All <c>name=value</c> pairs in order, like <c>parse_qsl</c>: empty pairs are skipped, a pair without <c>=</c> has an empty
    /// value, <c>+</c> is a space. With <paramref name="keepBlank"/> false a pair whose value is empty is dropped.
    /// </summary>
    public static List<(string Name, string Value)> Parse(string text, bool keepBlank)
    {
        var pairs = new List<(string, string)>();

        foreach (var part in text.Split('&'))
        {
            if (part.Length == 0)
                continue;

            var split = part.IndexOf('=', StringComparison.Ordinal);
            var name = split < 0 ? part : part[..split];
            var value = split < 0 ? string.Empty : part[(split + 1)..];

            if (value.Length == 0 && !keepBlank)
                continue;

            pairs.Add((PercentDecode(name.Replace('+', ' ')), PercentDecode(value.Replace('+', ' '))));
        }

        return pairs;
    }

    /// <summary>The first value of each name, in the order the names first appear (what <c>{k: v[0]}</c> over <c>parse_qs</c> gives).</summary>
    public static List<(string Name, string Value)> FirstValues(IEnumerable<(string Name, string Value)> pairs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(string, string)>();

        foreach (var (name, value) in pairs)
        {
            if (seen.Add(name))
                result.Add((name, value));
        }

        return result;
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
}
