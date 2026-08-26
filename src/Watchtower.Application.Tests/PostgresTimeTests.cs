using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the precision boundary between .NET's 100-nanosecond tick and PostgreSQL's microsecond
/// <c>timestamptz</c> — <see cref="PostgresTime"/> and the model-wide converter
/// <c>WatchtowerDbContext.ConfigureConventions</c> registers for it.
/// </summary>
/// <remarks>
/// Everything here needs a <em>fresh</em> context for the re-read. Inside the context that wrote the
/// row, EF hands back the very object it tracked and the assertion passes no matter what reached the
/// column — which is how the seventh digit stayed invisible on a developer's machine and only showed up
/// in CI, where the same checks ran against a materialised row.
/// </remarks>
public sealed class PostgresTimeTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An instant whose seventh fractional digit is non-zero — the digit PostgreSQL drops.</summary>
    private static DateTimeOffset SubMicrosecond {
        get {
            var whole = new DateTimeOffset(2026, 8, 23, 11, 42, 17, TimeSpan.Zero);
            // .1234567 seconds: 1234567 ticks past the second, so ticks % 10 == 7.
            return whole.AddTicks(1_234_567);
        }
    }

    [Fact]
    public void Truncation_DropsTheSeventhDigit_WithoutRoundingOrMovingTheOffset() {
        var offset = new DateTimeOffset(2026, 8, 23, 13, 42, 17, TimeSpan.FromHours(2)).AddTicks(1_234_567);

        var clipped = offset.ToMicrosecondPrecision();

        Assert.Equal(0, clipped.Ticks % PostgresTime.TicksPerMicrosecond);
        // Truncated, not rounded: ...4567 goes down to ...4560, never up to ...4570.
        Assert.Equal(offset.AddTicks(-7), clipped);
        Assert.Equal(TimeSpan.FromHours(2), clipped.Offset);
        // Already-clipped values are left exactly as they are.
        Assert.Equal(clipped, clipped.ToMicrosecondPrecision());
        Assert.Null(((DateTimeOffset?)null).ToMicrosecondPrecision());
    }

    [Fact]
    public async Task AnInstantAtTheColumnsOwnPrecision_RoundTripsExactly() {
        using var host = AuthTestHost.Start();
        // What every Watchtower clock read now produces (AuthSessionService.Now, and the converter for
        // everything else): the value the application holds is one the column can hold.
        var stored = SubMicrosecond.ToMicrosecondPrecision();

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            db.AuditEvents.Add(new AuditEvent {
                Category = "system", Action = "precision.probe", Target = "timestamptz",
                Success = true, CreatedAt = stored,
            });
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var row = await db.AuditEvents.AsNoTracking().SingleAsync(Ct);
            Assert.Equal(stored, row.CreatedAt);
        }
    }

    [Fact]
    public async Task AFinerInstant_IsClippedOnTheWayIn_RatherThanSilentlyByTheColumn() {
        using var host = AuthTestHost.Start();
        var finer = SubMicrosecond;
        Assert.NotEqual(0, finer.Ticks % PostgresTime.TicksPerMicrosecond);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            db.AuditEvents.Add(new AuditEvent {
                Category = "system", Action = "precision.probe", Target = "timestamptz",
                Success = true, CreatedAt = finer,
            });
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var row = await db.AuditEvents.AsNoTracking().SingleAsync(Ct);
            // The converter decides what is lost, and it loses exactly the digit the column cannot keep —
            // it does not round, and it does not shift the instant by a microsecond.
            Assert.Equal(finer.ToMicrosecondPrecision(), row.CreatedAt);
        }
    }

    [Fact]
    public async Task TheConverter_ReachesNullableProperties_FromTheSameRegistration() {
        using var host = AuthTestHost.Start();
        int deployed;
        int never;

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var withValue = NewStack("deployed");
            withValue.LastDeployedAt = SubMicrosecond;
            var withoutValue = NewStack("never-deployed");
            db.Stacks.AddRange(withValue, withoutValue);
            await db.SaveChangesAsync(Ct);
            deployed = withValue.Id;
            never = withoutValue.Id;
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var rows = await db.Stacks.AsNoTracking().ToDictionaryAsync(s => s.Id, Ct);
            // Properties<DateTimeOffset>() is declared once and covers DateTimeOffset? as well.
            Assert.Equal(SubMicrosecond.ToMicrosecondPrecision(), rows[deployed].LastDeployedAt);
            // A converter that ran over a null would be the failure mode worth catching here.
            Assert.Null(rows[never].LastDeployedAt);
        }
    }

    private static Stack NewStack(string name) => new() {
        Name = name,
        ComposeProjectName = name,
        Product = TestProducts.New(name),
    };
}
