namespace EvalToolkit.Core;

/// <summary>
/// Marker / version surface for the Core assembly.
/// Replaced with real types in the <c>core-models</c> phase A todo
/// (shared models, env-var helpers, job storage, logging sinks).
/// </summary>
public static class CoreInfo
{
    public const string Name = "EvalToolkit.Core";

    /// <summary>
    /// Semantic version of the Core assembly. Bumped per release.
    /// </summary>
    public const string Version = "0.1.0-alpha";
}
