using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.InternalCa;
using Watchtower.Application.Services.PortRoutes;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// A resolver with a table instead of a network: forward and reverse answers a test writes out, a
/// switch that makes every lookup throw — the fail-open case, which is what a LAN with no resolver at
/// all looks like from in here — and one that makes every lookup hang, which is what a nameserver that
/// accepted the query and went quiet looks like.
/// </summary>
internal sealed class TableDnsPreflight : DnsPreflight {
    public Dictionary<string, string[]> Forward { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string[]> Reverse { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Makes every lookup fail the way a dead resolver fails.</summary>
    public bool Throws { get; set; }

    /// <summary>Makes every lookup wait for its own cancellation, and never answer.</summary>
    public bool Hangs { get; set; }

    public override Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken ct) {
        if (Throws) throw new InvalidOperationException("no resolver configured");
        if (Hangs) return HangAsync(ct);
        return Task.FromResult<IReadOnlyList<string>>(
            Forward.TryGetValue(host, out var addresses) ? addresses : []);
    }

    public override Task<IReadOnlyList<string>> ResolveNamesAsync(IPAddress address, CancellationToken ct) {
        if (Throws) throw new InvalidOperationException("no resolver configured");
        if (Hangs) return HangAsync(ct);
        return Task.FromResult<IReadOnlyList<string>>(
            Reverse.TryGetValue(address.ToString(), out var names) ? names : []);
    }

    private static async Task<IReadOnlyList<string>> HangAsync(CancellationToken ct) {
        await Task.Delay(Timeout.Infinite, ct);
        return [];
    }
}

/// <summary>
/// The LAN-name suggestions the Settings page offers as chips, for the LAN names setting of ADR-0033
/// decision 6.
/// </summary>
/// <remarks>
/// Three properties matter more than any individual candidate, and all three are asserted here
/// repeatedly. Every emitted value has to pass <see cref="InternalCaNames.TryParseLanNames"/> — a chip
/// whose only effect is a Save that fails is worse than no chip. Every source has to be fail-open: a
/// daemon that is not there or a resolver that answers nothing produces fewer suggestions, never an
/// error under a field the operator was about to type in. And the address the browser arrived on is a
/// candidate like any other, held to the same rules — which is the whole reason the client is allowed
/// to render what it is sent without checking anything.
/// </remarks>
public sealed class LanNameSuggestionsTests : IDisposable {
    private readonly List<string> _tempFiles = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() {
        foreach (var path in _tempFiles) File.Delete(path);
    }

    // ── resolv.conf parsing ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASearchLine_YieldsItsDomains() {
        var domains = LanNameSuggestions.ParseSearchDomains("""
            nameserver 127.0.0.11
            search lan
            options ndots:0
            """);

        Assert.Equal(["lan"], domains);
    }

    [Fact]
    public void ASearchLineWithSeveralDomains_YieldsAllOfThemInOrder() {
        var domains = LanNameSuggestions.ParseSearchDomains("search lan. home.arpa EXAMPLE.internal");

        Assert.Equal(["lan", "home.arpa", "example.internal"], domains);
    }

    [Fact]
    public void AResolvConfWithNoSearchLine_YieldsNothing() {
        var domains = LanNameSuggestions.ParseSearchDomains("""
            nameserver 192.168.1.1
            options edns0
            """);

        Assert.Empty(domains);
    }

    [Fact]
    public void CommentsAndBlankLines_AreNotDomains() {
        var domains = LanNameSuggestions.ParseSearchDomains("""
            # search commented.invalid
            ; search also-commented.invalid

            search lan # trailing note
            """);

        Assert.Equal(["lan"], domains);
    }

    [Fact]
    public void ALaterSearchLine_ReplacesAnEarlierOne() {
        var domains = LanNameSuggestions.ParseSearchDomains("""
            search first.invalid
            search second.invalid third.invalid
            """);

        Assert.Equal(["second.invalid", "third.invalid"], domains);
    }

    [Fact]
    public async Task AnUnreadableResolvConf_IsNotAFailure() {
        var dns = new TableDnsPreflight();
        dns.Forward["nas"] = ["192.168.1.10"];
        using var docker = DockerHostNamed("nas");
        var service = Build(dns, docker, resolvConfPath: "/no/such/path/resolv.conf");

        var candidates = await service.SuggestAsync(hint: null, configuredLanNames: "", Ct);

        Assert.Equal(["nas"], candidates.Select(c => c.Value));
    }

    // ── Source 1: the address the browser used ───────────────────────────────────────────────────

    [Fact]
    public async Task TheAddressTheBrowserArrivedOn_IsTheFirstCandidateAndIsVerified() {
        var dns = new TableDnsPreflight();
        using var docker = DockerHostNamed(null);

        var candidates = await Build(dns, docker).SuggestAsync("192.168.1.10", "", Ct);

        var candidate = Assert.Single(candidates);
        Assert.Equal("192.168.1.10", candidate.Value);
        Assert.Equal("ip", candidate.Kind);
        Assert.Equal(LanNameSuggestions.BrowserSource, candidate.Source);
        Assert.True(candidate.Verified);
    }

    /// <summary>
    /// It comes first because it is the address the operator is looking at, not because it sorts
    /// highest — an unverified browser address would still lead.
    /// </summary>
    [Fact]
    public async Task TheBrowsersAddress_LeadsEvenWhenOtherCandidatesAreVerifiedToo() {
        var dns = new TableDnsPreflight();
        dns.Reverse["192.168.1.10"] = ["nas.lan"];
        dns.Forward["nas.lan"] = ["192.168.1.10"];
        using var docker = DockerHostNamed("box");

        var candidates = await Build(dns, docker).SuggestAsync("192.168.1.10", "", Ct);

        Assert.Equal(["192.168.1.10", "nas.lan", "box"], candidates.Select(c => c.Value));
        Assert.Equal(LanNameSuggestions.BrowserSource, candidates[0].Source);
    }

    [Fact]
    public async Task AnAddressHint_OffersItsReverseName_VerifiedWhenTheRoundTripAgrees() {
        var dns = new TableDnsPreflight();
        dns.Reverse["192.168.1.10"] = ["nas.lan"];
        dns.Forward["nas.lan"] = ["192.168.1.10"];
        using var docker = DockerHostNamed(null);

        var candidates = await Build(dns, docker).SuggestAsync("192.168.1.10", "", Ct);

        var candidate = Assert.Single(candidates, c => c.Value == "nas.lan");
        Assert.Equal("hostname", candidate.Kind);
        Assert.Equal("reverse-dns", candidate.Source);
        Assert.True(candidate.Verified);
    }

    [Fact]
    public async Task AnAddressHint_StillOffersAReverseNameThatDoesNotResolveBack_Unverified() {
        var dns = new TableDnsPreflight();
        dns.Reverse["192.168.1.10"] = ["nas.lan"];
        dns.Forward["nas.lan"] = ["203.0.113.7"];
        using var docker = DockerHostNamed(null);

        var candidates = await Build(dns, docker).SuggestAsync("192.168.1.10", "", Ct);

        var candidate = Assert.Single(candidates, c => c.Value == "nas.lan");
        Assert.False(candidate.Verified);
    }

    /// <summary>
    /// A resolver may answer in the mapped spelling, and <c>::ffff:192.168.1.10</c> is the address the
    /// round trip started from. Comparing the strings would call that a mismatch.
    /// </summary>
    [Fact]
    public async Task AMappedAddressInTheForwardAnswer_StillVerifiesTheRoundTrip() {
        var dns = new TableDnsPreflight();
        dns.Reverse["192.168.1.10"] = ["nas.lan"];
        dns.Forward["nas.lan"] = ["::ffff:192.168.1.10"];
        using var docker = DockerHostNamed(null);

        var candidates = await Build(dns, docker).SuggestAsync("192.168.1.10", "", Ct);

        Assert.True(Assert.Single(candidates, c => c.Value == "nas.lan").Verified);
    }

    [Fact]
    public async Task AHostnameHint_OffersTheAddressesItResolvesTo() {
        var dns = new TableDnsPreflight();
        dns.Forward["nas.lan"] = ["192.168.1.10", "192.168.1.11"];
        dns.Reverse["192.168.1.10"] = ["nas.lan"];
        using var docker = DockerHostNamed(null);

        var candidates = await Build(dns, docker).SuggestAsync("nas.lan", "", Ct);

        // The hint leads, then the verified address, then the one whose PTR does not name it back.
        Assert.Equal(["nas.lan", "192.168.1.10", "192.168.1.11"], candidates.Select(c => c.Value));
        Assert.All(candidates.Skip(1), c => Assert.Equal("ip", c.Kind));
        Assert.All(candidates.Skip(1), c => Assert.Equal("forward-dns", c.Source));
        Assert.True(candidates[1].Verified);
        Assert.False(candidates[2].Verified);
    }

    /// <summary>
    /// A hint spelled the mapped way is the same address as the plain one, so the two must not both be
    /// offered — and it is the plain spelling that goes into a certificate.
    /// </summary>
    [Fact]
    public async Task AMappedHint_IsOfferedAsThePlainAddress() {
        var dns = new TableDnsPreflight();
        using var docker = DockerHostNamed(null);

        var candidates = await Build(dns, docker).SuggestAsync("::ffff:192.168.1.10", "", Ct);

        Assert.Equal(["192.168.1.10"], candidates.Select(c => c.Value));
    }

    // ── Source 2: the Docker host's own name ─────────────────────────────────────────────────────

    [Fact]
    public async Task TheDockerHostsName_IsOfferedEvenWhenItDoesNotResolveHere() {
        var dns = new TableDnsPreflight();
        using var docker = DockerHostNamed("nas");

        var candidates = await Build(dns, docker).SuggestAsync(hint: null, "", Ct);

        var candidate = Assert.Single(candidates);
        Assert.Equal("nas", candidate.Value);
        Assert.Equal("docker-host", candidate.Source);
        Assert.False(candidate.Verified);
    }

    [Fact]
    public async Task ASearchDomainsFqdn_IsOfferedOnlyWhenItResolves() {
        var dns = new TableDnsPreflight();
        dns.Forward["nas.lan"] = ["192.168.1.10"];
        using var docker = DockerHostNamed("nas");
        var service = Build(dns, docker, resolvConf: "search lan home.arpa\n");

        var candidates = await service.SuggestAsync(hint: null, "", Ct);

        // nas.home.arpa resolves to nothing, so it is a guess confirmed by nothing and is not offered.
        Assert.Equal(["nas.lan", "nas"], candidates.Select(c => c.Value));
        Assert.Equal("docker-search-domain", candidates[0].Source);
        Assert.True(candidates[0].Verified);
    }

    // ── Exclusions ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every one of these is a value a browser can legally be pointed at, which is exactly why the hint
    /// is held to the same rules as everything else rather than trusted for arriving from the client.
    /// </summary>
    [Theory]
    [InlineData("localhost")]
    [InlineData("app.localhost")]
    [InlineData("host.docker.internal")]
    [InlineData("gateway.docker.internal")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.5")]
    [InlineData("::1")]
    [InlineData("169.254.10.5")]
    [InlineData("fe80::1")]
    [InlineData("0.0.0.0")]
    [InlineData("::ffff:127.0.0.2")]
    [InlineData("::ffff:169.254.10.5")]
    [InlineData("::ffff:0.0.0.0")]
    public async Task AnAddressThatMeansTheAskingMachine_IsNotOfferedEvenAsTheHint(string excluded) {
        var dns = new TableDnsPreflight();
        dns.Reverse["192.168.1.10"] = [excluded];
        dns.Forward[excluded] = ["192.168.1.10"];
        using var docker = DockerHostNamed(excluded);

        var fromTheBrowser = await Build(dns, docker).SuggestAsync(excluded, "", Ct);
        var fromASource = await Build(dns, DockerHostNamed(excluded)).SuggestAsync("192.168.1.10", "", Ct);

        Assert.Empty(fromTheBrowser);
        Assert.DoesNotContain(excluded, fromASource.Select(c => c.Value));
    }

    /// <summary>
    /// A browser will happily hold <c>my_nas</c> in its address bar, and the certificate's own parser
    /// refuses it — so a client that made its own chip out of the address bar would offer a Save that
    /// fails. The server runs the parser instead.
    /// </summary>
    [Fact]
    public async Task AHintTheCertificateCouldNotName_IsSilentlyAbsent() {
        var dns = new TableDnsPreflight();
        using var docker = DockerHostNamed(null);

        var candidates = await Build(dns, docker).SuggestAsync("my_nas", "", Ct);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task WhatTheSettingAlreadyNames_IsNotOfferedAgain() {
        var dns = new TableDnsPreflight();
        dns.Reverse["192.168.1.10"] = ["nas.lan"];
        dns.Forward["nas.lan"] = ["192.168.1.10"];
        using var docker = DockerHostNamed("nas");

        var candidates = await Build(dns, docker).SuggestAsync("192.168.1.10", "NAS.lan\n192.168.1.10", Ct);

        Assert.Equal(["nas"], candidates.Select(c => c.Value));
    }

    /// <summary>
    /// The setting holds the plain spelling and the browser arrived on the mapped one. They are one
    /// address, so the chip has to be gone — otherwise it never disappears however often it is clicked.
    /// </summary>
    [Fact]
    public async Task AConfiguredAddress_SuppressesItsMappedSpelling() {
        var dns = new TableDnsPreflight();
        using var docker = DockerHostNamed(null);

        var candidates = await Build(dns, docker).SuggestAsync("::ffff:192.168.1.10", "192.168.1.10", Ct);

        Assert.Empty(candidates);
    }

    /// <summary>
    /// The fully qualified spelling of a configured name is the same name, so the chip disappears when
    /// it is added — whichever of the two spellings each side used.
    /// </summary>
    [Fact]
    public async Task AConfiguredName_SuppressesItsFullyQualifiedSpelling() {
        var dns = new TableDnsPreflight();
        using var docker = DockerHostNamed(null);

        var candidates = await Build(dns, docker).SuggestAsync("nas.lan.", "nas.lan", Ct);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task ASettingThatDoesNotParseAtAll_StillSuppressesTheNamesItHolds() {
        var dns = new TableDnsPreflight();
        dns.Reverse["192.168.1.10"] = ["nas.lan"];
        using var docker = DockerHostNamed(null);

        // The second entry is junk, so TryParseLanNames refuses the whole value — the raw entries are
        // read anyway, because re-offering a name that is already in the box is the worse answer.
        var candidates = await Build(dns, docker).SuggestAsync("192.168.1.10", "nas.lan, not a name, 192.168.1.10", Ct);

        Assert.Empty(candidates);
    }

    // ── Dedupe, ordering and the parse contract ──────────────────────────────────────────────────

    [Fact]
    public async Task OneValueFoundTwice_IsOfferedOnceAndKeepsTheVerifiedAnswer() {
        var dns = new TableDnsPreflight();
        // The hint's reverse name and the Docker host's name are the same string, but only the hint's
        // round-trip verifies it.
        dns.Reverse["192.168.1.10"] = ["nas"];
        dns.Forward["nas"] = ["192.168.1.10"];
        using var docker = DockerHostNamed("NAS");

        var candidates = await Build(dns, docker).SuggestAsync("192.168.1.10", "", Ct);

        var candidate = Assert.Single(candidates, c => c.Value == "nas");
        Assert.True(candidate.Verified);
        Assert.Equal("reverse-dns", candidate.Source);
    }

    [Fact]
    public async Task AnUnverifiedFindThenAVerifiedOne_KeepsTheVerifiedSource() {
        var dns = new TableDnsPreflight();
        // The hint's reverse name does not resolve back, so it arrives unverified first; the Docker host
        // is called the same thing and does resolve, which is the better answer about the same value.
        dns.Reverse["192.168.1.10"] = ["nas"];
        dns.Forward["nas"] = ["203.0.113.7"];
        using var docker = DockerHostNamed("nas");

        var candidates = await Build(dns, docker).SuggestAsync("192.168.1.10", "", Ct);

        var candidate = Assert.Single(candidates, c => c.Value == "nas");
        Assert.True(candidate.Verified);
        Assert.Equal("docker-host", candidate.Source);
    }

    /// <summary>
    /// The Docker host is called what the browser arrived on. One chip, and it keeps the browser's
    /// reading — the page was served over it, which no resolver can better.
    /// </summary>
    [Fact]
    public async Task ADockerHostNameEqualToTheHint_IsOneChip() {
        var dns = new TableDnsPreflight();
        using var docker = DockerHostNamed("nas.lan");

        var candidates = await Build(dns, docker).SuggestAsync("nas.lan", "", Ct);

        var candidate = Assert.Single(candidates);
        Assert.Equal("nas.lan", candidate.Value);
        Assert.Equal(LanNameSuggestions.BrowserSource, candidate.Source);
    }

    [Fact]
    public async Task VerifiedCandidatesComeFirst_AfterTheBrowsers() {
        var dns = new TableDnsPreflight();
        dns.Forward["nas.lan"] = ["192.168.1.11", "192.168.1.10"];
        dns.Reverse["192.168.1.10"] = ["nas.lan"];
        using var docker = DockerHostNamed("box");

        var candidates = await Build(dns, docker).SuggestAsync("nas.lan", "", Ct);

        Assert.Equal(["nas.lan", "192.168.1.10", "192.168.1.11", "box"], candidates.Select(c => c.Value));
        Assert.True(candidates[1].Verified);
        Assert.All(candidates.Skip(2), c => Assert.False(c.Verified));
    }

    /// <summary>
    /// The contract the client leans on entirely: whatever arrives, clicking it produces a setting the
    /// Save accepts. Run over a hint of each kind so both branches of the first source are covered.
    /// </summary>
    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("nas.lan")]
    public async Task EveryEmittedValue_IsOneTheSettingWouldAccept(string hint) {
        var dns = new TableDnsPreflight();
        // Spellings the setting's parser has an opinion about: a trailing dot, mixed case, an
        // inet_aton form it refuses outright, and a name with an underscore in it.
        dns.Reverse["192.168.1.10"] = ["NAS.Lan.", "192.168.001.010", "my_nas"];
        dns.Forward["nas.lan"] = ["192.168.1.10", "192.168.001.010"];
        using var docker = DockerHostNamed("Box.");
        dns.Forward["box.lan"] = ["192.168.1.12"];
        var service = Build(dns, docker, resolvConf: "search LAN\n");

        var candidates = await service.SuggestAsync(hint, "", Ct);

        Assert.NotEmpty(candidates);
        Assert.DoesNotContain("192.168.001.010", candidates.Select(c => c.Value));
        Assert.DoesNotContain("my_nas", candidates.Select(c => c.Value));
        foreach (var candidate in candidates) {
            Assert.True(
                InternalCaNames.TryParseLanNames(candidate.Value, out var names, out var ips, out var reason),
                $"'{candidate.Value}' would be refused by the setting: {reason}");
            Assert.Equal(1, names.Count + ips.Count);
            Assert.Equal(names.Count == 1 ? "hostname" : "ip", candidate.Kind);
        }
    }

    // ── Fail-open, and the one failure that is not ───────────────────────────────────────────────

    [Fact]
    public async Task AResolverThatThrows_ProducesNoCandidatesAndNoException() {
        var dns = new TableDnsPreflight { Throws = true };
        using var docker = DockerHostNamed(null);

        var candidates = await Build(dns, docker).SuggestAsync("my_nas", "", Ct);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task ADaemonThatCannotAnswer_ProducesNoCandidatesAndNoException() {
        var dns = new TableDnsPreflight();
        using var docker = DockerClientEstate.Create(TimeSpan.FromMinutes(1));
        docker.Default.Responder = request => request.RequestUri!.AbsolutePath.EndsWith("/info", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) {
                Content = new StringContent("""{"message":"daemon is not running"}""", Encoding.UTF8, "application/json"),
            }
            : null;

        var candidates = await Build(dns, docker).SuggestAsync(hint: null, "", Ct);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task BothSeamsDown_IsStillAnEmptyAnswerRatherThanAFailure() {
        var dns = new TableDnsPreflight { Throws = true };
        using var docker = DockerClientEstate.Create(TimeSpan.FromMinutes(1));
        docker.Default.Responder = _ => throw new HttpRequestException("no socket at /var/run/docker.sock");

        var candidates = await Build(dns, docker).SuggestAsync("my_nas", "", Ct);

        Assert.Empty(candidates);
    }

    /// <summary>
    /// A nameserver that accepted every query and went quiet. The per-lookup cap and the total budget
    /// are what stand between that and a Settings page that hangs, so the bound is asserted rather than
    /// trusted — generously, since this measures wall-clock time on a shared runner.
    /// </summary>
    [Fact]
    public async Task AResolverThatNeverAnswers_IsBoundedByTheBudget() {
        var dns = new TableDnsPreflight { Hangs = true };
        using var docker = DockerHostNamed("nas");
        var service = Build(dns, docker, resolvConf: "search lan home.arpa\n");

        var started = Stopwatch.StartNew();
        var candidates = await service.SuggestAsync("nas.lan", "", Ct);
        started.Stop();

        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(6),
            $"The pass took {started.Elapsed}, so a hanging resolver is not bounded.");
        // The hint and the Docker host's name survive — neither needs an answer — and nothing that
        // depended on one does.
        Assert.Equal(["nas.lan", "nas"], candidates.Select(c => c.Value));
    }

    /// <summary>
    /// The budget expiring is fewer candidates; the caller cancelling is nobody waiting for an answer.
    /// Swallowing the second would mean spending the whole budget to produce a 200 for a closed socket.
    /// </summary>
    [Fact]
    public async Task ACancelledCaller_IsToldSoRatherThanHandedAnEmptyList() {
        var dns = new TableDnsPreflight();
        dns.Reverse["192.168.1.10"] = ["nas.lan"];
        using var docker = DockerHostNamed("nas");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Build(dns, docker).SuggestAsync("192.168.1.10", "", cancelled.Token));
    }

    // ── Wiring ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>A daemon whose <c>/info</c> reports <paramref name="name"/> as the host's name.</summary>
    private static DockerClientEstate DockerHostNamed(string? name) {
        var estate = DockerClientEstate.Create(TimeSpan.FromMinutes(1));
        estate.Default.Responder = request =>
            request.RequestUri!.AbsolutePath.EndsWith("/info", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(
                        name is null ? "{}" : $$"""{"Name":"{{name}}"}""",
                        Encoding.UTF8,
                        "application/json"),
                }
                : null;
        return estate;
    }

    private LanNameSuggestions Build(
        TableDnsPreflight dns,
        DockerClientEstate docker,
        string? resolvConf = null,
        string? resolvConfPath = null) {
        // No search domains at all unless a test says so — the machine's own /etc/resolv.conf must not
        // decide what this suite asserts.
        var path = resolvConfPath ?? Path.Combine(Path.GetTempPath(), $"resolv-{Guid.NewGuid():N}.conf");
        if (resolvConfPath is null && resolvConf is not null) {
            File.WriteAllText(path, resolvConf);
            _tempFiles.Add(path);
        }
        return new LanNameSuggestions(dns, docker.Client, NullLogger<LanNameSuggestions>.Instance) {
            ResolvConfPath = path,
        };
    }
}
