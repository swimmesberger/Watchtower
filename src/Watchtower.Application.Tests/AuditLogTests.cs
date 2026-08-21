using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Audit.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the general audit trail: the recorder (bounded retention, error capping) and
/// <c>audit.listEvents</c> (newest-first, category-prefix narrowing). The prefix rule matters:
/// <c>proxy</c> must match <c>proxy.cloudflare</c> without also matching a hypothetical
/// <c>proxyx</c> category.
/// </summary>
public sealed class AuditLogTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuditLog Recorder(AuthTestHost host) =>
        ActivatorUtilities.CreateInstance<AuditLog>(host.Services);

    [Fact]
    public async Task RecordAndList_RoundTrip_NewestFirst() {
        using var host = AuthTestHost.Start();
        var audit = Recorder(host);
        await audit.RecordAsync("proxy.cloudflare", "dns.create", "a.example.com", "proxied CNAME → t.cfargotunnel.com", ct: Ct);
        await audit.RecordAsync("proxy.cloudflare", "tunnel.config.push", "watchtower", "2 hostname rule(s), 1 foreign preserved", ct: Ct);
        await audit.RecordAsync("proxy.cloudflare", "access.app.sync", "a.example.com", null,
            success: false, error: "Cloudflare API 403", ct: Ct);

        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<ListAuditEvents>(scope.ServiceProvider);
        var result = await handler.HandleAsync(new ListAuditEvents.Query(), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["access.app.sync", "tunnel.config.push", "dns.create"],
            result.Value.Events.Select(e => e.Action));
        var failed = result.Value.Events[0];
        Assert.False(failed.Success);
        Assert.Equal("Cloudflare API 403", failed.Error);
        Assert.Null(failed.Actor); // Background reconcile — the UI renders "system".
    }

    [Fact]
    public async Task CategoryFilter_IsAPrefixOnDottedSegments() {
        using var host = AuthTestHost.Start();
        var audit = Recorder(host);
        await audit.RecordAsync("proxy.cloudflare", "dns.create", "a.example.com", null, ct: Ct);
        await audit.RecordAsync("proxyx", "other", "b", null, ct: Ct);
        await audit.RecordAsync("deploy", "stack.up", "shop", null, ct: Ct);

        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<ListAuditEvents>(scope.ServiceProvider);

        var proxyOnly = await handler.HandleAsync(new ListAuditEvents.Query("proxy"), Ct);
        var e = Assert.Single(proxyOnly.Value.Events);
        Assert.Equal("proxy.cloudflare", e.Category);

        var exact = await handler.HandleAsync(new ListAuditEvents.Query("proxy.cloudflare"), Ct);
        Assert.Single(exact.Value.Events);

        var all = await handler.HandleAsync(new ListAuditEvents.Query(), Ct);
        Assert.Equal(3, all.Value.Events.Count);
    }

    [Fact]
    public async Task Retention_KeepsOnlyTheNewestRows() {
        using var host = AuthTestHost.Start();
        await using (var seed = host.Services.CreateAsyncScope()) {
            var db = seed.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            for (var i = 0; i < AuditLog.MaxRows + 100; i++) {
                db.AuditEvents.Add(new AuditEvent {
                    Category = "proxy.cloudflare", Action = "dns.create", Target = $"h{i}", Success = true,
                });
            }
            await db.SaveChangesAsync(Ct);
        }

        // One recorded event triggers the opportunistic trim.
        await Recorder(host).RecordAsync("proxy.cloudflare", "dns.create", "newest", null, ct: Ct);

        await using var scope = host.Services.CreateAsyncScope();
        var db2 = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Equal(AuditLog.MaxRows, await db2.AuditEvents.CountAsync(Ct));
        // The newest row survived; the trimmed ones are the oldest.
        Assert.True(await db2.AuditEvents.AnyAsync(e => e.Target == "newest", Ct));
        Assert.False(await db2.AuditEvents.AnyAsync(e => e.Target == "h0", Ct));
    }

    [Fact]
    public async Task OverlongErrors_AreCapped() {
        using var host = AuthTestHost.Start();
        await Recorder(host).RecordAsync("proxy.cloudflare", "dns.create", "a", null,
            success: false, error: new string('x', 2000), ct: Ct);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = await db.AuditEvents.SingleAsync(Ct);
        Assert.Equal(500, row.Error!.Length);
    }
}
