using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// How one replay is judged (ADR-0017 §5). The rule is deliberately not "psql exited 0": a
/// <c>pg_dumpall --clean</c> script always errors on something harmless, and with
/// <c>ON_ERROR_STOP=0</c> it can also exit 0 having restored nothing at all. The databases the
/// manifest promised are the evidence; everything else is a report.
/// </summary>
public sealed class PostgresReplayOutcomeTests {
    private const string BenignDiagnostics =
        "psql:/tmp/db.sql:12: ERROR:  role \"postgres\" already exists\n"
        + "psql:/tmp/db.sql:40: ERROR:  role \"postgres\" already exists\n";

    [Fact]
    public void ACleanRunWithEveryDatabaseBackIsASuccess() {
        var result = PostgresReplayOutcome.Classify(0, "", ["app", "postgres"], ["app", "postgres"]);

        Assert.True(result.Succeeded);
        Assert.Null(result.Failure);
        Assert.Equal(0, result.ErrorLineCount);
        Assert.Empty(result.SampleErrors);
        Assert.Empty(result.MissingDatabases);
    }

    [Fact]
    public void DiagnosticsAloneDoNotFailTheReplay() {
        var result = PostgresReplayOutcome.Classify(0, BenignDiagnostics, ["app"], ["app", "postgres"]);

        // Every --clean script produces this; failing on it would mean no restore ever succeeds.
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.ErrorLineCount);
        Assert.Equal(2, result.SampleErrors.Count);
    }

    [Fact]
    public void TheSameDiagnosticRepeatedIsShownOnce() {
        var line = "psql:/tmp/db.sql:12: ERROR:  role \"postgres\" already exists\n";

        var result = PostgresReplayOutcome.Classify(0, string.Concat(Enumerable.Repeat(line, 6)), [], []);

        // Counted six times, shown once — an operator reading the log wants the distinct reasons.
        Assert.Equal(6, result.ErrorLineCount);
        Assert.Single(result.SampleErrors);
    }

    [Fact]
    public void OnlyPsqlsOwnLinesAreCounted() {
        // The server's own messages are translated ("FEHLER:"), so they cannot be matched on; the
        // program-name prefix psql puts in front of its diagnostics is locale-independent.
        var stderr = "NOTICE:  table \"x\" does not exist, skipping\n"
            + "FEHLER:  Rolle »postgres« existiert bereits\n"
            + "psql:/tmp/db.sql:12: ERROR:  role \"postgres\" already exists\n";

        var result = PostgresReplayOutcome.Classify(0, stderr, [], []);

        Assert.Equal(1, result.ErrorLineCount);
        Assert.StartsWith("psql:", Assert.Single(result.SampleErrors));
    }

    [Fact]
    public void AtMostTenDistinctLinesAreKept() {
        var stderr = string.Concat(Enumerable.Range(1, 25).Select(i => $"psql:/tmp/db.sql:{i}: ERROR:  no {i}\n"));

        var result = PostgresReplayOutcome.Classify(0, stderr, [], []);

        Assert.Equal(25, result.ErrorLineCount);
        Assert.Equal(10, result.SampleErrors.Count);
    }

    [Fact]
    public void AMissingDatabaseIsAFailureEvenWhenPsqlWasHappy() {
        var result = PostgresReplayOutcome.Classify(0, "", ["app", "reports", "postgres"], ["postgres"]);

        Assert.False(result.Succeeded);
        Assert.Equal(["app", "reports"], result.MissingDatabases);
        Assert.Contains("the database(s) app, reports are not on the server afterwards", result.Failure);
    }

    [Fact]
    public void ANonZeroExitIsAFailureEvenWhenEveryDatabaseIsThere() {
        // The databases may predate the restore — an aborted replay must never pass for a done one.
        var result = PostgresReplayOutcome.Classify(
            2, "psql: error: could not connect to server", ["app"], ["app"]);

        Assert.False(result.Succeeded);
        Assert.Empty(result.MissingDatabases);
        Assert.Contains("psql exited with code 2", result.Failure);
        Assert.Contains("could not connect to server", result.Failure);
    }

    [Fact]
    public void AnArchiveThatPromisedNoDatabasesJustHasToRunCleanly() {
        // A dump the manifest did not describe: there is nothing to check for, so psql's exit code is
        // all the evidence there is.
        Assert.True(PostgresReplayOutcome.Classify(0, "", [], []).Succeeded);
        Assert.False(PostgresReplayOutcome.Classify(1, "psql: fatal", [], []).Succeeded);
    }
}
