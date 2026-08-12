using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers what <see cref="ImagePruneBackgroundService"/> does with a prune that did not come back.
/// The loop swallows cancellation as "we are shutting down", which is right for a shutdown and
/// exactly wrong for a prune that ran into its ceiling — the only signal an unattended, UI-less
/// job has. Both branches are exercised directly; the loop's initial delay is not waited out.
/// </summary>
public sealed class ImagePruneReportingTests {
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task ACappedPruneIsReportedAsAFailure() {
        // Short cap standing in for the real 30 minutes, against a daemon that never answers.
        using var estate = DockerClientEstate.Create(
            pruneTimeout: TimeSpan.FromMilliseconds(50), hangLongRunning: true);
        var logger = new CapturingLogger();
        var service = NewService(estate, logger);

        await service.RunPruneAsync(Interval, CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.IsType<TimeoutException>(entry.Exception);
    }

    [Fact]
    public async Task AShutdownCancellationIsSwallowedSilently() {
        using var estate = DockerClientEstate.Create(
            pruneTimeout: TimeSpan.FromMinutes(30), hangLongRunning: true);
        var logger = new CapturingLogger();
        var service = NewService(estate, logger);
        using var shutdown = new CancellationTokenSource();

        var prune = service.RunPruneAsync(Interval, shutdown.Token);
        await shutdown.CancelAsync();
        await prune;

        // Nothing to report: the process is going down, and the daemon is left to finish or not.
        Assert.Empty(logger.Entries);
    }

    private static ImagePruneBackgroundService NewService(DockerClientEstate estate, ILogger<ImagePruneBackgroundService> logger) =>
        new(estate.Client, new StaticOptionsMonitor(new WatchtowerOptions()), logger);

    // ── Doubles ───────────────────────────────────────────────────────────────

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger : ILogger<ImagePruneBackgroundService> {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Debug is on so a "nothing to remove" run would show up too, rather than being invisible
        // to a test that asserts on emptiness.
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    private sealed class StaticOptionsMonitor(WatchtowerOptions value) : IOptionsMonitor<WatchtowerOptions> {
        public WatchtowerOptions CurrentValue { get; } = value;
        public WatchtowerOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<WatchtowerOptions, string?> listener) => null;
    }
}
