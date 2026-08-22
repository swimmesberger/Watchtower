using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Watchtower.Application.Services.Acme;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The ACME account key on disk. An ACME account is rate-limited per key and accumulates issuance
/// history, so the properties that matter are all about <em>not</em> losing one: a corrupt file is fatal
/// rather than replaced, a second load reuses what is there, and the registration survives a restart.
/// </summary>
public sealed class AcmeAccountKeyTests : IDisposable {
    private const string DirectoryUrl = "https://ca.test/directory";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "watchtower-acme-account-tests", Guid.NewGuid().ToString("N"));

    private AcmeAccountKey Load() => AcmeAccountKey.Load(_root, DirectoryUrl, NullLogger.Instance);

    [Fact]
    public void AFirstLoad_GeneratesAndPersistsAPrivateKey() {
        using var account = Load();

        var keyPath = Path.Combine(_root, AcmeAccountKey.KeyFileName);
        Assert.True(File.Exists(keyPath));
        Assert.Null(account.AccountUrl);
        Assert.Contains("PRIVATE KEY", File.ReadAllText(keyPath));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyPath));
    }

    [Fact]
    public void ASecondLoad_ReusesTheSameKey() {
        string first;
        using (var account = Load()) first = AcmeJws.Thumbprint(account.Key);

        using var reloaded = Load();

        Assert.Equal(first, AcmeJws.Thumbprint(reloaded.Key));
    }

    /// <summary>
    /// Fatal, and deliberately: silently regenerating would abandon the account the CA associates with
    /// this deployment — including whatever rate-limit allowance it has earned — with nothing in the log
    /// to connect the two.
    /// </summary>
    [Fact]
    public void ACorruptKeyFile_Throws() {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, AcmeAccountKey.KeyFileName), "not a key\n");

        Assert.ThrowsAny<CryptographicException>(() => Load());
    }

    [Fact]
    public void TheAccountUrl_RoundTripsAcrossLoads() {
        using (var account = Load()) {
            account.SetAccountUrl("https://ca.test/acct/42");
            Assert.Equal("https://ca.test/acct/42", account.AccountUrl);
        }

        using var reloaded = Load();
        Assert.Equal("https://ca.test/acct/42", reloaded.AccountUrl);

        var stored = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(_root, AcmeAccountKey.AccountFileName)),
            AcmeJsonContext.Default.AcmeAccountFile);
        Assert.Equal(DirectoryUrl, stored!.DirectoryUrl);
    }

    [Fact]
    public void ClearingTheAccountUrl_SurvivesAReload() {
        using (var account = Load()) {
            account.SetAccountUrl("https://ca.test/acct/42");
            account.ClearAccountUrl();
        }

        using var reloaded = Load();
        Assert.Null(reloaded.AccountUrl);
    }

    /// <summary>
    /// An account exists only at the CA that issued it. A directory folder copied between deployments —
    /// or a directory URL edited in place — must not present one CA's account URL to another.
    /// </summary>
    [Fact]
    public void AnAccountUrlFromAnotherDirectory_IsIgnored() {
        using (var account = Load()) account.SetAccountUrl("https://ca.test/acct/42");

        using var elsewhere = AcmeAccountKey.Load(_root, "https://other-ca.test/directory", NullLogger.Instance);

        Assert.Null(elsewhere.AccountUrl);
    }

    [Fact]
    public void AMissingOrUnreadableAccountFile_MeansNotRegistered() {
        using (var _ = Load()) { }
        File.WriteAllText(Path.Combine(_root, AcmeAccountKey.AccountFileName), "{ not json");

        using var account = Load();

        Assert.Null(account.AccountUrl);
    }

    public void Dispose() {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
