using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

// The non-admin account is created through the same helper the access tests use, so it is made the way
// the login endpoint makes one rather than by hand.

namespace Watchtower.Api.Tests;

/// <summary>
/// Who may download a full backup bundle (ADR-0027 §4). This is the sharpest authorization edge in the
/// feature: the tar carries the key-protection secret, the backup passphrase and the storage credentials
/// in plain text, so anyone who can fetch it can stand the instance up somewhere else. It is therefore
/// gated on <em>admin of the operator realm</em>, not merely on holding a valid session.
/// </summary>
public sealed class InstanceBundleEndpointTests {
    private const string BundleUrl = "/api/instance/bundle";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WatchtowerApiFactory AuthEnabled() => new(("Watchtower:Auth:Enabled", "true"));

    /// <summary>Stages a bundle file so the endpoint has something to serve.</summary>
    private static string StageBundle(WatchtowerApiFactory factory, string content = "bundle-bytes") {
        var directory = Directory.CreateTempSubdirectory("wt-bundle-endpoint").FullName;
        var path = Path.Combine(directory, "watchtower-bundle_test.tar");
        File.WriteAllText(path, content);
        factory.Services.GetRequiredService<BundleExportState>().Replace(new StagedBundle(
            path, "watchtower-bundle_test.tar", content.Length, DateTimeOffset.UtcNow,
            StackCount: 1, MissingStackCount: 0));
        return path;
    }

    /// <summary>Signs in and returns the <c>__wt_sso</c> cookie pair.</summary>
    private static async Task<string> SignInAsync(HttpClient client, string userName, string password) {
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { userName, password }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string? cookie) {
        var request = new HttpRequestMessage(HttpMethod.Get, BundleUrl);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request, Ct);
    }

    /// <summary>The password <see cref="AccessTestEstate.AddUserAsync"/> gives a new account.</summary>
    private const string OperatorPassword = "correct-horse-battery";

    [Fact]
    public async Task AnAnonymousCallerIsChallenged() {
        using var factory = AuthEnabled();
        using var client = factory.CreateApiClient();
        StageBundle(factory);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, cookie: null)).StatusCode);
    }

    [Fact]
    public async Task AnOperatorWithoutTheAdminRoleIsRefused() {
        // A signed-in operator can see the backup history and run stack backups. Taking the whole
        // instance off the box is a different privilege, and this is where the two part company.
        using var factory = AuthEnabled();
        using var client = factory.CreateApiClient();
        StageBundle(factory);
        await factory.AddUserAsync("olive", password: OperatorPassword);

        var cookie = await SignInAsync(client, "olive", OperatorPassword);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(client, cookie)).StatusCode);
    }

    [Fact]
    public async Task AnAdminGetsTheTarWithItsFileNameAndLength() {
        using var factory = AuthEnabled();
        using var client = factory.CreateApiClient();
        StageBundle(factory);
        var cookie = await SignInAsync(client, "admin", WatchtowerApiFactory.AdminPassword);

        var response = await GetAsync(client, cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-tar", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("watchtower-bundle_test.tar", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        // Declared up front so the browser can show real progress on a file that is typically large.
        Assert.Equal("bundle-bytes".Length, response.Content.Headers.ContentLength);
        Assert.Equal("bundle-bytes", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task TheDownloadIsAudited() {
        // The bundle leaving the box is the event worth being able to point at afterwards.
        using var factory = AuthEnabled();
        using var client = factory.CreateApiClient();
        StageBundle(factory);
        var cookie = await SignInAsync(client, "admin", WatchtowerApiFactory.AdminPassword);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, cookie)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        // The audit writer is best-effort and out-of-band, so give it a moment to land its row.
        AuditEvent? row = null;
        for (var attempt = 0; attempt < 50 && row is null; attempt++) {
            row = await db.AuditEvents.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Action == "bundle.download", Ct);
            if (row is null) await Task.Delay(100, Ct);
        }

        Assert.NotNull(row);
        Assert.Equal("backups", row.Category);
        Assert.Equal("admin", row.Actor);
        Assert.Contains("watchtower-bundle_test.tar", row.Detail);
    }

    [Fact]
    public async Task WithNothingStagedAnAdminGetsA404() {
        // Not an empty 200: "there is no bundle" and "here is an empty bundle" are different answers.
        using var factory = AuthEnabled();
        using var client = factory.CreateApiClient();
        var cookie = await SignInAsync(client, "admin", WatchtowerApiFactory.AdminPassword);

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, cookie)).StatusCode);
    }
}
