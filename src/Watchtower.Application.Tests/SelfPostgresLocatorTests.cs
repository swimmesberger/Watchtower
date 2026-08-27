using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Which container the instance self-backup dumps (ADR-0027). The rule is pure, so the whole of it is
/// exercised here without a daemon — and it has to be exact: the container this picks is the one whose
/// contents become "the backup of Watchtower", so choosing a neighbouring database would produce an
/// archive that looks entirely healthy and restores the wrong instance.
/// </summary>
public sealed class SelfPostgresLocatorTests {
    private static DockerContainerInfo Container(
        string name, string image = "postgres:18-alpine", string? service = null, string state = "running") =>
        new() {
            Id = $"id-{name}",
            Names = [$"/{name}"],
            Image = image,
            State = state,
            Status = $"Up 2 hours",
            Labels = service is null
                ? []
                : new Dictionary<string, string> { ["com.docker.compose.service"] = service },
        };

    [Fact]
    public void PicksTheContainerWhoseComposeServiceIsTheConnectionHost() {
        var postgres = Container("watchtower-postgres-1", service: "postgres");
        var other = Container("shop-db-1", service: "db");

        Assert.Same(postgres, SelfPostgresLocator.Choose([other, postgres], "postgres"));
    }

    [Fact]
    public void PicksTheContainerNamedAfterTheConnectionHost() {
        var postgres = Container("watchtower-pg");
        Assert.Same(postgres, SelfPostgresLocator.Choose([Container("other-db"), postgres], "watchtower-pg"));
    }

    [Fact]
    public void SeesThroughComposesReplicaSuffix() {
        // "Host=postgres" resolves to the service; the container it created is "{project}-postgres-1".
        var postgres = Container("watchtower-postgres-1");
        Assert.Same(postgres, SelfPostgresLocator.Choose([postgres, Container("shop-db-1")], "postgres"));
    }

    [Fact]
    public void ASingleCandidateWinsEvenWhenTheHostNamesNothing() {
        // A Compose install whose service is aliased differently from the host is ordinary, and with one
        // database on the daemon there is nothing to confuse it with.
        var postgres = Container("db-1", service: "db");
        Assert.Same(postgres, SelfPostgresLocator.Choose([postgres], "postgres.internal"));
    }

    [Fact]
    public void RefusesToGuessBetweenSeveralUnmatchedCandidates() {
        var error = Assert.Throws<InvalidOperationException>(() =>
            SelfPostgresLocator.Choose([Container("a-db-1"), Container("b-db-1")], "postgres.internal"));

        Assert.Contains("more than one to choose from", error.Message);
        Assert.Contains("a-db-1", error.Message);
        Assert.Contains("b-db-1", error.Message);
        // The way out is always named, so the message is actionable rather than merely correct.
        Assert.Contains("Watchtower:Backup:SelfPostgresContainer", error.Message);
    }

    [Fact]
    public void RefusesWhenSeveralContainersAnswerToTheSameHost() {
        var error = Assert.Throws<InvalidOperationException>(() =>
            SelfPostgresLocator.Choose(
                [Container("postgres", service: "postgres"), Container("wt-postgres-1", service: "postgres")],
                "postgres"));

        Assert.Contains("More than one", error.Message);
        Assert.Contains("Watchtower:Backup:SelfPostgresContainer", error.Message);
    }

    [Fact]
    public void FailsLoudlyWhenThereIsNoDatabaseContainerAtAll() {
        // The managed-PostgreSQL case, and also the daemon-unreachable case. Both have to say so: a
        // self-backup that quietly does nothing is worse than one that fails, because it is invisible
        // until the day it is needed.
        var error = Assert.Throws<InvalidOperationException>(() =>
            SelfPostgresLocator.Choose([], "db.eu-central-1.rds.amazonaws.com"));

        Assert.Contains("db.eu-central-1.rds.amazonaws.com", error.Message);
        Assert.Contains("managed or host-installed PostgreSQL", error.Message);
        Assert.Contains("Docker daemon could not be reached", error.Message);
    }

    [Fact]
    public void TheDumpTargetCarriesAStableServiceIdentity() {
        // Whatever the container is called, the SQL lands at backup/_dumps/watchtower.sql — so a restore
        // looks for one name rather than for whatever the source instance happened to name its container.
        var target = new SelfPostgresTarget(
            "abc123", "watchtower-postgres-1", "postgres:18-alpine", "postgres", "watchtower", "watchtower");
        var dump = target.ToDumpTarget();

        Assert.Equal("watchtower", dump.Service);
        Assert.Equal("abc123", dump.ContainerId);
        Assert.Equal(DumpEngine.Postgres, dump.Engine);
        // No volumes: an instance archive is the dump and nothing else, so there is no file snapshot for
        // a data volume to be excluded from.
        Assert.Null(dump.DataVolume);
        Assert.Empty(dump.MountedVolumes);
    }
}
