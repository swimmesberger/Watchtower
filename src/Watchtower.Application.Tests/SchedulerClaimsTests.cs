using Elarion.Abstractions.Scheduling;
using Elarion.Scheduling.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Persistence;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Scheduled jobs run once cluster-wide, not once per instance — ADR-0024 decision 3. The backup
/// schedule is a <c>[ScheduledJob]</c> minute tick (ADR-0018), so without claiming, two instances would
/// each start the nightly backup of every stack: two archives, two sets of container stops, twice the
/// storage.
/// </summary>
/// <remarks>
/// The claim itself is Elarion's (<c>pg_advisory_xact_lock</c> plus <c>ON CONFLICT</c>), and testing its
/// internals here would be testing the framework. What is worth pinning is the wiring: that Watchtower
/// registers the PostgreSQL coordinator rather than leaving the single-node default in place, that its
/// table is part of the migrated schema, and that two instances asking for the same occurrence produce
/// one winner.
/// </remarks>
public sealed class SchedulerClaimsTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheCoordinator_IsThePostgreSqlOne_NotTheSingleNodeDefault() {
        using var host = AuthTestHost.Start();

        var coordinator = host.Services.GetRequiredService<IScheduledOccurrenceCoordinator>();

        Assert.IsType<EfCoreScheduledOccurrenceCoordinator<WatchtowerDbContext>>(coordinator);
    }

    /// <summary>
    /// Two instances, one occurrence, one winner. This is the property the whole registration exists for,
    /// asserted against the coordinator rather than against a job so it does not depend on a schedule
    /// firing.
    /// </summary>
    [Fact]
    public async Task TwoInstances_ClaimingOneOccurrence_ProduceOneWinner() {
        using var first = AuthTestHost.Start();
        using var second = first.Restart();
        var occurrence = new DateTimeOffset(2026, 8, 23, 3, 0, 0, TimeSpan.Zero);

        var claims = await Task.WhenAll(
            ClaimAsync(first, occurrence),
            ClaimAsync(second, occurrence),
            ClaimAsync(first, occurrence));

        Assert.Equal(1, claims.Count(claimed => claimed));
        // …and the losing instances stay losers, rather than the row expiring into a second run.
        Assert.False(await ClaimAsync(second, occurrence));
    }

    /// <summary>A different occurrence of the same job is a different unit of work, and is claimable.</summary>
    [Fact]
    public async Task TheNextOccurrence_IsItsOwnClaim() {
        using var host = AuthTestHost.Start();
        var first = new DateTimeOffset(2026, 8, 23, 3, 0, 0, TimeSpan.Zero);

        Assert.True(await ClaimAsync(host, first));
        Assert.True(await ClaimAsync(host, first.AddDays(1)));
        Assert.Equal(2, await CountAsync(host));
    }

    private static async Task<bool> ClaimAsync(AuthTestHost host, DateTimeOffset occurrence) {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IScheduledOccurrenceCoordinator>()
            .TryClaimAsync(
                new ScheduledOccurrence { JobName = "backup-schedule", DueTimeUtc = occurrence }, Ct);
    }

    private static async Task<int> CountAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>()
            .SchedulerClaims.CountAsync(Ct);
    }
}
