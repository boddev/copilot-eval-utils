namespace EvalToolkit.EvalGen.Writers;

/// <summary>
/// Abstraction over <c>DateTimeOffset.UtcNow</c> so JSON writers that
/// stamp <c>generated_at</c> can be unit-tested deterministically.
/// Production code uses <see cref="SystemClock"/>; tests substitute
/// <see cref="FixedClock"/>.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Production clock backed by <see cref="DateTimeOffset.UtcNow"/>.</summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Clock that always returns a fixed instant. Intended for tests that
/// pin the <c>generated_at</c> field of writer output to a known value
/// (e.g. the writers-probe pre-flight pinned
/// <c>2024-01-15T12:34:56.789Z</c>).
/// </summary>
public sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
    }
    public DateTimeOffset UtcNow { get; }
}
