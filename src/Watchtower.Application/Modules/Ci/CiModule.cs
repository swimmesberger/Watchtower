using System.Text.Json.Serialization.Metadata;

namespace Watchtower.Application.Modules.Ci;

/// <summary>
/// Self-hosted GitHub Actions runners: per-repo enablement, ephemeral runner orchestration
/// status, and the GitHub repo picker (docs/ci-runners/design.md).
/// </summary>
[AppModule("Ci")]
public static partial class CiModule {
    /// <summary>Returns the JSON type info resolver for Ci module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => CiJsonContext.Default;
}
