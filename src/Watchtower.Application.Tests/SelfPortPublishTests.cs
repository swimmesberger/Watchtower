using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Elarion.Abstractions;
using Elarion.Abstractions.Serialization;
using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Acme;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers self-publishing host ports (ADR-0033): the plan that decides what a recreate would change, the
/// reading of a container's current bindings, and the handler that spawns the coordinator.
/// </summary>
/// <remarks>
/// The plan is where the safety property lives — Watchtower may only ever take away a binding it added
/// itself — and it is pure, so it is pinned here without a daemon. The handler half runs against the same
/// recording Docker double the self-update tests use; the real recreate cannot be exercised anywhere but
/// a Linux host with a live daemon, which is exactly why the seams below carry the weight.
/// </remarks>
[Collection(HostnameEnvironment.Name)]
public sealed class SelfPortPublishTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string SelfId = "abc123def4567890abcdef";

    // ── ComputePlan ──────────────────────────────────────────────────────────

    /// <summary>A container that publishes nothing: every routed port is a publish, and becomes managed.</summary>
    [Fact]
    public void AFreshContainer_PublishesEveryRoutedPort() {
        var plan = SelfPortPublishService.ComputePlan(desired: [9002, 9001], bound: [], managed: []);

        Assert.Equal([9001, 9002], plan.Publish);
        Assert.Empty(plan.Unpublish);
        Assert.Equal([9001, 9002], plan.NextManaged);
        Assert.False(plan.IsNoOp);
    }

    [Fact]
    public void EverythingAlreadyBound_IsANoOp() {
        var plan = SelfPortPublishService.ComputePlan(desired: [9001], bound: [9001], managed: [9001]);

        Assert.True(plan.IsNoOp);
        Assert.Equal([9001], plan.NextManaged);
    }

    /// <summary>
    /// The rule the whole feature rests on. An operator who publishes 9001 in their compose file has
    /// already satisfied the route, so nothing is republished — and, crucially, the port is not adopted
    /// into the managed set, so deleting the route later cannot take their binding away.
    /// </summary>
    [Fact]
    public void APortTheOperatorAlreadyPublishes_IsNeitherRepublishedNorAdopted() {
        var plan = SelfPortPublishService.ComputePlan(desired: [9001], bound: [9001], managed: []);

        Assert.True(plan.IsNoOp);
        Assert.Empty(plan.NextManaged);
    }

    /// <summary>And it stays that way once the route is gone: an unmanaged binding is never removed.</summary>
    [Fact]
    public void AnOperatorsBindingWithNoRoute_IsLeftAlone() {
        var plan = SelfPortPublishService.ComputePlan(desired: [], bound: [9001], managed: []);

        Assert.True(plan.IsNoOp);
        Assert.Empty(plan.Unpublish);
    }

    [Fact]
    public void AManagedPortWithNoRouteLeft_IsUnpublished() {
        var plan = SelfPortPublishService.ComputePlan(desired: [], bound: [9001], managed: [9001]);

        Assert.Equal([9001], plan.Unpublish);
        Assert.Empty(plan.NextManaged);
    }

    [Fact]
    public void AManagedPortThatIsStillRouted_IsKept() {
        var plan = SelfPortPublishService.ComputePlan(desired: [9001, 9002], bound: [9001], managed: [9001]);

        Assert.Equal([9002], plan.Publish);
        Assert.Empty(plan.Unpublish);
        Assert.Equal([9001, 9002], plan.NextManaged);
    }

    /// <summary>
    /// A claim on a port nothing binds — what a rolled-back recreate or an operator's <c>compose up</c>
    /// leaves behind. It can remove nothing, and it does not survive into the next managed set.
    /// </summary>
    [Fact]
    public void AClaimOnAPortThatIsNotBound_RemovesNothing() {
        var plan = SelfPortPublishService.ComputePlan(desired: [], bound: [], managed: [9001]);

        Assert.True(plan.IsNoOp);
        Assert.Empty(plan.NextManaged);
    }

    /// <summary>The same claim, with the route still there: the port is simply published again.</summary>
    [Fact]
    public void AClaimOnAPortThatDriftedAway_IsRepublished() {
        var plan = SelfPortPublishService.ComputePlan(desired: [9001], bound: [], managed: [9001]);

        Assert.Equal([9001], plan.Publish);
        Assert.Equal([9001], plan.NextManaged);
    }

    // ── Reading the container's bindings ─────────────────────────────────────

    [Fact]
    public void BoundHostPorts_ReadsTheDeclaredBindings() {
        var inspect = InspectJson(published: [9001, 9002]);

        Assert.Equal([9001, 9002], SelfPortPublishService.BoundHostPorts(inspect).Order());
    }

    /// <summary>
    /// An empty host port is Docker's "any free port", which is not an address anything can be reached
    /// at; junk is junk. Neither is worth failing the whole reading over.
    /// </summary>
    [Fact]
    public void BoundHostPorts_DropsEntriesThatNameNoPort() {
        var inspect = InspectJson(bindings: new JsonObject {
            ["8080/tcp"] = new JsonArray(new JsonObject { ["HostPort"] = "" }),
            ["8081/tcp"] = new JsonArray(new JsonObject { ["HostPort"] = "not-a-port" }),
            ["9001/tcp"] = new JsonArray(new JsonObject { ["HostPort"] = "9001" }),
        });

        Assert.Equal([9001], SelfPortPublishService.BoundHostPorts(inspect).Order());
    }

    [Fact]
    public void BoundHostPorts_OfAContainerThatPublishesNothing_IsEmpty() {
        Assert.Empty(SelfPortPublishService.BoundHostPorts(InspectJson()));
        Assert.Empty(SelfPortPublishService.BoundHostPorts([]));
    }

    // ── proxy.applyPortBindings ──────────────────────────────────────────────

    /// <summary>
    /// The plain path: a routed port nothing publishes yet spawns the coordinator with the amendment on
    /// its command line, and claims the port before doing so.
    /// </summary>
    [Fact]
    public async Task ApplyingAPendingPort_SpawnsTheCoordinatorAndClaimsThePort() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting();
        using var host = PortHost(estate);
        var stackId = await host.AddStackAsync("media");
        await host.AddPortRouteAsync(stackId, 9001);

        var result = await ApplyAsync(host);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.True(result.Value.Restarting);
        Assert.Equal([9001], result.Value.Published);
        Assert.Empty(result.Value.Unpublished);

        // Written before the spawn, because after it there is no "after": the coordinator ends this
        // process. See the setting's own documentation for why claiming early is the safe direction.
        Assert.Equal("9001", await ManagedPortsAsync(host));

        // Asserted as the argv the coordinator will actually parse, not as a substring of the JSON: the
        // flags are positional pairs, and "the body mentions 9001 somewhere" would pass just as happily
        // for a command line that publishes nothing.
        var create = await WaitForCoordinatorAsync(estate);
        Assert.Equal("registry.invalid/watchtower:latest", create["Image"]!.GetValue<string>());
        Assert.Equal(
            [
                "--self-update",
                "--container-id", SelfId,
                "--image", "registry.invalid/watchtower:latest",
                "--publish-ports", "9001",
                // Empty rather than absent: the flag and its value are one positional pair, so dropping
                // the value would make the next flag read as this one's argument.
                "--unpublish-ports", "",
            ],
            create["Cmd"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    /// <summary>
    /// The two recreate paths have a mutex and an apply stage each, and neither is the other's — but the
    /// container they would both stop, rename aside and recreate is one container. Two coordinators
    /// racing over it end with the loser acting on a container the winner already renamed, and its stop
    /// is the one step outside the coordinator's rollback.
    /// </summary>
    [Fact]
    public async Task ApplyingWhileASelfUpdateIsRestarting_IsRefused() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting();
        using var host = PortHost(estate);
        var stackId = await host.AddStackAsync("media");
        await host.AddPortRouteAsync(stackId, 9001);
        await SetRuntimeAsync(host, SelfUpdateService.KeyRuntime, new SelfUpdateRuntime {
            ApplyStage = "restarting", CoordinatorId = "coordinator",
        });

        var result = await ApplyAsync(host);

        Assert.False(result.IsSuccess);
        Assert.Contains("self-update is in progress", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(estate.Default.Requests, r => r.Contains("/containers/create"));
        // And it did not claim the ports on its way to being refused.
        Assert.True(string.IsNullOrEmpty(await ManagedPortsAsync(host)));
    }

    /// <summary>The mirror image: a self-update refused while a host-port recreate is on its way.</summary>
    [Fact]
    public async Task SelfUpdatingWhileAPortChangeIsRestarting_IsRefused() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting();
        using var host = PortHost(estate);
        await SetRuntimeAsync(host, SelfPortPublishService.KeyRuntime, new SelfPortPublishRuntime {
            ApplyStage = "restarting", CoordinatorId = "coordinator",
        });
        var selfUpdate = new SelfUpdateService(
            host.Services.GetRequiredService<IServiceScopeFactory>(), estate.Client,
            Options.Create(new WatchtowerOptions()), NullLogger<SelfUpdateService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => selfUpdate.ApplyUpdateAsync(actor: null, Ct));

        Assert.Contains("host-port change is being applied", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(estate.Default.Requests, r => r.Contains("/containers/create"));
    }

    [Fact]
    public async Task ApplyingAPendingPort_RecordsAnAuditRow() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting();
        using var host = PortHost(estate);
        var stackId = await host.AddStackAsync("media");
        await host.AddPortRouteAsync(stackId, 9001);

        Assert.True((await ApplyAsync(host)).IsSuccess);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Action == "ports.apply", Ct);
        Assert.Equal("proxy", row.Category);
        Assert.Equal(SelfPortPublishService.AuditTarget, row.Target);
        Assert.Contains("publish 9001", row.Detail!, StringComparison.Ordinal);
        Assert.True(row.Success);
    }

    /// <summary>
    /// Nothing to do is a success, not a refusal — the container is already in the state the button asks
    /// for, and restarting to prove it would be the wrong answer.
    /// </summary>
    [Fact]
    public async Task ApplyingWhenEveryPortIsPublished_IsAFriendlyNoOp() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting(published: [9001]);
        using var host = PortHost(estate);
        var stackId = await host.AddStackAsync("media");
        await host.AddPortRouteAsync(stackId, 9001);

        var result = await ApplyAsync(host);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.False(result.Value.Restarting);
        Assert.Contains("nothing to apply", result.Value.Message, StringComparison.Ordinal);
        // No recreate, so no coordinator and no audit row about one.
        Assert.DoesNotContain(estate.Default.Requests, r => r.Contains("/containers/create"));
    }

    /// <summary>Outside a container there is nothing to recreate, so the operator is told what to do instead.</summary>
    [Fact]
    public async Task ApplyingOutsideAContainer_IsRefusedWithTheManualInstructions() {
        using var hostname = HostnameEnvironment.Set("");
        using var estate = SelfInspecting();
        using var host = PortHost(estate);
        var stackId = await host.AddStackAsync("media");
        await host.AddPortRouteAsync(stackId, 9001);

        var result = await ApplyAsync(host);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Contains("-p {port}:{port}", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(estate.Default.Requests, r => r.Contains("/containers/create"));
    }

    /// <summary>
    /// A second instance holding the acme-issuer lease is the one cheap proof that this deployment has
    /// more than one container — and each of them has its own, so recreating this one would be half a fix
    /// against a record the other instances share.
    /// </summary>
    [Fact]
    public async Task ApplyingWhileAnotherInstanceHoldsTheLease_IsRefused() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting();
        using var host = PortHost(estate, leaseHeld: false, leaseHolder: "watchtower-2");
        var stackId = await host.AddStackAsync("media");
        await host.AddPortRouteAsync(stackId, 9001);

        var result = await ApplyAsync(host);

        Assert.False(result.IsSuccess);
        Assert.Contains("watchtower-2", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(estate.Default.Requests, r => r.Contains("/containers/create"));
    }

    // ── proxy.getPortBindings ────────────────────────────────────────────────

    [Fact]
    public async Task TheStatus_ReportsWhichRoutedPortsAreBound() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting(published: [9001]);
        using var host = PortHost(estate);
        var stackId = await host.AddStackAsync("media");
        await host.AddPortRouteAsync(stackId, 9001, serviceName: "jellyfin");
        await host.AddPortRouteAsync(stackId, 9002, serviceName: "photos");

        var status = await StatusAsync(host);

        Assert.True(status.ContainerDetected);
        Assert.Null(status.UnavailableReason);
        var bound = Assert.Single(status.Ports, p => p.Port == 9001);
        Assert.True(bound.Bound);
        // Bound but not Watchtower's doing: the operator published it, so it is not Watchtower's to remove.
        Assert.False(bound.Managed);
        Assert.Equal("jellyfin", bound.ServiceName);
        Assert.False(Assert.Single(status.Ports, p => p.Port == 9002).Bound);
    }

    [Fact]
    public async Task TheStatusOutsideAContainer_SaysSoAndReportsNothingBound() {
        using var hostname = HostnameEnvironment.Set("");
        using var estate = SelfInspecting(published: [9001]);
        using var host = PortHost(estate);
        var stackId = await host.AddStackAsync("media");
        await host.AddPortRouteAsync(stackId, 9001);

        var status = await StatusAsync(host);

        Assert.False(status.ContainerDetected);
        Assert.NotNull(status.UnavailableReason);
        Assert.False(Assert.Single(status.Ports).Bound);
    }

    // ── Startup reconcile of the managed set ─────────────────────────────────

    /// <summary>
    /// The self-healing half of claiming a port before the recreate. A claim on a port the container
    /// does not publish is what a rolled-back recreate leaves behind — and what an operator's
    /// <c>docker compose up -d</c> leaves behind, since that rebuilds the container from their file and
    /// drops the port. Releasing the claim is what puts the port back on the page as "not published".
    /// </summary>
    [Fact]
    public async Task Startup_ReleasesClaimsOnPortsTheContainerNoLongerPublishes() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting(published: [9001]);
        using var host = PortHost(estate);
        await SetManagedPortsAsync(host, "9001,9002");

        await host.Services.GetRequiredService<SelfPortPublishService>().StartAsync(Ct);

        Assert.Equal("9001", await ManagedPortsAsync(host));
    }

    /// <summary>
    /// A container that cannot be inspected is not evidence that nothing is published, so nothing is
    /// released — the opposite would silently hand every port Watchtower published to the operator, and
    /// the routes would then never offer to clean them up.
    /// </summary>
    [Fact]
    public async Task Startup_KeepsTheClaimsWhenTheContainerCannotBeInspected() {
        using var hostname = HostnameEnvironment.Set("");
        using var estate = SelfInspecting(published: [9001]);
        using var host = PortHost(estate);
        await SetManagedPortsAsync(host, "9001,9002");

        await host.Services.GetRequiredService<SelfPortPublishService>().StartAsync(Ct);

        Assert.Equal("9001,9002", await ManagedPortsAsync(host));
    }

    /// <summary>A port left published by a deleted route has no row of its own, so the plan reports it.</summary>
    [Fact]
    public async Task TheStatus_ReportsAManagedPortThatNoRouteWantsAnyMore() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting(published: [9001]);
        using var host = PortHost(estate);
        await SetManagedPortsAsync(host, "9001");

        var status = await StatusAsync(host);

        Assert.Empty(status.Ports);
        Assert.Equal([9001], status.PendingUnpublish);
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A host whose <see cref="SelfPortPublishService"/> talks to <paramref name="estate"/> rather than to
    /// a Docker socket, with the acme-issuer lease under the test's control (it is the multi-instance
    /// signal) and the Proxy module's JSON context contributed — the apply state is a typed setting, and
    /// the module bootstrapper that would normally register its shape does not run here.
    /// </summary>
    private static AuthTestHost PortHost(
        DockerClientEstate estate, bool leaseHeld = true, string? leaseHolder = null) =>
        AuthTestHost.Start(services => {
            services.AddApplyPortBindings();
            services.AddGetPortBindings();
            services.ConfigureElarionJson(o =>
                o.TypeInfoResolvers.Add(Modules.Proxy.ProxyModule.GetJsonTypeInfoResolver()));
            var lease = new StubRoleLease(CertificateManager.IssuerRole, leaseHeld, leaseHolder);
            services.RemoveAll<SelfPortPublishService>();
            services.AddSingleton(sp => new SelfPortPublishService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                estate.Client,
                // Its own too: self-detection is the self-update's HOSTNAME → inspect, and pointing it
                // anywhere but the double would have it looking for a real container.
                new SelfUpdateService(
                    sp.GetRequiredService<IServiceScopeFactory>(), estate.Client,
                    Options.Create(new WatchtowerOptions()), NullLogger<SelfUpdateService>.Instance),
                lease,
                Options.Create(new WatchtowerOptions()),
                NullLogger<SelfPortPublishService>.Instance,
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
        });

    /// <summary>
    /// A Docker double whose inspect of <see cref="SelfId"/> answers with a container publishing
    /// <paramref name="published"/> — the one request the canned bodies cannot express, since they
    /// describe a container with no ports at all.
    /// </summary>
    private static DockerClientEstate SelfInspecting(params int[] published) {
        var estate = DockerClientEstate.Create(pruneTimeout: TimeSpan.FromMinutes(30));
        var body = InspectJson(published).ToJsonString();
        estate.Default.Responder = request =>
            request.RequestUri!.AbsolutePath.EndsWith($"/containers/{SelfId}/json", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
                : null;
        return estate;
    }

    /// <summary>An inspect record for this container, publishing the given host ports.</summary>
    private static JsonObject InspectJson(params int[] published) {
        var bindings = new JsonObject();
        foreach (var port in published)
            bindings[$"{port}/tcp"] = new JsonArray(new JsonObject { ["HostPort"] = port.ToString() });
        return InspectJson(bindings);
    }

    private static JsonObject InspectJson(JsonObject bindings) => new() {
        ["Id"] = SelfId,
        ["Name"] = "/watchtower",
        // The root "Image" is the image *id*; the name/tag the coordinator is launched from lives on
        // Config. The typed inspect model requires both, so a body missing either reads as "not a
        // container" and the whole feature politely disappears.
        ["Image"] = "sha256:test",
        ["Config"] = new JsonObject { ["Image"] = "registry.invalid/watchtower:latest" },
        ["HostConfig"] = new JsonObject { ["PortBindings"] = bindings },
        ["State"] = new JsonObject { ["Status"] = "running", ["ExitCode"] = 0 },
    };

    private static async Task<Result<ApplyPortBindings.Response>> ApplyAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IHandler<ApplyPortBindings.Command, Result<ApplyPortBindings.Response>>>()
            .HandleAsync(new ApplyPortBindings.Command(), Ct);
    }

    private static async Task<GetPortBindings.Response> StatusAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<IHandler<GetPortBindings.Query, Result<GetPortBindings.Response>>>()
            .HandleAsync(new GetPortBindings.Query(), Ct);
        Assert.True(result.IsSuccess, Describe(result));
        return result.Value;
    }

    private static async Task SetManagedPortsAsync(AuthTestHost host, string value) {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsManager>().SetStringAsync(
            WatchtowerSettingPaths.ProxyYarpManagedHostPorts, value, SettingsScope.Global,
            expectedVersion: null, Ct);
    }

    private static async Task<string?> ManagedPortsAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISettingsManager>()
            .GetStringAsync(WatchtowerSettingPaths.ProxyYarpManagedHostPorts, SettingsScope.Global, Ct);
    }

    /// <summary>
    /// The coordinator's create body, parsed. Polled rather than awaited: the spawn is the apply mutex's
    /// task, and the handler deliberately answers before it — the coordinator waits three seconds
    /// precisely so the request that asked for the restart can be answered first.
    /// </summary>
    private static async Task<JsonObject> WaitForCoordinatorAsync(DockerClientEstate estate) {
        for (var attempt = 0; attempt < 100; attempt++) {
            var index = estate.Default.Requests.FindIndex(r => r.Contains("/containers/create"));
            if (index >= 0) return JsonNode.Parse(estate.Default.Bodies[index] ?? "{}")!.AsObject();
            await Task.Delay(50, Ct);
        }
        Assert.Fail("The coordinator container was never created.");
        return [];
    }

    /// <summary>Seeds one of the two apply records, for the cross-guard tests.</summary>
    private static async Task SetRuntimeAsync<T>(AuthTestHost host, string key, T value) {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsManager>()
            .SetAsync(key, value, SettingsScope.Global, expectedVersion: null, Ct);
    }

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
