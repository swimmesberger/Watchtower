using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// Covers the public management API (<c>/api/mgmt/*</c>) against the real host pipeline: a stack
/// authenticating with its own App API token, managing the tenants of a template an operator granted it.
/// </summary>
/// <remarks>
/// <para>
/// The security invariants are the reason this file exists, and every one of them is a test here: an
/// unknown token is refused, a stack whose App API is switched off is refused, a template the caller has
/// no grant on is <em>not found</em> rather than forbidden (so the surface is not an oracle for template
/// ids), a slug belonging to another template does not resolve, deletion needs its own capability, and
/// no response ever carries an environment value or a deploy's captured output.
/// </para>
/// <para>
/// Endpoints that read live container state (<c>…/tenants/{slug}</c> and <c>…/logs</c>) are exercised
/// only on the paths that short-circuit before Docker, which is where their authorization decision is
/// made anyway. Their <c>503</c> path is deliberately <em>not</em> covered, and that is a decision
/// rather than an oversight: <see cref="DockerEngineClient"/> hard-codes its connect callback to the
/// Unix socket at <c>/var/run/docker.sock</c> with no host, socket-path or <c>DOCKER_HOST</c> setting
/// anywhere in its options, so whether a request yields <c>503</c> or <c>200</c> depends entirely on
/// whether the machine running the tests happens to have a daemon — a developer box and a CI runner
/// with Docker would disagree with a Windows box or a bare container. Making it assertable means adding
/// a configurable endpoint to production code purely for the test, which is a change to make on its own
/// merits (it would also let an operator point Watchtower at a remote or rootless daemon), not smuggled
/// in here. The 503 body and helper are shared with the App API, so the response itself is not
/// surface-specific.
/// </para>
/// </remarks>
public sealed class MgmtApiTests {
    private const string Templates = "/api/mgmt/templates";

    // -- Authentication --------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic aGk6dGhlcmU=")]
    [InlineData("Bearer not-a-watchtower-token")]
    [InlineData("Bearer wtapp_thisisnotarealtoken")]
    public async Task AnythingButAKnownToken_Is401(string? authorization) {
        using var factory = new WatchtowerApiFactory();
        using var client = factory.CreateApiClient();

        var request = new HttpRequestMessage(HttpMethod.Get, Templates);
        if (!string.IsNullOrEmpty(authorization)) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Missing or invalid App API token.", await BodyAsync(response), StringComparison.Ordinal);
    }

    /// <summary>One kill switch closes both public surfaces: the App API flag also closes this one.</summary>
    [Fact]
    public async Task ACallerWhoseAppApiIsSwitchedOff_Is403() {
        using var factory = new WatchtowerApiFactory();
        var (stackId, token) = await factory.AddCallerStackAsync("vendor-console", appApiEnabled: false);
        var templateId = await factory.AddTemplateAsync("billing");
        await factory.GrantManagementAsync(stackId, templateId);

        using var client = factory.CreateApiClient();
        var response = await GetAsync(client, Templates, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // -- The grant gate --------------------------------------------------------------------------

    /// <summary>
    /// A template that exists but is not granted answers exactly as one that does not exist. Anything
    /// else would let a caller enumerate template ids by their status codes.
    /// </summary>
    [Fact]
    public async Task AnUngrantedTemplateAnswersTheSame404AsAnUnknownOne() {
        using var factory = new WatchtowerApiFactory();
        var (_, token) = await factory.AddCallerStackAsync("vendor-console");
        var ungranted = await factory.AddTemplateAsync("billing");

        using var client = factory.CreateApiClient();
        var existing = await GetAsync(client, $"{Templates}/{ungranted}/tenants", token);
        var missing = await GetAsync(client, $"{Templates}/424242/tenants", token);

        Assert.Equal(HttpStatusCode.NotFound, existing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(await BodyAsync(existing), await BodyAsync(missing));
        Assert.Contains("Template not found.", await BodyAsync(existing), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryTemplateScopedRouteIsClosedWithoutAGrant() {
        using var factory = new WatchtowerApiFactory();
        var (_, token) = await factory.AddCallerStackAsync("vendor-console");
        var templateId = await factory.AddTemplateAsync("billing");
        await factory.AddTenantAsync(templateId, "acme");

        using var client = factory.CreateApiClient();
        var prefix = $"{Templates}/{templateId}";

        foreach (var (method, url) in new (HttpMethod, string)[] {
                     (HttpMethod.Get, $"{prefix}/tenants"),
                     (HttpMethod.Post, $"{prefix}/tenants"),
                     (HttpMethod.Get, $"{prefix}/tenants/acme"),
                     (HttpMethod.Post, $"{prefix}/tenants/acme/deploy"),
                     (HttpMethod.Get, $"{prefix}/tenants/acme/deployments"),
                     (HttpMethod.Get, $"{prefix}/tenants/acme/logs"),
                     (HttpMethod.Delete, $"{prefix}/tenants/acme"),
                 }) {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add("Authorization", $"Bearer {token}");
            if (method == HttpMethod.Post) request.Content = JsonBody("""{"slug":"other"}""");
            var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // Nothing was created or destroyed by any of those calls.
        Assert.Equal(1, await factory.ReadAsync(db => db.Stacks.CountAsync(
            s => s.TemplateId == templateId, TestContext.Current.CancellationToken)));
    }

    // -- GET /api/mgmt/templates -----------------------------------------------------------------

    [Fact]
    public async Task Templates_ListsOnlyTheGrantedOnes_WithTheCallersCapabilityAndTenantCount() {
        using var factory = new WatchtowerApiFactory();
        var (stackId, token) = await factory.AddCallerStackAsync("vendor-console");
        var billing = await factory.AddTemplateAsync("billing");
        var secret = await factory.AddTemplateAsync("secret-product");
        await factory.AddTenantAsync(billing, "acme");
        await factory.AddTenantAsync(billing, "globex");
        await factory.AddTenantAsync(secret, "hidden", stackNamePrefix: "secret-product", domain: "hidden.other.com");
        await factory.GrantManagementAsync(stackId, billing, allowDelete: true);

        using var client = factory.CreateApiClient();
        var body = await JsonAsync(await GetAsync(client, Templates, token));

        var only = Assert.Single(body.RootElement.GetProperty("templates").EnumerateArray().ToList());
        Assert.Equal(billing, only.GetProperty("id").GetInt32());
        Assert.Equal("billing", only.GetProperty("name").GetString());
        Assert.Equal("{tenant}.example.com", only.GetProperty("domainPattern").GetString());
        Assert.Equal("web", only.GetProperty("targetServiceName").GetString());
        Assert.True(only.GetProperty("allowDelete").GetBoolean());
        Assert.Equal(2, only.GetProperty("tenantCount").GetInt32());
    }

    [Fact]
    public async Task Templates_IsEmptyForACallerWithNoGrants() {
        using var factory = new WatchtowerApiFactory();
        var (_, token) = await factory.AddCallerStackAsync("vendor-console");
        await factory.AddTemplateAsync("billing");

        using var client = factory.CreateApiClient();
        var body = await JsonAsync(await GetAsync(client, Templates, token));

        Assert.Empty(body.RootElement.GetProperty("templates").EnumerateArray());
    }

    // -- GET …/tenants ---------------------------------------------------------------------------

    [Fact]
    public async Task Tenants_ListsTheTemplatesOwnTenantsNewestFirst_AndNoOneElses() {
        using var factory = new WatchtowerApiFactory();
        var (stackId, token) = await factory.AddCallerStackAsync("vendor-console");
        var billing = await factory.AddTemplateAsync("billing");
        var portal = await factory.AddTemplateAsync("portal", "{tenant}.portal.example.com");
        await factory.AddTenantAsync(billing, "acme", lastDeployStatus: DeployStatus.Success);
        await factory.AddTenantAsync(billing, "globex");
        await factory.AddTenantAsync(portal, "acme", "portal", "acme.portal.example.com");
        await factory.GrantManagementAsync(stackId, billing);

        using var client = factory.CreateApiClient();
        var response = await GetAsync(client, $"{Templates}/{billing}/tenants", token);
        var raw = await BodyAsync(response);
        var tenants = JsonDocument.Parse(raw).RootElement.GetProperty("tenants").EnumerateArray().ToList();

        Assert.Equal(["globex", "acme"], tenants.Select(t => t.GetProperty("slug").GetString()));
        Assert.Equal("acme.example.com", tenants[1].GetProperty("domain").GetString());
        Assert.Equal("success", tenants[1].GetProperty("lastDeployStatus").GetString());
        // The other template's tenant shares the slug "acme" but a different domain, which must not appear.
        Assert.DoesNotContain("portal.example.com", raw, StringComparison.Ordinal);
        // Environment values are never projected onto this surface.
        Assert.DoesNotContain("hunter2", raw, StringComparison.Ordinal);
    }

    // -- POST …/tenants --------------------------------------------------------------------------

    [Fact]
    public async Task CreateTenant_ProvisionsIt_AndQueuesTheInitialDeploy() {
        using var factory = new WatchtowerApiFactory();
        var (stackId, token) = await factory.AddCallerStackAsync("vendor-console");
        var billing = await factory.AddTemplateAsync("billing");
        await factory.GrantManagementAsync(stackId, billing);

        using var client = factory.CreateApiClient();
        var response = await SendAsync(client, HttpMethod.Post, $"{Templates}/{billing}/tenants", token,
            """{"slug":"Acme","env":{"API_KEY":"secret-value"}}""");
        var raw = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("acme", created.GetProperty("slug").GetString());
        Assert.Equal("acme.example.com", created.GetProperty("domain").GetString());
        var tenantStackId = created.GetProperty("stackId").GetInt32();
        var deployId = created.GetProperty("deploy").GetProperty("id").GetInt32();
        Assert.Equal("queued", created.GetProperty("deploy").GetProperty("status").GetString());
        Assert.Equal($"{Templates}/{billing}/tenants/acme", response.Headers.Location?.ToString());

        // The stack really exists, bound to the template, with the override stored…
        var tenant = await factory.ReadAsync(db => db.Stacks.SingleAsync(
            s => s.Id == tenantStackId, TestContext.Current.CancellationToken));
        Assert.Equal(billing, tenant.TemplateId);
        Assert.Equal("acme", tenant.TenantSlug);
        Assert.Equal("secret-value", (await factory.EnvOfAsync(tenantStackId))["API_KEY"]);
        // …and the value it stored is not echoed back to the caller.
        Assert.DoesNotContain("secret-value", raw, StringComparison.Ordinal);

        // The deploy was actually enqueued, against the new stack, as a tenant creation.
        Assert.Equal([(tenantStackId, "tenant-create")], factory.DeployQueue.Calls);
        var deployEvent = await factory.ReadAsync(db => db.DeployEvents.SingleAsync(
            e => e.Id == deployId, TestContext.Current.CancellationToken));
        Assert.Equal(tenantStackId, deployEvent.StackId);
        Assert.Equal("queued", deployEvent.Status);
    }

    [Fact]
    public async Task CreateTenant_RefusesASlugTheTemplateAlreadyHasWith409() {
        using var factory = new WatchtowerApiFactory();
        var (stackId, token) = await factory.AddCallerStackAsync("vendor-console");
        var billing = await factory.AddTemplateAsync("billing");
        await factory.AddTenantAsync(billing, "acme");
        await factory.GrantManagementAsync(stackId, billing);

        using var client = factory.CreateApiClient();
        var response = await SendAsync(client, HttpMethod.Post, $"{Templates}/{billing}/tenants", token,
            """{"slug":"acme"}""");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(factory.DeployQueue.Calls);
    }

    [Theory]
    // An unusable slug…
    [InlineData("""{"slug":"not a slug"}""")]
    [InlineData("""{"slug":""}""")]
    [InlineData("{}")]
    // …and an environment name that is not one. Keys become lines in the generated compose .env, so a
    // name carrying a newline or an '=' could write further assignments into that file.
    [InlineData("""{"slug":"acme","env":{"BAD KEY":"x"}}""")]
    [InlineData("""{"slug":"acme","env":{"OK=INJECTED\nEVIL":"x"}}""")]
    [InlineData("""{"slug":"acme","env":{"1LEADINGDIGIT":"x"}}""")]
    public async Task CreateTenant_RejectsABadSlugOrEnvironmentNameWith400(string body) {
        using var factory = new WatchtowerApiFactory();
        var (stackId, token) = await factory.AddCallerStackAsync("vendor-console");
        var billing = await factory.AddTemplateAsync("billing");
        await factory.GrantManagementAsync(stackId, billing);

        using var client = factory.CreateApiClient();
        var response = await SendAsync(client, HttpMethod.Post, $"{Templates}/{billing}/tenants", token, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await factory.ReadAsync(db => db.Stacks.AnyAsync(
            s => s.TemplateId == billing, TestContext.Current.CancellationToken)));
    }

    /// <summary>
    /// <c>accessible</c> is a literal segment of this surface's own tenant routes and outranks
    /// <c>{slug}</c> when routing, so a tenant of that name would be created and then be unreachable on
    /// <c>GET …/tenants/{slug}</c>. The shared provisioning path refuses it, which is what makes the
    /// collision impossible rather than merely documented.
    /// </summary>
    [Theory]
    [InlineData("accessible")]
    [InlineData("Accessible")]
    public async Task CreateTenant_RefusesTheReservedSlugWith400(string slug) {
        using var factory = new WatchtowerApiFactory();
        var (stackId, token) = await factory.AddCallerStackAsync("vendor-console");
        var billing = await factory.AddTemplateAsync("billing");
        await factory.GrantManagementAsync(stackId, billing);

        using var client = factory.CreateApiClient();
        var response = await SendAsync(client, HttpMethod.Post, $"{Templates}/{billing}/tenants", token,
            $$"""{"slug":"{{slug}}"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("reserved", await BodyAsync(response), StringComparison.Ordinal);
        Assert.False(await factory.ReadAsync(db => db.Stacks.AnyAsync(
            s => s.TemplateId == billing, TestContext.Current.CancellationToken)));
        Assert.Empty(factory.DeployQueue.Calls);
    }

    [Fact]
    public async Task CreateTenant_RejectsAMalformedBodyWith400() {
        using var factory = new WatchtowerApiFactory();
        var (stackId, token) = await factory.AddCallerStackAsync("vendor-console");
        var billing = await factory.AddTemplateAsync("billing");
        await factory.GrantManagementAsync(stackId, billing);

        using var client = factory.CreateApiClient();
        var response = await SendAsync(client, HttpMethod.Post, $"{Templates}/{billing}/tenants", token, "not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -- POST …/deploy ---------------------------------------------------------------------------

    [Fact]
    public async Task Deploy_QueuesARedeployOfThatTenantOnly() {
        using var factory = new WatchtowerApiFactory();
        var (stackId, token) = await factory.AddCallerStackAsync("vendor-console");
        var billing = await factory.AddTemplateAsync("billing");
        var tenantStackId = await factory.AddTenantAsync(billing, "acme");
        await factory.GrantManagementAsync(stackId, billing);

        using var client = factory.CreateApiClient();
        var response = await SendAsync(client, HttpMethod.Post, $"{Templates}/{billing}/tenants/acme/deploy", token);
        var body = JsonDocument.Parse(await BodyAsync(response)).RootElement;

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("queued", body.GetProperty("deploy").GetProperty("status").GetString());
        Assert.Equal([(tenantStackId, "mgmt-deploy")], factory.DeployQueue.Calls);

        var deployEvent = await factory.ReadAsync(db => db.DeployEvents.SingleAsync(
            e => e.Id == body.GetProperty("deploy").GetProperty("id").GetInt32(),
            TestContext.Current.CancellationToken));
        Assert.Equal(tenantStackId, deployEvent.StackId);
        Assert.Equal("mgmt-deploy", deployEvent.TriggeredBy);
    }

    /// <summary>
    /// Tenant lookups are constrained by the granted template, so a slug that names a tenant of another
    /// template is simply not found — on every tenant-scoped route.
    /// </summary>
    [Theory]
    [InlineData("GET", "")]
    [InlineData("GET", "/deployments")]
    [InlineData("GET", "/logs")]
    [InlineData("POST", "/deploy")]
    [InlineData("DELETE", "")]
    public async Task ASlugFromAnotherTemplate_IsNotFound(string method, string suffix) {
        using var factory = new WatchtowerApiFactory();
        var (stackId, token) = await factory.AddCallerStackAsync("vendor-console");
        var billing = await factory.AddTemplateAsync("billing");
        var portal = await factory.AddTemplateAsync("portal", "{tenant}.portal.example.com");
        var otherTenant = await factory.AddTenantAsync(portal, "elsewhere", "portal", "elsewhere.portal.example.com");
        await factory.GrantManagementAsync(stackId, billing, allowDelete: true);

        using var client = factory.CreateApiClient();
        var response = await SendAsync(
            client, new HttpMethod(method), $"{Templates}/{billing}/tenants/elsewhere{suffix}", token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Tenant not found.", await BodyAsync(response), StringComparison.Ordinal);
        // The other template's tenant is untouched — no deploy queued, no row removed.
        Assert.Empty(factory.DeployQueue.Calls);
        Assert.True(await factory.ReadAsync(db => db.Stacks.AnyAsync(
            s => s.Id == otherTenant, TestContext.Current.CancellationToken)));
    }

    // -- GET …/deployments -----------------------------------------------------------------------

    [Fact]
    public async Task Deployments_ReturnsTheHistoryWithoutTheCapturedOutput() {
        using var factory = new WatchtowerApiFactory();
        var (stackId, token) = await factory.AddCallerStackAsync("vendor-console");
        var billing = await factory.AddTemplateAsync("billing");
        var tenantStackId = await factory.AddTenantAsync(billing, "acme");
        await factory.AddDeployEventAsync(tenantStackId, "success", "registry-password-in-here");
        await factory.AddDeployEventAsync(tenantStackId, "failed", "another-secret");
        await factory.GrantManagementAsync(stackId, billing);

        using var client = factory.CreateApiClient();
        var raw = await BodyAsync(await GetAsync(client, $"{Templates}/{billing}/tenants/acme/deployments", token));
        var deployments = JsonDocument.Parse(raw).RootElement.GetProperty("deployments").EnumerateArray().ToList();

        Assert.Equal(2, deployments.Count);
        Assert.Equal(["failed", "success"], deployments.Select(d => d.GetProperty("status").GetString()));
        // Deploy output is produced with git and registry credentials in scope; it never leaves the host.
        Assert.DoesNotContain("registry-password-in-here", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("another-secret", raw, StringComparison.Ordinal);
        Assert.False(deployments[0].TryGetProperty("output", out _));
    }

    [Fact]
    public async Task Deployments_HonoursTheLimit() {
        using var factory = new WatchtowerApiFactory();
        var (stackId, token) = await factory.AddCallerStackAsync("vendor-console");
        var billing = await factory.AddTemplateAsync("billing");
        var tenantStackId = await factory.AddTenantAsync(billing, "acme");
        for (var i = 0; i < 3; i++) await factory.AddDeployEventAsync(tenantStackId, "success");
        await factory.GrantManagementAsync(stackId, billing);

        using var client = factory.CreateApiClient();
        var one = await JsonAsync(
            await GetAsync(client, $"{Templates}/{billing}/tenants/acme/deployments?limit=1", token));
        var capped = await JsonAsync(
            await GetAsync(client, $"{Templates}/{billing}/tenants/acme/deployments?limit=100000", token));

        Assert.Single(one.RootElement.GetProperty("deployments").EnumerateArray());
        Assert.Equal(3, capped.RootElement.GetProperty("deployments").EnumerateArray().Count());
    }

    // -- DELETE …/tenants/{slug} -----------------------------------------------------------------

    [Fact]
    public async Task Delete_WithoutTheCapability_Is403_AndTellsTheCallerNothingElse() {
        using var factory = new WatchtowerApiFactory();
        var (stackId, token) = await factory.AddCallerStackAsync("vendor-console");
        var billing = await factory.AddTemplateAsync("billing");
        var tenantStackId = await factory.AddTenantAsync(billing, "acme");
        await factory.GrantManagementAsync(stackId, billing, allowDelete: false);

        using var client = factory.CreateApiClient();
        var known = await SendAsync(client, HttpMethod.Delete, $"{Templates}/{billing}/tenants/acme", token);
        var unknown = await SendAsync(client, HttpMethod.Delete, $"{Templates}/{billing}/tenants/nosuch", token);

        Assert.Equal(HttpStatusCode.Forbidden, known.StatusCode);
        Assert.Contains("Delete is not permitted for this grant.", await BodyAsync(known), StringComparison.Ordinal);
        // The refusal happens before the tenant is looked up, so it does not disclose which slugs exist.
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.Equal(await BodyAsync(known), await BodyAsync(unknown));

        Assert.Empty(factory.Compose.Downs);
        Assert.True(await factory.ReadAsync(db => db.Stacks.AnyAsync(
            s => s.Id == tenantStackId, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Delete_BringsTheProjectDown_CascadesTheRows_AndReloadsTheProxy() {
        using var factory = new WatchtowerApiFactory();
        var (stackId, token) = await factory.AddCallerStackAsync("vendor-console");
        var billing = await factory.AddTemplateAsync("billing");
        var tenantStackId = await factory.AddTenantAsync(billing, "acme");
        await factory.AddDeployEventAsync(tenantStackId, "success");
        await factory.GrantManagementAsync(stackId, billing, allowDelete: true);
        var reloadsBefore = factory.Caddy.ApplyCount;
        ProbeStack(factory, tenantStackId);

        using var client = factory.CreateApiClient();
        var response = await SendAsync(client, HttpMethod.Delete, $"{Templates}/{billing}/tenants/acme", token);
        var body = JsonDocument.Parse(await BodyAsync(response)).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("acme", body.GetProperty("slug").GetString());
        Assert.True(body.GetProperty("deleted").GetBoolean());

        // Compose was told to bring down that tenant's own project, keeping its volumes by default.
        Assert.Equal([("billing-acme", false)], factory.Compose.Downs);
        // And it was told so while the row naming that project still existed; the proxy was reloaded
        // only after it was gone. Asserting the end state alone would not tell this apart from the
        // reverse order, which would strand containers nothing names.
        Assert.True(factory.Compose.StackExistedAtDown);
        Assert.False(factory.Caddy.StackExistedAtApply);
        // Only then were the rows deleted — and the proxy reloaded, so it stops serving the dead domain.
        Assert.False(await factory.ReadAsync(db => db.Stacks.AnyAsync(
            s => s.Id == tenantStackId, TestContext.Current.CancellationToken)));
        Assert.False(await factory.ReadAsync(db => db.Routes.AnyAsync(
            r => r.StackId == tenantStackId, TestContext.Current.CancellationToken)));
        Assert.False(await factory.ReadAsync(db => db.StackEnvVars.AnyAsync(
            v => v.StackId == tenantStackId, TestContext.Current.CancellationToken)));
        Assert.False(await factory.ReadAsync(db => db.DeployEvents.AnyAsync(
            e => e.StackId == tenantStackId, TestContext.Current.CancellationToken)));
        Assert.Equal(reloadsBefore + 1, factory.Caddy.ApplyCount);
    }

    [Fact]
    public async Task Delete_PassesTheVolumePurgeThroughToCompose() {
        using var factory = new WatchtowerApiFactory();
        var (stackId, token) = await factory.AddCallerStackAsync("vendor-console");
        var billing = await factory.AddTemplateAsync("billing");
        await factory.AddTenantAsync(billing, "acme");
        await factory.GrantManagementAsync(stackId, billing, allowDelete: true);

        using var client = factory.CreateApiClient();
        var response = await SendAsync(
            client, HttpMethod.Delete, $"{Templates}/{billing}/tenants/acme?volumes=true", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([("billing-acme", true)], factory.Compose.Downs);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    public async Task Delete_DuringAnInFlightDeploy_Is409_AndChangesNothing(string status) {
        using var factory = new WatchtowerApiFactory();
        var (stackId, token) = await factory.AddCallerStackAsync("vendor-console");
        var billing = await factory.AddTemplateAsync("billing");
        var tenantStackId = await factory.AddTenantAsync(billing, "acme");
        await factory.AddDeployEventAsync(tenantStackId, status);
        await factory.GrantManagementAsync(stackId, billing, allowDelete: true);

        using var client = factory.CreateApiClient();
        var response = await SendAsync(client, HttpMethod.Delete, $"{Templates}/{billing}/tenants/acme", token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(factory.Compose.Downs);
        Assert.True(await factory.ReadAsync(db => db.Stacks.AnyAsync(
            s => s.Id == tenantStackId, TestContext.Current.CancellationToken)));
    }

    /// <summary>
    /// Compose failing leaves containers up, so the row that names them must survive too: the caller
    /// retries rather than being left with orphaned containers no record points at.
    /// </summary>
    [Fact]
    public async Task Delete_WhenComposeCannotStopTheTenant_Is500_AndKeepsEverything() {
        using var factory = new WatchtowerApiFactory();
        factory.Compose.DownExitCode = 1;
        var (stackId, token) = await factory.AddCallerStackAsync("vendor-console");
        var billing = await factory.AddTemplateAsync("billing");
        var tenantStackId = await factory.AddTenantAsync(billing, "acme");
        await factory.GrantManagementAsync(stackId, billing, allowDelete: true);
        var reloadsBefore = factory.Caddy.ApplyCount;
        ProbeStack(factory, tenantStackId);

        using var client = factory.CreateApiClient();
        var response = await SendAsync(client, HttpMethod.Delete, $"{Templates}/{billing}/tenants/acme", token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        // The compose output stays in Watchtower's log, not in a body a vendor's UI can read.
        Assert.DoesNotContain("stubbed compose down", await BodyAsync(response), StringComparison.Ordinal);
        Assert.True(factory.Compose.StackExistedAtDown);
        Assert.True(await factory.ReadAsync(db => db.Stacks.AnyAsync(
            s => s.Id == tenantStackId, TestContext.Current.CancellationToken)));
        Assert.Equal(reloadsBefore, factory.Caddy.ApplyCount);
        Assert.Null(factory.Caddy.StackExistedAtApply);
    }

    /// <summary>
    /// Slugs are normalized on the way in, so they resolve the same way they did at creation — and the
    /// response echoes the stored identifier rather than the casing the URL happened to use.
    /// </summary>
    [Fact]
    public async Task Delete_ResolvesTheSlugTheWayItWasStored() {
        using var factory = new WatchtowerApiFactory();
        var (stackId, token) = await factory.AddCallerStackAsync("vendor-console");
        var billing = await factory.AddTemplateAsync("billing");
        await factory.AddTenantAsync(billing, "acme");
        await factory.GrantManagementAsync(stackId, billing, allowDelete: true);

        using var client = factory.CreateApiClient();
        var response = await SendAsync(client, HttpMethod.Delete, $"{Templates}/{billing}/tenants/ACME", token);
        var body = JsonDocument.Parse(await BodyAsync(response)).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("acme", body.GetProperty("slug").GetString());
        Assert.Equal([("billing-acme", false)], factory.Compose.Downs);
    }

    [Fact]
    public async Task Delete_OfAnUnknownSlug_Is404() {
        using var factory = new WatchtowerApiFactory();
        var (stackId, token) = await factory.AddCallerStackAsync("vendor-console");
        var billing = await factory.AddTemplateAsync("billing");
        await factory.GrantManagementAsync(stackId, billing, allowDelete: true);

        using var client = factory.CreateApiClient();
        var response = await SendAsync(client, HttpMethod.Delete, $"{Templates}/{billing}/tenants/nosuch", token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Compose.Downs);
    }

    // -- Helpers ---------------------------------------------------------------------------------

    /// <summary>
    /// Makes both teardown doubles sample whether <paramref name="stackId"/> still exists at the moment
    /// they are called, so the order of compose-down / row delete / proxy reload is assertable rather
    /// than merely implied by the end state.
    /// </summary>
    private static void ProbeStack(WatchtowerApiFactory factory, int stackId) {
        // A scope of its own: this must read committed state, not whatever the request's own context is
        // tracking mid-teardown.
        bool Probe() {
            using var scope = factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>()
                .Stacks.AsNoTracking().Any(s => s.Id == stackId);
        }

        factory.Compose.StackProbe = Probe;
        factory.Caddy.StackProbe = Probe;
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string url, string? token) =>
        SendAsync(client, HttpMethod.Get, url, token);

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, string? token, string? jsonBody = null) {
        var request = new HttpRequestMessage(method, url);
        if (token is not null) request.Headers.Add("Authorization", $"Bearer {token}");
        if (jsonBody is not null) request.Content = JsonBody(jsonBody);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private static Task<string> BodyAsync(HttpResponseMessage response) =>
        response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await BodyAsync(response));
}
