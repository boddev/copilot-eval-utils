namespace EvalToolkit.EvalGen.Readers;

/// <summary>
/// A single record emitted by a dataset reader. Mirrors the TS
/// <c>Record&lt;string, unknown&gt;</c> shape: ordered string keys
/// (insertion order preserved across the wire) mapping to dynamic
/// values whose runtime type depends on the source format.
///
/// Cell-value typing per format:
/// <list type="bullet">
///   <item><b>CSV/TSV</b>: every value is a <see cref="string"/>
///     (matches <c>csv-parse</c> default behavior — no implicit
///     numeric coercion).</item>
///   <item><b>JSON/JSONL</b>: values are whatever
///     <see cref="System.Text.Json.JsonElement"/> decoded them as —
///     <see cref="string"/>, <see cref="double"/> (or <see cref="long"/>
///     when integral), <see cref="bool"/>, <c>null</c>, nested
///     <c>IReadOnlyList&lt;object?&gt;</c>, or nested
///     <see cref="DatasetRow"/>.</item>
///   <item><b>TXT/MD</b>: emits a fixed
///     <c>{chunk_number:int, content:string, word_count:int}</c>
///     shape per chunk.</item>
///   <item><b>XLSX/DOCX/PDF/PPTX</b>: format-specific, defined in
///     later reader-port slices.</item>
/// </list>
///
/// Insertion order is the contract because the TS impl relies on
/// JavaScript Object key-insertion ordering — downstream writers and
/// the parity harness compare wire output where field order is
/// observable. <see cref="DatasetRow"/> is therefore implemented as a
/// thin wrapper over an order-preserving collection (a list of pairs)
/// instead of a plain <see cref="Dictionary{TKey,TValue}"/>.
/// </summary>
public sealed class DatasetRow
{
    private readonly List<KeyValuePair<string, object?>> _entries;

    public DatasetRow(int capacity = 0)
    {
        _entries = capacity > 0
            ? new List<KeyValuePair<string, object?>>(capacity)
            : new List<KeyValuePair<string, object?>>();
    }

    public int Count => _entries.Count;

    public IReadOnlyList<KeyValuePair<string, object?>> Entries => _entries;

    /// <summary>
    /// Set <paramref name="key"/> to <paramref name="value"/>. If the
    /// key already exists, its value is replaced in place (preserving
    /// the original insertion position). Otherwise the key is appended.
    /// Mirrors JS object property assignment semantics.
    /// </summary>
    public void Set(string key, object? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        for (int i = 0; i < _entries.Count; i++)
        {
            if (string.Equals(_entries[i].Key, key, StringComparison.Ordinal))
            {
                _entries[i] = new KeyValuePair<string, object?>(key, value);
                return;
            }
        }
        _entries.Add(new KeyValuePair<string, object?>(key, value));
    }

    public object? this[string key]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(key);
            foreach (var entry in _entries)
            {
                if (string.Equals(entry.Key, key, StringComparison.Ordinal))
                {
                    return entry.Value;
                }
            }
            return null;
        }
        set => Set(key, value);
    }

    public bool ContainsKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        foreach (var entry in _entries)
        {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
