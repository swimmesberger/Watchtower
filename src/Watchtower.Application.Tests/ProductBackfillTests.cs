using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The migration statement that gave every pre-ADR-0026 stack and template a product. It is the one
/// piece of this change that runs exactly once per installation and can never be re-run, so it is
/// exercised here against a real PostgreSQL rather than reasoned about.
/// </summary>
/// <remarks>
/// The pre-migration shape is reconstructed on an already-migrated database — the four source columns
/// added back, <c>product_id</c> relaxed — rather than by migrating to the previous migration, because
/// what is under test is the statement, not EF's ordering of it. The statement is then run verbatim
/// from <see cref="ProductBackfillSql"/>, so a drift between this fixture and the migration is a
/// compile error rather than a passing test of the wrong SQL.
/// </remarks>
public sealed class ProductBackfillTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The headline case: a template and its tenants carry identical copied source columns today, so
    /// they collapse onto one product — which is the propagation fix, landing as a side effect of the
    /// grouping rule. A second stack on the same repository with a different branch joins them and
    /// keeps its branch as an override.
    /// </summary>
    [Fact]
    public async Task CollapsesIdenticalSourcesOntoOneProduct_AndKeepsDivergentBranchesAsOverrides() {
        var connectionString = PostgresTestServer.CreateDatabase();
        try {
            await using var connection = await OpenLegacyAsync(connectionString);

            await InsertTemplateAsync(connection, 1, "shop", "https://github.com/acme/shop.git", "docker-compose.yml", "main");
            await InsertStackAsync(connection, 1, "shop-acme", "https://github.com/acme/shop.git", "docker-compose.yml", "main");
            await InsertStackAsync(connection, 2, "shop-globex", "https://github.com/acme/shop.git", "docker-compose.yml", "main");
            // Same repository, different branch: one product, one override — keying on the branch would
            // have forked the catalogue into per-branch duplicates.
            await InsertStackAsync(connection, 3, "shop-staging", "https://github.com/acme/shop.git", "docker-compose.yml", "develop");

            await BackfillAsync(connection);

            await using var db = Context(connectionString);
            var product = await db.Products.AsNoTracking().SingleAsync(Ct);
            Assert.Equal("shop", product.Name);
            Assert.Equal("https://github.com/acme/shop.git", product.RepositoryUrl);
            Assert.Equal("docker-compose.yml", product.ComposeFilePath);
            Assert.Equal("main", product.DefaultBranch);

            var stacks = await db.Stacks.AsNoTracking().OrderBy(s => s.Id).ToListAsync(Ct);
            Assert.All(stacks, s => Assert.Equal(product.Id, s.ProductId));
            Assert.Null(stacks[0].BranchOverride);
            Assert.Null(stacks[1].BranchOverride);
            Assert.Equal("develop", stacks[2].BranchOverride);

            var template = await db.StackTemplates.AsNoTracking().SingleAsync(Ct);
            Assert.Equal(product.Id, template.ProductId);
            Assert.Null(template.BranchOverride);
        } finally {
            PostgresTestServer.Drop(connectionString);
        }
    }

    /// <summary>
    /// The normalization rule, end to end in SQL: cosmetic spellings of one URL group together, and the
    /// scp form is deliberately its own product — the same verdicts <c>ProductSourceKeyTests</c> pins
    /// for the C# half.
    /// </summary>
    [Fact]
    public async Task GroupsCosmeticUrlSpellingsAndKeepsTheScpFormApart() {
        var connectionString = PostgresTestServer.CreateDatabase();
        try {
            await using var connection = await OpenLegacyAsync(connectionString);

            await InsertStackAsync(connection, 1, "a", "https://github.com/acme/web.git", "docker-compose.yml", "main");
            await InsertStackAsync(connection, 2, "b", "https://GitHub.com/acme/web/", "docker-compose.yml", "main");
            await InsertStackAsync(connection, 3, "c", " https://github.com/acme/web ", "/docker-compose.yml", "main");
            await InsertStackAsync(connection, 4, "d", "git@github.com:acme/web.git", "docker-compose.yml", "main");

            await BackfillAsync(connection);

            await using var db = Context(connectionString);
            var stacks = await db.Stacks.AsNoTracking().OrderBy(s => s.Id).ToListAsync(Ct);
            Assert.Equal(stacks[0].ProductId, stacks[1].ProductId);
            Assert.Equal(stacks[0].ProductId, stacks[2].ProductId);
            Assert.NotEqual(stacks[0].ProductId, stacks[3].ProductId);

            // The representative is the lowest-id row, so the stored URL is the first stack's spelling.
            var first = await db.Products.AsNoTracking().SingleAsync(p => p.Id == stacks[0].ProductId, Ct);
            Assert.Equal("https://github.com/acme/web.git", first.RepositoryUrl);
        } finally {
            PostgresTestServer.Drop(connectionString);
        }
    }

    /// <summary>
    /// Two products whose repositories end in the same segment: the compose file's directory
    /// disambiguates, and a name that is still taken gets a numeric suffix. Nothing may collide —
    /// <c>products.name</c> is unique, and a migration must not fail on a name it chose itself.
    /// </summary>
    [Fact]
    public async Task DisambiguatesNamesByComposeDirectoryThenBySuffix() {
        var connectionString = PostgresTestServer.CreateDatabase();
        try {
            await using var connection = await OpenLegacyAsync(connectionString);

            // One repository, two compose files → two products sharing a base name.
            await InsertStackAsync(connection, 1, "api", "https://github.com/acme/mono.git", "apps/api/compose.yaml", "main");
            await InsertStackAsync(connection, 2, "web", "https://github.com/acme/mono.git", "apps/web/compose.yaml", "main");
            // Two different hosts serving a repository of the same name, both at the repository root:
            // the directory cannot separate them, so the suffix has to.
            await InsertStackAsync(connection, 3, "x", "https://github.com/acme/shop.git", "docker-compose.yml", "main");
            await InsertStackAsync(connection, 4, "y", "https://gitlab.com/acme/shop.git", "docker-compose.yml", "main");
            // …and a repository literally named after the disambiguated form of the first pair.
            await InsertStackAsync(connection, 5, "z", "https://github.com/acme/mono-apps-api.git", "docker-compose.yml", "main");

            await BackfillAsync(connection);

            await using var db = Context(connectionString);
            var names = await db.Products.AsNoTracking().Select(p => p.Name).ToListAsync(Ct);
            Assert.Equal(5, names.Count);
            Assert.Equal(5, names.Distinct(StringComparer.Ordinal).Count());
            Assert.Contains("mono-apps-web", names);
            // Both spellings of "mono-apps-api" are present, one of them suffixed.
            Assert.Equal(2, names.Count(n => n.StartsWith("mono-apps-api", StringComparison.Ordinal)));
            Assert.Equal(2, names.Count(n => n.StartsWith("shop", StringComparison.Ordinal)));

            // And every stack got exactly one of them.
            Assert.Equal(5, await db.Stacks.AsNoTracking().Select(s => s.ProductId).Distinct().CountAsync(Ct));
        } finally {
            PostgresTestServer.Drop(connectionString);
        }
    }

    /// <summary>A URL with no usable last segment still yields a name, the way BackupNaming does.</summary>
    [Fact]
    public async Task FallsBackToAnUnnamedProductForADegenerateUrl() {
        var connectionString = PostgresTestServer.CreateDatabase();
        try {
            await using var connection = await OpenLegacyAsync(connectionString);
            await InsertStackAsync(connection, 1, "odd", "https://", "docker-compose.yml", "main");

            await BackfillAsync(connection);

            await using var db = Context(connectionString);
            Assert.Equal("unnamed", (await db.Products.AsNoTracking().SingleAsync(Ct)).Name);
        } finally {
            PostgresTestServer.Drop(connectionString);
        }
    }

    /// <summary>
    /// A product carries one clone credential, so a key whose rows disagree has to lose all but one.
    /// The representative's wins — and the migration says so out loud, because it is a decision made on
    /// the operator's behalf that nothing else would record.
    /// </summary>
    [Fact]
    public async Task KeepsTheRepresentativesCredential_AndAnnouncesADivergentGroup() {
        var connectionString = PostgresTestServer.CreateDatabase();
        var notices = new List<string>();
        try {
            await using var connection = await OpenLegacyAsync(connectionString);
            connection.Notice += (_, e) => notices.Add(e.Notice.MessageText);
            await InsertCredentialsAsync(connection, 1, 2);

            await InsertStackAsync(connection, 1, "a", "https://github.com/acme/web.git", "docker-compose.yml", "main", 1);
            await InsertStackAsync(connection, 2, "b", "https://github.com/acme/web.git", "docker-compose.yml", "main", 2);
            // "no credential" disagrees with "credential 1" just as much as two ids do.
            await InsertStackAsync(connection, 3, "c", "https://github.com/acme/web.git", "docker-compose.yml", "main");
            // A key nobody disagrees about must stay quiet.
            await InsertStackAsync(connection, 4, "d", "https://github.com/acme/other.git", "docker-compose.yml", "main", 2);

            await BackfillAsync(connection);

            await using var db = Context(connectionString);
            var web = await db.Products.AsNoTracking().SingleAsync(p => p.Name == "web", Ct);
            // Stack 1 is the lowest id, so its credential is the one the product keeps.
            Assert.Equal(1, web.CredentialId);
            Assert.Equal(2, (await db.Products.AsNoTracking().SingleAsync(p => p.Name == "other", Ct)).CredentialId);

            var announced = Assert.Single(notices, n => n.Contains("products backfill", StringComparison.Ordinal));
            Assert.Contains("https://github.com/acme/web", announced, StringComparison.Ordinal);
            Assert.Contains("3 different git credentials", announced, StringComparison.Ordinal);
            Assert.Contains("keeps credential 1", announced, StringComparison.Ordinal);
        } finally {
            PostgresTestServer.Drop(connectionString);
        }
    }

    /// <summary>
    /// A key reached only by templates: the DISTINCT ON ordering has to fall through to the <c>t</c>
    /// arm rather than find nothing, or a template-only product would come out with a null branch.
    /// </summary>
    [Fact]
    public async Task PicksATemplateAsTheRepresentativeWhenNoStackUsesTheKey() {
        var connectionString = PostgresTestServer.CreateDatabase();
        try {
            await using var connection = await OpenLegacyAsync(connectionString);
            await InsertCredentialsAsync(connection, 1);

            // Two templates, no stacks: the lower id supplies the default branch and the credential.
            await InsertTemplateAsync(connection, 4, "later", "https://github.com/acme/fleet.git", "docker-compose.yml", "release", 1);
            await InsertTemplateAsync(connection, 2, "earlier", "https://github.com/acme/fleet.git", "docker-compose.yml", "main", 1);

            await BackfillAsync(connection);

            await using var db = Context(connectionString);
            var product = await db.Products.AsNoTracking().SingleAsync(Ct);
            Assert.Equal("fleet", product.Name);
            Assert.Equal("main", product.DefaultBranch);
            Assert.Equal(1, product.CredentialId);

            var templates = await db.StackTemplates.AsNoTracking().OrderBy(x => x.Id).ToListAsync(Ct);
            Assert.All(templates, x => Assert.Equal(product.Id, x.ProductId));
            Assert.Null(templates.Single(x => x.Name == "earlier").BranchOverride);
            Assert.Equal("release", templates.Single(x => x.Name == "later").BranchOverride);
        } finally {
            PostgresTestServer.Drop(connectionString);
        }
    }

    /// <summary>
    /// The <c>Down</c> nobody runs until the day they have to. It is best-effort by contract, but
    /// "best effort" still has to mean the four columns come back holding what each row deploys —
    /// including a tenant's, which inherits its template's override rather than the product default.
    /// The round trip then re-derives the same products, which is the property that makes a downgrade
    /// followed by a retry survivable.
    /// </summary>
    [Fact]
    public async Task DownRestoresTheSourceColumns_AndTheRoundTripReDerivesTheProducts() {
        var connectionString = PostgresTestServer.CreateDatabase();
        try {
            int templateId, tenantId, standaloneId;
            await using (var db = Context(connectionString)) {
                var product = new Product {
                    Name = "shop",
                    RepositoryUrl = "https://github.com/acme/shop.git",
                    ComposeFilePath = "docker-compose.yml",
                    DefaultBranch = "main",
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                var template = new StackTemplate {
                    Name = "shop", Product = product, BranchOverride = "develop",
                    DomainPattern = "{tenant}.example.com", TargetServiceName = "web", TargetPort = 8080,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                // Inherits "develop" from the template without storing it.
                var tenant = new Stack {
                    Name = "shop-acme", ComposeProjectName = "shop-acme",
                    Product = product, Template = template, TenantSlug = "acme",
                };
                var standalone = new Stack {
                    Name = "shop-hotfix", ComposeProjectName = "shop-hotfix",
                    Product = product, BranchOverride = "hotfix",
                };
                db.AddRange(product, template, tenant, standalone);
                await db.SaveChangesAsync(Ct);
                templateId = template.Id;
                tenantId = tenant.Id;
                standaloneId = standalone.Id;
            }

            await MigrateToAsync(connectionString, PreviousMigration);

            await using (var connection = new NpgsqlConnection(connectionString)) {
                await connection.OpenAsync(Ct);
                Assert.Equal(
                    ("https://github.com/acme/shop.git", "docker-compose.yml", "develop"),
                    await ReadSourceAsync(connection, "stacks", tenantId));
                Assert.Equal(
                    ("https://github.com/acme/shop.git", "docker-compose.yml", "hotfix"),
                    await ReadSourceAsync(connection, "stacks", standaloneId));
                Assert.Equal(
                    ("https://github.com/acme/shop.git", "docker-compose.yml", "develop"),
                    await ReadSourceAsync(connection, "stack_templates", templateId));
                // The table itself is gone, so there is nothing left pointing at a product.
                Assert.False(await TableExistsAsync(connection, "products"));
            }

            await MigrateToAsync(connectionString, targetMigration: null);

            await using (var db = Context(connectionString)) {
                // One product again — the tenant and the template agree on the source, and the
                // standalone's hotfix is a branch difference, not a second product.
                var product = await db.Products.AsNoTracking().SingleAsync(Ct);
                Assert.Equal("https://github.com/acme/shop.git", product.RepositoryUrl);

                // The property that matters is not which row became the representative — that is an id
                // ordering nobody should depend on — but that every row still deploys what it deployed
                // before the downgrade.
                var stacks = await db.Stacks.AsNoTracking()
                    .Include(s => s.Product).Include(s => s.Template).ToListAsync(Ct);
                Assert.All(stacks, s => Assert.Equal(product.Id, s.ProductId));
                Assert.Equal(
                    "develop",
                    ProductSourceResolver.Resolve(stacks.Single(s => s.Id == tenantId)).Branch);
                Assert.Equal(
                    "hotfix",
                    ProductSourceResolver.Resolve(stacks.Single(s => s.Id == standaloneId)).Branch);

                var template = await db.StackTemplates.AsNoTracking()
                    .Include(x => x.Product).SingleAsync(x => x.Id == templateId, Ct);
                Assert.Equal("develop", ProductSourceResolver.Resolve(template).Branch);
            }
        } finally {
            PostgresTestServer.Drop(connectionString);
        }
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    /// <summary>The migration immediately before <c>AddProducts</c> — where <c>Down</c> lands.</summary>
    private const string PreviousMigration = "20260824192127_AddCiRegistrySync";

    private static async Task MigrateToAsync(string connectionString, string? targetMigration) {
        await using var db = Context(connectionString);
        var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(targetMigration, cancellationToken: Ct);
    }

    private static async Task<(string Url, string Path, string Branch)> ReadSourceAsync(
        NpgsqlConnection connection, string table, int id) {
        await using var command = connection.CreateCommand();
        // The table name is one of two literals above, never input.
        command.CommandText = $"""SELECT repository_url, compose_file_path, branch FROM "{table}" WHERE id = @id""";
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string table) {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass('public.' || @table) IS NOT NULL";
        command.Parameters.AddWithValue("table", table);
        return (bool)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>
    /// Puts the four source columns back and relaxes <c>product_id</c> — the shape the migration sees
    /// at the moment it runs the backfill.
    /// </summary>
    private static async Task<NpgsqlConnection> OpenLegacyAsync(string connectionString) {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        foreach (var table in new[] { "stacks", "stack_templates" }) {
            await ExecuteAsync(connection, $"""
                ALTER TABLE "{table}"
                    ADD COLUMN repository_url text,
                    ADD COLUMN compose_file_path text,
                    ADD COLUMN branch text,
                    ADD COLUMN credential_id integer;
                ALTER TABLE "{table}" ALTER COLUMN product_id DROP NOT NULL;
                """);
        }
        return connection;
    }

    private static Task BackfillAsync(NpgsqlConnection connection) =>
        ExecuteAsync(connection, ProductBackfillSql.Sql);

    private static async Task InsertStackAsync(
        NpgsqlConnection connection, int id, string name, string repositoryUrl, string composeFilePath,
        string branch, int? credentialId = null) {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO stacks (id, name, compose_project_name, repository_url, compose_file_path, branch,
                                credential_id, auto_deploy_mode, webhook_enabled, app_api_enabled,
                                backup_enabled, backup_stop_containers, backup_quiesce_mode, desired_state,
                                created_at)
            VALUES (@id, @name, @name, @url, @path, @branch, @credential, 'Off', false, true, false, true,
                    'Stop', 'Running', now())
            """;
        Bind(command, id, name, repositoryUrl, composeFilePath, branch, credentialId);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task InsertTemplateAsync(
        NpgsqlConnection connection, int id, string name, string repositoryUrl, string composeFilePath,
        string branch, int? credentialId = null) {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO stack_templates (id, realm_id, name, repository_url, compose_file_path, branch,
                                         credential_id, domain_pattern, target_service_name, target_port,
                                         created_at)
            VALUES (@id, 1, @name, @url, @path, @branch, @credential, '{tenant}.example.com', 'web', 8080,
                    now())
            """;
        Bind(command, id, name, repositoryUrl, composeFilePath, branch, credentialId);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Two credentials to hand a stack, so a divergent-credential group is representable.</summary>
    private static async Task InsertCredentialsAsync(NpgsqlConnection connection, params int[] ids) {
        foreach (var id in ids) {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO credentials (id, name, username, token, created_at)
                VALUES (@id, @name, 'git', 'token', now())
                """;
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("name", $"cred-{id}");
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    private static void Bind(
        NpgsqlCommand command, int id, string name, string repositoryUrl, string composeFilePath,
        string branch, int? credentialId) {
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("url", repositoryUrl);
        command.Parameters.AddWithValue("path", composeFilePath);
        command.Parameters.AddWithValue("branch", branch);
        command.Parameters.AddWithValue("credential", (object?)credentialId ?? DBNull.Value);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql) {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static WatchtowerDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<WatchtowerDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);
}
