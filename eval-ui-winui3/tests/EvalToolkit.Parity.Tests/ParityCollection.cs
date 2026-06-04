namespace EvalToolkit.Parity.Tests;

/// <summary>
/// xUnit collection definition that serializes every parity test in
/// this assembly. The harness component tests mutate
/// <see cref="EvalToolkit.Parity.Harness.EvalGenLocator.OverrideEnvVar"/>;
/// the smoke tests read it. Per GPT-5.5 round-3 review, racing those
/// across xUnit's default parallel test-class execution can produce
/// intermittent failures where one test's env-var manipulation bleeds
/// into another's locator probe.
///
/// All parity tests opt into this collection via an
/// <c>[Collection("Parity")]</c> attribute on the test class.
/// </summary>
[CollectionDefinition("Parity", DisableParallelization = true)]
#pragma warning disable CA1711 // xUnit-mandated suffix for collection definitions.
public class ParityCollection { }
#pragma warning restore CA1711
