namespace Watchtower.Application.Services;

/// <summary>Shared vocabulary of the stack stop/start feature (ADR-0025).</summary>
public static class StackLifecycle {
    /// <summary>Audit trail category for stack lifecycle operations.</summary>
    public const string AuditCategory = "stacks";

    /// <summary>The label Compose stamps on every container of a project.</summary>
    public const string ComposeProjectLabel = "com.docker.compose.project";

    /// <summary>
    /// The last part of a compose output, for error messages: the failure reason is at the end, and
    /// the full log can hold a whole pull's progress bars.
    /// </summary>
    public static string Tail(string output, int maxChars = 500) {
        var trimmed = output.Trim();
        return trimmed.Length <= maxChars ? trimmed : "…" + trimmed[^maxChars..];
    }
}
