using EvalToolkit.Core;

namespace EvalToolkit.UI.Models;

/// <summary>
/// Display-friendly option for the generation provider ComboBox in
/// step 2. Bound to <c>EvalToolkit.Core.LLMProvider</c> via
/// <c>SelectedValuePath="Value"</c> so the wire enum drives selection
/// without a per-UI enum that duplicates the engine model (and risks
/// drifting from <c>LLMProviders.ToWireString</c>).
/// </summary>
public sealed record ProviderOption(string Label, LLMProvider Value);
