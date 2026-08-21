using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Toolchain detection over a working tree is a heuristic; these tests pin exactly which signals
/// count, how versions normalize, and — most importantly — that workflow-declared versions beat
/// manifest-derived ones, since the workflow names what jobs will actually install.
/// </summary>
public sealed class CiToolchainDetectorTests : IDisposable {
    private readonly string _root = Directory.CreateTempSubdirectory("watchtower-ci-detect-").FullName;

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private void WriteFile(string relativePath, string content) {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ── Tree-level detection ─────────────────────────────────────────────────

    [Fact]
    public void Detect_EmptyTree_YieldsEmptyProfile() {
        var profile = CiToolchainDetector.Detect(_root);
        Assert.True(profile.IsEmpty);
        Assert.Empty(profile.Toolchains);
        Assert.False(profile.HasDockerfile);
    }

    [Fact]
    public void Detect_MissingRoot_YieldsEmptyProfileInsteadOfThrowing() {
        var profile = CiToolchainDetector.Detect(Path.Combine(_root, "does-not-exist"));
        Assert.True(profile.IsEmpty);
    }

    [Fact]
    public void Detect_ReadsManifestsAndDockerfile() {
        WriteFile("global.json", """{ "sdk": { "version": "10.0.100" } }""");
        WriteFile("src/App/App.csproj", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        WriteFile(".nvmrc", "v22.4.1\n");
        WriteFile("go.mod", "module example.com/app\n\ngo 1.24.1\n");
        WriteFile("Dockerfile", "FROM scratch\n");

        var profile = CiToolchainDetector.Detect(_root);

        Assert.True(profile.HasDockerfile);
        Assert.Equal([
            new CiToolchain("dotnet", "10.0", "global.json"),
            new CiToolchain("go", "1.24.1", "go.mod"),
            new CiToolchain("node", "22.4.1", ".nvmrc"),
        ], profile.Toolchains);
    }

    [Fact]
    public void Detect_WorkflowVersionsSupersedeManifestVersionsOfTheSameKind() {
        // The manifest says .NET 8, but the workflow — what jobs actually install — says 10.
        WriteFile("global.json", """{ "sdk": { "version": "8.0.100" } }""");
        WriteFile(".nvmrc", "20");
        WriteFile("go.mod", "module m\ngo 1.24\n");
        WriteFile(".github/workflows/ci.yml", """
            jobs:
              build:
                runs-on: [self-hosted]
                steps:
                  - uses: actions/checkout@v4
                  - uses: actions/setup-dotnet@v4
                    with:
                      dotnet-version: 10.0.x
            """);

        var profile = CiToolchainDetector.Detect(_root);

        // dotnet comes only from the workflow; node/go kinds keep their manifest signal.
        Assert.Equal([
            new CiToolchain("dotnet", "10.0", "workflow"),
            new CiToolchain("go", "1.24", "go.mod"),
            new CiToolchain("node", "20", ".nvmrc"),
        ], profile.Toolchains);
    }

    [Fact]
    public void Detect_SkipsDependencyDirectories() {
        WriteFile("node_modules/dep/dep.csproj", "<Project><PropertyGroup><TargetFramework>net6.0</TargetFramework></PropertyGroup></Project>");
        WriteFile("node_modules/dep/Dockerfile", "FROM scratch");

        var profile = CiToolchainDetector.Detect(_root);

        Assert.True(profile.IsEmpty);
    }

    [Fact]
    public void Detect_MalformedManifestsContributeNothing() {
        WriteFile("global.json", "{ not json");
        WriteFile("package.json", "also not json");
        WriteFile("go.mod", "module only, no go directive\n");

        var profile = CiToolchainDetector.Detect(_root);

        Assert.Empty(profile.Toolchains);
    }

    // ── Workflow parsing ─────────────────────────────────────────────────────

    [Fact]
    public void ParseWorkflow_ReadsInlineBlockScalarAndFlowListVersions() {
        var lines = """
            jobs:
              build:
                steps:
                  - uses: actions/setup-node@v4
                    with:
                      node-version: 22
                  - uses: actions/setup-dotnet@v4
                    with:
                      dotnet-version: |
                        8.0.x
                        10.0.x
                  - uses: actions/setup-go@v5
                    with:
                      go-version: [ '1.23', '1.24' ]
            """.Split('\n');

        var result = CiToolchainDetector.ParseWorkflow(lines);

        Assert.Equal([
            new CiToolchain("node", "22", "workflow"),
            new CiToolchain("dotnet", "8.0", "workflow"),
            new CiToolchain("dotnet", "10.0", "workflow"),
            new CiToolchain("go", "1.23", "workflow"),
            new CiToolchain("go", "1.24", "workflow"),
        ], result);
    }

    [Fact]
    public void ParseWorkflow_IgnoresMatrixExpressionsAndForeignVersionKeys() {
        var lines = """
            steps:
              - uses: actions/setup-node@v4
                with:
                  node-version: ${{ matrix.node }}
              - uses: actions/setup-dotnet@v4
                with:
                  node-version: 22
            """.Split('\n');

        // A matrix value cannot be resolved statically, and a node-version under setup-dotnet is
        // someone else's input — neither may produce a toolchain.
        Assert.Empty(CiToolchainDetector.ParseWorkflow(lines));
    }

    [Fact]
    public void ParseWorkflow_StopsScanningAtTheNextStep() {
        var lines = """
            steps:
              - uses: actions/setup-dotnet@v4
              - name: build
                run: dotnet build
              - uses: actions/setup-node@v4
                with:
                  node-version: 22
            """.Split('\n');

        // setup-dotnet has no version input of its own; it must not adopt the node step's.
        Assert.Equal([new CiToolchain("node", "22", "workflow")], CiToolchainDetector.ParseWorkflow(lines));
    }

    // ── Version normalization ────────────────────────────────────────────────

    [Theory]
    [InlineData("10", "10.0")]
    [InlineData("10.0", "10.0")]
    [InlineData("10.0.x", "10.0")]
    [InlineData("10.0.100", "10.0")]
    [InlineData("8.0.402", "8.0")]
    [InlineData("''", null)]
    [InlineData("latest", null)]
    public void NormalizeDotnet_ProducesChannels(string raw, string? expected) =>
        Assert.Equal(expected, CiToolchainDetector.NormalizeDotnetVersion(raw));

    [Theory]
    [InlineData("22", "22")]
    [InlineData("v22", "22")]
    [InlineData("22.x", "22")]
    [InlineData("22.4", "22")]
    [InlineData("22.4.1", "22.4.1")]
    [InlineData("lts/*", null)]
    public void NormalizeNode_KeepsExactVersionsAndMajorLines(string raw, string? expected) =>
        Assert.Equal(expected, CiToolchainDetector.NormalizeNodeVersion(raw));

    [Theory]
    [InlineData("1.24", "1.24")]
    [InlineData("1.24.x", "1.24")]
    [InlineData("1.24.1", "1.24.1")]
    [InlineData("1", null)]
    [InlineData("stable", null)]
    public void NormalizeGo_RequiresAMinorLine(string raw, string? expected) =>
        Assert.Equal(expected, CiToolchainDetector.NormalizeGoVersion(raw));

    // ── Manifest parsers ─────────────────────────────────────────────────────

    [Fact]
    public void ParsePackageJson_UsesTheEnginesMajor() {
        var result = CiToolchainDetector.ParsePackageJson("""{ "engines": { "node": ">=22 <23" } }""");
        Assert.Equal([new CiToolchain("node", "22", "package.json")], result);
    }

    [Fact]
    public void ParsePackageJson_WithoutEngines_SaysNothing() =>
        Assert.Empty(CiToolchainDetector.ParsePackageJson("""{ "name": "app" }"""));

    [Fact]
    public void ParseCsproj_ReadsMultiTargetingAndPlatformSuffixes() {
        var result = CiToolchainDetector.ParseCsproj(
            "<Project><PropertyGroup><TargetFrameworks>net8.0;net10.0-windows;netstandard2.1</TargetFrameworks></PropertyGroup></Project>");
        Assert.Equal([
            new CiToolchain("dotnet", "8.0", "csproj"),
            new CiToolchain("dotnet", "10.0", "csproj"),
        ], result);
    }

    // ── Profile hash semantics (what triggers a re-warm) ─────────────────────

    [Fact]
    public void ComputeHash_IgnoresSourceAttributionAndDockerfile() {
        var fromWorkflow = new CiToolchainProfile {
            Toolchains = [new CiToolchain("dotnet", "10.0", "workflow")], HasDockerfile = true,
        };
        var fromManifest = new CiToolchainProfile {
            Toolchains = [new CiToolchain("dotnet", "10.0", "global.json")], HasDockerfile = false,
        };

        // Same installs → same hash: neither the signal source nor a Dockerfile changes what the
        // warmer would do, so neither may trigger a re-warm.
        Assert.Equal(fromWorkflow.ComputeHash(), fromManifest.ComputeHash());
        Assert.NotEqual(fromWorkflow.ComputeHash(), CiToolchainProfile.Empty.ComputeHash());
    }

    [Fact]
    public void Profile_RoundTripsThroughJson() {
        var profile = new CiToolchainProfile {
            Toolchains = [new CiToolchain("node", "22", "workflow")], HasDockerfile = true,
        };

        var restored = CiToolchainProfile.FromJson(profile.ToJson());

        Assert.NotNull(restored);
        Assert.True(restored.HasDockerfile);
        Assert.Equal(profile.Toolchains, restored.Toolchains);
        Assert.Null(CiToolchainProfile.FromJson(null));
        Assert.Null(CiToolchainProfile.FromJson("corrupt {"));
    }
}
