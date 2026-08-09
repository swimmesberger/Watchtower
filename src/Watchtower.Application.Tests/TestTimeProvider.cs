namespace Watchtower.Application.Tests;

/// <summary>
/// A hand-movable clock for the session tests. Written here rather than taken from
/// <c>Microsoft.Extensions.TimeProvider.Testing</c> so the test project gains no package for four lines
/// of behaviour; only <see cref="GetUtcNow"/> is ever consulted by the code under test.
/// </summary>
public sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider {
    /// <summary>The instant every <see cref="GetUtcNow"/> call returns.</summary>
    public DateTimeOffset Now { get; set; } = now;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan by) => Now += by;
}
