using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Abstractions.Validation;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Inventory;

/// <summary>
/// Maintains the registry of managed machines with their metadata and group membership.
/// Provides discovery, import/export, and metadata refresh.
///
/// <see cref="RefreshMetadataAsync"/> uses <see cref="IEndpointStateProvider"/> (typically
/// Prometheus) when available, falling back to direct remote execution when no metrics data exists.
/// </summary>
internal sealed class InventoryService : IInventoryService
{
    private readonly IMachineRepository _machineRepo;
    private readonly IRemoteExecutor _executor;
    private readonly ICorrelationContext _correlation;
    private readonly ILogger<InventoryService> _logger;
    private readonly IEndpointStateProvider? _stateProvider;

    public InventoryService(
        IMachineRepository machineRepo,
        IRemoteExecutor executor,
        ICorrelationContext correlation,
        ILogger<InventoryService> logger,
        IEndpointStateProvider? stateProvider = null)
    {
        _machineRepo = machineRepo;
        _executor = executor;
        _correlation = correlation;
        _logger = logger;
        _stateProvider = stateProvider;
    }

    public async Task<Machine> AddAsync(MachineCreateRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var machine = new Machine(
            Id: Guid.NewGuid(),
            Hostname: request.Hostname,
            Fqdn: request.Fqdn,
            IpAddresses: [],
            OsType: request.OsType,
            OsVersion: string.Empty,
            ConnectionMode: request.ConnectionMode,
            Protocol: request.Protocol,
            Port: request.Port,
            CredentialId: request.CredentialId,
            State: MachineState.Offline,
            Tags: request.Tags?.AsReadOnly() ?? new Dictionary<string, string>().AsReadOnly(),
            Hardware: null,
            CreatedUtc: now,
            UpdatedUtc: now,
            LastContactUtc: now);

        await _machineRepo.AddAsync(machine, ct);
        await _machineRepo.SaveChangesAsync(ct);

        _logger.LogInformation("[{CorrelationId}] Machine added: {Host} ({Os}, {Protocol})",
            _correlation.CorrelationId, request.Hostname, request.OsType, request.Protocol);

        return machine;
    }

    public async Task<Machine> UpdateAsync(Guid id, MachineUpdateRequest request, CancellationToken ct = default)
    {
        var existing = await _machineRepo.GetByIdAsync(id, ct)
            ?? throw new KeyNotFoundException($"Machine {id} not found.");

        var updated = existing with
        {
            Hostname = request.Hostname ?? existing.Hostname,
            Fqdn = request.Fqdn ?? existing.Fqdn,
            ConnectionMode = request.ConnectionMode ?? existing.ConnectionMode,
            Protocol = request.Protocol ?? existing.Protocol,
            Port = request.Port ?? existing.Port,
            CredentialId = request.CredentialId ?? existing.CredentialId,
            State = request.State ?? existing.State,
            Tags = request.Tags?.AsReadOnly() ?? existing.Tags,
            UpdatedUtc = DateTime.UtcNow
        };

        await _machineRepo.UpdateAsync(updated, ct);
        await _machineRepo.SaveChangesAsync(ct);

        _logger.LogInformation("[{CorrelationId}] Machine updated: {MachineId}", _correlation.CorrelationId, id);
        return updated;
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        await _machineRepo.SoftDeleteAsync(id, ct);
        await _machineRepo.SaveChangesAsync(ct);
        _logger.LogInformation("[{CorrelationId}] Machine soft-deleted: {MachineId}", _correlation.CorrelationId, id);
    }

    public async Task BatchRemoveAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        await _machineRepo.SoftDeleteRangeAsync(ids, ct);
        await _machineRepo.SaveChangesAsync(ct);
        _logger.LogInformation("[{CorrelationId}] Batch soft-deleted {Count} machines", _correlation.CorrelationId, ids.Count);
    }

    public async Task<Machine?> GetAsync(Guid id, CancellationToken ct = default)
    {
        return await _machineRepo.GetByIdAsync(id, ct);
    }

    public async Task<PagedResult<Machine>> QueryAsync(MachineQuery query, CancellationToken ct = default)
    {
        return await _machineRepo.QueryAsync(query, ct);
    }

    public async Task<Machine> RefreshMetadataAsync(Guid id, CancellationToken ct = default)
    {
        var machine = await _machineRepo.GetByIdAsync(id, ct)
            ?? throw new KeyNotFoundException($"Machine {id} not found.");

        _logger.LogInformation("[{CorrelationId}] Refreshing metadata for {Host}",
            _correlation.CorrelationId, machine.Hostname);

        HardwareInfo? hardware = null;

        // ── Prometheus path (preferred) ───────────────────────────────────────
        if (_stateProvider is not null)
        {
            var metrics = await _stateProvider.GetHardwareMetricsAsync(
                machine.Hostname.Value, machine.OsType, ct);

            if (metrics is not null)
            {
                _logger.LogDebug("[{CorrelationId}] Hardware metrics for {Host} from Prometheus",
                    _correlation.CorrelationId, machine.Hostname);

                hardware = new HardwareInfo(
                    CpuCores: 0,  // Prometheus CPU usage % is available, but core count requires lshw/nproc
                    RamBytes: metrics.MemoryTotalBytes ?? machine.Hardware?.RamBytes ?? 0,
                    Disks: BuildDiskInfoFromMetrics(metrics, machine.Hardware?.Disks ?? []),
                    Architecture: machine.Hardware?.Architecture ?? string.Empty);
            }
        }

        // ── Remote fallback (when Prometheus has no data for this endpoint) ──
        if (hardware is null)
        {
            var target = ToMachineTarget(machine);
            var infoCommand = machine.OsType == OsType.Linux
                ? "echo $(nproc),$(free -b | awk '/Mem:/{print $2}'),$(uname -m)"
                : "Write-Output \"$($env:NUMBER_OF_PROCESSORS),$([math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory)),$($env:PROCESSOR_ARCHITECTURE)\"";

            var result = await _executor.ExecuteAsync(target,
                new RemoteCommand(infoCommand, TimeSpan.FromSeconds(30)), ct);

            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout))
            {
                var parts = result.Stdout.Trim().Split(',');
                if (parts.Length >= 3)
                {
                    _ = int.TryParse(parts[0], out var cpuCores);
                    _ = long.TryParse(parts[1], out var ramBytes);
                    hardware = new HardwareInfo(cpuCores, ramBytes, [], parts[2].Trim());
                }
            }
        }

        // Determine online state: if Prometheus says so, trust it; otherwise assume online
        // because we just successfully contacted the machine (or it's in inventory).
        var newState = MachineState.Online;
        if (_stateProvider is not null)
        {
            var isOnline = await _stateProvider.GetEndpointOnlineAsync(machine.Hostname.Value, ct);
            // Only downgrade to Offline if Prometheus actively reports the endpoint as down.
            // If Prometheus has no data at all, keep Online (don't penalize unmonitored machines).
            newState = isOnline ? MachineState.Online : machine.State;
        }

        var updated = machine with
        {
            Hardware = hardware ?? machine.Hardware,
            State = newState,
            LastContactUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };

        await _machineRepo.UpdateAsync(updated, ct);
        await _machineRepo.SaveChangesAsync(ct);

        _logger.LogInformation("[{CorrelationId}] Metadata refreshed for {Host}: CPU={Cores}, RAM={Ram}",
            _correlation.CorrelationId, machine.Hostname, hardware?.CpuCores, hardware?.RamBytes);

        return updated;
    }

    private static DiskInfo[] BuildDiskInfoFromMetrics(HardwareMetrics metrics, DiskInfo[] existing)
    {
        if (metrics.DiskTotalBytes is null) return existing;
        // Prometheus gives us single root disk stats; update or create root entry.
        var root = existing.FirstOrDefault(d => d.MountPoint is "/" or "C:");
        if (root is not null)
        {
            return existing
                .Select(d => d.MountPoint == root.MountPoint
                    ? d with { TotalBytes = metrics.DiskTotalBytes.Value,
                                FreeBytes = metrics.DiskFreeBytes ?? d.FreeBytes }
                    : d)
                .ToArray();
        }

        // No existing entry — create a synthetic root disk entry.
        return [new DiskInfo("/", metrics.DiskTotalBytes.Value, metrics.DiskFreeBytes ?? 0)];
    }

    public async Task<IReadOnlyList<Machine>> DiscoverAsync(CidrRange range, CancellationToken ct = default)
    {
        _logger.LogInformation("[{CorrelationId}] Starting network discovery in {Range}",
            _correlation.CorrelationId, range);

        // Parse CIDR to enumerate IPs
        var ips = ParseCidrRange(range);
        var discovered = new List<Machine>();

        foreach (var ip in ips)
        {
            ct.ThrowIfCancellationRequested();

            if (!Hostname.TryCreate(ip.ToString(), out var hostname, out _))
                continue;

            var target = new MachineTarget(
                MachineId: Guid.Empty,
                Hostname: hostname,
                OsType: OsType.Linux, // Probe as Linux first
                ConnectionMode: MachineConnectionMode.Agentless,
                Protocol: TransportProtocol.Ssh,
                Port: 22,
                CredentialId: Guid.Empty);

            var test = await _executor.TestConnectionAsync(target, ct);
            if (test.Reachable)
            {
                var machine = new Machine(
                    Id: Guid.NewGuid(),
                    Hostname: hostname,
                    Fqdn: null,
                    IpAddresses: [ip],
                    OsType: test.DetectedOs ?? OsType.Linux,
                    OsVersion: test.OsVersion ?? string.Empty,
                    ConnectionMode: MachineConnectionMode.Agentless,
                    Protocol: TransportProtocol.Ssh,
                    Port: 22,
                    CredentialId: Guid.Empty,
                    State: MachineState.Online,
                    Tags: new Dictionary<string, string> { ["discovered"] = "true" }.AsReadOnly(),
                    Hardware: null,
                    CreatedUtc: DateTime.UtcNow,
                    UpdatedUtc: DateTime.UtcNow,
                    LastContactUtc: DateTime.UtcNow);

                discovered.Add(machine);
            }
        }

        _logger.LogInformation("[{CorrelationId}] Discovery complete: found {Count} machines in {Range}",
            _correlation.CorrelationId, discovered.Count, range);

        return discovered;
    }

    public async Task ImportAsync(Stream csvStream, CancellationToken ct = default)
    {
        using var reader = new StreamReader(csvStream, Encoding.UTF8);
        var header = await reader.ReadLineAsync(ct);
        if (header is null) return;

        var count = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            var fields = line.Split(',');
            if (fields.Length < 5) continue;

            if (!Hostname.TryCreate(fields[0].Trim(), out var hostname, out _)) continue;
            if (!Enum.TryParse<OsType>(fields[1].Trim(), true, out var osType)) continue;
            if (!Enum.TryParse<TransportProtocol>(fields[2].Trim(), true, out var protocol)) continue;
            if (!int.TryParse(fields[3].Trim(), CultureInfo.InvariantCulture, out var port)) continue;

            var request = new MachineCreateRequest(
                hostname, null, osType, MachineConnectionMode.Agentless, protocol, port, Guid.Empty);
            await AddAsync(request, ct);
            count++;
        }

        _logger.LogInformation("[{CorrelationId}] Imported {Count} machines from CSV", _correlation.CorrelationId, count);
    }

    public async Task ExportAsync(MachineQuery query, Stream destination, ExportFormat format, CancellationToken ct = default)
    {
        var result = await _machineRepo.QueryAsync(query with { PageSize = 10000 }, ct);

        if (format == ExportFormat.Json)
        {
            await JsonSerializer.SerializeAsync(destination, result.Items, cancellationToken: ct);
        }
        else
        {
            await using var writer = new StreamWriter(destination, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync("Hostname,OsType,Protocol,Port,State,ConnectionMode");
            foreach (var m in result.Items)
            {
                await writer.WriteLineAsync(
                    $"{m.Hostname},{m.OsType},{m.Protocol},{m.Port},{m.State},{m.ConnectionMode}");
            }
        }

        _logger.LogInformation("[{CorrelationId}] Exported {Count} machines as {Format}",
            _correlation.CorrelationId, result.Items.Count, format);
    }

    private static MachineTarget ToMachineTarget(Machine m) => new(
        m.Id, m.Hostname, m.OsType, m.ConnectionMode, m.Protocol, m.Port, m.CredentialId);

    private static List<IPAddress> ParseCidrRange(CidrRange range)
    {
        var parts = range.ToString().Split('/');
        var baseIp = IPAddress.Parse(parts[0]);
        var prefix = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var hostBits = 32 - prefix;
        var hostCount = Math.Min(1 << hostBits, 256); // Cap at /24 for safety

        var baseBytes = baseIp.GetAddressBytes();
        var baseUint = (uint)(baseBytes[0] << 24 | baseBytes[1] << 16 | baseBytes[2] << 8 | baseBytes[3]);

        var addresses = new List<IPAddress>();
        for (var i = 1; i < hostCount - 1; i++) // Skip network and broadcast
        {
            var ip = baseUint + (uint)i;
            addresses.Add(new IPAddress(new[]
            {
                (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip
            }));
        }

        return addresses;
    }
}
