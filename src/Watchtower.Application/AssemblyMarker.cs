using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Watchtower.Application.Pipeline;

// Turns the Elarion source generators on for this assembly: handler/module/validator discovery,
// RPC/HTTP/MCP maps, scheduled jobs, and the EF Core DbSet/[EntityConfiguration] generator.
[assembly: UseElarion]

// Secure by default (docs/central-auth/design.md §2.5): every handler in this assembly requires an
// authenticated principal unless it carries [AllowAnonymous]. Deliberately UNCONDITIONAL — gating it on
// Auth:Enabled would mean the protected configuration is the one nobody compiles against. The no-auth
// deployment keeps working because ICurrentUser is then ImplicitAdminCurrentUser, which is authenticated.
[assembly: ElarionAuthorizationDefaults]

// The application's own decorator pipeline (ADR-0024): a lost optimistic-concurrency race becomes a
// Conflict result rather than an unhandled exception. See WatchtowerPipelineAttribute.
[assembly: WatchtowerPipeline]

namespace Watchtower.Application;

/// <summary>Marker type used to reference the Application assembly.</summary>
public static class AssemblyMarker;
