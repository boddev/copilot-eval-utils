using System.Security.Cryptography;
using System.Text;

namespace EvalToolkit.Core;

/// <summary>
/// Stable, deterministic identifier generation for eval items and
/// item groups. The TS implementation uses <b>SHA-256 → lowercase hex →
/// first 12 characters</b> for both surfaces, with a <c>|</c> separator
/// between preimage parts.
///
/// These IDs ship on disk in <c>EvalSet.items[].id</c> and are matched
/// across tools, so byte-for-byte parity with the TS impl is mandatory.
/// </summary>
/// <remarks>
/// Verified TS sources:
/// <list type="bullet">
///   <item><c>eval-gen/src/validator.ts:95-99</c> —
///     <c>generateItemId(prompt, sourceLocation)</c>:
///     <c>sha256(`${prompt}|${sourceLocation}`).digest('hex').slice(0,12)</c>.</item>
///   <item><c>eval-gen/src/writers.ts:167-171</c> —
///     <c>stableGroupHash(group)</c>:
///     <c>sha256(group.map(i =&gt; i.id || `${i.prompt}|${i.source_location}`).join('|')).digest('hex').slice(0,12)</c>.</item>
/// </list>
/// An earlier revision of this file incorrectly claimed the TS impl
/// used xxhash3 and shipped a SHA-256-truncated-to-16B-base32-no-pad
/// placeholder. Both were wrong; this file is now the corrected
/// version. The phase A todo <c>xxhash3-port</c> has been dropped as
/// false-premise.
/// </remarks>
public static class StableIds
{
    /// <summary>Hex-character count taken from the SHA-256 digest. Locked at 12 to match TS.</summary>
    private const int HexLength = 12;

    /// <summary>Separator used between preimage parts. Locked at <c>|</c> to match TS.</summary>
    public const char Separator = '|';

    /// <summary>
    /// Stable id for a generated eval item. Equivalent to TS
    /// <c>generateItemId(prompt, sourceLocation)</c>.
    /// </summary>
    public static string ItemId(string prompt, string sourceLocation)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(sourceLocation);
        return Sha256Hex12($"{prompt}{Separator}{sourceLocation}");
    }

    /// <summary>
    /// Stable id for a group of items (used for m365 multi-prompt
    /// document <c>thread_id</c>). Equivalent to TS
    /// <c>stableGroupHash(group)</c>. For each item, the preimage piece
    /// is <c>item.id</c> if non-empty, otherwise
    /// <c>"{prompt}|{sourceLocation}"</c>; pieces are joined with
    /// <c>|</c>.
    /// </summary>
    public static string GroupHash(IEnumerable<GeneratedEvalItem> group)
    {
        ArgumentNullException.ThrowIfNull(group);
        StringBuilder preimage = new();
        bool first = true;
        foreach (GeneratedEvalItem item in group)
        {
            if (!first)
            {
                preimage.Append(Separator);
            }
            preimage.Append(GroupPiece(item));
            first = false;
        }
        return Sha256Hex12(preimage.ToString());
    }

    /// <summary>
    /// Hash an arbitrary content string with the same SHA-256 → hex →
    /// 12-char pipeline. Useful where the TS impl hashes a constructed
    /// preimage that doesn't naturally decompose into the
    /// <see cref="ItemId"/> shape. Callers are responsible for using
    /// <c>|</c> as the part separator if mirroring a multi-part TS hash.
    /// </summary>
    public static string ContentHash12(string preimage)
    {
        ArgumentNullException.ThrowIfNull(preimage);
        return Sha256Hex12(preimage);
    }

    private static string GroupPiece(GeneratedEvalItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        // TS uses `item.id ||` — a JS-falsy check, so empty string also
        // falls through to the prompt|sourceLocation fallback.
        if (!string.IsNullOrEmpty(item.Id))
        {
            return item.Id;
        }
        return $"{item.Prompt}{Separator}{item.SourceLocation}";
    }

    private static string Sha256Hex12(string preimage)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(preimage));
        // Render full hex (lowercase) then slice to 12 chars. ToHexString
        // uses uppercase; we lowercase explicitly to match TS digest('hex').
        return Convert.ToHexString(hash).ToLowerInvariant()[..HexLength];
    }
}

