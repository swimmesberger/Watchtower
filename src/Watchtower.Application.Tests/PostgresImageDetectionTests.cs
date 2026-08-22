using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Which image references count as "runs a PostgreSQL server" (ADR-0017). The rule is an exact match
/// on the repository name, and the non-matches are the reason: the registry is full of images whose
/// names contain "postgres" while being a REST layer, an exporter or a connection pooler. Dumping one
/// of those means running <c>pg_dumpall</c> in a container that has no server — a failed backup for a
/// stack that never asked for a dump.
/// </summary>
public sealed class PostgresImageDetectionTests {
    [Theory]
    [InlineData("postgres")]
    [InlineData("postgres:16-alpine")]
    [InlineData("docker.io/library/postgres:15")]
    [InlineData("registry.example.com:5000/mirror/postgres:16")]
    [InlineData("bitnami/postgresql:16")]
    [InlineData("postgis/postgis:16-3.4")]
    [InlineData("pgvector/pgvector:pg16")]
    [InlineData("timescale/timescaledb:latest-pg16")]
    [InlineData("postgres@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("Postgres:16")]
    // A registry with a port and no tag: the colon belongs to the host, not to a tag. Cutting at the
    // last colon regardless would leave "registry" behind and miss a perfectly ordinary mirror of the
    // official image.
    [InlineData("registry:5000/postgres")]
    public void RecognizesAPostgresImage(string image) =>
        Assert.True(DatabaseDumpTargets.IsPostgresImage(image), image);

    [Theory]
    [InlineData("postgrest/postgrest")]
    [InlineData("prometheuscommunity/postgres-exporter")]
    [InlineData("bitnami/postgresql-repmgr")]
    [InlineData("edoburu/pgbouncer")]
    [InlineData("x/postgres-backup-local")]
    [InlineData("myorg/my-postgres-app")]
    [InlineData("mysql:8")]
    [InlineData("busybox:stable")]
    public void LeavesEverythingElseAlone(string image) =>
        Assert.False(DatabaseDumpTargets.IsPostgresImage(image), image);

    [Theory]
    [InlineData("postgres:16-alpine", "postgres")]
    [InlineData("registry:5000/postgres", "postgres")]
    [InlineData("registry:5000/library/Postgres:16", "postgres")]
    [InlineData("postgres@sha256:abc", "postgres")]
    [InlineData("ghcr.io/org/team/app:1.2.3", "app")]
    public void ReducesAReferenceToItsRepositoryName(string image, string expected) =>
        Assert.Equal(expected, DatabaseDumpTargets.RepositoryName(image));
}
