using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Watchtower.Application.Services;

/// <summary>
/// The Docker engine's private half of direct environment injection: reads the services and their
/// labels out of <c>docker compose config --format json</c>, and renders an
/// <see cref="EnvInjectionPlan"/> as a Compose override file.
/// </summary>
/// <remarks>
/// Everything Compose-shaped about the feature lives here (ADR-0010's seam rule); the policy that
/// decides who gets what is <see cref="EnvInjectionPlan"/> and knows nothing about any of it. The
/// override is merged in with a second <c>--file</c> after the repository's own, which is what makes
/// its <c>environment</c> entries authoritative for the keys it defines without the repository having
/// to pass anything through.
/// </remarks>
internal static class ComposeOverrideFile {
    /// <summary>
    /// Extracts the service names, the images they run, and their
    /// <see cref="EnvInjectionPlan.InjectTokenLabel"/> and <see cref="EnvInjectionPlan.ReleaseImageLabel"/>
    /// values from the normalized project JSON.
    /// </summary>
    /// <remarks>
    /// Only names, the image and those two labels are read. The rest of the document is the fully
    /// resolved project — including every environment value the repository defines — and is
    /// deliberately neither parsed nor retained. The image is the interpolated one Compose resolved, so
    /// <c>image: ghcr.io/acme/web:${TAG}</c> arrives with the tag already substituted; a build-only
    /// service declares no image and comes back with null.
    /// </remarks>
    /// <param name="configJson">stdout of <c>docker compose config --format json</c>.</param>
    /// <returns>The services in document order; empty when the document declares none.</returns>
    /// <exception cref="JsonException">The output was not valid JSON.</exception>
    public static IReadOnlyList<EnvInjectionService> ParseServices(string configJson) {
        using var document = JsonDocument.Parse(configJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("services", out var services)
            || services.ValueKind != JsonValueKind.Object)
            return [];

        var result = new List<EnvInjectionService>();
        foreach (var service in services.EnumerateObject()) {
            string? injectToken = null;
            string? releaseImage = null;
            string? image = null;
            if (service.Value.ValueKind == JsonValueKind.Object) {
                if (service.Value.TryGetProperty("labels", out var labels)) {
                    injectToken = ReadLabel(labels, EnvInjectionPlan.InjectTokenLabel);
                    releaseImage = ReadLabel(labels, EnvInjectionPlan.ReleaseImageLabel);
                }
                if (service.Value.TryGetProperty("image", out var imageValue)
                    && imageValue.ValueKind == JsonValueKind.String)
                    image = imageValue.GetString();
            }
            result.Add(new EnvInjectionService(service.Name, injectToken, image, releaseImage));
        }
        return result;
    }

    /// <summary>
    /// Renders the two plans as one Compose override document. Returns null when neither has anything
    /// to say — a <c>services:</c> key with no entries under it is not a valid Compose file.
    /// </summary>
    /// <remarks>
    /// One file, not two, and for the same reason there is one <c>ParseServices</c>: compose merges
    /// override files in argument order, and a second one would put the deploy in the business of
    /// reasoning about which of its own files wins. Both plans are already ordered by service name, so
    /// the merge below is a walk over their union in that same order — deterministic, and diffable
    /// between deploys.
    /// <para>
    /// <c>image:</c> is written before <c>environment:</c> purely for readability; Compose's merge is
    /// key-wise and cares about neither order nor which of the two keys a service happens to carry.
    /// The digest goes through the same <see cref="QuoteValue"/> path as every injected value, so a
    /// reference containing a <c>$</c> survives Compose's interpolation pass — digests do not contain
    /// one today, and a value-specific exemption is exactly how that stops being true unnoticed.
    /// </para>
    /// </remarks>
    /// <param name="plan">
    /// The environment-injection plan. Each service's variables are written in the plan's own order;
    /// the services themselves come out in ordinal name order, which is the order both plans are
    /// already built in — so an environment-only render is byte-identical to what this produced before
    /// image pinning existed.
    /// </param>
    /// <param name="imagePlan">
    /// The image-pinning plan, or null in <c>Git</c> mode — where this method renders precisely what it
    /// rendered before ADR-0026's release stage, byte for byte.
    /// </param>
    /// <param name="devicePlan">
    /// The device-mapping plan (ADR-0030), or null for a stack with none — where this method again
    /// renders exactly its pre-device output. Each device becomes a <c>devices:</c> list entry in
    /// Compose's <c>host:container[:permissions]</c> string form. Compose merges <c>devices:</c> by
    /// container path, so entries here append to whatever the repository declares and replace only an
    /// entry with the same container path — the per-host-wins rule ADR-0030 decides on.
    /// </param>
    /// <returns>The file body, or null when there is nothing to write.</returns>
    public static string? Render(
        EnvInjectionPlan plan, ImagePinPlan? imagePlan = null, DeviceMappingPlan? devicePlan = null) {
        var pins = imagePlan?.Services ?? [];
        var deviceServices = devicePlan?.Services ?? [];
        if (plan.Services.Count == 0 && pins.Count == 0 && deviceServices.Count == 0) return null;

        var variablesByService = plan.Services.ToDictionary(s => s.ServiceName, StringComparer.Ordinal);
        var pinsByService = pins.ToDictionary(p => p.ServiceName, StringComparer.Ordinal);
        var devicesByService = deviceServices.ToDictionary(d => d.ServiceName, StringComparer.Ordinal);
        var names = variablesByService.Keys
            .Union(pinsByService.Keys, StringComparer.Ordinal)
            .Union(devicesByService.Keys, StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal);

        // '\n' rather than AppendLine: the file is handed to a Compose CLI that may well be running in a
        // Linux container, and a deterministic body is easier to reason about than a per-OS one.
        var body = new StringBuilder();
        body.Append("# Generated by Watchtower for this deploy — not part of the repository.\n");
        body.Append("services:\n");
        foreach (var name in names) {
            body.Append("  ").Append(QuoteKey(name)).Append(":\n");
            if (pinsByService.TryGetValue(name, out var pin))
                body.Append("    image: ").Append(QuoteValue(pin.Image)).Append('\n');
            if (variablesByService.TryGetValue(name, out var service)) {
                body.Append("    environment:\n");
                foreach (var variable in service.Variables)
                    body.Append("      ").Append(QuoteKey(variable.Key)).Append(": ")
                        .Append(QuoteValue(variable.Value)).Append('\n');
            }
            if (devicesByService.TryGetValue(name, out var devices)) {
                if (devices.Devices.Count > 0) {
                    body.Append("    devices:\n");
                    foreach (var device in devices.Devices)
                        body.Append("      - ").Append(QuoteValue(DeviceText(device))).Append('\n');
                }
                // Compose appends group_add across files, so the repository's own groups survive.
                // GIDs render as quoted strings: an unquoted number is looked up as a group *name*
                // inside the container by some runtimes, and the probed GID rarely has one there.
                if (devices.GroupIds.Count > 0) {
                    body.Append("    group_add:\n");
                    foreach (var groupId in devices.GroupIds)
                        body.Append("      - ").Append(QuoteValue(groupId.ToString(CultureInfo.InvariantCulture)))
                            .Append('\n');
                }
            }
        }
        return body.ToString();
    }

    /// <summary>
    /// A device as Compose's string form spells it: <c>host:container</c>, with <c>:permissions</c>
    /// only when the operator chose some — so the runtime default stays the runtime's to define.
    /// </summary>
    private static string DeviceText(ServiceDevice device) =>
        device.Permissions is { } permissions
            ? $"{device.HostPath}:{device.ContainerPath}:{permissions}"
            : $"{device.HostPath}:{device.ContainerPath}";

    /// <summary>Reads one label out of the map (or the <c>KEY=VALUE</c> list) Compose emitted.</summary>
    /// <remarks>
    /// The normalized document uses a map, but the list form is accepted too so that a Compose version
    /// echoing back the repository's own list syntax cannot silently turn the label into "absent" —
    /// which would look exactly like an opt-out that the author never wrote.
    /// </remarks>
    private static string? ReadLabel(JsonElement labels, string key) {
        if (labels.ValueKind == JsonValueKind.Object)
            return labels.TryGetProperty(key, out var value) ? ScalarText(value) : null;

        if (labels.ValueKind != JsonValueKind.Array) return null;
        foreach (var entry in labels.EnumerateArray()) {
            if (entry.ValueKind != JsonValueKind.String) continue;
            var text = entry.GetString()!;
            var separator = text.IndexOf('=');
            // A bare "key" with no '=' is Compose's "take it from the environment" form, which carries
            // no value here — treated as absent rather than as an empty (and therefore invalid) one.
            if (separator <= 0 || !string.Equals(text[..separator], key, StringComparison.Ordinal)) continue;
            return text[(separator + 1)..];
        }
        return null;
    }

    /// <summary>Renders a JSON scalar as the text a label value would have had in YAML.</summary>
    private static string? ScalarText(JsonElement value) => value.ValueKind switch {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };

    /// <summary>Quotes a mapping key (a service name or a variable name) as a YAML scalar.</summary>
    /// <remarks>
    /// No <c>$</c> escaping here, unlike <see cref="QuoteValue"/>. Keys are either Watchtower's own
    /// reserved variable names or service names, and the compose-spec constrains those to
    /// <c>^[a-zA-Z0-9._-]+$</c> — a name carrying a <c>$</c> would have been rejected by the
    /// <c>config</c> call this list came from, so escaping one would only ever rewrite a key that
    /// cannot reach here into one that fails to match the repository's service.
    /// </remarks>
    private static string QuoteKey(string key) => Quote(key);

    /// <summary>
    /// Quotes a value as a YAML scalar, first escaping it against Compose's own interpolation pass.
    /// </summary>
    /// <remarks>
    /// Compose interpolates <c>$</c> in every file it is given, this generated one included, so a value
    /// containing a literal <c>$</c> has to be written <c>$$</c> or it would be read as a variable
    /// reference and substituted away. Quoting cannot prevent that — interpolation happens after the
    /// YAML is parsed.
    /// </remarks>
    private static string QuoteValue(string value) => Quote(value.Replace("$", "$$", StringComparison.Ordinal));

    /// <summary>Writes <paramref name="text"/> as a quoted YAML scalar that round-trips exactly.</summary>
    private static string Quote(string text) {
        if (!text.Contains('\n', StringComparison.Ordinal) && !text.Contains('\r', StringComparison.Ordinal))
            return $"'{text.Replace("'", "''", StringComparison.Ordinal)}'";

        // A single-quoted scalar folds line breaks into spaces, so a value carrying one has to use the
        // double-quoted style — the only one with explicit escapes. No injected value is expected to
        // contain a line break; this exists so that if one ever did, it would survive rather than
        // silently change shape.
        var escaped = text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
