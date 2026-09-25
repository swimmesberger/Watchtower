namespace Watchtower.Application.Entities;

/// <summary>
/// One browser's Web Push subscription (ADR-0041) — an installed PWA or a browser profile an operator
/// turned notifications on in. The endpoint is the push service's URL for that browser (Google, Apple,
/// Mozilla…); <see cref="P256dh"/> and <see cref="Auth"/> are the browser's keys every payload is
/// encrypted to.
/// </summary>
/// <remarks>
/// The endpoint is globally unique — a push service never hands two browsers the same one — so it is the
/// natural key a re-subscribe upserts on, and the owner is <em>reassigned</em> rather than duplicated
/// when a different account signs in on the same browser: the device belongs to whoever enabled
/// notifications on it last, which is the only owner who can still turn them off there.
/// </remarks>
public sealed class PushSubscription {
    /// <summary>Surrogate key (UUID v7, so rows sort by creation).</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The owner, as <c>ICurrentUser.UserId</c> reports it: a <see cref="User"/> id rendered as a string
    /// for a real account, or <c>ImplicitAdminCurrentUser.LocalUserId</c> (<c>"local"</c>) while
    /// authentication is switched off. A string rather than a foreign key on purpose — the no-auth
    /// operator has no <c>users</c> row, and a subscription outliving its account is cleaned up by the
    /// sender rather than by a cascade.
    /// </summary>
    public required string UserId { get; set; }

    /// <summary>The push service URL this browser receives on. Unique across the table.</summary>
    public required string Endpoint { get; set; }

    /// <summary>The browser's P-256 ECDH public key (base64url) the payload is encrypted to (RFC 8291).</summary>
    public required string P256dh { get; set; }

    /// <summary>The browser's 16-byte authentication secret (base64url) for the payload encryption.</summary>
    public required string Auth { get; set; }

    /// <summary>What the browser said it was when it subscribed — only so an operator can tell devices apart.</summary>
    public string? UserAgent { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the browser last (re-)subscribed. The frontend refreshes on every app start.</summary>
    public DateTimeOffset LastSeenAt { get; set; }
}
