using Elarion.Abstractions.Coordination;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Services.Acme;

namespace Watchtower.Application.Tests;

/// <summary>
/// An <see cref="IRoleLease"/> whose answer a test sets — the seam for everything gated on
/// "is this instance the holder" (ADR-0024 decision 5).
/// </summary>
/// <remarks>
/// The real lease is acquired by a hosted service on a heartbeat, which is two things a unit test should
/// not have to wait for: a background loop and a wall clock. Substituting the lease also lets a test
/// assert the interesting half — that a <em>non</em>-holder makes no CA request at all — which no amount
/// of waiting could produce reliably.
/// </remarks>
public sealed class StubRoleLease(string role, bool isHeld = true, string? holder = null) : IRoleLease {
    public string Role { get; } = role;

    /// <summary>Settable, so one test can watch a handover rather than needing two hosts.</summary>
    public bool IsHeld { get; set; } = isHeld;

    public string? CurrentHolder { get; set; } = holder;
}

/// <summary>Registration helpers for <see cref="StubRoleLease"/>.</summary>
public static class StubRoleLeaseRegistration {
    /// <summary>
    /// Replaces the <c>acme-issuer</c> lease with one this test controls. Removing the real registration
    /// first is what makes this work at all: <c>AddElarionPostgreSqlRoleLease</c> refuses a second lease
    /// for the same role, on the grounds that a process holding one role twice would compete with itself.
    /// </summary>
    public static StubRoleLease UseStubIssuerLease(
        this IServiceCollection services, bool isHeld = true, string? holder = null) {
        var stub = new StubRoleLease(CertificateManager.IssuerRole, isHeld, holder);
        foreach (var descriptor in services
                     .Where(d => d.ServiceType == typeof(IRoleLease)
                                 && d.IsKeyedService
                                 && Equals(d.ServiceKey, CertificateManager.IssuerRole))
                     .ToList())
            services.Remove(descriptor);
        services.AddKeyedSingleton<IRoleLease>(CertificateManager.IssuerRole, stub);
        return stub;
    }
}
