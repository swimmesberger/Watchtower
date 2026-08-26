using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The Bearer-challenge half of <c>GetRemoteDigestAsync</c>'s registry auth. The failure this
/// guards: an htpasswd registry answers 401 with <c>Basic realm="Registry Realm"</c>, and treating
/// that display string as a token endpoint hands HttpClient a relative URI — every update check
/// against the registry then throws "An invalid request URI was provided".
/// </summary>
public sealed class DockerRegistryAuthTests {
    [Fact]
    public void ADisplayStringRealm_IsNotATokenEndpoint() =>
        Assert.Null(DockerEngineClient.ResolveBearerTokenUrl(
            "Registry Realm", service: null, scope: null, repoPath: "immocentral-web"));

    [Fact]
    public void ARelativeRealm_IsNotATokenEndpoint() =>
        Assert.Null(DockerEngineClient.ResolveBearerTokenUrl(
            "/auth/token", service: null, scope: null, repoPath: "immocentral-web"));

    [Fact]
    public void AMissingRealm_YieldsNoUrl() =>
        Assert.Null(DockerEngineClient.ResolveBearerTokenUrl(
            null, service: "registry", scope: "repository:a:pull", repoPath: "a"));

    [Fact]
    public void AnAbsoluteRealm_CarriesServiceAndScope() =>
        Assert.Equal(
            "https://auth.example.com/token?service=registry.example.com&scope=repository%3Amyorg%2Fapp%3Apull",
            DockerEngineClient.ResolveBearerTokenUrl(
                "https://auth.example.com/token", "registry.example.com", "repository:myorg/app:pull", "myorg/app"));

    [Fact]
    public void AMissingScope_DefaultsToRepositoryPull() =>
        Assert.Equal(
            "https://auth.example.com/token?service=&scope=repository%3Amyorg%2Fapp%3Apull",
            DockerEngineClient.ResolveBearerTokenUrl(
                "https://auth.example.com/token", service: null, scope: null, repoPath: "myorg/app"));
}
