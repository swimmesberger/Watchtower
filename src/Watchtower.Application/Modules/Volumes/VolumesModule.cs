using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Watchtower.Application.Modules.Volumes.Handlers;

namespace Watchtower.Application.Modules.Volumes;

/// <summary>Docker volume inspection and lifecycle (list, sizes, recreate, remove, prune orphans).</summary>
[AppModule("Volumes")]
public static partial class VolumesModule {
    /// <summary>Returns the JSON type info resolver for Volumes module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => VolumesJsonContext.Default;
}

/// <summary>
/// A named Docker volume enriched with compose context and a server-computed lifecycle.
/// </summary>
/// <param name="Name">Full volume name (e.g. <c>web-app_pgdata</c>).</param>
/// <param name="Driver">Volume driver (<c>local</c> etc.).</param>
/// <param name="Project"><c>com.docker.compose.project</c> label, or null when not compose-managed.</param>
/// <param name="ComposeVolume"><c>com.docker.compose.volume</c> label — the short name in the compose file.</param>
/// <param name="Mountpoint">Host path of the volume's data directory.</param>
/// <param name="CreatedAt">ISO-8601 creation timestamp, or null when the daemon omits it.</param>
/// <param name="Labels">All volume labels (never null).</param>
/// <param name="Scope">Volume scope (<c>local</c>).</param>
/// <param name="InUseBy">Names of containers referencing the volume, running OR stopped.</param>
/// <param name="RefCount">Count of containers referencing the volume, running OR stopped.</param>
/// <param name="Lifecycle">
/// <c>live</c> = referenced by ≥1 container; <c>declared</c> = has a compose project label but zero
/// containers; <c>orphaned</c> = no project label AND zero containers.
/// </param>
public sealed record VolumeDto(
    string Name,
    string Driver,
    string? Project,
    string? ComposeVolume,
    string Mountpoint,
    string? CreatedAt,
    IReadOnlyDictionary<string, string> Labels,
    string Scope,
    IReadOnlyList<string> InUseBy,
    int RefCount,
    string Lifecycle);

/// <summary>A single volume's on-disk size, from <c>/system/df</c> (expensive, on-demand only).</summary>
public sealed record VolumeSizeDto(string Name, long SizeBytes, int RefCount);

/// <summary>
/// Returned immediately after a recreate is enqueued on the deploy queue. Mirrors the wire shape of
/// the Stacks module's deploy-accepted result (<c>{ deployEventId, status }</c>); declared locally
/// because Elarion forbids one module depending on another module's internal types.
/// </summary>
public sealed record VolumeRecreateAcceptedDto(int DeployEventId, string Status);

/// <summary>JSON serializer context for Volumes module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(VolumeDto))]
[JsonSerializable(typeof(VolumeSizeDto))]
[JsonSerializable(typeof(VolumeRecreateAcceptedDto))]
[JsonSerializable(typeof(ListVolumes.Query), TypeInfoPropertyName = "ListVolumesQuery")]
[JsonSerializable(typeof(ListVolumes.Response), TypeInfoPropertyName = "ListVolumesResponse")]
[JsonSerializable(typeof(GetVolumeSizes.Query), TypeInfoPropertyName = "GetVolumeSizesQuery")]
[JsonSerializable(typeof(GetVolumeSizes.Response), TypeInfoPropertyName = "GetVolumeSizesResponse")]
[JsonSerializable(typeof(RecreateVolumes.Command), TypeInfoPropertyName = "RecreateVolumesCommand")]
[JsonSerializable(typeof(RecreateVolumes.Response), TypeInfoPropertyName = "RecreateVolumesResponse")]
[JsonSerializable(typeof(RemoveVolume.Command), TypeInfoPropertyName = "RemoveVolumeCommand")]
[JsonSerializable(typeof(RemoveVolume.Response), TypeInfoPropertyName = "RemoveVolumeResponse")]
[JsonSerializable(typeof(PruneOrphanVolumes.Command), TypeInfoPropertyName = "PruneOrphanVolumesCommand")]
[JsonSerializable(typeof(PruneOrphanVolumes.Response), TypeInfoPropertyName = "PruneOrphanVolumesResponse")]
public sealed partial class VolumesJsonContext : JsonSerializerContext;
