using System.Text.RegularExpressions;

namespace EvalToolkit.EvalGen.Readers;

/// <summary>
/// JavaScript-compatible string helpers used by the reader port to
/// avoid the silent divergences between .NET's
/// <see cref="string.Trim()"/> / <see cref="Regex"/> <c>\s</c> and the
/// ECMAScript whitespace set.
///
/// <para>The two divergences that bit the slice-1 parity port
/// (reviewer rounds 5 and 6):</para>
/// <list type="bullet">
///   <item><c>U+FEFF</c> (BOM / ZWNBSP) — ECMAScript whitespace; .NET
///     classifies it as a word character, so <see cref="string.Trim()"/>
///     leaves it alone and <c>Regex \s</c> doesn't match it.</item>
///   <item><c>U+0085</c> (NEL) — .NET whitespace; ECMAScript does
///     <b>not</b> classify it as whitespace, so .NET trims/splits on it
///     but JS does not.</item>
/// </list>
///
/// <para>The character set below is the exact ECMAScript
/// <c>WhiteSpace</c> + <c>LineTerminator</c> union as defined in the
/// ECMA-262 lexical grammar:
/// <c>\t \n \v \f \r \u0020 \u00a0 \u1680 \u2000–\u200a \u2028 \u2029
/// \u202f \u205f \u3000 \ufeff</c>. Both <see cref="Trim"/> and the
/// <see cref="WhitespaceRun"/> regex draw from this set so callers
/// stay byte-equivalent with JS regardless of which they use.</para>
/// </summary>
internal static class JsCompat
{
    /// <summary>
    /// Compiled regex matching one-or-more JavaScript whitespace
    /// characters. Suitable as a drop-in replacement for
    /// <c>/\s+/</c> in ported code.
    /// </summary>
    public static readonly Regex WhitespaceRun = new(
        @"[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_leading = new(
        @"\A[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_trailing = new(
        @"[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]+\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Trims leading and trailing JavaScript whitespace from
    /// <paramref name="value"/>. Equivalent to
    /// <c>String.prototype.trim</c>.
    /// </summary>
    public static string Trim(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            return value;
        }
        string head = s_leading.Replace(value, string.Empty);
        return s_trailing.Replace(head, string.Empty);
    }
}
