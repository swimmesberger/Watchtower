using System.Text.Json.Serialization.Metadata;

namespace Watchtower.Application.Modules.Stacks;

/// <summary>Stack management: CRUD, deployment triggering, environment variables, and update checks.</summary>
[AppModule("Stacks")]
public static partial class StacksModule {
    /// <summary>Returns the JSON type info resolver for Stacks module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => StacksJsonContext.Default;
}
