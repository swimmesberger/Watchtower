namespace Watchtower.Application.Persistence;

/// <summary>
/// The precision boundary between .NET and PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// A .NET tick is 100 nanoseconds, so <see cref="DateTimeOffset"/> carries seven fractional digits.
/// PostgreSQL's <c>timestamptz</c> stores microseconds — six. Every instant Watchtower writes therefore
/// comes back one digit shorter than it went in, and a value that was computed in memory stops comparing
/// equal to its own round trip the moment that seventh digit is non-zero. Nine times out of ten the
/// identity map hides it (the re-read hands back the very object that was written); the tenth time is a
/// fresh context, or another process, or CI.
/// </para>
/// <para>
/// <see cref="ToMicrosecondPrecision(DateTimeOffset)"/> is the one place that boundary is crossed
/// deliberately. <c>WatchtowerDbContext.ConfigureConventions</c> applies it to every mapped
/// <see cref="DateTimeOffset"/> property on the way to the database, so nothing can be stored at a
/// precision the column will not hold; call it directly wherever a computed instant is kept in memory
/// <em>and</em> compared against what the database gives back.
/// </para>
/// </remarks>
public static class PostgresTime {
    /// <summary>.NET ticks in one microsecond — the resolution of a PostgreSQL <c>timestamptz</c>.</summary>
    public const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    /// <summary>
    /// Drops <paramref name="value"/> to the precision PostgreSQL stores, keeping its offset. Truncates
    /// rather than rounds: a stored instant must never move forward past the one that was observed.
    /// </summary>
    public static DateTimeOffset ToMicrosecondPrecision(this DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % TicksPerMicrosecond, value.Offset);

    /// <summary>Nullable overload of <see cref="ToMicrosecondPrecision(DateTimeOffset)"/>.</summary>
    public static DateTimeOffset? ToMicrosecondPrecision(this DateTimeOffset? value) =>
        value?.ToMicrosecondPrecision();
}
