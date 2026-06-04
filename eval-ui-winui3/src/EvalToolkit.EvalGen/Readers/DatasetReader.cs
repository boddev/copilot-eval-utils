using System.Globalization;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Readers;

/// <summary>
/// Options for <see cref="DatasetReader.ReadDatasetFile"/>. Mirrors
/// the TS <c>ReadDatasetOptions</c> interface in
/// <c>eval-gen/src/readers/index.ts</c>.
/// </summary>
public sealed record ReadDatasetOptions
{
    /// <summary>
    /// Recursive directory traversal. Defaults to <c>true</c> to match
    /// the TS contract (<c>options.recursive !== false</c>).
    /// </summary>
    public bool Recursive { get; init; } = true;

    /// <summary>
    /// Optional extension allow-list (no leading dot). Case-insensitive.
    /// </summary>
    public IReadOnlyList<string>? Extensions { get; init; }
}

/// <summary>
/// Top-level dataset reader. Mirrors the TS <c>readDatasetFile</c>
/// orchestrator in <c>eval-gen/src/readers/index.ts</c>:
///
/// <list type="bullet">
///   <item>Accepts a single file path, a directory path, or a
///     comma-separated list of either.</item>
///   <item>Resolves each input to an absolute path and verifies it
///     exists.</item>
///   <item>For directories, discovers supported files (recursive by
///     default) honoring the extension allow-list.</item>
///   <item>Reads every file, stamps each record with
///     <c>_source_file</c> (the file's base name), and merges them in
///     traversal order.</item>
///   <item>Returns the merged records, the most-recently-read file's
///     format, and the list of source file base names.</item>
///   <item>Sorts directory entries with ordinal (case-sensitive)
///     ordering — matches JS <c>Array.prototype.sort()</c> default
///     UTF-16 code-unit ordering for ASCII paths and avoids surprising
///     locale-dependent reordering in CI.</item>
/// </list>
/// </summary>
public static class DatasetReader
{
    private static readonly HashSet<string> s_supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".tsv", ".json", ".jsonl", ".xlsx", ".xls",
        ".docx", ".pdf", ".pptx", ".txt", ".md",
    };

    /// <summary>Reserved attribute stamped onto every record.</summary>
    public const string SourceFileField = "_source_file";

    /// <summary>
    /// Read a dataset from <paramref name="fileInput"/>, which may be:
    /// <list type="bullet">
    ///   <item>A single file path (<c>"data.csv"</c>).</item>
    ///   <item>A directory path (<c>"data/"</c>).</item>
    ///   <item>A comma-separated list of files / directories
    ///     (<c>"a.csv,b.csv,inputs/"</c>).</item>
    /// </list>
    /// </summary>
    public static DatasetReadResult ReadDatasetFile(string fileInput, ReadDatasetOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileInput);
        options ??= new ReadDatasetOptions();

        string[] inputs = fileInput
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();

        var filesToRead = new List<string>();
        foreach (string input in inputs)
        {
            string absPath = Path.GetFullPath(input);
            if (File.Exists(absPath))
            {
                filesToRead.Add(absPath);
            }
            else if (Directory.Exists(absPath))
            {
                var discovered = DiscoverFilesInDirectory(absPath, options);
                if (discovered.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"No supported files found in directory: {absPath}");
                }
                filesToRead.AddRange(discovered);
            }
            else
            {
                throw new FileNotFoundException(
                    $"File or directory not found: {absPath}", absPath);
            }
        }

        if (filesToRead.Count == 0)
        {
            throw new InvalidOperationException("No files to read");
        }

        var allRecords = new List<DatasetRow>();
        InputFormat primaryFormat = InputFormat.Csv;
        foreach (string filePath in filesToRead)
        {
            ReadResult result = ReadSingleFile(filePath);
            primaryFormat = result.Format; // "Last format wins" — matches TS.
            string fileName = Path.GetFileName(filePath);
            foreach (DatasetRow row in result.Records)
            {
                row.Set(SourceFileField, fileName);
                allRecords.Add(row);
            }
        }

        if (allRecords.Count == 0)
        {
            throw new InvalidOperationException("All dataset files are empty");
        }

        return new DatasetReadResult
        {
            Records = allRecords,
            Format = primaryFormat,
            SourceFiles = filesToRead.Select(Path.GetFileName).Select(n => n!).ToArray(),
        };
    }

    /// <summary>
    /// Dispatch a single file to its per-format reader. Throws
    /// <see cref="NotSupportedException"/> for the slice-2+ formats
    /// (XLSX / DOCX / PDF / PPTX) until those readers land.
    /// </summary>
    public static ReadResult ReadSingleFile(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        string ext = Path.GetExtension(absolutePath).ToLowerInvariant();
        IDatasetReader reader = ext switch
        {
            ".csv" or ".tsv" => new CsvReader(),
            ".json" => new JsonReader(),
            ".jsonl" => new JsonlReader(),
            ".txt" or ".md" => new TextFileReader(),
            ".xlsx" or ".xls" or ".docx" or ".pdf" or ".pptx" =>
                throw new NotSupportedException(
                    $"Reader for '{ext}' is not yet ported (slice {GetSliceForExtension(ext)} of readers-port). " +
                    $"Slice 1 supports: csv, tsv, json, jsonl, txt, md."),
            _ => throw new NotSupportedException(
                $"Unsupported file format: {ext}. Supported (slice 1): csv, tsv, json, jsonl, txt, md."),
        };
        return reader.Read(absolutePath);
    }

    /// <summary>
    /// Recursively walk <paramref name="dirPath"/>, returning every
    /// file with a supported extension (filtered to
    /// <paramref name="options"/>'s allow-list if set), sorted in
    /// ordinal order so traversal is deterministic across runs and
    /// across platforms.
    /// </summary>
    public static IReadOnlyList<string> DiscoverFilesInDirectory(string dirPath, ReadDatasetOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dirPath);
        ArgumentNullException.ThrowIfNull(options);

        HashSet<string>? extFilter = null;
        if (options.Extensions is { Count: > 0 } extList)
        {
            extFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string ext in extList)
            {
                if (string.IsNullOrWhiteSpace(ext))
                {
                    continue;
                }
                extFilter.Add(ext.TrimStart('.').ToLowerInvariant());
            }
        }

        var collected = new List<string>();
        WalkDirectory(dirPath, options, extFilter, collected);
        // Ordinal sort matches JS String.prototype.localeCompare-free
        // <c>Array.prototype.sort()</c> default which compares UTF-16
        // code units; for ASCII paths it's identical to a byte sort.
        collected.Sort(StringComparer.Ordinal);
        return collected;
    }

    private static void WalkDirectory(
        string dirPath,
        ReadDatasetOptions options,
        HashSet<string>? extFilter,
        List<string> collected)
    {
        foreach (string filePath in Directory.EnumerateFiles(dirPath))
        {
            string fileName = Path.GetFileName(filePath);
            string extWithDot = Path.GetExtension(fileName).ToLowerInvariant();
            if (!s_supportedExtensions.Contains(extWithDot))
            {
                continue;
            }
            if (extFilter is not null)
            {
                string extNoDot = extWithDot.TrimStart('.');
                if (!extFilter.Contains(extNoDot))
                {
                    continue;
                }
            }
            collected.Add(filePath);
        }
        if (!options.Recursive)
        {
            return;
        }
        foreach (string subDir in Directory.EnumerateDirectories(dirPath))
        {
            WalkDirectory(subDir, options, extFilter, collected);
        }
    }

    private static string GetSliceForExtension(string ext) => ext switch
    {
        ".xlsx" or ".xls" => "2",
        ".docx" => "3",
        ".pptx" => "4",
        ".pdf" => "5",
        _ => "?",
    };
}
