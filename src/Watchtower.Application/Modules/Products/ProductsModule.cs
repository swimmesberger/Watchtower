using System.Text.Json.Serialization.Metadata;

namespace Watchtower.Application.Modules.Products;

/// <summary>
/// The catalogue of deployable things (ADR-0026): a product is a git repository plus the compose file,
/// default branch and clone credential that turn it into something Watchtower can run. Every
/// <c>Stack</c> and every <c>StackTemplate</c> references one, so this module owns the source that
/// used to be denormalized onto both. Handlers are exposed as <c>products.*</c> JSON-RPC methods.
/// </summary>
/// <remarks>
/// Authorization follows the neighbouring deployment modules — <c>Stacks</c>, <c>Tenancy</c>,
/// <c>Registries</c> — rather than the access-control plane: an authenticated principal, from the
/// assembly-wide <c>[ElarionAuthorizationDefaults]</c>, and no additional role gate. Editing a product
/// is editing a deployment's configuration, not granting anyone access to anything, and gating it
/// harder than the stack it configures would only move the same act to another surface.
/// </remarks>
[AppModule("Products")]
public static partial class ProductsModule {
    /// <summary>Returns the JSON type info resolver for Products module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => ProductsJsonContext.Default;
}
