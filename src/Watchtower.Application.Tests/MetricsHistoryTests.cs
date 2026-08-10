using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Metrics;
using Watchtower.Application.Modules.Metrics.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the SQLite metrics history (ADR-0013): the minute aggregation and rollup/retention write path
/// (<see cref="MetricsPersistenceService"/>), the history read path (<see cref="SqliteMetricsSource"/>),
/// the runtime backend router (<see cref="MetricsSourceRouter"/>), and the config handlers' validation.
/// </summary>
public sealed class MetricsHistoryTests {
    private static readonly DateTimeOffset T0 =
        new(2026, 8, 10, 10, 0, 5, TimeSpan.Zero); // five seconds into a minute bucket

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Write path: minute aggregation ────────────────────────────────────────

    [Fact]
    public async Task TicksAggregateIntoOneMinuteRow_FlushedOnTheBoundary() {
        using var host = AuthTestHost.Start(("Watchtower:Metrics:Backend", "sqlite"));
        host.Time.Now = T0;
        var persistence = host.Services.GetRequiredService<MetricsPersistenceService>();

        await persistence.RecordTickAsync(Host(cpu: 10, mem: 40), [Container("web", cpu: 1, mem: 100)], Ct);
        host.Time.Advance(TimeSpan.FromSeconds(10));
        await persistence.RecordTickAsync(Host(cpu: 20, mem: 60), [Container("web", cpu: 3, mem: 300)], Ct);

        // Nothing hits the database until the minute closes.
        await AssertRowCountsAsync(host, expectedHost: 0, expectedContainer: 0);

        host.Time.Advance(TimeSpan.FromSeconds(60));
        await persistence.RecordTickAsync(Host(cpu: 99, mem: 99), [Container("web", cpu: 9, mem: 900)], Ct);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        var hostRow = await db.MetricHostSamples.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(MetricsPersistenceService.RawTierSeconds, hostRow.TierSeconds);
        Assert.Equal(T0.ToUnixTimeSeconds() / 60 * 60, hostRow.TUnixSeconds);
        Assert.Equal(15, hostRow.CpuPercent);
        Assert.Equal(50, hostRow.MemPercent);

        var containerRow = await db.MetricContainerSamples.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("web", containerRow.ContainerName);
        Assert.Equal(2, containerRow.CpuPercent);
        Assert.Equal(200, containerRow.MemUsedBytes);
        Assert.Equal("mystack", containerRow.StackName);
    }

    [Fact]
    public async Task OfflineContainersAndUnavailableHost_AreGapsNotZeros() {
        using var host = AuthTestHost.Start(("Watchtower:Metrics:Backend", "sqlite"));
        host.Time.Now = T0;
        var persistence = host.Services.GetRequiredService<MetricsPersistenceService>();

        await persistence.RecordTickAsync(
            HostSnapshot.Unavailable("host-proc-not-mounted"),
            [Container("db", cpu: 5, mem: 100, online: false)], Ct);
        host.Time.Advance(TimeSpan.FromSeconds(70));
        await persistence.RecordTickAsync(
            HostSnapshot.Unavailable("host-proc-not-mounted"), [], Ct);

        await AssertRowCountsAsync(host, expectedHost: 0, expectedContainer: 0);
    }

    // ── Write path: rollup + retention ────────────────────────────────────────

    [Fact]
    public async Task Maintain_RollsMinutesUp_AndEnforcesBothWindows() {
        using var host = AuthTestHost.Start(("Watchtower:Metrics:Backend", "sqlite"));
        host.Time.Now = T0;
        var persistence = host.Services.GetRequiredService<MetricsPersistenceService>();
        var now = T0.ToUnixTimeSeconds();

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            // A 10-minute stretch of minute rows five hours ago: rolls up, stays raw (inside 72h).
            var recent = (now - 5 * 3600) / 600 * 600;
            for (var i = 0; i < 10; i++) {
                db.MetricHostSamples.Add(new MetricHostSample {
                    TierSeconds = 60, TUnixSeconds = recent + i * 60, CpuPercent = i, MemPercent = 50,
                });
                db.MetricContainerSamples.Add(new MetricContainerSample {
                    TierSeconds = 60, TUnixSeconds = recent + i * 60,
                    ContainerName = "web", StackName = "s", CpuPercent = i * 2, MemUsedBytes = 100,
                });
            }
            // Minute rows 80 hours ago: roll up first, then fall out of the raw window.
            var old = (now - 80 * 3600) / 600 * 600;
            db.MetricHostSamples.Add(new MetricHostSample {
                TierSeconds = 60, TUnixSeconds = old, CpuPercent = 42, MemPercent = 42,
            });
            // A rollup row beyond the 30-day retention: deleted.
            db.MetricHostSamples.Add(new MetricHostSample {
                TierSeconds = 600, TUnixSeconds = now - 31 * 86400L, CpuPercent = 1,
            });
            await db.SaveChangesAsync(Ct);
        }

        // Two passes: rollup work is bounded per pass (MaxRollupBucketsPerPass) and the 80h-old host
        // stretch is processed first, so the recent host minutes land in the second pass.
        await persistence.MaintainAsync(Ct);
        await persistence.MaintainAsync(Ct);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

            // The recent stretch (all inside one aligned 600s bucket) produced one averaged rollup row.
            var recentStart = (now - 5 * 3600) / 600 * 600;
            var hostRollup = await db.MetricHostSamples.AsNoTracking()
                .SingleAsync(x => x.TierSeconds == 600 && x.TUnixSeconds >= recentStart, Ct);
            Assert.Equal(recentStart, hostRollup.TUnixSeconds);
            Assert.Equal(4.5, hostRollup.CpuPercent); // avg of 0..9

            var containerRollup = await db.MetricContainerSamples.AsNoTracking()
                .SingleAsync(x => x.TierSeconds == 600 && x.TUnixSeconds == recentStart, Ct);
            Assert.Equal("web", containerRollup.ContainerName);
            Assert.Equal(9, containerRollup.CpuPercent); // avg of 0,2,…,18

            // 80h-old raw is gone but preserved as a rollup; the 31-day rollup is gone.
            Assert.False(await db.MetricHostSamples.AnyAsync(
                x => x.TierSeconds == 60 && x.TUnixSeconds < now - 72 * 3600, Ct));
            Assert.True(await db.MetricHostSamples.AnyAsync(
                x => x.TierSeconds == 600 && x.TUnixSeconds == (now - 80 * 3600) / 600 * 600, Ct));
            Assert.False(await db.MetricHostSamples.AnyAsync(
                x => x.TUnixSeconds == now - 31 * 86400L, Ct));
        }
    }

    // ── Read path ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task HistoryQuery_BucketsRawRowsToTheRequestedStep() {
        using var host = AuthTestHost.Start(("Watchtower:Metrics:Backend", "sqlite"));
        host.Time.Now = T0;
        var source = host.Services.GetRequiredService<SqliteMetricsSource>();
        var now = T0.ToUnixTimeSeconds();
        var start = (now - 3600) / 120 * 120; // an hour ago, aligned to the 2-minute step

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            for (var i = 0; i < 4; i++) { // two 2-minute buckets: (10,20) and (30,40)
                db.MetricHostSamples.Add(new MetricHostSample {
                    TierSeconds = 60, TUnixSeconds = start + i * 60, CpuPercent = (i + 1) * 10, MemPercent = 1,
                });
                db.MetricContainerSamples.Add(new MetricContainerSample {
                    TierSeconds = 60, TUnixSeconds = start + i * 60,
                    ContainerName = "api", StackName = "s", CpuPercent = i, MemUsedBytes = 1000, MemLimitBytes = 2000,
                });
            }
            await db.SaveChangesAsync(Ct);
        }

        var window = MetricsWindow.History(
            DateTimeOffset.FromUnixTimeSeconds(start),
            DateTimeOffset.FromUnixTimeSeconds(start + 239),
            TimeSpan.FromSeconds(120));

        var readout = await source.GetHostAsync(window, Ct);
        Assert.Equal(2, readout.History.Count);
        Assert.Equal(15, readout.History[0].CpuPercent);
        Assert.Equal(35, readout.History[1].CpuPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(start), readout.History[0].T);

        var containers = await source.GetContainersAsync(window, Ct);
        var api = Assert.Single(containers);
        Assert.Equal("api", api.Latest.ContainerName);
        Assert.Equal("api", api.Latest.ContainerId); // name is the identity in history
        Assert.Equal(2, api.History.Count);
        Assert.Equal(0.5, api.History[0].CpuPercent);
        Assert.Equal(2.5, api.History[1].CpuPercent);
        Assert.Equal(50, api.Latest.MemPercent); // 1000 of 2000
    }

    [Fact]
    public async Task HistoryQuery_OlderThanTheRawWindow_ServesTheRollupTier() {
        using var host = AuthTestHost.Start(("Watchtower:Metrics:Backend", "sqlite"));
        host.Time.Now = T0;
        var source = host.Services.GetRequiredService<SqliteMetricsSource>();
        var now = T0.ToUnixTimeSeconds();
        var start = (now - 7 * 86400L) / 600 * 600; // a week back — raw rows are long gone there

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            db.MetricHostSamples.Add(new MetricHostSample {
                TierSeconds = 600, TUnixSeconds = start, CpuPercent = 77, MemPercent = 7,
            });
            await db.SaveChangesAsync(Ct);
        }

        var window = MetricsWindow.History(
            DateTimeOffset.FromUnixTimeSeconds(start - 600),
            DateTimeOffset.FromUnixTimeSeconds(start + 3600),
            TimeSpan.FromSeconds(60)); // finer than the tier — floored to 600

        var readout = await source.GetHostAsync(window, Ct);
        var point = Assert.Single(readout.History);
        Assert.Equal(77, point.CpuPercent);
    }

    // ── Router ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Router_FollowsTheOptionsAtRuntime_AndDegradesMisconfiguredInflux() {
        using var host = AuthTestHost.Start(("Watchtower:Metrics:Backend", "sqlite"));
        var monitor = new MutableOptionsMonitor(new WatchtowerOptions());
        var router = new MetricsSourceRouter(
            host.Services.GetRequiredService<InMemoryMetricsSource>(),
            host.Services.GetRequiredService<SqliteMetricsSource>(),
            monitor,
            NullLoggerFactory.Instance);

        // Default → sqlite, history available.
        Assert.Equal("sqlite", router.Capabilities.Source);
        Assert.True(router.Capabilities.HistoryAvailable);

        monitor.Value = new WatchtowerOptions { Metrics = new MetricsOptions { Backend = "memory" } };
        Assert.Equal("memory", router.Capabilities.Source);
        Assert.False(router.Capabilities.HistoryAvailable);

        // influxdb with no connection settings: capabilities still advertise the backend, reads degrade.
        monitor.Value = new WatchtowerOptions { Metrics = new MetricsOptions { Backend = "influxdb" } };
        Assert.Equal("influxdb", router.Capabilities.Source);
        var readout = await router.GetHostAsync(MetricsWindow.Live, Ct);
        Assert.False(readout.Snapshot.Available);
        Assert.Equal("influx-misconfigured", readout.Snapshot.Reason);
        Assert.Empty(await router.GetContainersAsync(MetricsWindow.Live, Ct));

        // Fixing the settings rebuilds a real reader from the new snapshot.
        monitor.Value = new WatchtowerOptions {
            Metrics = new MetricsOptions {
                Backend = "influxdb",
                Influx = new InfluxOptions {
                    Url = "http://influx.invalid:8086", Org = "o", Bucket = "b", Token = "t",
                },
            },
        };
        Assert.Equal("influxdb", router.Capabilities.Source);
        Assert.True(router.Capabilities.HistoryAvailable);
        router.Dispose();
    }

    // ── Config handlers ───────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateConfig_ValidatesBackendRetentionAndInfluxSettings() {
        using var host = AuthTestHost.Start(("Watchtower:Metrics:Backend", "sqlite"));
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<UpdateMetricsConfig>(scope.ServiceProvider);

        var bad = await handler.HandleAsync(new UpdateMetricsConfig.Command("postgres", 30), Ct);
        Assert.False(bad.IsSuccess);

        var badRetention = await handler.HandleAsync(new UpdateMetricsConfig.Command("sqlite", 0), Ct);
        Assert.False(badRetention.IsSuccess);

        // influxdb without connection values must not switch and strand the dashboard.
        var missingInflux = await handler.HandleAsync(new UpdateMetricsConfig.Command("influxdb", 30), Ct);
        Assert.False(missingInflux.IsSuccess);

        var ok = await handler.HandleAsync(new UpdateMetricsConfig.Command(
            "influxdb", 30,
            InfluxUrl: "http://influxdb:8086", InfluxOrg: "org", InfluxBucket: "wt", InfluxToken: "secret"), Ct);
        Assert.True(ok.IsSuccess);
        Assert.Equal("influxdb", ok.Value.Config.Backend);
        Assert.True(ok.Value.Config.Influx.HasToken);
        // The token itself never travels back.
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(
            ok.Value.Config, MetricsJsonContext.Default.MetricsConfig));
    }

    [Fact]
    public async Task GetConfig_MasksTheToken() {
        using var host = AuthTestHost.Start(
            ("Watchtower:Metrics:Backend", "influxdb"),
            ("Watchtower:Metrics:Influx:Url", "http://influxdb:8086"),
            ("Watchtower:Metrics:Influx:Org", "org"),
            ("Watchtower:Metrics:Influx:Bucket", "wt"),
            ("Watchtower:Metrics:Influx:Token", "super-secret"));
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<GetMetricsConfig>(scope.ServiceProvider);

        var result = await handler.HandleAsync(new GetMetricsConfig.Query(), Ct);
        Assert.True(result.IsSuccess);
        Assert.Equal("influxdb", result.Value.Config.Backend);
        Assert.True(result.Value.Config.Influx.HasToken);
        Assert.DoesNotContain("super-secret", System.Text.Json.JsonSerializer.Serialize(
            result.Value.Config, MetricsJsonContext.Default.MetricsConfig));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HostSnapshot Host(double cpu, double mem) => new() {
        Available = true,
        CpuPercent = cpu,
        MemPercent = mem,
        MemUsedBytes = (long)(mem * 100),
        DiskSource = "unavailable",
        SampledAt = DateTimeOffset.UtcNow,
    };

    private static ContainerSnapshot Container(string name, double cpu, long mem, bool online = true) => new() {
        ContainerId = name + "-id",
        ContainerName = name,
        StackName = "mystack",
        CpuPercent = cpu,
        MemUsedBytes = mem,
        MemLimitBytes = 4000,
        MemPercent = null,
        Online = online,
        SampledAt = DateTimeOffset.UtcNow,
    };

    private static async Task AssertRowCountsAsync(AuthTestHost host, int expectedHost, int expectedContainer) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Equal(expectedHost, await db.MetricHostSamples.CountAsync(Ct));
        Assert.Equal(expectedContainer, await db.MetricContainerSamples.CountAsync(Ct));
    }

    /// <summary>An options monitor whose value the test can swap — the router re-reads it per call.</summary>
    private sealed class MutableOptionsMonitor(WatchtowerOptions value) : IOptionsMonitor<WatchtowerOptions> {
        public WatchtowerOptions Value { get; set; } = value;
        public WatchtowerOptions CurrentValue => Value;
        public WatchtowerOptions Get(string? name) => Value;
        public IDisposable? OnChange(Action<WatchtowerOptions, string?> listener) => null;
    }
}
