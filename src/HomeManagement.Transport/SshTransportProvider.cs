using System.Diagnostics;
using System.Text;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace HomeManagement.Transport;

/// <summary>
/// SSH transport provider using SSH.NET. Handles command execution
/// and file transfer over SSH connections.
/// </summary>
internal sealed class SshTransportProvider
{
    private readonly ICredentialVault _vault;
    private readonly ILogger<SshTransportProvider> _logger;

    public SshTransportProvider(ICredentialVault vault, ILogger<SshTransportProvider> logger)
    {
        _vault = vault;
        _logger = logger;
    }

    public async Task<RemoteResult> ExecuteAsync(MachineTarget target, RemoteCommand command, CancellationToken ct)
    {
        using var credential = await _vault.GetPayloadAsync(target.CredentialId, ct);
        using var client = CreateSshClient(target, credential);
        client.ConnectionInfo.Timeout = command.Timeout;
        client.Connect();
        try
        {
            var commandText = WrapWithElevation(command);
            var sw = Stopwatch.StartNew();

            using var sshCmd = client.CreateCommand(commandText);

            var result = sshCmd.Execute();
            sw.Stop();

            return new RemoteResult(
                sshCmd.ExitStatus ?? -1,
                result,
                sshCmd.Error,
                sw.Elapsed,
                TimedOut: false);
        }
        finally
        {
            client.Disconnect();
        }
    }

    public async Task TransferFileAsync(MachineTarget target, FileTransferRequest request,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        using var credential = await _vault.GetPayloadAsync(target.CredentialId, ct);
        using var client = CreateScpClient(target, credential);
        client.Connect();
        try
        {
            if (request.Direction == FileTransferDirection.Upload)
            {
                using var stream = File.OpenRead(request.LocalPath);
                client.Upload(stream, request.RemotePath);
            }
            else
            {
                using var stream = File.Create(request.LocalPath);
                client.Download(request.RemotePath, stream);
            }
        }
        finally
        {
            client.Disconnect();
        }
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(MachineTarget target, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var credential = await _vault.GetPayloadAsync(target.CredentialId, ct);
            using var client = CreateSshClient(target, credential);
            client.Connect();

            // Detect OS via uname
            using var cmd = client.CreateCommand("uname -s");
            var osString = cmd.Execute().Trim();
            sw.Stop();
            client.Disconnect();

            var detectedOs = osString.Contains("Linux", StringComparison.OrdinalIgnoreCase)
                ? OsType.Linux : OsType.Windows;

            return new ConnectionTestResult(
                Reachable: true,
                DetectedOs: detectedOs,
                OsVersion: osString,
                Latency: sw.Elapsed,
                ProtocolVersion: "SSH-2.0",
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Connection test failed for {Host}:{Port}", target.Hostname, target.Port);
            return new ConnectionTestResult(
                Reachable: false,
                DetectedOs: null,
                OsVersion: null,
                Latency: sw.Elapsed,
                ProtocolVersion: null,
                ErrorMessage: ex.Message);
        }
    }

    private static SshClient CreateSshClient(MachineTarget target, CredentialPayload credential)
    {
        var password = Encoding.UTF8.GetString(credential.DecryptedPayload);
        return new SshClient(target.Hostname.ToString(), target.Port, credential.Username, password);
    }

    private static ScpClient CreateScpClient(MachineTarget target, CredentialPayload credential)
    {
        var password = Encoding.UTF8.GetString(credential.DecryptedPayload);
        return new ScpClient(target.Hostname.ToString(), target.Port, credential.Username, password);
    }

    private static string WrapWithElevation(RemoteCommand command)
    {
        return command.Elevation switch
        {
            ElevationMode.Sudo => $"sudo {command.CommandText}",
            ElevationMode.SudoAsUser when command.RunAsUser is not null =>
                $"sudo -u {command.RunAsUser} {command.CommandText}",
            _ => command.CommandText
        };
    }
}
