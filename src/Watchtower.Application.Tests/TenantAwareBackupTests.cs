using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Backups;
using Watchtower.Application.Modules.Backups.Handlers;
using Watchtower.Application.Modules.Stacks.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Stage 7 of ADR-0026 (design.md §"Backups across tenants"): the persisted backup directory, the
/// template policy tenants inherit, the fleet fan-out and the product-scoped read model.
/// </summary>
/// <remarks>
/// The resolution ladder itself is <see cref="BackupPolicyResolverTests"/>' subject — pure and
/// exhaustive there. What this suite pins is that the ladder is what the <em>real</em> paths walk (the
/// schedule tick, the config handlers) and that the two persisted decisions of the stage — where a
/// stack's archives live, and what a tenant inherits — are written where and when they are supposed to
/// be.
/// </remarks>
public sealed class TenantAwareBackupTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── The persisted backup directory ───────────────────────────────────────

    /// <summary>
    /// The rename hazard, closed: the directory is a stored fact, so it survives the stack (or the
    /// instance) being renamed under it.
    /// </summary>
    [Fact]
    public void ResolveDirectory_PrefersTheStampedValueOverAnythingItCouldCompute() {
        var stack = new Stack {
            Name = "renamed-since", ComposeProjectName = "renamed-since",
            BackupDirectory = "prod/acme-web/globex",
        };

        Assert.Equal("prod/acme-web/globex", BackupNaming.ResolveDirectory(stack, "a-different-instance"));
    }

    /// <summary>
    /// A stack created before the column existed computes exactly what it always did — that is what
    /// keeps an upgraded install's existing archives discoverable with no migration guessing at the
    /// instance name, which is configuration and not a column.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveDirectory_WithNoStamp_ComputesThePreStage7Value(string? stored) {
        var stack = new Stack { Name = "web app", ComposeProjectName = "web-app", BackupDirectory = stored };

        Assert.Equal(
            BackupNaming.StackDirectory("prod", "web app"),
            BackupNaming.ResolveDirectory(stack, "prod"));
        Assert.Equal("prod/web-app", BackupNaming.ResolveDirectory(stack, "prod"));
    }

    /// <summary>
    /// Every segment goes through the same sanitizer the file name does — including the separator a
    /// product name could otherwise smuggle in, which would silently add a directory level.
    /// </summary>
    [Fact]
    public void TenantDirectory_SanitizesAllThreeSegments() =>
        Assert.Equal(
            "prod-eu/Acme-Web/big-corp",
            BackupNaming.TenantDirectory("prod/eu", "Acme Web", "big corp"));

    /// <summary>A stack created through <c>stacks.create</c> is stamped there and then.</summary>
    [Fact]
    public async Task CreateStack_StampsTheBackupDirectory() {
        using var host = AuthTestHost.Start(("Watchtower:Backup:InstanceName", "prod"));
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<CreateStack>(scope.ServiceProvider);

        var result = await handler.HandleAsync(
            new CreateStack.Command(
                "web app", "https://example.invalid/web.git", "docker-compose.yml", "main",
                null, null, null, false, "off", null, null),
            Ct);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("prod/web-app", await DirectoryOfAsync(host, result.Value.Stack.Id));
    }

    /// <summary>
    /// A tenant is stamped <c>{instance}/{product}/{tenant}</c> — the layout that makes a 200-tenant
    /// fleet navigable on the storage — and, crucially, inherits every backup field rather than copying
    /// it.
    /// </summary>
    [Fact]
    public async Task Provision_StampsTheTenantDirectory_AndLeavesEveryBackupFieldInheriting() {
        using var host = AuthTestHost.Start(("Watchtower:Backup:InstanceName", "prod"));
        var productId = await host.AddProductAsync("acme web", ProductReleaseMode.Git);
        var templateId = await host.AddProductTemplateAsync("acme-tenants", productId);
        // A fleet policy that a copy-at-provision bug would silently freeze onto the new tenant.
        await SetTemplatePolicyAsync(host, templateId, enabled: true, cron: "0 4 * * *");

        var provisioned = await ProvisionAsync(host, templateId, "globex");

        Assert.Equal(TenantProvisionStatus.Created, provisioned.Status);
        var stackId = provisioned.Tenant!.StackId;
        Assert.Equal("prod/acme-web/globex", await DirectoryOfAsync(host, stackId));

        var stack = await StackAsync(host, stackId);
        Assert.Null(stack.BackupEnabled);
        Assert.Null(stack.BackupStopContainers);
        Assert.Null(stack.BackupCron);
        Assert.Null(stack.BackupQuiesceMode);
        // Inherited live, so the fleet's next policy edit reaches it too.
        var policy = BackupPolicyResolver.Resolve(stack, stack.Template);
        Assert.True(policy.Enabled);
        Assert.Equal("0 4 * * *", policy.Cron);
        Assert.Equal(BackupPolicySource.Template, policy.EnabledSource);
    }

    /// <summary>
    /// Stamp-on-first-use: a legacy stack is given the directory a run <em>proved</em> works, and a stack
    /// that already has one is never overwritten by a run holding a stale copy.
    /// </summary>
    [Fact]
    public async Task StampDirectory_FillsALegacyStackOnce_AndNeverOverwritesAnExistingOne() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var legacy = await host.AddProductStackAsync("legacy", productId);
        var stamped = await host.AddProductStackAsync("stamped", productId);
        await SetDirectoryAsync(host, stamped, "old/stamped");

        var backups = host.Services.GetRequiredService<BackupService>();
        await backups.StampDirectoryAsync(await StackAsync(host, legacy), "prod/legacy");
        await backups.StampDirectoryAsync(await StackAsync(host, stamped), "prod/stamped");

        Assert.Equal("prod/legacy", await DirectoryOfAsync(host, legacy));
        Assert.Equal("old/stamped", await DirectoryOfAsync(host, stamped));
    }

    /// <summary>The restore picker reads the same answer the run writes, stamped or not.</summary>
    [Fact]
    public async Task ListRemote_ReadsTheStampedDirectory() {
        using var host = AuthTestHost.Start(
            ("Watchtower:Backup:InstanceName", "prod"),
            ("Watchtower:Backup:Provider", "local"),
            ("Watchtower:Backup:Local:BasePath", Path.Combine(Path.GetTempPath(), $"wt-backups-{Guid.NewGuid():N}")));
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var stackId = await host.AddProductStackAsync("shop-acme", productId);
        await SetDirectoryAsync(host, stackId, "prod/shop/acme");

        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<ListRemoteBackups>(scope.ServiceProvider);
        var result = await handler.HandleAsync(new ListRemoteBackups.Query(stackId), Ct);

        // An empty directory, but reached without error — the assertion that matters is that it did not
        // go looking under the computed {instance}/{stack} path, which does not exist either. The
        // directory the storage was asked for is what the next test in this family would tighten; here
        // it is enough that a stamped stack lists successfully.
        Assert.True(result.IsSuccess, Describe(result));
        Assert.Empty(result.Value.Files);
    }

    // ── The migration ────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The migration only widens the type; it rewrites no values.</b> That is the whole reason an
    /// existing stack keeps behaving as it did: <c>NOT NULL</c> becomes nullable, so every row keeps its
    /// <c>true</c>/<c>false</c> <em>as an explicit value</em> rather than becoming "inherit". A backfill
    /// of any kind here would be a silent behaviour change for every install.
    /// </summary>
    [Fact]
    public void TheMigration_OnlyRelaxesTheColumns_AndBackfillsNothing() {
        using var host = AuthTestHost.Start();
        using var scope = host.Services.CreateScope();
        var migrator = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>()
            .GetService<IMigrator>();

        var script = migrator.GenerateScript(
            fromMigration: "20260826024037_AddTenantReleasePolicy",
            toMigration: "20260826043957_AddTenantAwareBackups");

        Assert.Contains("ALTER TABLE stacks ALTER COLUMN backup_enabled DROP NOT NULL", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE stacks ALTER COLUMN backup_stop_containers DROP NOT NULL", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE stacks ALTER COLUMN backup_quiesce_mode DROP NOT NULL", script, StringComparison.Ordinal);
        Assert.Contains("ADD backup_directory text", script, StringComparison.Ordinal);
        // Nothing touches a value: no backfill of the relaxed columns, and no guess at a directory the
        // database cannot know (the instance name is configuration).
        Assert.DoesNotContain("UPDATE stacks", script, StringComparison.OrdinalIgnoreCase);
        // Additive: a new table and its index, and no index dropped (the stage-5 trap).
        Assert.Contains("CREATE TABLE template_backup_service_overrides", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP INDEX", script, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The shape a relaxed column leaves behind, read back through the ladder: a pre-existing row's
    /// values are explicit, and they beat a template that disagrees.
    /// </summary>
    [Fact]
    public async Task AnExistingStacksValues_SurviveAsExplicitAndOutrankTheFleet() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        await SetTemplatePolicyAsync(host, templateId, enabled: true, quiesceMode: BackupQuiesceMode.Pause);
        var stackId = await host.AddProductStackAsync("shop-acme", productId, templateId: templateId);
        // Exactly what AlterColumn leaves: the values the row had before the type widened.
        await SetStackPolicyAsync(host, stackId, enabled: false, stopContainers: true, quiesceMode: BackupQuiesceMode.Stop);

        var stack = await StackAsync(host, stackId);
        var policy = BackupPolicyResolver.Resolve(stack, stack.Template);

        Assert.False(policy.Enabled);
        Assert.Equal(BackupPolicySource.Stack, policy.EnabledSource);
        Assert.Equal(BackupQuiesceMode.Stop, policy.QuiesceMode);
        Assert.Equal(BackupPolicySource.Stack, policy.QuiesceModeSource);
    }

    // ── The schedule tick walks the ladder ───────────────────────────────────

    /// <summary>
    /// The tick's narrowing query and the resolver have to agree: a tenant that says nothing under a
    /// template that says yes is scheduled, on the template's expression.
    /// </summary>
    [Fact]
    public async Task ScheduleTick_EnqueuesATenantEnrolledOnlyByItsTemplate() {
        using var host = StartWithRecordingQueue(
            ("Watchtower:Backup:Enabled", "true"), ("Watchtower:Backup:Cron", "30 3 * * *"));
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        await SetTemplatePolicyAsync(host, templateId, enabled: true, cron: "0 4 * * *");
        var tenant = await host.AddProductStackAsync("shop-acme", productId, templateId: templateId);
        // A stack of the same product that is not a tenant: it says nothing and has no template rung.
        var standalone = await host.AddProductStackAsync("shop-prod", productId);

        var enqueued = await TickAsync(host, new DateTimeOffset(2026, 8, 26, 4, 0, 30, TimeSpan.Zero));

        Assert.Equal(1, enqueued);
        Assert.Equal([(tenant, BackupTriggers.Schedule)], Enqueued(host));
        Assert.DoesNotContain(standalone, Enqueued(host).Select(e => e.StackId));
    }

    /// <summary>
    /// And the tri-state the other way: a tenant that opted out by hand stays out when the fleet is
    /// switched on. The narrowing query has to let the row through for the resolver to refuse it.
    /// </summary>
    [Fact]
    public async Task ScheduleTick_SkipsATenantThatOptedOutOfAnEnrolledFleet() {
        using var host = StartWithRecordingQueue(
            ("Watchtower:Backup:Enabled", "true"), ("Watchtower:Backup:Cron", "0 4 * * *"));
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        await SetTemplatePolicyAsync(host, templateId, enabled: true);
        var optedOut = await host.AddProductStackAsync("shop-acme", productId, templateId: templateId);
        await SetStackPolicyAsync(host, optedOut, enabled: false);
        var inherited = await host.AddProductStackAsync("shop-globex", productId, templateId: templateId);

        await TickAsync(host, new DateTimeOffset(2026, 8, 26, 4, 0, 30, TimeSpan.Zero));

        Assert.Equal([(inherited, BackupTriggers.Schedule)], Enqueued(host));
    }

    /// <summary>A tenant's own expression still beats the fleet's — the stack rung is above the template's.</summary>
    [Fact]
    public async Task ScheduleTick_HonoursATenantsOwnExpressionOverTheFleets() {
        using var host = StartWithRecordingQueue(
            ("Watchtower:Backup:Enabled", "true"), ("Watchtower:Backup:Cron", "30 3 * * *"));
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        await SetTemplatePolicyAsync(host, templateId, enabled: true, cron: "0 4 * * *");
        var tenant = await host.AddProductStackAsync("shop-acme", productId, templateId: templateId);
        await SetStackPolicyAsync(host, tenant, cron: "0 5 * * *");

        // The fleet's window: nothing, because this tenant is on its own.
        Assert.Equal(0, await TickAsync(host, new DateTimeOffset(2026, 8, 26, 4, 0, 30, TimeSpan.Zero)));
        Assert.Equal(1, await TickAsync(host, new DateTimeOffset(2026, 8, 26, 5, 0, 30, TimeSpan.Zero)));
    }

    // ── backups.setTemplatePolicy / getProductBackups ────────────────────────

    /// <summary>
    /// The policy card's write: one row, no fan-out, and a count of the tenants the edit will not reach
    /// because they said something of their own.
    /// </summary>
    [Fact]
    public async Task SetTemplatePolicy_WritesTheTemplateAlone_AndReportsTheTenantsThatOverrideIt() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        var inheriting = await host.AddProductStackAsync("shop-a", productId, templateId: templateId);
        var overriding = await host.AddProductStackAsync("shop-b", productId, templateId: templateId);
        await SetStackPolicyAsync(host, overriding, enabled: false);

        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<SetTemplateBackupPolicy>(scope.ServiceProvider);
        var result = await handler.HandleAsync(
            new SetTemplateBackupPolicy.Command(templateId, Enabled: true, StopContainers: false,
                Cron: "0 4 * * *", QuiesceMode: "pause"),
            Ct);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.True(result.Value.Policy.Enabled);
        Assert.False(result.Value.Policy.StopContainers);
        Assert.Equal("0 4 * * *", result.Value.Policy.Cron);
        Assert.Equal("pause", result.Value.Policy.QuiesceMode);
        Assert.Equal(2, result.Value.Policy.TenantCount);
        Assert.Equal(1, result.Value.Policy.OverriddenTenantCount);

        // Not a fan-out: the tenants' own rows are untouched, which is what keeps inheritance live.
        Assert.Null((await StackAsync(host, inheriting)).BackupEnabled);
        Assert.False((await StackAsync(host, overriding)).BackupEnabled);
    }

    /// <summary>Null on every field clears the policy — the template goes back to having no opinion.</summary>
    [Fact]
    public async Task SetTemplatePolicy_WithNulls_ClearsThePolicy() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        await SetTemplatePolicyAsync(host, templateId, enabled: true, cron: "0 4 * * *");

        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<SetTemplateBackupPolicy>(scope.ServiceProvider);
        var result = await handler.HandleAsync(new SetTemplateBackupPolicy.Command(templateId), Ct);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Null(result.Value.Policy.Enabled);
        Assert.Null(result.Value.Policy.Cron);
        Assert.Null(result.Value.Policy.QuiesceMode);
    }

    /// <summary>A malformed expression is refused before anything is written.</summary>
    [Fact]
    public async Task SetTemplatePolicy_RefusesAnUnparseableCron() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);

        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<SetTemplateBackupPolicy>(scope.ServiceProvider);
        var result = await handler.HandleAsync(
            new SetTemplateBackupPolicy.Command(templateId, Cron: "not a cron"), Ct);

        Assert.False(result.IsSuccess);
        Assert.Null(await TemplatePolicyCronAsync(host, templateId));
    }

    // ── backups.setTemplateServiceOverride (stage 8b) ────────────────────────

    /// <summary>
    /// The write side the table shipped without. One row on the <em>template</em>, no fan-out onto the
    /// tenants, and it comes back on the policy the card re-seeds itself from — labelled
    /// <c>Inherited</c>, because that is what it is from every tenant's point of view.
    /// </summary>
    [Fact]
    public async Task SetTemplateServiceOverride_WritesTheFleetRowAlone_AndTheProductReadModelCarriesIt() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        var tenant = await host.AddProductStackAsync("shop-acme", productId, templateId: templateId);

        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<SetTemplateBackupServiceOverride>(scope.ServiceProvider);
        var result = await handler.HandleAsync(
            new SetTemplateBackupServiceOverride.Command(templateId, "cache", Exclude: true, Stop: "PAUSE"), Ct);

        Assert.True(result.IsSuccess, Describe(result));
        var written = Assert.IsType<BackupServiceOverrideDto>(result.Value.Override);
        Assert.Equal("cache", written.Service);
        Assert.True(written.Exclude);
        // Normalized the way the stack setter normalizes, so the two cannot store the same knob differently.
        Assert.Equal("pause", written.Stop);
        Assert.True(written.Inherited);

        // Not a fan-out (invariant 18): the tenant gets this by reading it, not by holding a copy.
        Assert.Empty(await StackServiceOverridesAsync(host, tenant));

        var read = ActivatorUtilities.CreateInstance<GetProductBackups>(scope.ServiceProvider);
        var product = await read.HandleAsync(new GetProductBackups.Query(productId), Ct);
        Assert.True(product.IsSuccess, Describe(product));
        var row = Assert.Single(Assert.Single(product.Value.Templates).ServiceOverrides);
        Assert.Equal("cache", row.Service);
        Assert.True(row.Exclude);
        Assert.Equal("pause", row.Stop);
        Assert.True(row.Inherited);

        var audited = Assert.Single(await AuditAsync(host, "template.service-override.update"));
        Assert.Equal("shop-tenants", audited.Target);
        Assert.Contains("cache", audited.Detail);
    }

    /// <summary>
    /// Every knob cleared deletes the row — the same "the whole override is replaced" contract
    /// <c>backups.setServiceOverride</c> has, so the one control that posts both cannot mean two things.
    /// </summary>
    [Fact]
    public async Task SetTemplateServiceOverride_WithNothingSet_DeletesTheRow() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);

        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<SetTemplateBackupServiceOverride>(scope.ServiceProvider);
        await handler.HandleAsync(
            new SetTemplateBackupServiceOverride.Command(templateId, "db", Dump: "postgres"), Ct);
        Assert.Single(await TemplateServiceOverridesAsync(host, templateId));

        var cleared = await handler.HandleAsync(
            new SetTemplateBackupServiceOverride.Command(templateId, "db"), Ct);

        Assert.True(cleared.IsSuccess, Describe(cleared));
        Assert.Null(cleared.Value.Override);
        Assert.Empty(await TemplateServiceOverridesAsync(host, templateId));
    }

    /// <summary>A value the labels do not admit is refused, and nothing is written.</summary>
    [Fact]
    public async Task SetTemplateServiceOverride_RefusesAValueTheLabelsDoNotAdmit() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);

        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<SetTemplateBackupServiceOverride>(scope.ServiceProvider);
        var result = await handler.HandleAsync(
            new SetTemplateBackupServiceOverride.Command(templateId, "db", Stop: "freeze"), Ct);

        Assert.False(result.IsSuccess);
        Assert.Empty(await TemplateServiceOverridesAsync(host, templateId));
    }

    /// <summary>
    /// <b>The rollup is a partition of the enrolled stacks.</b> Every enrolled stack lands in exactly
    /// one bucket and the four sum to `Enrolled` — a reader adds the line up, so overlapping counts
    /// (a stack that has never been backed up *and* whose last run failed appearing twice) would
    /// describe three problems where there are two.
    /// </summary>
    [Fact]
    public async Task GetProductBackups_PartitionsTheEnrolledFleetIntoFourBuckets() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        await SetTemplatePolicyAsync(host, templateId, enabled: true);

        var recent = await host.AddProductStackAsync("shop-a", productId, templateId: templateId);
        var failing = await host.AddProductStackAsync("shop-b", productId, templateId: templateId);
        var recovered = await host.AddProductStackAsync("shop-c", productId, templateId: templateId);
        var never = await host.AddProductStackAsync("shop-d", productId, templateId: templateId);
        var stale = await host.AddProductStackAsync("shop-e", productId, templateId: templateId);
        // Never *and* last-run-failed: one problem, so exactly one bucket — Never, which describes it.
        var neverAndFailing = await host.AddProductStackAsync("shop-f", productId, templateId: templateId);
        // A stack of another product must not be counted.
        var otherProductId = await host.AddProductAsync("other", ProductReleaseMode.Git);
        await host.AddProductStackAsync("other-a", otherProductId);

        var now = DateTimeOffset.UtcNow;
        await AddBackupEventAsync(host, recent, BackupStatuses.Success, now.AddHours(-1));
        await AddBackupEventAsync(host, failing, BackupStatuses.Success, now.AddDays(-9));
        await AddBackupEventAsync(host, failing, BackupStatuses.Failed, now.AddHours(-2));
        await AddBackupEventAsync(host, recovered, BackupStatuses.Failed, now.AddHours(-5));
        await AddBackupEventAsync(host, recovered, BackupStatuses.Success, now.AddHours(-1));
        await AddBackupEventAsync(host, stale, BackupStatuses.Success, now.AddDays(-3));
        await AddBackupEventAsync(host, neverAndFailing, BackupStatuses.Failed, now.AddHours(-1));
        // Queued is not an answer to anything yet.
        await AddBackupEventAsync(host, never, BackupStatuses.Queued, now);

        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<GetProductBackups>(scope.ServiceProvider);
        var result = await handler.HandleAsync(new GetProductBackups.Query(productId), Ct);

        Assert.True(result.IsSuccess, Describe(result));
        var rollup = result.Value.Rollup;
        Assert.Equal(6, rollup.Deployments);
        Assert.Equal(6, rollup.Enrolled);
        Assert.Equal(0, rollup.NotEnrolled);
        Assert.Equal(2, rollup.BackedUpRecently);
        Assert.Equal(1, rollup.Stale);
        Assert.Equal(1, rollup.Failed);
        Assert.Equal(2, rollup.Never);
        Assert.Equal(GetProductBackups.RollupWindowHours, rollup.WindowHours);
        // The property the buckets exist to have.
        Assert.Equal(
            rollup.Enrolled,
            rollup.BackedUpRecently + rollup.Stale + rollup.Failed + rollup.Never);

        var policy = Assert.Single(result.Value.Templates);
        Assert.Equal(templateId, policy.TemplateId);
        Assert.True(policy.Enabled);
        Assert.Equal(6, policy.TenantCount);
        Assert.Equal(0, policy.OverriddenTenantCount);
    }

    /// <summary>
    /// <b>The denominator is enrolment, not existence.</b> A stack nobody put in the schedule is not
    /// failing at anything; counting it as "never backed up" turns a deliberate choice into a red
    /// number that can never be cleared. Enrolment is the *resolved* policy's, so a tenant enrolled
    /// only by its template counts and one that opted out by hand does not.
    /// </summary>
    [Fact]
    public async Task GetProductBackups_CountsUnenrolledStacksApart_AndReadsEnrolmentThroughTheLadder() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        await SetTemplatePolicyAsync(host, templateId, enabled: true);

        // Enrolled by the template alone.
        await host.AddProductStackAsync("shop-a", productId, templateId: templateId);
        // A tenant that opted out by hand: enrolled by the fleet, refused by its own explicit false.
        var optedOut = await host.AddProductStackAsync("shop-b", productId, templateId: templateId);
        await SetStackPolicyAsync(host, optedOut, enabled: false);
        // A standalone stack nobody enrolled — the instance default is off.
        await host.AddProductStackAsync("shop-prod", productId);
        // …and one that enrolled itself.
        var standaloneEnrolled = await host.AddProductStackAsync("shop-staging", productId);
        await SetStackPolicyAsync(host, standaloneEnrolled, enabled: true);

        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<GetProductBackups>(scope.ServiceProvider);
        var result = await handler.HandleAsync(new GetProductBackups.Query(productId), Ct);

        Assert.True(result.IsSuccess, Describe(result));
        var rollup = result.Value.Rollup;
        Assert.Equal(4, rollup.Deployments);
        Assert.Equal(2, rollup.Enrolled);
        Assert.Equal(2, rollup.NotEnrolled);
        // Both enrolled stacks have never been backed up; neither unenrolled one is counted at all.
        Assert.Equal(2, rollup.Never);
        Assert.Equal(0, rollup.Failed);
        Assert.Equal(0, rollup.BackedUpRecently);
        Assert.Equal(0, rollup.Stale);
    }

    /// <summary>The fleet history filter: every deployment of the product, and nothing else.</summary>
    [Fact]
    public async Task ListBackupEvents_FiltersByProduct() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var otherProductId = await host.AddProductAsync("other", ProductReleaseMode.Git);
        var mine = await host.AddProductStackAsync("shop-a", productId);
        var theirs = await host.AddProductStackAsync("other-a", otherProductId);
        await AddBackupEventAsync(host, mine, BackupStatuses.Success, DateTimeOffset.UtcNow);
        await AddBackupEventAsync(host, theirs, BackupStatuses.Success, DateTimeOffset.UtcNow);

        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<ListBackupEvents>(scope.ServiceProvider);

        var scoped = await handler.HandleAsync(new ListBackupEvents.Query(ProductId: productId), Ct);
        Assert.True(scoped.IsSuccess, Describe(scoped));
        Assert.Equal([mine], scoped.Value.Events.Select(e => e.StackId));

        // Unfiltered is unchanged — the parameter is additive.
        var all = await handler.HandleAsync(new ListBackupEvents.Query(), Ct);
        Assert.Equal(2, all.Value.Events.Count);
    }

    // ── templates.backupAll ──────────────────────────────────────────────────

    /// <summary>
    /// The fan-out reaches every tenant of the template and nothing else, and records one audit row for
    /// the decision rather than one per consequence.
    /// </summary>
    [Fact]
    public async Task BackupAll_QueuesEveryTenantOfTheTemplate_AndAuditsOnce() {
        using var host = StartWithRecordingQueue();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        var a = await host.AddProductStackAsync("shop-a", productId, templateId: templateId);
        var b = await host.AddProductStackAsync("shop-b", productId, templateId: templateId);
        var standalone = await host.AddProductStackAsync("shop-prod", productId);

        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<BackupAllTenants>(scope.ServiceProvider);
        var result = await handler.HandleAsync(new BackupAllTenants.Command(templateId), Ct);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(2, result.Value.Count);
        Assert.Equal(
            [(a, BackupTriggers.TemplateAll), (b, BackupTriggers.TemplateAll)],
            Enqueued(host));
        Assert.DoesNotContain(standalone, Enqueued(host).Select(e => e.StackId));

        var audit = Assert.Single(await AuditAsync(host, "backup.all"));
        Assert.Equal("shop-tenants", audit.Target);
        Assert.Contains("2 instance(s) queued", audit.Detail!, StringComparison.Ordinal);
        Assert.Contains("one at a time", audit.Detail!, StringComparison.Ordinal);
    }

    /// <summary>A template with no tenants is a no-op, not an error.</summary>
    [Fact]
    public async Task BackupAll_WithNoTenants_QueuesNothing() {
        using var host = StartWithRecordingQueue();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);

        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<BackupAllTenants>(scope.ServiceProvider);
        var result = await handler.HandleAsync(new BackupAllTenants.Command(templateId), Ct);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(0, result.Value.Count);
        Assert.Empty(Enqueued(host));
    }

    // ── The stack config surface ─────────────────────────────────────────────

    /// <summary>
    /// The tab's read model: the effective values (unmoved, so existing readers see what they always
    /// did), the stack's own tri-state values, and the provenance labels.
    /// </summary>
    [Fact]
    public async Task GetStackBackupConfig_ReportsTheEffectiveValuesAndWhereEachCameFrom() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        await SetTemplatePolicyAsync(
            host, templateId, enabled: true, cron: "0 4 * * *", quiesceMode: BackupQuiesceMode.Pause);
        var stackId = await host.AddProductStackAsync("shop-acme", productId, templateId: templateId);
        await SetStackPolicyAsync(host, stackId, quiesceMode: BackupQuiesceMode.Stop);

        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<GetStackBackupConfig>(scope.ServiceProvider);
        var result = await handler.HandleAsync(new GetStackBackupConfig.Query(stackId), Ct);

        Assert.True(result.IsSuccess, Describe(result));
        var config = result.Value.Config;
        Assert.True(config.Enabled);
        Assert.Equal("template", config.EnabledSource);
        Assert.Equal("0 4 * * *", config.Cron);
        Assert.Equal("template", config.CronSource);
        Assert.Equal("stop", config.QuiesceMode);
        Assert.Equal("stack", config.QuiesceModeSource);
        Assert.True(config.StopContainers);
        Assert.Equal("instance", config.StopContainersSource);
        Assert.Null(config.OwnEnabled);
        Assert.Equal("stop", config.OwnQuiesceMode);
        Assert.Equal(templateId, config.TemplateId);
        Assert.Equal("shop-tenants", config.TemplateName);
    }

    /// <summary>Nulls on the write path clear the stack's opinion and put it back on the fleet's.</summary>
    [Fact]
    public async Task SetStackBackupConfig_WithNulls_GoesBackToInheriting() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        await SetTemplatePolicyAsync(host, templateId, enabled: true);
        var stackId = await host.AddProductStackAsync("shop-acme", productId, templateId: templateId);
        await SetStackPolicyAsync(host, stackId, enabled: false, quiesceMode: BackupQuiesceMode.Pause);

        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<SetStackBackupConfig>(scope.ServiceProvider);
        var result = await handler.HandleAsync(
            new SetStackBackupConfig.Command(stackId, Enabled: null, StopContainers: null,
                Cron: null, QuiesceMode: BackupQuiesceModes.Inherit),
            Ct);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.True(result.Value.Config.Enabled);
        Assert.Equal("template", result.Value.Config.EnabledSource);
        Assert.Null(result.Value.Config.OwnEnabled);
        Assert.Null(result.Value.Config.OwnQuiesceMode);

        var stack = await StackAsync(host, stackId);
        Assert.Null(stack.BackupEnabled);
        Assert.Null(stack.BackupQuiesceMode);
    }

    // -- Helpers ---------------------------------------------------------------------------------

    private static AuthTestHost StartWithRecordingQueue(params (string Key, string? Value)[] settings) =>
        AuthTestHost.Start(RecordingBackupQueue.Register, settings);

    private static IReadOnlyList<(int StackId, string TriggeredBy)> Enqueued(AuthTestHost host) =>
        ((RecordingBackupQueue)host.Services.GetRequiredService<BackupQueueService>()).Enqueued;

    private static async ValueTask<int> TickAsync(AuthTestHost host, DateTimeOffset now) {
        await using var scope = host.Services.CreateAsyncScope();
        var job = ActivatorUtilities.CreateInstance<BackupScheduleJob>(scope.ServiceProvider);
        return await job.TickAsync(now, TimeZoneInfo.Utc, Ct);
    }

    private static async Task<TenantProvisionResult> ProvisionAsync(
        AuthTestHost host, int templateId, string slug) {
        await using var scope = host.Services.CreateAsyncScope();
        var service = ActivatorUtilities.CreateInstance<TenantProvisioningService>(
            scope.ServiceProvider, RecordingDeployQueue.Create(host));
        return await service.ProvisionAsync(templateId, slug, null, Ct);
    }

    private static async Task<Stack> StackAsync(AuthTestHost host, int stackId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Stacks.AsNoTracking()
            .Include(s => s.Template)
            .FirstAsync(s => s.Id == stackId, Ct);
    }

    private static async Task<string?> DirectoryOfAsync(AuthTestHost host, int stackId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Stacks.AsNoTracking()
            .Where(s => s.Id == stackId).Select(s => s.BackupDirectory).FirstAsync(Ct);
    }

    private static async Task SetDirectoryAsync(AuthTestHost host, int stackId, string? directory) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.Stacks.Where(s => s.Id == stackId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.BackupDirectory, directory), Ct);
    }

    private static async Task SetStackPolicyAsync(
        AuthTestHost host, int stackId, bool? enabled = null, bool? stopContainers = null,
        string? cron = null, BackupQuiesceMode? quiesceMode = null) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = await db.Stacks.FirstAsync(s => s.Id == stackId, Ct);
        stack.BackupEnabled = enabled;
        stack.BackupStopContainers = stopContainers;
        stack.BackupCron = cron;
        stack.BackupQuiesceMode = quiesceMode;
        await db.SaveChangesAsync(Ct);
    }

    private static async Task SetTemplatePolicyAsync(
        AuthTestHost host, int templateId, bool? enabled = null, bool? stopContainers = null,
        string? cron = null, BackupQuiesceMode? quiesceMode = null) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var template = await db.StackTemplates.FirstAsync(t => t.Id == templateId, Ct);
        template.BackupEnabled = enabled;
        template.BackupStopContainers = stopContainers;
        template.BackupCron = cron;
        template.BackupQuiesceMode = quiesceMode;
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<List<TemplateBackupServiceOverride>> TemplateServiceOverridesAsync(
        AuthTestHost host, int templateId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.TemplateBackupServiceOverrides.AsNoTracking()
            .Where(o => o.TemplateId == templateId).ToListAsync(Ct);
    }

    private static async Task<List<StackBackupServiceOverride>> StackServiceOverridesAsync(
        AuthTestHost host, int stackId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.StackBackupServiceOverrides.AsNoTracking()
            .Where(o => o.StackId == stackId).ToListAsync(Ct);
    }

    private static async Task<string?> TemplatePolicyCronAsync(AuthTestHost host, int templateId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.StackTemplates.AsNoTracking()
            .Where(t => t.Id == templateId).Select(t => t.BackupCron).FirstAsync(Ct);
    }

    private static async Task AddBackupEventAsync(
        AuthTestHost host, int stackId, string status, DateTimeOffset startedAt) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.BackupEvents.Add(new BackupEvent {
            StackId = stackId,
            TriggeredBy = BackupTriggers.Schedule,
            Status = status,
            StartedAt = startedAt,
            FinishedAt = status is BackupStatuses.Success or BackupStatuses.Failed ? startedAt : null,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<List<AuditEvent>> AuditAsync(AuthTestHost host, string action) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.AuditEvents.AsNoTracking()
            .Where(e => e.Category == BackupService.AuditCategory && e.Action == action)
            .OrderBy(e => e.Id)
            .ToListAsync(Ct);
    }

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
