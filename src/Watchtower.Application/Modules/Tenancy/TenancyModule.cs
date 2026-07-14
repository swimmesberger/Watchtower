using System.Text.Json.Serialization.Metadata;

namespace Watchtower.Application.Modules.Tenancy;

/// <summary>
/// Multi-tenancy module: reusable <c>StackTemplate</c>s instantiated once per tenant. Each tenant is an
/// isolated <c>Stack</c> (own containers/network/volumes via its compose project name) bound to a
/// subdomain. Handlers are exposed as <c>templates.*</c> JSON-RPC methods.
/// </summary>
[AppModule("Tenancy")]
public static partial class TenancyModule {
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => TenancyJsonContext.Default;
}
