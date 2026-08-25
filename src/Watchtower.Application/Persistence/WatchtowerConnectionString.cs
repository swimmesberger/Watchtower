using Microsoft.Extensions.Configuration;

namespace Watchtower.Application.Persistence;

/// <summary>
/// The one place Watchtower's PostgreSQL connection string is resolved from configuration (ADR-0024).
/// </summary>
/// <remarks>
/// Two keys, in this order:
/// <list type="number">
///   <item><description>
///     <c>Watchtower:Database:ConnectionString</c> — what an operator sets, as
///     <c>WATCHTOWER__DATABASE__CONNECTIONSTRING</c> in the compose file.
///   </description></item>
///   <item><description>
///     <c>ConnectionStrings:watchtower</c> — what .NET Aspire injects for
///     <c>api.WithReference(db)</c>, so the AppHost needs no Watchtower-specific wiring.
///   </description></item>
/// </list>
/// There is no default. A missing connection string is a configuration error the host must fail on
/// loudly rather than fall back from: the previous fallback was a file path, and silently pointing a
/// second instance at an empty database is exactly the failure ADR-0024 exists to prevent.
/// </remarks>
public static class WatchtowerConnectionString {
    /// <summary>The primary configuration key, as an operator writes it.</summary>
    public const string ConfigurationKey = "Watchtower:Database:ConnectionString";

    /// <summary>The Aspire/<c>ConnectionStrings</c> fallback key.</summary>
    public const string ConnectionStringName = "watchtower";

    /// <summary>Resolves the connection string, or throws when neither key is set.</summary>
    public static string Resolve(IConfiguration configuration) =>
        Find(configuration) ?? throw new InvalidOperationException(
            $"No PostgreSQL connection string configured. Set '{ConfigurationKey}' "
            + $"(WATCHTOWER__DATABASE__CONNECTIONSTRING) or 'ConnectionStrings:{ConnectionStringName}'. "
            + "Watchtower has required PostgreSQL since ADR-0024 (see docs/upgrading.md).");

    /// <summary>Resolves the connection string, or null when neither key is set.</summary>
    public static string? Find(IConfiguration configuration) {
        var configured = configuration[ConfigurationKey];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var fromConnectionStrings = configuration.GetConnectionString(ConnectionStringName);
        return string.IsNullOrWhiteSpace(fromConnectionStrings) ? null : fromConnectionStrings;
    }
}
