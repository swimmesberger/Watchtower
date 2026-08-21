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

        var proxyOnly = await handler.HandleAsync(new ListAuditEvents.Query(Category: "proxy"), Ct);
        var e = Assert.Single(proxyOnly.Value.Events);
        Assert.Equal("proxy.cloudflare", e.Category);

        var exact = await handler.HandleAsync(new ListAuditEvents.Query(Category: "proxy.cloudflare"), Ct);
        Assert.Single(exact.Value.Events);

        var all = await handler.HandleAsync(new ListAuditEvents.Query(), Ct);
        Assert.Equal(3, all.Value.Events.Count);
    }

    [Fact]
    public async Task Retention_KeepsOnlyTheNewestRows_PerCategory() {
        using var host = AuthTestHost.Start();
        await using (var seed = host.Services.CreateAsyncScope()) {
            var db = seed.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            for (var i = 0; i < AuditLog.MaxRowsPerCategory + 100; i++) {
                db.AuditEvents.Add(new AuditEvent {
                    Category = "auth", Action = "login.ok", Target = $"u{i}", Success = true,
                });
            }
            // A quiet category must not be evicted by a chatty one.
            db.AuditEvents.Add(new AuditEvent {
                Category = "backups", Action = "run", Target = "shop", Success = true,
            });
            await db.SaveChangesAsync(Ct);
        }

        // One recorded event — in any category — triggers the opportunistic trim of every category over cap.
        await Recorder(host).RecordAsync("proxy.cloudflare", "dns.create", "newest", null, ct: Ct);

        await using var scope = host.Services.CreateAsyncScope();
        var db2 = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Equal(AuditLog.MaxRowsPerCategory, await db2.AuditEvents.CountAsync(e => e.Category == "auth", Ct));
        // The trimmed rows are the oldest of their category; other categories are untouched.
        Assert.False(await db2.AuditEvents.AnyAsync(e => e.Target == "u0", Ct));
        Assert.True(await db2.AuditEvents.AnyAsync(e => e.Target == "shop", Ct));
        Assert.True(await db2.AuditEvents.AnyAsync(e => e.Target == "newest", Ct));
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
