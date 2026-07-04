using Elarion.Abstractions;

// Turns the Elarion source generators on for this assembly: handler/module/validator discovery,
// RPC/HTTP/MCP maps, scheduled jobs, and the EF Core DbSet/[EntityConfiguration] generator.
[assembly: UseElarion]

namespace Watchtower.Application;

/// <summary>Marker type used to reference the Application assembly.</summary>
public static class AssemblyMarker;
