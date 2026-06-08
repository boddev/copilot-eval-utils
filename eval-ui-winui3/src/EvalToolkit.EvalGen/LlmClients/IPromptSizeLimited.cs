namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// Implemented by LLM clients that transmit the prompt on a process command
/// line (currently only the GitHub Copilot CLI), which the OS caps at roughly
/// 32K characters. The generation pipeline reads <see cref="MaxPromptChars"/>
/// to split large stages into batches that each fit, instead of crashing with a
/// cryptic Win32 "filename or extension is too long" error.
///
/// <para>Clients that send the prompt over stdin or HTTP have no such limit and
/// do not implement this interface; those stages keep their original
/// single-call behavior.</para>
/// </summary>
public interface IPromptSizeLimited
{
    /// <summary>
    /// The largest <c>prompt</c> (in characters) that
    /// <see cref="ILlmClient.GenerateStructuredAsync{T}"/> can safely transmit
    /// in a single call. Pipeline stages that assemble large prompts should
    /// keep each call at or below this value.
    /// </summary>
    int MaxPromptChars { get; }
}
