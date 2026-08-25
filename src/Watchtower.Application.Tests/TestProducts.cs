using Watchtower.Application.Entities;

namespace Watchtower.Application.Tests;

/// <summary>
/// Product fixtures for tests that only need <em>a</em> deployable source (ADR-0026): most suites care
/// about routes, backups, access or concurrency, and the product is now the required thing a stack
/// cannot exist without. One helper rather than the same four lines in twenty files.
/// </summary>
/// <remarks>
/// The defaults reproduce what those tests used to spell inline — <c>https://example.invalid/{name}.git</c>,
/// <c>docker-compose.yml</c>, <c>main</c> — so a suite's behaviour is unchanged by the move. Products are
/// unique on name, so a fixture that needs several stacks of one product creates the product once and
/// reuses its id rather than calling <see cref="New"/> per stack.
/// </remarks>
internal static class TestProducts {
    public const string ComposeFilePath = "docker-compose.yml";
    public const string DefaultBranch = "main";

    /// <summary>An unsaved product named after whatever will deploy it.</summary>
    public static Product New(
        string name, string? repositoryUrl = null, string? defaultBranch = null, int? credentialId = null) => new() {
        Name = name,
        RepositoryUrl = repositoryUrl ?? $"https://example.invalid/{name}.git",
        ComposeFilePath = ComposeFilePath,
        DefaultBranch = defaultBranch ?? DefaultBranch,
        CredentialId = credentialId,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
