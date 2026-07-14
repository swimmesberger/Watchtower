using Watchtower.Application.Entities;

namespace Watchtower.Application.Modules.Proxy;

/// <summary>A public route projection for the API (enum fields lowercased for the client).</summary>
public sealed record RouteDto(
    int Id,
    int StackId,
    string? StackName,
    string Domain,
    string ServiceName,
    int ContainerPort,
    bool TlsEnabled,
    bool IsPrimary,
    string Kind,
    string Status,
    string? StatusDetail,
    DateTimeOffset? CertNotAfter,
    DateTimeOffset CreatedAt);

/// <summary>In-memory projection + validation helpers (not translatable to SQL).</summary>
public static class RouteMapping {
    public static RouteDto ToDto(Route r) => new(
        r.Id, r.StackId, r.Stack?.Name, r.Domain, r.ServiceName, r.ContainerPort,
        r.TlsEnabled, r.IsPrimary,
        r.Kind.ToString().ToLowerInvariant(),
        r.Status.ToString().ToLowerInvariant(),
        r.StatusDetail, r.CertNotAfter, r.CreatedAt);

    /// <summary>Normalizes a domain: trimmed and lowercased. Returns null when blank/whitespace.</summary>
    public static string? NormalizeDomain(string? domain) {
        var d = domain?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(d) ? null : d;
    }

    public static DomainKind ParseKind(string? kind) =>
        Enum.TryParse<DomainKind>(kind, ignoreCase: true, out var k) ? k : DomainKind.Managed;
}
