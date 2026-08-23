using Watchtower.Application.Persistence;

namespace Watchtower.Application.Tests;

/// <summary>
/// A hand-movable clock for the session tests. Written here rather than taken from
/// <c>Microsoft.Extensions.TimeProvider.Testing</c> so the test project gains no package for four lines
/// of behaviour; only <see cref="GetUtcNow"/> is ever consulted by the code under test.
/// </summary>
/// <remarks>
/// Readings are clipped to microseconds (<see cref="PostgresTime"/>) — the finest instant a PostgreSQL
/// <c>timestamptz</c> can give back. A test clock that ticked in .NET's 100-nanosecond units would hand
/// the assertions a seventh fractional digit no column in this deployment stores, so
/// <c>Assert.Equal(host.Time.Now, row.CreatedAt)</c> would be asserting a precision the system never
/// promised, and would fail or pass depending on whether a re-read had gone through the identity map.
/// </remarks>
public sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider {
    private DateTimeOffset _now = now.ToMicrosecondPrecision();

    /// <summary>The instant every <see cref="GetUtcNow"/> call returns.</summary>
    public DateTimeOffset Now {
        get => _now;
        set => _now = value.ToMicrosecondPrecision();
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan by) => Now += by;
}
