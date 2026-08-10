using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Pins the two database guarantees the grant-handling code is written against, exercised over the real
/// migrated schema (<see cref="AuthTestHost"/> runs the actual migrations).
/// </summary>
/// <remarks>
/// These are asserted rather than assumed because three separate places lean on them in prose and would
/// each be quietly wrong if the schema stopped enforcing them:
/// <list type="bullet">
///   <item><description>
///     <c>GetAccess</c> splits one grant query into two id lists on the basis that the split is total —
///     which holds only because every row has exactly one subject.
///   </description></item>
///   <item><description>
///     <c>SetAccess</c> judges each row against the target set of its own subject kind, and treats a row
///     that is neither as unreachable ("which the table's CHECK constraint forbids").
///   </description></item>
///   <item><description>
///     <c>RouteAccessGrant</c>'s own remarks promise that uniqueness is per subject kind, which is what
///     lets a route name both a user and a group that user belongs to.
///   </description></item>
/// </list>
/// A CHECK constraint is also the one guard that survives a writer other than these handlers — a manual
/// fix-up, a future import — so it is worth knowing it is really there.
/// </remarks>
public sealed class RouteAccessGrantSchemaTests {
    /// <summary>A grant naming both a user and a group has two readings; the schema refuses it.</summary>
    [Fact]
    public async Task AGrantNamingBothAUserAndAGroup_IsRefusedByTheCheckConstraint() {
        using var host = AuthTestHost.Start();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Restricted);
        var alice = await host.AddUserAsync("alice");
        var staff = await host.AddGroupAsync("staff", alice);

        await AssertRejectedAsync(host, new RouteAccessGrant {
            RouteId = route.Id, UserId = alice, GroupId = staff,
        });
    }

    /// <summary>...and one naming neither grants nobody, so it is refused just as hard.</summary>
    [Fact]
    public async Task AGrantNamingNoSubjectAtAll_IsRefusedByTheCheckConstraint() {
        using var host = AuthTestHost.Start();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Restricted);

        await AssertRejectedAsync(host, new RouteAccessGrant { RouteId = route.Id });
    }

    /// <summary>
    /// The partial unique index on (route_id, group_id): re-granting the same group is idempotent rather
    /// than duplicated, so a revoke can never leave a forgotten second row still granting access.
    /// </summary>
    [Fact]
    public async Task ASecondGrantForTheSameRouteAndGroup_IsRefusedByThePartialUniqueIndex() {
        using var host = AuthTestHost.Start();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Restricted);
        var staff = await host.AddGroupAsync("staff");
        await host.GrantGroupAsync(route.Id, staff);

        await AssertRejectedAsync(host, new RouteAccessGrant { RouteId = route.Id, GroupId = staff });
    }

    /// <summary>
    /// The flip side of the same pair of indexes, and the reason they are partial rather than one composite
    /// index over both columns: several group grants on one route all carry a null <c>user_id</c>, and a
    /// naive unique index on (route_id, user_id) would read them as duplicates of each other.
    /// </summary>
    [Fact]
    public async Task SeveralGroupGrantsOnOneRoute_CoexistDespiteTheirNullUserIds() {
        using var host = AuthTestHost.Start();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Restricted);
        var alice = await host.AddUserAsync("alice");

        await host.GrantGroupAsync(route.Id, await host.AddGroupAsync("staff"));
        await host.GrantGroupAsync(route.Id, await host.AddGroupAsync("viewers"));
        await host.GrantGroupAsync(route.Id, await host.AddGroupAsync("admins"));
        // ...and a direct grant sits alongside them for the same reason, from the other index.
        await host.GrantUserAsync(route.Id, alice);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Equal(4, await db.RouteAccessGrants.CountAsync(
            g => g.RouteId == route.Id, TestContext.Current.CancellationToken));
    }

    /// <summary>Inserts the row on a fresh scope and asserts the database refuses it.</summary>
    private static async Task AssertRejectedAsync(AuthTestHost host, RouteAccessGrant grant) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.RouteAccessGrants.Add(grant);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }
}
