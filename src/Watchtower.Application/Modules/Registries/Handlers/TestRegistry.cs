using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Registries.Handlers;

/// <summary>Validates a registry's credentials by running <c>docker login</c> (token piped via stdin).</summary>
[Handler("registries.test")]
public sealed class TestRegistry(WatchtowerDbContext db)
    : IHandler<TestRegistry.Command, Result<TestRegistry.Response>> {
    public sealed record Command(int Id);
    public sealed record Response(string Message);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var registry = await db.Registries
            .AsNoTracking()
            .Where(r => r.Id == command.Id)
            .Select(r => new { r.Url, r.CredentialId })
            .FirstOrDefaultAsync(ct);
        if (registry is null)
            return AppError.NotFound($"Registry {command.Id} not found");
        if (registry.CredentialId is null)
            return AppError.Validation("Registry has no credential assigned.");

        var cred = await db.Credentials.AsNoTracking()
            .Where(c => c.Id == registry.CredentialId.Value)
            .Select(c => new { c.Username, c.Token })
            .FirstOrDefaultAsync(ct);
        if (cred is null)
            return AppError.Validation("Linked credential not found.");

        var (exitCode, output) = await DockerLoginAsync(registry.Url, cred.Username, cred.Token, ct);
        return exitCode == 0
            ? new Response("Login successful.")
            : AppError.Validation(output);
    }

    /// <summary>Runs <c>docker login</c> with the token piped via stdin to avoid shell exposure.</summary>
    private static async Task<(int ExitCode, string Output)> DockerLoginAsync(
        string url, string username, string token, CancellationToken ct) {
        var output = new StringBuilder();

        var startInfo = new ProcessStartInfo("docker") {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("login");
        startInfo.ArgumentList.Add("--username");
        startInfo.ArgumentList.Add(username);
        startInfo.ArgumentList.Add("--password-stdin");
        startInfo.ArgumentList.Add(url);

        // Scope credential persistence to a throwaway DOCKER_CONFIG: without it the CLI
        // writes to $HOME/.docker, which may not exist or be writable (non-root containers),
        // failing the test even though authentication succeeded.
        var tempConfigDir = Path.Combine(Path.GetTempPath(), $"watchtower-login-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempConfigDir);
        startInfo.Environment["DOCKER_CONFIG"] = tempConfigDir;

        try {
            using var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Write the token to stdin and close it so docker login can proceed.
            await process.StandardInput.WriteAsync(token);
            process.StandardInput.Close();

            await process.WaitForExitAsync(ct);
            return (process.ExitCode, output.ToString());
        } finally {
            try { Directory.Delete(tempConfigDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
