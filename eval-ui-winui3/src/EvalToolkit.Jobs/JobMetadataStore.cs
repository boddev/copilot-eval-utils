using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EvalToolkit.Jobs;

/// <summary>
/// Atomic <see cref="JobMetadata"/> persistence to <c>{jobDir}/job.json</c>.
/// Writes go via a <c>.tmp</c> sibling that's <see cref="File.Move(string,string,bool)"/>'d
/// over the target so a reader never sees a half-written file.
/// </summary>
public static class JobMetadataStore
{
    public const string FileName = "job.json";
    public const string TempSuffix = ".tmp";

    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Path to the metadata file inside <paramref name="jobDirectory"/>.
    /// Does not check existence.
    /// </summary>
    public static string PathFor(string jobDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobDirectory);
        return Path.Combine(jobDirectory, FileName);
    }

    /// <summary>
    /// Serialize and atomically write <paramref name="metadata"/> into
    /// <paramref name="jobDirectory"/>. Creates the directory if missing.
    /// </summary>
    public static void Write(string jobDirectory, JobMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobDirectory);
        ArgumentNullException.ThrowIfNull(metadata);
        Directory.CreateDirectory(jobDirectory);

        string target = PathFor(jobDirectory);
        string temp = target + TempSuffix;

        string json = JsonSerializer.Serialize(metadata, s_options);

        // UTF-8 without BOM.
        File.WriteAllText(temp, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // overwrite: true gives us a single atomic rename on NTFS.
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>
    /// Attempt to load <c>job.json</c>. Returns <c>null</c> when the file
    /// is missing, unreadable, or malformed — callers are expected to
    /// fall back to a synthesized summary.
    /// </summary>
    public static JobMetadata? TryRead(string jobDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobDirectory);
        string path = PathFor(jobDirectory);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<JobMetadata>(json, s_options);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
