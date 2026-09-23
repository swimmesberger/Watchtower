using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the access half of <c>proxy.createRoute</c> (ADR-0035): the mode a new service route starts
/// under when the request says nothing, who is allowed to say something, and the one provider where
/// "protected" can mean "unreachable" — Cloudflare with no allow source, which is refused at the point
/// the route is typed rather than discovered as a hostname the edge denies to everybody.
/// </summary>
/// <remarks>
/// The default lives in a setting rather than in the handler, so both directions are asserted here: a
/// deployment that never touched it publishes gated routes, and one that set <c>public</c> gets exactly
/// what it asked for. The refusals are all Validation except the role gate, which is Forbidden — an
/// administrator's decision refused to a non-administrator, not a malformed request.
/// </remarks>
public sealed class CreateRouteAccessDefaultTests {
    private static readonly Action<IServiceCollection> WithRouteHandlers = services => {
        services.AddCreateRoute();
        services.RemoveAll<IProxyProvider>();
        services.AddSingleton<IProxyProvider, RecordingProxyProvider>();
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── The default ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ARouteThatSaysNothingAboutAccess_IsAuthenticated() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var stackId = await host.AddStackAsync("blog");

        var result = await CreateAsync(host, ServiceCommand(stackId, "blog.example.invalid"));

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("authenticated", result.Value.Route.AccessMode);
        var route = await RouteAsync(host, result.Value.Route.Id);
        Assert.Equal(AccessMode.Authenticated, route.AccessMode);
        Assert.Null(route.BypassPaths);
    }

    [Fact]
    public async Task ADeploymentThatConfiguredThePublicDefault_GetsPublicRoutes() {
        using var host = AuthTestHost.Start(
            WithRouteHandlers, ("Watchtower:Proxy:DefaultAccessMode", "public"));
        var stackId = await host.AddStackAsync("blog");

        var result = await CreateAsync(host, ServiceCommand(stackId, "blog.example.invalid"));

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("public", result.Value.Route.AccessMode);
        Assert.Equal(AccessMode.Public, (await RouteAsync(host, result.Value.Route.Id)).AccessMode);
    }

    /// <summary>An unreadable stored value fails closed rather than publishing the next route openly.</summary>
    [Fact]
    public async Task AnUnreadableConfiguredDefault_StillProducesAProtectedRoute() {
        using var host = AuthTestHost.Start(
            WithRouteHandlers, ("Watchtower:Proxy:DefaultAccessMode", "restricted"));
        var stackId = await host.AddStackAsync("blog");

        var result = await CreateAsync(host, ServiceCommand(stackId, "blog.example.invalid"));

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("authenticated", result.Value.Route.AccessMode);
    }

    // ── Explicit values ──────────────────────────────────────────────────────

    [Fact]
    public async Task AnExplicitPublicRoute_IsStoredPublic_UnderTheProtectedDefault() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var stackId = await host.AddStackAsync("blog");

        var result = await CreateAsync(
            host,
            ServiceCommand(stackId, "blog.example.invalid") with { AccessMode = AccessMode.Public });

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("public", result.Value.Route.AccessMode);
    }

    [Fact]
    public async Task BypassPaths_AreStoredNormalised_OnAProtectedRoute() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var stackId = await host.AddStackAsync("blog");

        var result = await CreateAsync(
            host,
            ServiceCommand(stackId, "blog.example.invalid") with {
                AccessMode = AccessMode.Authenticated, BypassPaths = "  /webhooks/  \n\n/healthz\n",
            });

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("/webhooks/\n/healthz", (await RouteAsync(host, result.Value.Route.Id)).BypassPaths);
    }

    /// <summary>A Public route has no access control, so a bypass line would only ever be dead state.</summary>
    [Fact]
    public async Task BypassPaths_AreNotStoredOnAPublicRoute() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var stackId = await host.AddStackAsync("blog");

        var result = await CreateAsync(
            host,
            ServiceCommand(stackId, "blog.example.invalid") with {
                AccessMode = AccessMode.Public, BypassPaths = "/webhooks/",
            });

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Null((await RouteAsync(host, result.Value.Route.Id)).BypassPaths);
    }

    [Fact]
    public async Task AnUnrootedBypassPath_IsRefused() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var stackId = await host.AddStackAsync("blog");

        var result = await CreateAsync(
            host,
            ServiceCommand(stackId, "blog.example.invalid") with { BypassPaths = "webhooks/" });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Contains("must start with '/'", result.Error.Message, StringComparison.Ordinal);
        Assert.Empty(await DomainsAsync(host));
    }

    /// <summary>
    /// A new hostname that admits nobody reads as a broken deployment rather than a policy. Refused for
    /// naming nobody — no longer for being Restricted, which a create can now say in full.
    /// </summary>
    [Fact]
    public async Task ARestrictedRouteNamingNobody_IsRefused() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var stackId = await host.AddStackAsync("blog");

        var result = await CreateAsync(
            host,
            ServiceCommand(stackId, "blog.example.invalid") with { AccessMode = AccessMode.Restricted });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Contains("at least one user or group", result.Error.Message, StringComparison.Ordinal);
        Assert.Empty(await DomainsAsync(host));
    }

    // ── The whole policy in one write ────────────────────────────────────────

    /// <summary>
    /// The two-step create is gone: a Restricted route is created with its grants in the same write, so it
    /// is never committed — and so never reconciled to an edge — under the instance-wide allow-list first.
    /// </summary>
    [Fact]
    public async Task ARestrictedRouteWithGrants_IsCreatedWithThemInTheSameWrite() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var stackId = await host.AddStackAsync("blog");
        var alice = await host.AddUserAsync("alice");
        var groupId = await host.AddGroupAsync("family", alice);

        var result = await CreateAsync(host, ServiceCommand(stackId, "blog.example.invalid") with {
            AccessMode = AccessMode.Restricted,
            GrantedUserIds = [alice],
            GrantedGroupIds = [groupId],
        });

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("restricted", result.Value.Route.AccessMode);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var grants = await db.RouteAccessGrants.AsNoTracking()
            .Where(g => g.RouteId == result.Value.Route.Id).ToListAsync(Ct);
        Assert.Contains(grants, g => g.UserId == alice);
        Assert.Contains(grants, g => g.GroupId == groupId);
    }

    [Fact]
    public async Task AnAuthenticatedRouteWithRules_IsCreatedWithThemAttachedInOrder() {
        using var host = AuthTestHost.Start(WithRouteHandlers, CloudflareSettings());
        var stackId = await host.AddStackAsync("blog");
        var family = await host.AddAccessRuleAsync("family", [ExternalPolicy("pol-family")]);
        var friends = await host.AddAccessRuleAsync("friends", [ExternalPolicy("pol-friends")]);

        var result = await CreateAsync(host, ServiceCommand(stackId, "blog.example.invalid") with {
            AccessMode = AccessMode.Authenticated,
            AccessRuleIds = [friends, family],
        });

        Assert.True(result.IsSuccess, Describe(result));
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var attached = await db.RouteAccessRules.AsNoTracking()
            .Where(a => a.RouteId == result.Value.Route.Id).OrderBy(a => a.Order)
            .Select(a => a.AccessRuleId).ToListAsync(Ct);
        // The caller's order, not the ids' — it is the precedence the policies attach in at the edge.
        Assert.Equal([friends, family], attached);
    }

    [Fact]
    public async Task IdentityForwarding_CanBeChosenAtCreate() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var stackId = await host.AddStackAsync("blog");

        var result = await CreateAsync(host, ServiceCommand(stackId, "blog.example.invalid") with {
            IdentityHeaderMode = IdentityHeaderMode.Remote,
        });

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(IdentityHeaderMode.Remote, (await RouteAsync(host, result.Value.Route.Id)).IdentityHeaderMode);
    }

    /// <summary>
    /// The same validation proxy.setAccess runs — here the portability check (ADR-0039 decision 4): a rule
    /// the active provider cannot enforce is refused at create exactly as it would be on a later change.
    /// </summary>
    [Fact]
    public async Task ARuleTheProviderCannotEnforce_IsRefused_AndWritesNoRow() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var stackId = await host.AddStackAsync("blog");
        var friends = await host.AddAccessRuleAsync("friends", [ExternalPolicy("pol-friends")]);

        var result = await CreateAsync(host, ServiceCommand(stackId, "blog.example.invalid") with {
            AccessMode = AccessMode.Authenticated,
            AccessRuleIds = [friends],
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("external policy clauses", result.Error.Message, StringComparison.Ordinal);
        Assert.Empty(await DomainsAsync(host));
    }

    [Fact]
    public async Task RulesOnARestrictedCreate_AreRefused() {
        using var host = AuthTestHost.Start(WithRouteHandlers, CloudflareSettings());
        var stackId = await host.AddStackAsync("blog");
        var alice = await host.AddUserAsync("alice");
        var family = await host.AddAccessRuleAsync("family", [ExternalPolicy("pol-family")]);

        var result = await CreateAsync(host, ServiceCommand(stackId, "blog.example.invalid") with {
            AccessMode = AccessMode.Restricted,
            GrantedUserIds = [alice],
            AccessRuleIds = [family],
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("would widen that", result.Error.Message, StringComparison.Ordinal);
        Assert.Empty(await DomainsAsync(host));
    }

    /// <summary>
    /// The realm comes from the stack the create names, because there is no route row yet to resolve it
    /// from — the same answer RouteAccessPolicy gives once the route exists.
    /// </summary>
    [Fact]
    public async Task AGrantFromAnotherRealmThanTheStacks_IsRefused_AndWritesNoRow() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var tenants = await host.AddRealmAsync("tenants");
        var templateId = await host.AddRealmTemplateAsync("tenant-apps", tenants);
        var stackId = await host.AddStackAsync("acme", templateId, tenantSlug: "acme");
        var operatorAccount = await host.AddUserAsync("alice");

        var result = await CreateAsync(host, ServiceCommand(stackId, "acme.example.invalid") with {
            AccessMode = AccessMode.Restricted,
            GrantedUserIds = [operatorAccount],
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("different realm", result.Error.Message, StringComparison.Ordinal);
        Assert.Empty(await DomainsAsync(host));
    }

    // ── The audit trail ──────────────────────────────────────────────────────

    /// <summary>
    /// Granting access at creation leaves the trace proxy.setAccess would have left for the same decision
    /// made a moment later — otherwise creating with grants would be the way to grant without one.
    /// </summary>
    [Fact]
    public async Task AnExplicitPolicy_IsAudited() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var stackId = await host.AddStackAsync("blog");
        var alice = await host.AddUserAsync("alice");

        var result = await CreateAsync(host, ServiceCommand(stackId, "blog.example.invalid") with {
            AccessMode = AccessMode.Restricted,
            GrantedUserIds = [alice],
        });

        Assert.True(result.IsSuccess, Describe(result));
        var audit = await AccessAuditAsync(host);
        var row = Assert.Single(audit);
        Assert.Contains("mode=Restricted", row, StringComparison.Ordinal);
        Assert.Contains("on create", row, StringComparison.Ordinal);
    }

    /// <summary>The configured default is a setting, audited when it was set — not a decision made here.</summary>
    [Fact]
    public async Task TheDefaultPolicy_IsNotAudited() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var stackId = await host.AddStackAsync("blog");

        var result = await CreateAsync(host, ServiceCommand(stackId, "blog.example.invalid"));

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Empty(await AccessAuditAsync(host));
    }

    // ── The role gate ────────────────────────────────────────────────────────

    /// <summary>
    /// The gate fires on <em>any</em> explicit value, not only a non-default one: the default can change
    /// between the moment a form is rendered and the moment it is submitted.
    /// </summary>
    [Theory]
    [InlineData("mode")]
    [InlineData("bypassPaths")]
    [InlineData("grantedUsers")]
    [InlineData("grantedGroups")]
    [InlineData("accessRules")]
    [InlineData("identityHeaders")]
    public async Task ANonAdministratorNamingAnAccessPolicy_IsForbidden_AndWritesNoRow(string field) {
        using var host = AuthTestHost.Start(WithRouteHandlers, ("Watchtower:Auth:Enabled", "true"));
        var stackId = await host.AddStackAsync("blog");
        var command = ServiceCommand(stackId, "blog.example.invalid");
        // Every field of the policy trips the gate on its own — the new ones included, or granting at
        // create would be the one way a non-administrator could decide who reaches a hostname.
        command = field switch {
            "mode" => command with { AccessMode = AccessMode.Public },
            "bypassPaths" => command with { BypassPaths = "/webhooks/" },
            "grantedUsers" => command with { GrantedUserIds = [1] },
            "grantedGroups" => command with { GrantedGroupIds = [1] },
            "accessRules" => command with { AccessRuleIds = [1] },
            _ => command with { IdentityHeaderMode = IdentityHeaderMode.Remote },
        };

        await using (var scope = host.Services.CreateAsyncScope()) {
            TestPrincipal.Seed(scope.ServiceProvider, isAdmin: false);
            var result = await SendAsync<CreateRoute.Command, CreateRoute.Response>(
                scope.ServiceProvider, command);

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorKind.Forbidden, result.Error.Kind);
        }

        Assert.Empty(await DomainsAsync(host));
    }

    /// <summary>
    /// The gate is on the decision, not on creating routes: a non-administrator that names no policy
    /// still gets a route, under the deployment's default.
    /// </summary>
    [Fact]
    public async Task ANonAdministratorNamingNoPolicy_CreatesTheRouteUnderTheDefault() {
        using var host = AuthTestHost.Start(WithRouteHandlers, ("Watchtower:Auth:Enabled", "true"));
        var stackId = await host.AddStackAsync("blog");

        await using var scope = host.Services.CreateAsyncScope();
        TestPrincipal.Seed(scope.ServiceProvider, isAdmin: false);
        var result = await SendAsync<CreateRoute.Command, CreateRoute.Response>(
            scope.ServiceProvider, ServiceCommand(stackId, "blog.example.invalid"));

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("authenticated", result.Value.Route.AccessMode);
    }

    // ── The Cloudflare guard ─────────────────────────────────────────────────

    /// <summary>
    /// Under Cloudflare the gate is an Access application, and one nobody can pass denies everyone. Said
    /// here, while the operator is looking, rather than left to a reconcile warning.
    /// </summary>
    [Fact]
    public async Task ProtectedUnderCloudflareWithNoAllowSource_IsRefused() {
        using var host = AuthTestHost.Start(WithRouteHandlers, CloudflareSettings());
        var stackId = await host.AddStackAsync("blog");

        var result = await CreateAsync(host, ServiceCommand(stackId, "blog.example.invalid"));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Contains("Settings → Reverse proxy", result.Error.Message, StringComparison.Ordinal);
        Assert.Empty(await DomainsAsync(host));
    }

    [Fact]
    public async Task ProtectedUnderCloudflareWithAnAllowSource_IsCreated() {
        using var host = AuthTestHost.Start(
            WithRouteHandlers,
            [.. CloudflareSettings(), ("Watchtower:Proxy:Cloudflare:AccessAllowedEmailDomains", "example.com")]);
        var stackId = await host.AddStackAsync("blog");

        var result = await CreateAsync(host, ServiceCommand(stackId, "blog.example.invalid"));

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("authenticated", result.Value.Route.AccessMode);
    }

    /// <summary>
    /// The guard is route-aware (ADR-0039 decision 5): the instance-wide settings are only this route's
    /// allow-list when it attaches no rules. A Cloudflare-only setup built entirely from access rules used
    /// to be unable to create a protected route at all.
    /// </summary>
    [Fact]
    public async Task ProtectedUnderCloudflareWithNoAllowSource_ButWithRules_IsCreated() {
        using var host = AuthTestHost.Start(WithRouteHandlers, CloudflareSettings());
        var stackId = await host.AddStackAsync("blog");
        var family = await host.AddAccessRuleAsync("family", [ExternalPolicy("pol-family")]);

        var result = await CreateAsync(host, ServiceCommand(stackId, "blog.example.invalid") with {
            AccessMode = AccessMode.Authenticated,
            AccessRuleIds = [family],
        });

        Assert.True(result.IsSuccess, Describe(result));
    }

    /// <summary>A Restricted route's allow-list is its grants; the instance-wide settings never apply to it.</summary>
    [Fact]
    public async Task RestrictedUnderCloudflareWithNoAllowSource_IsCreated() {
        using var host = AuthTestHost.Start(WithRouteHandlers, CloudflareSettings());
        var stackId = await host.AddStackAsync("blog");
        var alice = await host.AddUserAsync("alice");

        var result = await CreateAsync(host, ServiceCommand(stackId, "blog.example.invalid") with {
            AccessMode = AccessMode.Restricted,
            GrantedUserIds = [alice],
        });

        Assert.True(result.IsSuccess, Describe(result));
    }

    /// <summary>A Public route needs no Access application, so there is nothing for it to fail on.</summary>
    [Fact]
    public async Task APublicRouteUnderCloudflareWithNoAllowSource_IsCreated() {
        using var host = AuthTestHost.Start(WithRouteHandlers, CloudflareSettings());
        var stackId = await host.AddStackAsync("blog");

        var result = await CreateAsync(
            host,
            ServiceCommand(stackId, "blog.example.invalid") with { AccessMode = AccessMode.Public });

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("public", result.Value.Route.AccessMode);
    }

    /// <summary>
    /// Nothing is reconciling while the proxy is off, so the guard has no edge to speak for — and
    /// refusing then would leave an operator unable to lay routes out before turning the plane on.
    /// </summary>
    [Fact]
    public async Task ADisabledCloudflareProxy_DoesNotRefuseAProtectedRoute() {
        using var host = AuthTestHost.Start(
            WithRouteHandlers,
            ("Watchtower:Proxy:Enabled", "false"),
            ("Watchtower:Proxy:Provider", "cloudflare"));
        var stackId = await host.AddStackAsync("blog");

        var result = await CreateAsync(host, ServiceCommand(stackId, "blog.example.invalid"));

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("authenticated", result.Value.Route.AccessMode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>An enabled Cloudflare provider with no Access allow source configured.</summary>
    private static (string Key, string? Value)[] CloudflareSettings() => [
        ("Watchtower:Proxy:Enabled", "true"),
        ("Watchtower:Proxy:Provider", "cloudflare"),
    ];

    /// <summary>A service-route create command that says nothing about access.</summary>
    private static CreateRoute.Command ServiceCommand(int stackId, string domain) =>
        new(stackId, domain, ServiceName: "web", ContainerPort: 8080, TlsEnabled: true, IsPrimary: false,
            Kind: null);

    private static async Task<Result<CreateRoute.Response>> CreateAsync(
        AuthTestHost host, CreateRoute.Command command) {
        await using var scope = host.Services.CreateAsyncScope();
        return await SendAsync<CreateRoute.Command, CreateRoute.Response>(scope.ServiceProvider, command);
    }

    private static async Task<Route> RouteAsync(AuthTestHost host, int routeId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Routes.AsNoTracking().SingleAsync(r => r.Id == routeId, Ct);
    }

    private static AccessRuleClause ExternalPolicy(string id) =>
        new() { Kind = AccessClauseKind.ExternalPolicy, Value = id };

    /// <summary>The detail of every route-access audit row, oldest first.</summary>
    private static async Task<List<string?>> AccessAuditAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == AuthEventKinds.RouteAccessChanged)
            .OrderBy(e => e.Id).Select(e => e.Detail).ToListAsync(Ct);
    }

    /// <summary>Every routed hostname — empty is how "nothing was written" is asserted.</summary>
    private static async Task<List<string?>> DomainsAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Routes.AsNoTracking().Select(r => r.Domain).ToListAsync(Ct);
    }

    private static ValueTask<Result<TResponse>> SendAsync<TRequest, TResponse>(
        IServiceProvider scope, TRequest request) =>
        scope.GetRequiredService<IHandler<TRequest, Result<TResponse>>>().HandleAsync(request, Ct);

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
