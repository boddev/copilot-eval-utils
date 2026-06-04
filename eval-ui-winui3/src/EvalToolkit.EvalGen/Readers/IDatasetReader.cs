namespace EvalToolkit.EvalGen.Readers;

/// <summary>
/// Per-format dataset reader. Each implementation reads a single file
/// of the format it owns and returns the records + the detected
/// format. Multi-file / directory orchestration lives in
/// <see cref="DatasetReader"/>.
///
/// Implementations are synchronous because the TS-equivalent readers
/// for the slice-1 formats (CSV/TSV/JSON/JSONL/TXT/MD) are also
/// synchronous; async-only readers (DOCX, PDF) land in later slices
/// with their own async-flavored interface or async method.
/// </summary>
public interface IDatasetReader
{
    ReadResult Read(string absolutePath);
}
