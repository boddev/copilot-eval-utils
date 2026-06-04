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
    public void Core_Version_Tag_Distinguishes_From_Node_Tools()
    {
        // Artifacts produced by the C# port must declare a different
        // version string than the Node tool so a consumer can tell
        // which implementation produced a file.
        Assert.StartsWith("evaltoolkit-", CoreInfo.ArtifactVersionTag);
    }
}
