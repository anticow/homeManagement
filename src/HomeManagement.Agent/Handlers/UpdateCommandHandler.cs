using System.Net.Http;
using HomeManagement.Agent.Communication;
using HomeManagement.Agent.Configuration;
using HomeManagement.Agent.Protocol;
using HomeManagement.Agent.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Agent.Handlers;

/// <summary>
/// Handles <see cref="UpdateDirective"/> messages: downloads the new binary,
/// verifies its SHA-256 hash and Ed25519 signature, stages it, and signals a restart.
/// </summary>
public sealed class UpdateCommandHandler(
    IntegrityChecker integrity,
    ShutdownCoordinator shutdown,
    IOptions<AgentConfiguration> config,
    ILogger<UpdateCommandHandler> logger)
{
    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public async Task HandleAsync(UpdateDirective directive, CancellationToken ct)
    {
        logger.LogInformation("Starting update to version {Version} from {Url}",
            directive.TargetVersion, directive.DownloadUrl);

        var stagingDir = Path.Combine(Path.GetTempPath(), "hm-agent-update", directive.TargetVersion);
        Directory.CreateDirectory(stagingDir);

        var stagedFile = Path.Combine(stagingDir, "hm-agent-update.bin");

        try
        {
            // Download the update binary
            using var response = await s_httpClient.GetAsync(directive.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var fs = new FileStream(stagedFile, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fs, ct);
            await fs.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download update binary from {Url}", directive.DownloadUrl);
            return;
        }

        // Verify SHA-256 integrity
        if (directive.BinarySha256.Length > 0)
        {
            var hashValid = await integrity.VerifySha256Async(stagedFile, directive.BinarySha256.Memory, ct);
            if (!hashValid)
            {
                logger.LogError("SHA-256 verification failed for update {Version} — aborting", directive.TargetVersion);
                CleanupStaging(stagingDir);
                return;
            }
        }

        // Verify Ed25519 signature if both signature and public key are available
        if (directive.SignatureEd25519.Length > 0 && config.Value.UpdateSigningPublicKey is { Length: > 0 } pubKey)
        {
            var sigValid = await integrity.VerifyEd25519Async(
                stagedFile, directive.SignatureEd25519.Memory, pubKey, ct);
            if (!sigValid)
            {
                logger.LogError("Ed25519 signature verification failed for update {Version} — aborting",
                    directive.TargetVersion);
                CleanupStaging(stagingDir);
                return;
            }

            logger.LogInformation("Ed25519 signature verified for update {Version}", directive.TargetVersion);
        }
        else if (directive.SignatureEd25519.Length > 0)
        {
            logger.LogWarning("Update {Version} includes Ed25519 signature but no public key is configured — skipping signature check",
                directive.TargetVersion);
        }

        logger.LogInformation("Update {Version} staged and verified at {Path}; requesting restart",
            directive.TargetVersion, stagedFile);

        // Signal graceful shutdown so the external service manager can restart with the new binary
        await shutdown.RequestShutdownAsync(
            $"Restarting for update to {directive.TargetVersion}", delayMs: 0, ct);
    }

    private void CleanupStaging(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to clean up staging directory {Dir}", dir); }
    }
}
