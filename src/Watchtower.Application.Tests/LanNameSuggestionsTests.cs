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
/// A resolver with a table instead of a network: forward and reverse answers a test writes out, and a
/// switch that makes every lookup throw — the fail-open case, which is what a LAN with no resolver at
/// all looks like from in here.
/// </summary>
internal sealed class TableDnsPreflight : DnsPreflight {
    public Dictionary<string, string[]> Forward { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string[]> Reverse { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Makes every lookup fail the way a dead resolver fails.</summary>
    public bool Throws { get; set; }

    public override Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken ct) {
        if (Throws) throw new InvalidOperationException("no resolver configured");
        return Task.FromResult<IReadOnlyList<string>>(
            Forward.TryGetValue(host, out var addresses) ? addresses : []);
    }

    public override Task<IReadOnlyList<string>> ResolveNamesAsync(IPAddress address, CancellationToken ct) {
        if (Throws) throw new InvalidOperationException("no resolver configured");
        return Task.FromResult<IReadOnlyList<string>>(
            Reverse.TryGetValue(address.ToString(), out var names) ? names : []);
    }
}

/// <summary>
/// The LAN-name suggestions the Settings page offers as chips (ADR-0033 decision 6).
/// </summary>
/// <remarks>
/// Two properties matter more than any individual candidate, and both are asserted here repeatedly.
/// Every emitted value has to pass <see cref="InternalCaNames.TryParseLanNames"/> — a chip whose only
/// effect is a Save that fails is worse than no chip. And every source has to be fail-open: a daemon
/// that is not there or a resolver that answers nothing produces fewer suggestions, never an error
/// under a field the operator was about to type in.
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
    public async Task AnAddressHint_OffersItsReverseName_VerifiedWhenTheRoundTripAgrees() {
        var dns = new TableDnsPreflight();
        dns.Reverse["192.168.1.10"] = ["nas.lan"];
        dns.Forward["nas.lan"] = ["192.168.1.10"];
        using var docker = DockerHostNamed(null);

        var candidates = await Build(dns, docker).SuggestAsync("192.168.1.10", "", Ct);

        var candidate = Assert.Single(candidates);
        Assert.Equal("nas.lan", candidate.Value);
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

        var candidate = Assert.Single(candidates);
        Assert.Equal("nas.lan", candidate.Value);
        Assert.False(candidate.Verified);
    }

    [Fact]
    public async Task AHostnameHint_OffersTheAddressesItResolvesTo() {
        var dns = new TableDnsPreflight();
        dns.Forward["nas.lan"] = ["192.168.1.10", "192.168.1.11"];
        dns.Reverse["192.168.1.10"] = ["nas.lan"];
        using var docker = DockerHostNamed(null);

        var candidates = await Build(dns, docker).SuggestAsync("nas.lan", "", Ct);

        Assert.Equal(["192.168.1.10", "192.168.1.11"], candidates.Select(c => c.Value));
        Assert.All(candidates, c => Assert.Equal("ip", c.Kind));
        Assert.All(candidates, c => Assert.Equal("forward-dns", c.Source));
        // Verified-first ordering, and only the address whose PTR names the hint back is verified.
        Assert.True(candidates[0].Verified);
        Assert.False(candidates[1].Verified);
    }

    [Fact]
    public async Task TheHintItself_IsNeverOfferedBack() {
        var dns = new TableDnsPreflight();
        // A resolver that answers the hint with itself — the shape a hosts-file entry produces.
        dns.Forward["nas.lan"] = ["192.168.1.10"];
        dns.Reverse["192.168.1.10"] = ["nas.lan"];
        using var docker = DockerHostNamed(null);

        var candidates = await Build(dns, docker).SuggestAsync("nas.lan", "", Ct);

        Assert.DoesNotContain("nas.lan", candidates.Select(c => c.Value));
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

    [Theory]
    [InlineData("localhost")]
    [InlineData("app.localhost")]
    [InlineData("host.docker.internal")]
    [InlineData("gateway.docker.internal")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("169.254.10.5")]
    [InlineData("fe80::1")]
    [InlineData("0.0.0.0")]
    public async Task NamesAndAddressesThatMeanThisMachine_AreNeverOffered(string reverseName) {
        var dns = new TableDnsPreflight();
        dns.Reverse["192.168.1.10"] = [reverseName];
        dns.Forward[reverseName] = ["192.168.1.10"];
        using var docker = DockerHostNamed(reverseName);

        var candidates = await Build(dns, docker).SuggestAsync("192.168.1.10", "", Ct);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task AnExcludedHint_ContributesNothing() {
        var dns = new TableDnsPreflight();
        dns.Reverse["127.0.0.1"] = ["nas.lan"];
        using var docker = DockerHostNamed(null);

        var candidates = await Build(dns, docker).SuggestAsync("127.0.0.1", "", Ct);

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

    [Fact]
    public async Task ASettingThatDoesNotParseAtAll_StillSuppressesTheNamesItHolds() {
        var dns = new TableDnsPreflight();
        dns.Reverse["192.168.1.10"] = ["nas.lan"];
        using var docker = DockerHostNamed(null);

        // The second entry is junk, so TryParseLanNames refuses the whole value — the raw entries are
        // read anyway, because re-offering a name that is already in the box is the worse answer.
        var candidates = await Build(dns, docker).SuggestAsync("192.168.1.10", "nas.lan, not a name", Ct);

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

        var candidate = Assert.Single(candidates);
        Assert.Equal("nas", candidate.Value);
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

        var candidate = Assert.Single(candidates);
        Assert.True(candidate.Verified);
        Assert.Equal("docker-host", candidate.Source);
    }

    [Fact]
    public async Task VerifiedCandidatesComeFirst() {
        var dns = new TableDnsPreflight();
        dns.Forward["nas.lan"] = ["192.168.1.11", "192.168.1.10"];
        dns.Reverse["192.168.1.10"] = ["nas.lan"];
        using var docker = DockerHostNamed("box");

        var candidates = await Build(dns, docker).SuggestAsync("nas.lan", "", Ct);

        Assert.Equal(["192.168.1.10", "192.168.1.11", "box"], candidates.Select(c => c.Value));
        Assert.True(candidates[0].Verified);
        Assert.All(candidates.Skip(1), c => Assert.False(c.Verified));
    }

    [Fact]
    public async Task EveryEmittedValue_IsOneTheSettingWouldAccept() {
        var dns = new TableDnsPreflight();
        // Spellings the setting's parser has an opinion about: a trailing dot, mixed case, and an
        // inet_aton form it refuses outright.
        dns.Reverse["192.168.1.10"] = ["NAS.Lan.", "192.168.001.010"];
        dns.Forward["nas.lan"] = ["192.168.1.10"];
        using var docker = DockerHostNamed("Box.");
        var service = Build(dns, docker, resolvConf: "search LAN\n");
        dns.Forward["box.lan"] = ["192.168.1.12"];

        var candidates = await service.SuggestAsync("192.168.1.10", "", Ct);

        Assert.NotEmpty(candidates);
        Assert.DoesNotContain("192.168.001.010", candidates.Select(c => c.Value));
        foreach (var candidate in candidates) {
            Assert.True(
                InternalCaNames.TryParseLanNames(candidate.Value, out var names, out var ips, out var reason),
                $"'{candidate.Value}' would be refused by the setting: {reason}");
            Assert.Equal(1, names.Count + ips.Count);
            Assert.Equal(candidate.Kind, names.Count == 1 ? "hostname" : "ip");
        }
    }

    // ── Fail-open ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AResolverThatThrows_ProducesNoCandidatesAndNoException() {
        var dns = new TableDnsPreflight { Throws = true };
        using var docker = DockerHostNamed(null);

        var candidates = await Build(dns, docker).SuggestAsync("192.168.1.10", "", Ct);

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

        var candidates = await Build(dns, docker).SuggestAsync("nas.lan", "", Ct);

        Assert.Empty(candidates);
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
        var service = new LanNameSuggestions(dns, docker.Client, NullLogger<LanNameSuggestions>.Instance);
        if (resolvConfPath is not null) {
            service.ResolvConfPath = resolvConfPath;
        } else if (resolvConf is not null) {
            var path = Path.Combine(Path.GetTempPath(), $"resolv-{Guid.NewGuid():N}.conf");
            File.WriteAllText(path, resolvConf);
            _tempFiles.Add(path);
            service.ResolvConfPath = path;
        } else {
            // No search domains at all unless a test says so — the host's own /etc/resolv.conf must not
            // decide what this suite asserts.
            service.ResolvConfPath = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.conf");
        }
        return service;
    }
}
