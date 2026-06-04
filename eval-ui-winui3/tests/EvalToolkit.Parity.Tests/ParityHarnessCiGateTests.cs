using EvalToolkit.Parity.Harness;

namespace EvalToolkit.Parity.Tests;

/// <summary>
/// CI-gated fail-closed assertion that the TS parity entrypoint is
/// actually buildable from the developer's checkout. The other smoke
/// tests skip cleanly when <c>eval-gen/dist/parity-entrypoint.js</c>
/// isn't present, which is correct for local dev — but it means a
/// regression in the TS build step (a tsconfig include change, a file
/// rename, a partially-failed build) would turn the entire parity
/// suite green-but-vacuous in CI without anyone noticing.
///
/// Per Opus-4.8 round-3 review (the single highest-leverage hardening
/// of the parity harness): one test that requires the entrypoint to
/// exist in CI converts "skip if missing" into "must be present"
/// without losing the local-dev ergonomics. Trigger gate: the
/// well-known <c>CI</c> environment variable (set on GitHub Actions /
/// GitLab CI / most CI providers) OR
/// <see cref="EvalGenLocator.OverrideEnvVar"/> being set (the override
/// is an explicit "I'm pointing the harness somewhere specific" signal
/// and should always resolve).
/// </summary>
[Collection("Parity")]
public class ParityHarnessCiGateTests
{
    [Fact]
    public void EvalGenLocator_IsAvailable_WhenRunningInCi()
    {
        if (!IsCiOrOverride())
        {
            // Local dev path: defer to the per-test guards inside the
            // individual smoke tests. Returning here keeps the test
            // green so the suite still passes locally before the dev
            // has built the TS side.
            return;
        }

        Assert.True(
            EvalGenLocator.IsAvailable(),
            "TS parity entrypoint must be present in CI. " +
            "If this test failed, either `npm run build` in eval-gen/ " +
            "didn't run before `dotnet test`, or the build silently " +
            "failed to emit dist/parity-entrypoint.js. Check the " +
            "build-evaltoolkit-winui3.yml workflow steps and the " +
            "eval-gen/tsconfig.json `include` glob.");
    }

    private static bool IsCiOrOverride() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EvalGenLocator.OverrideEnvVar));
}
