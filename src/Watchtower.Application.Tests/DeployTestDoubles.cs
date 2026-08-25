using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Tests;

/// <summary>One compose invocation a deploy made, as the recording CLI saw it.</summary>
/// <param name="Args">
/// The complete argument list the service assembled, <c>compose</c> first. Recorded verbatim because
/// the position of Watchtower's override among the <c>--file</c> flags is what makes it authoritative.
/// </param>
/// <param name="OverrideContent">
/// What the override file held at the moment of the call, or null when no override was passed. The
/// deploy deletes it on the way out, so it can only be captured here.
/// </param>
internal sealed record ComposeInvocation(IReadOnlyList<string> Args, string? OverrideContent) {
    /// <summary>The subcommand: <c>config</c>, <c>pull</c> or <c>up</c>.</summary>
    public string Command {
        get {
            // Every option this service emits ahead of the subcommand carries a value, so the first
            // token that is neither an option nor an option's value is the subcommand itself.
            for (var i = 1; i < Args.Count; i++) {
                if (Args[i].StartsWith("--", StringComparison.Ordinal)) { i++; continue; }
                return Args[i];
            }
            return string.Empty;
        }
    }

    /// <summary>Every <c>--file</c> value, in the order compose will merge them.</summary>
    public IReadOnlyList<string> ComposeFiles => [.. ValuesOf("--file")];

    /// <summary>The <c>--env-file</c> value, or null when none was passed.</summary>
    public string? EnvFilePath => ValuesOf("--env-file").FirstOrDefault();

    /// <summary>The second <c>--file</c> — Watchtower's override — or null when none was passed.</summary>
    public string? OverrideFilePath => ComposeFiles.Count > 1 ? ComposeFiles[1] : null;

    private IEnumerable<string> ValuesOf(string flag) =>
        Args.Select((a, i) => (a, i)).Where(x => x.a == flag && x.i + 1 < Args.Count).Select(x => Args[x.i + 1]);
}

/// <summary>
/// A compose CLI that records what a deploy asked of it and answers from canned values instead of
/// starting a subprocess.
/// </summary>
/// <remarks>
/// <para>
/// The deploy's whole reason for calling <c>config</c> is to learn the services and their labels, so
/// <see cref="ConfigJson"/> is what steers every injection decision under test — no Docker daemon is
/// needed to vary it. Each recorded invocation also snapshots the override file's <em>content</em>,
/// because by the time the test can look the deploy has already deleted it, and "the file existed and
/// said the right thing while compose was running" is exactly the claim worth pinning.
/// </para>
/// <para>
/// It stands in at the argument-list seam rather than overriding <c>PullAsync</c>/<c>UpAsync</c>: those
/// methods are where the override file is turned into a second <c>--file</c>, so replacing them would
/// leave the one step this feature depends on unexecuted by every test.
/// </para>
/// </remarks>
internal sealed class RecordingComposeCliService()
    : ComposeCliService(Options.Create(new WatchtowerOptions())) {
    private readonly List<ComposeInvocation> _invocations = [];

    /// <summary>stdout the stubbed <c>config --format json</c> returns.</summary>
    public string ConfigJson { get; set; } = """{ "services": { "app": {} } }""";

    /// <summary>Exit code the stubbed <c>config</c> reports; non-zero aborts the deploy.</summary>
    public int ConfigExitCode { get; set; }

    /// <summary>stderr the stubbed <c>config</c> returns.</summary>
    public string ConfigDiagnostics { get; set; } = string.Empty;

    /// <summary>
    /// Awaited at the start of every <c>config</c> call. How a test holds one deploy open while it
    /// enqueues the next — <c>config</c> is the probe point because every deploy that runs at all makes
    /// exactly one, and a short-circuited deploy makes none.
    /// </summary>
    public Func<Task>? OnConfig { get; set; }

    /// <summary>Exit codes the stubbed <c>pull</c> and <c>up</c> report.</summary>
    public int PullExitCode { get; set; }

    /// <inheritdoc cref="PullExitCode"/>
    public int UpExitCode { get; set; }

    /// <summary>Every invocation this service was asked for, in order.</summary>
    public IReadOnlyList<ComposeInvocation> Invocations {
        get { lock (_invocations) return [.. _invocations]; }
    }

    /// <summary>The single invocation of <paramref name="command"/>, failing if there was not exactly one.</summary>
    public ComposeInvocation Invocation(string command) =>
        Invocations.Single(i => i.Command == command);

    protected override async Task<ComposeConfigResult> RunCapturedAsync(string[] args, CancellationToken ct) {
        Record(args);
        if (OnConfig is { } hook) await hook();
        return new ComposeConfigResult(ConfigExitCode, ConfigJson, ConfigDiagnostics);
    }

    protected override Task<(int ExitCode, string Output)> RunAsync(
        string[] args, string? dockerConfigDir, Action<string>? onLine, CancellationToken ct) {
        var invocation = Record(args);
        var exitCode = invocation.Command == "pull" ? PullExitCode : UpExitCode;
        return Task.FromResult((exitCode, $"stubbed compose {invocation.Command}"));
    }

    private ComposeInvocation Record(string[] args) {
        var invocation = new ComposeInvocation(args, ReadOverride(args));
        lock (_invocations) _invocations.Add(invocation);
        return invocation;
    }

    /// <summary>Snapshots the override file while it still exists — the deploy deletes it on the way out.</summary>
    private static string? ReadOverride(string[] args) {
        var path = new ComposeInvocation(args, null).OverrideFilePath;
        return path is not null && File.Exists(path) ? File.ReadAllText(path) : null;
    }
}

/// <summary>
/// A compose CLI that holds each deploy inside its <c>config</c> call for a moment and records how many
/// deploys were inside it at the same time — the only way to observe the cross-stack gate, which bounds
/// a region rather than producing a value.
/// </summary>
/// <remarks>
/// <c>config</c> is the probe point because every deploy makes exactly one, and it happens after the
/// permit has been taken and well before it is released. The hold has to be long enough that deploys
/// genuinely overlap; the assertion is about the <em>peak</em>, so a slow machine only makes the
/// overlap more certain, never the test more fragile.
/// </remarks>
internal sealed class ConcurrencyProbeComposeCliService()
    : ComposeCliService(Options.Create(new WatchtowerOptions())) {
    private int _inFlight;
    private int _peak;
    private int _observed;

    /// <summary>How long each deploy is held inside its <c>config</c> call.</summary>
    public TimeSpan Hold { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>The most deploys that were ever inside the probe at the same time.</summary>
    public int Peak => Volatile.Read(ref _peak);

    /// <summary>How many deploys reached the probe at all.</summary>
    public int Observed => Volatile.Read(ref _observed);

    protected override async Task<ComposeConfigResult> RunCapturedAsync(string[] args, CancellationToken ct) {
        Interlocked.Increment(ref _observed);
        var current = Interlocked.Increment(ref _inFlight);
        // Compare-and-swap rather than Math.Max on a read: two deploys arriving together must not both
        // read the old peak and write the same value back.
        int peak;
        while (current > (peak = Volatile.Read(ref _peak))
               && Interlocked.CompareExchange(ref _peak, current, peak) != peak) { /* retry */ }
        try {
            await Task.Delay(Hold, ct);
        } finally {
            Interlocked.Decrement(ref _inFlight);
        }
        return new ComposeConfigResult(0, """{ "services": { "app": {} } }""", string.Empty);
    }

    protected override Task<(int ExitCode, string Output)> RunAsync(
        string[] args, string? dockerConfigDir, Action<string>? onLine, CancellationToken ct) =>
        Task.FromResult((0, "stubbed compose"));
}

/// <summary>One clone a deploy asked for, as the recording git service saw it.</summary>
/// <param name="Command">
/// <c>clone</c> for a branch-head clone, <c>clone-at-commit</c> for the release-pinned form — the
/// distinction the back-compat guarantee turns on.
/// </param>
/// <param name="RepositoryUrl">The remote, without any embedded token.</param>
/// <param name="Branch">The branch the deploy resolved.</param>
/// <param name="CommitSha">The commit, for <c>clone-at-commit</c> only.</param>
internal sealed record GitInvocation(
    string Command, string RepositoryUrl, string Branch, string? CommitSha = null);

/// <summary>
/// A git clone that materializes an empty checkout directory instead of contacting a remote, and
/// records what it was asked for. Every stack in a test names a repository that does not exist, and a
/// clone is the first thing a deploy does, so nothing downstream is reachable without this.
/// </summary>
/// <remarks>
/// The recording half is what makes "a Git-mode deploy clones exactly as it always did" a checkable
/// claim rather than an intention: the two clone forms are different git commands, and which one a
/// deploy chooses is the first observable difference between the two product modes.
/// </remarks>
internal sealed class StubGitCloneService : GitCloneService {
    /// <summary>The commit the stubbed checkout reports; a plausible 40-character SHA.</summary>
    public const string HeadCommit = "0123456789abcdef0123456789abcdef01234567";

    private readonly List<GitInvocation> _clones = [];

    /// <summary>The commit the last <see cref="CloneAtCommitAsync"/> was asked for, or null for a branch clone.</summary>
    public string? RequestedCommit { get; private set; }

    /// <summary>Every clone this service was asked for, in order.</summary>
    public IReadOnlyList<GitInvocation> Clones {
        get { lock (_clones) return [.. _clones]; }
    }

    public override Task<(int ExitCode, string Output)> CloneAsync(
        string repositoryUrl, string branch, string? token, string targetDir,
        Action<string>? onLine, CancellationToken ct) {
        lock (_clones) _clones.Add(new GitInvocation("clone", repositoryUrl, branch));
        Directory.CreateDirectory(targetDir);
        return Task.FromResult((0, "stubbed clone"));
    }

    public override Task<(int ExitCode, string Output)> CloneAtCommitAsync(
        string repositoryUrl, string branch, string commitSha, string? token, string targetDir,
        Action<string>? onLine, CancellationToken ct) {
        lock (_clones) _clones.Add(new GitInvocation("clone-at-commit", repositoryUrl, branch, commitSha));
        RequestedCommit = commitSha;
        Directory.CreateDirectory(targetDir);
        return Task.FromResult((0, "stubbed clone at commit"));
    }

    /// <summary>The commit a pinned clone was asked for, or the branch clone's stubbed head.</summary>
    public override Task<string?> GetHeadCommitAsync(string repoDir, CancellationToken ct) =>
        Task.FromResult<string?>(RequestedCommit ?? HeadCommit);

    /// <summary>What <c>ls-remote</c> answers; null means the branch could not be resolved.</summary>
    public string? RemoteHead { get; init; }

    /// <summary>
    /// Every branch this service was asked the remote head of. Empty is the assertion in release mode
    /// for a pinned stack, which is not tracking a branch at all.
    /// </summary>
    public List<string> RemoteHeadLookups { get; } = [];

    public override Task<string?> GetRemoteHeadAsync(
        string repositoryUrl, string branch, string? token, CancellationToken ct) {
        lock (RemoteHeadLookups) RemoteHeadLookups.Add(branch);
        return Task.FromResult(RemoteHead);
    }
}
