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

    /// <summary>The coordinator a seeded apply record points at; long enough for the refusal's [..12].</summary>
    private const string StuckCoordinatorId = "f00dcafe12345678deadbeef";

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

    /// <summary>
    /// What is claimed <em>while</em> the recreate is in flight is not the same set as what is claimed
    /// after it. The recreate may roll back — the new port is held by another process, the start fails and
    /// the old container comes back still binding the released port — and the startup reconcile only ever
    /// prunes claims, so a port dropped from the claim in advance could never be adopted again.
    /// </summary>
    [Fact]
    public void ThePreSpawnClaim_KeepsThePortsTheRecreateIsAboutToRelease() {
        var plan = SelfPortPublishService.ComputePlan(desired: [9002], bound: [9001], managed: [9001]);

        Assert.Equal([9001], plan.Unpublish);
        Assert.Equal([9002], plan.NextManaged);
        Assert.Equal([9001, 9002], plan.ClaimedThroughTheRecreate);
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

    /// <summary>
    /// The protocol is part of the key, and a port route serves HTTPS. Reading a <c>9001/udp</c> binding
    /// as satisfying a route on 9001 would leave the route permanently unreachable while the page
    /// reported the port as published — and no apply would ever add the TCP binding, because the plan
    /// would see nothing to publish.
    /// </summary>
    [Fact]
    public void BoundHostPorts_IgnoresABindingOnAnotherProtocol() {
        var inspect = InspectJson(bindings: new JsonObject {
            ["9001/udp"] = new JsonArray(new JsonObject { ["HostPort"] = "9001" }),
            ["9002/sctp"] = new JsonArray(new JsonObject { ["HostPort"] = "9002" }),
            // No suffix is tcp — the same reading the daemon gives a bare port number.
            ["9003"] = new JsonArray(new JsonObject { ["HostPort"] = "9003" }),
            ["9004/tcp"] = new JsonArray(new JsonObject { ["HostPort"] = "9004" }),
        });

        Assert.Equal([9003, 9004], SelfPortPublishService.BoundHostPorts(inspect).Order());
    }

    /// <summary>
    /// An entry that is not an object at all would make the string indexer throw, and this reading sits
    /// on the status path the Routes page polls — one unreadable entry costs that entry, not the page.
    /// </summary>
    [Fact]
    public void BoundHostPorts_SkipsAnEntryThatIsNotAnObject() {
        var inspect = InspectJson(bindings: new JsonObject {
            ["9001/tcp"] = new JsonArray("not an object", new JsonObject { ["HostPort"] = "9001" }),
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
    /// The release half, and the claim it leaves behind. The claim is written before the coordinator
    /// exists, so it has to describe a recreate that may never happen: a rollback leaves 9001 bound, and a
    /// claim already dropped could never be picked up again — the startup reconcile only prunes. So the
    /// port stays claimed until it is genuinely gone, at which point <c>managed ∩ bound</c> drops it.
    /// </summary>
    [Fact]
    public async Task ApplyingARelease_KeepsClaimingThePortUntilTheRecreateHasHappened() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting(published: [9001]);
        using var host = PortHost(estate);
        // Watchtower published 9001 for a route that has since been deleted.
        await SetManagedPortsAsync(host, "9001");

        var result = await ApplyAsync(host);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.True(result.Value.Restarting);
        Assert.Equal([9001], result.Value.Unpublished);
        Assert.Equal("9001", await ManagedPortsAsync(host));

        var create = await WaitForCoordinatorAsync(estate);
        Assert.Equal(
            [
                "--self-update",
                "--container-id", SelfId,
                "--image", "registry.invalid/watchtower:latest",
                "--publish-ports", "",
                "--unpublish-ports", "9001",
            ],
            create["Cmd"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    /// <summary>
    /// …and the next start is what ends the claim, once the port really is gone. This is the half that
    /// makes keeping it safe rather than permanent.
    /// </summary>
    [Fact]
    public async Task Startup_AfterTheReleaseLanded_DropsTheClaim() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting();
        using var host = PortHost(estate);
        await SetManagedPortsAsync(host, "9001");

        await host.Services.GetRequiredService<SelfPortPublishService>().StartAsync(Ct);

        Assert.True(string.IsNullOrEmpty(await ManagedPortsAsync(host)));
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
            ApplyStage = "restarting", CoordinatorId = StuckCoordinatorId,
        });

        var result = await ApplyAsync(host);

        Assert.False(result.IsSuccess);
        Assert.Contains("started by the Watchtower self-update", result.Error.Message, StringComparison.Ordinal);
        // Actionable rather than merely true: a coordinator that never exits blocks both paths until
        // somebody removes it, so the refusal has to say which container and what to do about it.
        Assert.Contains($"coordinator {StuckCoordinatorId[..12]}", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("remove that container and restart Watchtower", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(estate.Default.Requests, r => r.Contains("/containers/create"));
        // And it did not claim the ports on its way to being refused.
        Assert.True(string.IsNullOrEmpty(await ManagedPortsAsync(host)));
    }

    /// <summary>
    /// A port another container already publishes cannot be published here as well. Refused before the
    /// stage claim, because the alternative is the coordinator recreating this container, the start
    /// failing on the bind, the rollback putting the old container back, and the operator being told
    /// only that the port is "not published" — with nothing naming what holds it.
    /// </summary>
    [Fact]
    public async Task ApplyingAPortAnotherContainerHolds_IsRefusedBeforeAnythingIsClaimed() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting();
        estate.ListsContainers(new ListedContainer(
            "9e" + new string('0', 30), "media-jellyfin-1", PublicPort: 9001,
            Project: "media", Service: "jellyfin"));
        using var host = PortHost(estate);
        var stackId = await host.AddStackAsync("media");
        await host.AddPortRouteAsync(stackId, 9001);

        var result = await ApplyAsync(host);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "Host port 9001 is already published by container media-jellyfin-1 (stack media, service "
            + "jellyfin). A port route needs that port for Watchtower's own listener — remove that "
            + "ports: entry from the stack or choose another port.",
            result.Error.Message);
        // Nothing was started, nothing was claimed, and the stage the other recreate path reads is
        // untouched — a refusal that left "restarting" behind would block that path for this process's life.
        Assert.DoesNotContain(estate.Default.Requests, r => r.Contains("/containers/create"));
        Assert.Equal("idle", (await RuntimeAsync(host)).ApplyStage);
        Assert.True(string.IsNullOrEmpty(await ManagedPortsAsync(host)));
    }

    /// <summary>
    /// And the page says so per row before anyone presses the button, which is where an operator is
    /// actually looking when a port route does not answer.
    /// </summary>
    [Fact]
    public async Task TheStatus_NamesTheContainerHoldingAnUnpublishedPort() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting();
        estate.ListsContainers(new ListedContainer(
            "9e" + new string('0', 30), "media-jellyfin-1", PublicPort: 9001,
            Project: "media", Service: "jellyfin"));
        using var host = PortHost(estate);
        var stackId = await host.AddStackAsync("media");
        await host.AddPortRouteAsync(stackId, 9001);

        var status = await StatusAsync(host);

        var row = Assert.Single(status.Ports);
        Assert.False(row.Bound);
        Assert.NotNull(row.BlockedBy);
        Assert.Contains("media-jellyfin-1", row.BlockedBy, StringComparison.Ordinal);
    }

    /// <summary>
    /// A port that is already published here is not blocked by anything, and asking Docker about it
    /// would be a container list per poll for a question with no consequence.
    /// </summary>
    [Fact]
    public async Task TheStatusOfAPublishedPort_ReportsNoBlockerAndAsksNothing() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting(published: [9001]);
        using var host = PortHost(estate);
        var stackId = await host.AddStackAsync("media");
        await host.AddPortRouteAsync(stackId, 9001);

        var status = await StatusAsync(host);

        var row = Assert.Single(status.Ports);
        Assert.True(row.Bound);
        Assert.Null(row.BlockedBy);
        Assert.DoesNotContain(estate.Default.Requests, r => r.Contains("/containers/json"));
    }

    /// <summary>
    /// The guard is fail-open: a daemon that cannot answer the container list refuses nothing, so an
    /// apply on a bare-process install — or through a hiccup — goes ahead as it did before.
    /// </summary>
    [Fact]
    public async Task ApplyingWhenTheContainerListCannotBeRead_GoesAhead() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting();
        estate.FailsTheContainerList();
        using var host = PortHost(estate);
        var stackId = await host.AddStackAsync("media");
        await host.AddPortRouteAsync(stackId, 9001);

        var result = await ApplyAsync(host);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.True(result.Value.Restarting);
        await WaitForCoordinatorAsync(estate);
    }

    /// <summary>
    /// The stage the other path's guard reads has to be true by the time the accepted call returns.
    /// Writing it from inside the fire-and-forget task instead would publish it only after that task's
    /// first await — a window in which a second apply reads "idle", passes the guard, and spawns the
    /// second coordinator this guard exists to prevent.
    /// </summary>
    /// <remarks>
    /// The background task is held at its first instruction rather than merely being slower than the
    /// assertion. That distinction is the test: against an in-memory Docker double the task runs to
    /// completion in well under the time the accepted call spends writing its audit row, so a version
    /// that published the stage from inside the task would satisfy an unheld assertion every time while
    /// being exactly as wrong. Held, it cannot have contributed anything that is read below.
    /// </remarks>
    [Fact]
    public async Task AnAcceptedApply_HasPublishedItsStageBeforeItReturns() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var gate = new SemaphoreSlim(0, 1);
        using var estate = SelfInspecting();
        using var host = PortHost(estate, beforeSpawn: ct => gate.WaitAsync(ct));
        var service = host.Services.GetRequiredService<SelfPortPublishService>();
        var stackId = await host.AddStackAsync("media");
        await host.AddPortRouteAsync(stackId, 9001);

        try {
            Assert.True((await ApplyAsync(host)).IsSuccess);

            // Read straight from the store, so what is asserted is what another instance — or the other
            // path's guard, which goes through the same store — would see at this instant.
            await using var scope = host.Services.CreateAsyncScope();
            var runtime = await scope.ServiceProvider.GetRequiredService<ISettingsManager>().GetAsync(
                SelfPortPublishService.KeyRuntime, new SelfPortPublishRuntime(), SettingsScope.Global, Ct);
            Assert.Equal("restarting", runtime.ApplyStage);
        } finally {
            // Cancels the held task rather than releasing it: letting it run on would have it writing
            // through a host this test is about to dispose.
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The race the single pre-flight read could not close. Between reading <c>self.runtime</c> and
    /// writing its own stage this path does a Docker inspect and several database round trips, and the
    /// self-update reads <em>this</em> record somewhere in that window — so both could read "idle" and
    /// both could spawn a coordinator over one container id, which is the failure the cross-guard exists
    /// to prevent.
    /// </summary>
    /// <remarks>
    /// The other record is seeded from the seam that runs between this path's stage write and its verify
    /// read — after the pre-flight read has already passed. That is not a convenience: two applies cannot
    /// be made to interleave on demand, and a test that seeded beforehand would be exercising the cheap
    /// refusal it is trying to prove is insufficient.
    /// </remarks>
    [Fact]
    public async Task ASelfUpdateThatClaimsDuringTheWindow_MakesThisApplyStandDown() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting();
        // Indirected through a field the host does not need at construction time, so the seam can write
        // through the very host it belongs to.
        Func<CancellationToken, Task> inTheWindow = _ => Task.CompletedTask;
        using var host = PortHost(estate, beforeVerify: ct => inTheWindow(ct));
        inTheWindow = _ => SetRuntimeAsync(host, SelfUpdateService.KeyRuntime, new SelfUpdateRuntime {
            ApplyStage = "restarting", CoordinatorId = StuckCoordinatorId,
        });
        var stackId = await host.AddStackAsync("media");
        await host.AddPortRouteAsync(stackId, 9001);

        var result = await ApplyAsync(host);

        Assert.False(result.IsSuccess);
        Assert.Contains("started by the Watchtower self-update", result.Error.Message, StringComparison.Ordinal);
        // Its own stage is back to idle: nothing failed, and leaving it at "restarting" would block the
        // path that won for as long as this process lives.
        var runtime = await RuntimeAsync(host);
        Assert.Equal("idle", runtime.ApplyStage);
        Assert.Null(runtime.ApplyError);
        // Nothing was spawned, and nothing was claimed on the way out.
        Assert.DoesNotContain(estate.Default.Requests, r => r.Contains("/containers/create"));
        Assert.True(string.IsNullOrEmpty(await ManagedPortsAsync(host)));
    }

    /// <summary>
    /// Standing down must not double as clearing the record. The claim overwrites the apply stage and its
    /// error, so a revert that wrote a flat "idle" would resolve a previous apply's recorded failure —
    /// losing the only account of why the ports are not published, on a call that itself did nothing.
    /// </summary>
    [Fact]
    public async Task StandingDownFromALostRace_LeavesAnEarlierErrorWhereItWas() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting();
        Func<CancellationToken, Task> inTheWindow = _ => Task.CompletedTask;
        using var host = PortHost(estate, beforeVerify: ct => inTheWindow(ct));
        inTheWindow = _ => SetRuntimeAsync(host, SelfUpdateService.KeyRuntime, new SelfUpdateRuntime {
            ApplyStage = "restarting", CoordinatorId = StuckCoordinatorId,
        });
        var stackId = await host.AddStackAsync("media");
        await host.AddPortRouteAsync(stackId, 9001);
        await SetRuntimeAsync(host, SelfPortPublishService.KeyRuntime, new SelfPortPublishRuntime {
            ApplyStage = "error", ApplyError = "Publishing the host ports failed (exit 1)",
        });

        Assert.False((await ApplyAsync(host)).IsSuccess);

        var runtime = await RuntimeAsync(host);
        Assert.Equal("error", runtime.ApplyStage);
        Assert.Equal("Publishing the host ports failed (exit 1)", runtime.ApplyError);
    }

    /// <summary>The mirror image: a self-update refused while a host-port recreate is on its way.</summary>
    [Fact]
    public async Task SelfUpdatingWhileAPortChangeIsRestarting_IsRefused() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting();
        using var host = PortHost(estate);
        await SetRuntimeAsync(host, SelfPortPublishService.KeyRuntime, new SelfPortPublishRuntime {
            ApplyStage = "restarting", CoordinatorId = StuckCoordinatorId,
        });
        var selfUpdate = new SelfUpdateService(
            host.Services.GetRequiredService<IServiceScopeFactory>(), estate.Client,
            Options.Create(new WatchtowerOptions()), NullLogger<SelfUpdateService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => selfUpdate.ApplyUpdateAsync(actor: null, Ct));

        Assert.Contains("started by the host-port change", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"coordinator {StuckCoordinatorId[..12]}", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(estate.Default.Requests, r => r.Contains("/containers/create"));
    }

    /// <summary>
    /// And the mirror of the race, so the guard is closed from both sides rather than only from the one
    /// the port work happened to be written on.
    /// </summary>
    [Fact]
    public async Task APortChangeThatClaimsDuringTheWindow_MakesTheSelfUpdateStandDown() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting();
        using var host = PortHost(estate);
        var selfUpdate = new SelfUpdateService(
            host.Services.GetRequiredService<IServiceScopeFactory>(), estate.Client,
            Options.Create(new WatchtowerOptions()), NullLogger<SelfUpdateService>.Instance,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            beforeVerify: _ => SetRuntimeAsync(host, SelfPortPublishService.KeyRuntime,
                new SelfPortPublishRuntime { ApplyStage = "restarting", CoordinatorId = StuckCoordinatorId }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => selfUpdate.ApplyUpdateAsync(actor: null, Ct));

        Assert.Contains("started by the host-port change", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(estate.Default.Requests, r => r.Contains("/containers/create"));
        // Its own record is back where it was — here that is the default, so idle with no error. The pull
        // never started, and a stage left at "pulling" would block the winner.
        Assert.Equal("idle", (await UpdateRuntimeAsync(host)).ApplyStage);
    }

    /// <summary>
    /// And the same on this side: the claim overwrites the apply stage and its error, so standing down
    /// has to put both back rather than write a blank idle over a previous update's recorded failure.
    /// </summary>
    [Fact]
    public async Task ASelfUpdateStandingDown_LeavesAnEarlierErrorWhereItWas() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting();
        using var host = PortHost(estate);
        await SetRuntimeAsync(host, SelfUpdateService.KeyRuntime, new SelfUpdateRuntime {
            ApplyStage = "error", ApplyError = "Coordinator failed (exit 1)",
        });
        var selfUpdate = new SelfUpdateService(
            host.Services.GetRequiredService<IServiceScopeFactory>(), estate.Client,
            Options.Create(new WatchtowerOptions()), NullLogger<SelfUpdateService>.Instance,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            beforeVerify: _ => SetRuntimeAsync(host, SelfPortPublishService.KeyRuntime,
                new SelfPortPublishRuntime { ApplyStage = "restarting", CoordinatorId = StuckCoordinatorId }));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => selfUpdate.ApplyUpdateAsync(actor: null, Ct));

        var runtime = await UpdateRuntimeAsync(host);
        Assert.Equal("error", runtime.ApplyStage);
        Assert.Equal("Coordinator failed (exit 1)", runtime.ApplyError);
    }

    /// <summary>The self-update's apply record, read the way another instance would read it.</summary>
    private static async Task<SelfUpdateRuntime> UpdateRuntimeAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISettingsManager>().GetAsync(
            SelfUpdateService.KeyRuntime, new SelfUpdateRuntime(), SettingsScope.Global, Ct);
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

    /// <summary>
    /// And a no-op apply is what finally clears an error a failed one recorded. Without this the message
    /// is permanent: the operator publishes the port by hand, the plan has nothing left to do, and this
    /// branch returns before any stage is written — so the Routes page keeps reporting a failure about a
    /// world that no longer exists.
    /// </summary>
    [Fact]
    public async Task ANoOpApply_ClearsTheErrorAPreviousOneRecorded() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting(published: [9001]);
        using var host = PortHost(estate);
        var stackId = await host.AddStackAsync("media");
        await host.AddPortRouteAsync(stackId, 9001);
        await SetRuntimeAsync(host, SelfPortPublishService.KeyRuntime, new SelfPortPublishRuntime {
            ApplyStage = "error", ApplyError = "Publishing the host ports failed (exit 1)",
        });

        var result = await ApplyAsync(host);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.False(result.Value.Restarting);
        var runtime = await RuntimeAsync(host);
        Assert.Equal("idle", runtime.ApplyStage);
        Assert.Null(runtime.ApplyError);
    }

    /// <summary>
    /// The same clear on the way in, because the button that would trigger a no-op apply is not offered
    /// once every routed port is bound — so without this, an operator who fixed the ports by hand would
    /// read the old failure until the settings row was edited.
    /// </summary>
    [Fact]
    public async Task Startup_ClearsAnApplyErrorEveryRoutedPortHasOutlived() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting(published: [9001]);
        using var host = PortHost(estate);
        var stackId = await host.AddStackAsync("media");
        await host.AddPortRouteAsync(stackId, 9001);
        await SetRuntimeAsync(host, SelfPortPublishService.KeyRuntime, new SelfPortPublishRuntime {
            ApplyStage = "error", ApplyError = "Publishing the host ports failed (exit 1)",
        });

        await host.Services.GetRequiredService<SelfPortPublishService>().StartAsync(Ct);

        var runtime = await RuntimeAsync(host);
        Assert.Equal("idle", runtime.ApplyStage);
        Assert.Null(runtime.ApplyError);
    }

    /// <summary>
    /// The restart <em>after</em> the one that reconciled a failed release. By then the stage says error
    /// and the coordinator id has been cleared, so nothing marks this start as the aftermath of anything
    /// — but the release is still pending: the port is bound, still claimed, and no route wants it. Read
    /// as "every routed port is published" that looks resolved; read as "an apply would have nothing left
    /// to do" it plainly is not.
    /// </summary>
    [Fact]
    public async Task Startup_KeepsAnApplyErrorWhileAReleaseIsStillPending() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting(published: [9001]);
        using var host = PortHost(estate);
        // The route that wanted 9001 is gone, the release failed and rolled back, and the claim survived
        // it — which is exactly what makes the port releasable on the next attempt.
        await SetManagedPortsAsync(host, "9001");
        await SetRuntimeAsync(host, SelfPortPublishService.KeyRuntime, new SelfPortPublishRuntime {
            ApplyStage = "error", ApplyError = "Publishing the host ports failed (exit 1)",
        });

        await host.Services.GetRequiredService<SelfPortPublishService>().StartAsync(Ct);

        var runtime = await RuntimeAsync(host);
        Assert.Equal("error", runtime.ApplyStage);
        Assert.NotNull(runtime.ApplyError);
        // And the claim is still there, so the page still offers the release.
        Assert.Equal("9001", await ManagedPortsAsync(host));
    }

    /// <summary>And it stays where a routed port is still unpublished — the failure has not been resolved.</summary>
    [Fact]
    public async Task Startup_KeepsAnApplyErrorWhileAPortIsStillMissing() {
        using var hostname = HostnameEnvironment.Set(SelfId);
        using var estate = SelfInspecting();
        using var host = PortHost(estate);
        var stackId = await host.AddStackAsync("media");
        await host.AddPortRouteAsync(stackId, 9001);
        await SetRuntimeAsync(host, SelfPortPublishService.KeyRuntime, new SelfPortPublishRuntime {
            ApplyStage = "error", ApplyError = "Publishing the host ports failed (exit 1)",
        });

        await host.Services.GetRequiredService<SelfPortPublishService>().StartAsync(Ct);

        Assert.Equal("error", (await RuntimeAsync(host)).ApplyStage);
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
        DockerClientEstate estate, bool leaseHeld = true, string? leaseHolder = null,
        Func<CancellationToken, Task>? beforeSpawn = null,
        Func<CancellationToken, Task>? beforeVerify = null) =>
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
                new HostPortOccupancy(estate.Client, NullLogger<HostPortOccupancy>.Instance),
                lease,
                Options.Create(new WatchtowerOptions()),
                NullLogger<SelfPortPublishService>.Instance,
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
                beforeSpawn, beforeVerify));
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

    /// <summary>This service's apply record, read the way another instance would read it.</summary>
    private static async Task<SelfPortPublishRuntime> RuntimeAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISettingsManager>().GetAsync(
            SelfPortPublishService.KeyRuntime, new SelfPortPublishRuntime(), SettingsScope.Global, Ct);
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
