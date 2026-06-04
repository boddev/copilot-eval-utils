using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Tests;

/// <summary>
/// Smoke test that confirms the test project resolves the EvalGen
/// project reference and builds. Real per-component tests live in
/// <c>CoreModelsTests</c> / <c>EnvHelpersTests</c> / etc.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void EvalGen_Assembly_Marker_Is_Present()
    {
        // EvalGenInfo is the last placeholder marker until the
        // evalgen-engine-port todo replaces it with real code.
        Assert.Equal("EvalToolkit.EvalGen", EvalGenInfo.Name);
    }

    [Fact]
    public void Core_Wire_EvalGen_Version_Matches_Node_Tool()
    {
        // The on-wire `evalgen_version` field MUST match the Node tool
        // ("1.0.0") byte-for-byte so eval files round-trip between the
        // two implementations and the parity harness doesn't have to
        // special-case-mask this field.
        Assert.Equal("1.0.0", CoreInfo.WireEvalgenVersion);
    }

    [Fact]
    public void Core_Generator_Tool_Distinguishes_From_Node_Tools()
    {
        // Provenance for the C# port lives in the additive
        // `metadata.generator_tool` field, which carries this string.
        Assert.StartsWith("evaltoolkit-csharp/", CoreInfo.GeneratorTool);
    }
}
