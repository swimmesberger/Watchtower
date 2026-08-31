using System.Reflection;
using Elarion.Abstractions;
using Watchtower.Application.Modules.Proxy;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Every request and response type the Proxy module's handlers carry has to be reachable through
/// <see cref="ProxyJsonContext"/>. Reflection serialization is off, so a type missing from the context
/// is not a slow path — it is a JSON-RPC method that answers "internal error" to every single call,
/// with the reason only in the server log.
/// </summary>
/// <remarks>
/// Asserted over the whole module rather than per handler because the failure mode is an omission: a
/// new handler is written, its transport works in a unit test that calls it directly, and nothing says
/// the wire is broken until somebody opens the page. Found exactly that way — <c>proxy.getInternalCa</c>
/// shipped without its two entries.
/// </remarks>
public sealed class ProxyJsonContextCoverageTests {
    [Fact]
    public void EveryProxyHandlersRequestAndResponseIsSerializable() {
        var missing = new List<string>();
        foreach (var handler in HandlerTypes()) {
            foreach (var payload in new[] { "Query", "Command", "Response" }) {
                var type = handler.GetNestedType(payload, BindingFlags.Public);
                if (type is null) continue;
                if (ProxyJsonContext.Default.GetTypeInfo(type) is null)
                    missing.Add($"{handler.Name}.{payload}");
            }
        }

        Assert.True(
            missing.Count == 0,
            $"Not registered on ProxyJsonContext: {string.Join(", ", missing)}. Add a "
            + "[JsonSerializable] entry for each — without one the method fails at dispatch.");
    }

    /// <summary>The module's handler classes, found the way the generator finds them: by the attribute.</summary>
    private static IEnumerable<Type> HandlerTypes() =>
        typeof(ProxyJsonContext).Assembly.GetTypes()
            .Where(t => t.Namespace == "Watchtower.Application.Modules.Proxy.Handlers")
            .Where(t => t.GetCustomAttribute<HandlerAttribute>() is not null);
}
