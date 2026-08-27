using System.Text.Json;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The instance archive's manifest (ADR-0027). It has to be self-describing enough that a restore can
/// refuse it: <c>kind</c> tells a reader it is not a stack archive, and <c>lastMigrationId</c> is what a
/// target instance checks before replaying, since migrations only roll forward.
/// </summary>
public sealed class InstanceBackupManifestTests {
    private static readonly DateTimeOffset TakenAt = new(2026, 8, 26, 3, 15, 0, TimeSpan.Zero);

    private static SelfPostgresTarget Target() => new(
        "abc123", "watchtower-postgres-1", "postgres:18-alpine", "postgres", "watchtower", "watchtower");

    private static BackupService.BackupDumpEntry Dump() => new(
        "watchtower", DumpEngine.Postgres, "_dumps/watchtower.sql", "postgres:18-alpine", "watchtower",
        "watchtower-postgres-1", Volumes: [], ["postgres", "watchtower"], 65536);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void DeclaresItsKindVersionAndSchema() {
        var manifest = Parse(InstanceBackupService.BuildManifest(
            "prod", Target(), TakenAt, Dump(), "20260826192454_AddInstanceBackupEvents"));

        Assert.Equal(1, manifest.GetProperty("formatVersion").GetInt32());
        // The discriminator: a reader holding one archive must be able to tell which of the two it is
        // without inferring it from which keys happen to be missing.
        Assert.Equal("watchtower-instance", manifest.GetProperty("kind").GetString());
        Assert.Equal("watchtower", manifest.GetProperty("tool").GetString());
        Assert.Equal("prod", manifest.GetProperty("instance").GetString());
        Assert.Equal("watchtower", manifest.GetProperty("database").GetString());
        Assert.Equal("2026-08-26T03:15:00.0000000Z", manifest.GetProperty("createdAtUtc").GetString());
        Assert.Equal(
            "20260826192454_AddInstanceBackupEvents", manifest.GetProperty("lastMigrationId").GetString());
        // Always true: the run refuses to produce an unencrypted instance archive at all.
        Assert.True(manifest.GetProperty("encrypted").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(manifest.GetProperty("appVersion").GetString()));
    }

    [Fact]
    public void CarriesTheDumpInTheSameShapeAStackArchiveDoes() {
        // Same node builder as the stack manifest, so tooling that reads one reads the other.
        var dumps = Parse(InstanceBackupService.BuildManifest("prod", Target(), TakenAt, Dump(), "m"))
            .GetProperty("dumps");

        var dump = Assert.Single(dumps.EnumerateArray());
        Assert.Equal("watchtower", dump.GetProperty("service").GetString());
        Assert.Equal("postgres", dump.GetProperty("engine").GetString());
        Assert.Equal("_dumps/watchtower.sql", dump.GetProperty("file").GetString());
        Assert.Equal("watchtower-postgres-1", dump.GetProperty("container").GetString());
        Assert.Equal(65536, dump.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(
            ["postgres", "watchtower"],
            dump.GetProperty("databases").EnumerateArray().Select(d => d.GetString()));
        Assert.Empty(dump.GetProperty("volumes").EnumerateArray());
    }

    [Fact]
    public void RecordsANullMigrationRatherThanOmittingTheKey() {
        // A database with no migrations applied is a real state, and a key that is sometimes absent is
        // harder for a reader to handle than one that is sometimes null.
        var manifest = Parse(InstanceBackupService.BuildManifest("prod", Target(), TakenAt, Dump(), null));

        Assert.Equal(JsonValueKind.Null, manifest.GetProperty("lastMigrationId").ValueKind);
    }
}
