using Watchtower.Application.Entities;

namespace Watchtower.Application.Services;

/// <summary>The git source a deploy (or an update check) actually uses, after inheritance.</summary>
/// <param name="RepositoryUrl">The product's remote.</param>
/// <param name="ComposeFilePath">The product's compose file path — product-only, never overridden.</param>
/// <param name="Branch">The effective branch: stack override, else template override, else product default.</param>
/// <param name="CredentialId">The product's clone credential — product-only, never overridden.</param>
public readonly record struct ProductSource(
    string RepositoryUrl, string ComposeFilePath, string Branch, int? CredentialId);

/// <summary>
/// The one place that answers "what does this stack actually clone?" (ADR-0026). Static rather than a
/// service: it is a pure function of rows the caller has already loaded, so injecting it would add a
/// constructor parameter and nothing else.
/// </summary>
/// <remarks>
/// Callers must have the <see cref="Stack.Product"/> navigation loaded, and
/// <see cref="Stack.Template"/> too when the stack is a tenant — a missing include would otherwise
/// read as "no override" and silently deploy the wrong branch, so it throws instead.
/// </remarks>
public static class ProductSourceResolver {
    /// <summary>The effective source of a stack. <paramref name="stack"/> must have its product loaded.</summary>
    public static ProductSource Resolve(Stack stack) {
        ArgumentNullException.ThrowIfNull(stack);
        var product = Require(stack.Product, $"Stack {stack.Id} was loaded without its product.");
        return Resolve(product, stack.BranchOverride ?? stack.Template?.BranchOverride);
    }

    /// <summary>The effective source of a template. <paramref name="template"/> must have its product loaded.</summary>
    public static ProductSource Resolve(StackTemplate template) {
        ArgumentNullException.ThrowIfNull(template);
        var product = Require(template.Product, $"Template {template.Id} was loaded without its product.");
        return Resolve(product, template.BranchOverride);
    }

    /// <summary>
    /// The effective source of an explicit product plus an already-resolved override — for callers
    /// deciding what a row <em>would</em> deploy before writing anything to it.
    /// </summary>
    public static ProductSource Resolve(Product product, string? branchOverride) {
        ArgumentNullException.ThrowIfNull(product);
        return new ProductSource(
            product.RepositoryUrl,
            product.ComposeFilePath,
            branchOverride ?? product.DefaultBranch,
            product.CredentialId);
    }

    /// <summary>
    /// What <paramref name="stack"/> would deploy with <em>no</em> override of its own: its template's
    /// override when it is a tenant, else the product default. This — not the product default — is the
    /// base <see cref="OverrideFor"/> has to compare a saved branch against, or a tenant of a
    /// <c>develop</c> template would have <c>develop</c> written onto it as a per-stack pin the first
    /// time anyone saved its settings, severing exactly the inheritance ADR-0026 exists to restore.
    /// </summary>
    public static string InheritedBranch(Stack stack) {
        ArgumentNullException.ThrowIfNull(stack);
        var product = Require(stack.Product, $"Stack {stack.Id} was loaded without its product.");
        return stack.Template?.BranchOverride ?? product.DefaultBranch;
    }

    /// <summary>
    /// The value a <c>BranchOverride</c> should hold for a requested branch: null when the request is
    /// empty or already equal to <paramref name="inheritedBranch"/> — what the row would deploy without
    /// an override of its own — so an unchanged form save clears rather than pins one.
    /// </summary>
    /// <param name="requestedBranch">What the caller posted.</param>
    /// <param name="inheritedBranch">
    /// The branch that applies with no override present: for a stack,
    /// <see cref="InheritedBranch(Stack)"/>; for a template, its product's default.
    /// </param>
    public static string? OverrideFor(string? requestedBranch, string inheritedBranch) {
        var branch = requestedBranch?.Trim();
        return string.IsNullOrEmpty(branch) || string.Equals(branch, inheritedBranch, StringComparison.Ordinal)
            ? null
            : branch;
    }

    private static Product Require(Product? product, string message) =>
        product ?? throw new InvalidOperationException(
            message + " Include(x => x.Product) — the source lives there since ADR-0026.");
}
