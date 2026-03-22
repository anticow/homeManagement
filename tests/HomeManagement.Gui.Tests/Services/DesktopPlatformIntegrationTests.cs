using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security;
using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;
using HomeManagement.Gui.Services;
using HomeManagement.Gui.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Gui.Tests.Services;

public sealed class DesktopPlatformIntegrationTests : IDisposable
{
    private readonly ServiceCollection _services = new();
    private readonly BehaviorSubject<bool> _vaultLockState = new(false);
    private ServiceProvider? _provider;

    [Fact]
    public void PlatformMode_WhenVaultLocked_StartsOnUnlockScreen()
    {
        using var provider = BuildMainWindowProvider(isVaultUnlocked: false);
        var navigation = provider.GetRequiredService<NavigationService>();

        _ = new MainWindowViewModel(
            navigation,
            provider.GetRequiredService<IJobScheduler>(),
            provider.GetRequiredService<IAgentGateway>(),
            provider.GetRequiredService<ICredentialVault>(),
            new DesktopPlatformOptions
            {
                BrokerBaseUrl = "https://broker.cowgomu.net",
                AuthBaseUrl = "https://authapi.cowgomu.net",
                Username = "admin"
            });

        navigation.CurrentPage.Should().BeOfType<VaultUnlockViewModel>();
    }

    [Fact]
    public void PlatformMode_WhenVaultAlreadyUnlocked_StartsOnDashboard()
    {
        using var provider = BuildMainWindowProvider(isVaultUnlocked: true);
        var navigation = provider.GetRequiredService<NavigationService>();

        _ = new MainWindowViewModel(
            navigation,
            provider.GetRequiredService<IJobScheduler>(),
            provider.GetRequiredService<IAgentGateway>(),
            provider.GetRequiredService<ICredentialVault>(),
            new DesktopPlatformOptions
            {
                BrokerBaseUrl = "https://broker.cowgomu.net",
                AuthBaseUrl = "https://authapi.cowgomu.net",
                Username = "admin"
            });

        navigation.CurrentPage.Should().BeOfType<DashboardViewModel>();
    }

    [Fact]
    public async Task UnlockScreen_NavigatesToDashboard_WhenOpenedAsFirstPage()
    {
        using var provider = BuildMainWindowProvider(isVaultUnlocked: false);
        var navigation = provider.GetRequiredService<NavigationService>();
        var vault = (TestCredentialVault)provider.GetRequiredService<ICredentialVault>();
        var viewModel = provider.GetRequiredService<VaultUnlockViewModel>();

        navigation.NavigateTo(viewModel);

        await viewModel.UnlockCommand.Execute(CreateSecureString("test-password"));

        vault.UnlockCallCount.Should().Be(1);
        navigation.CurrentPage.Should().BeOfType<DashboardViewModel>();
    }

    [Fact]
    public void AddDesktopPlatformClients_RegistersRemoteGuiServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentGateway:BaseUrl"] = "https://gateway.cowgomu.net",
                ["AgentGateway:ApiKey"] = "test-key",
                ["AgentGateway:PollIntervalSeconds"] = "5"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
        services.AddDesktopPlatformClients(new DesktopPlatformOptions
        {
            BrokerBaseUrl = "https://broker.cowgomu.net",
            AuthBaseUrl = "https://authapi.cowgomu.net",
            AgentGatewayBaseUrl = "https://gateway.cowgomu.net",
            Username = "admin"
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<DesktopPlatformOptions>().Username.Should().Be("admin");
        provider.GetRequiredService<IInventoryService>().Should().NotBeNull();
        provider.GetRequiredService<IPatchService>().Should().NotBeNull();
        provider.GetRequiredService<IServiceController>().Should().NotBeNull();
        provider.GetRequiredService<IJobScheduler>().Should().NotBeNull();
        provider.GetRequiredService<IAuditLogger>().Should().NotBeNull();
        provider.GetRequiredService<ICredentialVault>().Should().NotBeNull();
        provider.GetRequiredService<ISystemHealthService>().Should().NotBeNull();
        provider.GetRequiredService<IRemoteExecutor>().Should().NotBeNull();
        provider.GetRequiredService<IAgentGateway>().Should().NotBeNull();
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _vaultLockState.Dispose();
    }

    private ServiceProvider BuildMainWindowProvider(bool isVaultUnlocked)
    {
        _provider?.Dispose();
        _services.Clear();
        _vaultLockState.OnNext(isVaultUnlocked);

        _services.AddSingleton<NavigationService>();
        _services.AddSingleton<ICredentialVault>(_ => new TestCredentialVault(_vaultLockState, isVaultUnlocked));
        _services.AddSingleton<IJobScheduler, TestJobScheduler>();
        _services.AddSingleton<IAgentGateway, TestAgentGateway>();
        _services.AddSingleton<IInventoryService, TestInventoryService>();
        _services.AddSingleton<ISystemHealthService, TestSystemHealthService>();
        _services.AddTransient<DashboardViewModel>();
        _services.AddTransient<VaultUnlockViewModel>();

        _provider = _services.BuildServiceProvider();
        return _provider;
    }

    private static SecureString CreateSecureString(string value)
    {
        var secureString = new SecureString();
        foreach (var character in value)
        {
            secureString.AppendChar(character);
        }

        secureString.MakeReadOnly();
        return secureString;
    }

    private sealed class TestCredentialVault : ICredentialVault
    {
        private readonly BehaviorSubject<bool> _lockState;

        public TestCredentialVault(BehaviorSubject<bool> lockState, bool isUnlocked)
        {
            _lockState = lockState;
            IsUnlocked = isUnlocked;
        }

        public int UnlockCallCount { get; private set; }

        public Task UnlockAsync(SecureString masterPassword, CancellationToken ct = default)
        {
            UnlockCallCount++;
            IsUnlocked = true;
            _lockState.OnNext(true);
            return Task.CompletedTask;
        }

        public Task LockAsync(CancellationToken ct = default)
        {
            IsUnlocked = false;
            _lockState.OnNext(false);
            return Task.CompletedTask;
        }

        public bool IsUnlocked { get; private set; }
        public IObservable<bool> LockStateChanged => _lockState.AsObservable();
        public Task<CredentialEntry> AddAsync(CredentialCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialEntry> UpdateAsync(Guid id, CredentialUpdateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CredentialEntry>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialPayload> GetPayloadAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RotateEncryptionKeyAsync(SecureString newMasterPassword, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> ExportAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ImportAsync(byte[] encryptedBlob, SecureString password, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class TestJobScheduler : IJobScheduler
    {
        public Task<JobId> SubmitAsync(JobDefinition job, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ScheduleId> ScheduleAsync(JobDefinition job, string cronExpression, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CancelAsync(JobId jobId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UnscheduleAsync(ScheduleId scheduleId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JobStatus> GetStatusAsync(JobId jobId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PagedResult<JobSummary>> ListJobsAsync(JobQuery query, CancellationToken ct = default) =>
            Task.FromResult(new PagedResult<JobSummary>(Array.Empty<JobSummary>(), 0, query.Page, query.PageSize));
        public Task<IReadOnlyList<ScheduledJobSummary>> ListSchedulesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScheduledJobSummary>>(Array.Empty<ScheduledJobSummary>());
        public IObservable<JobProgressEvent> ProgressStream => Observable.Never<JobProgressEvent>();
        public IObservable<JobProgressEvent> GetJobProgressStream(JobId jobId) => Observable.Never<JobProgressEvent>();
    }

    private sealed class TestAgentGateway : IAgentGateway
    {
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<ConnectedAgent> GetConnectedAgents() => Array.Empty<ConnectedAgent>();
        public Task<RemoteResult> SendCommandAsync(string agentId, RemoteCommand command, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentMetadata> GetMetadataAsync(string agentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RequestUpdateAsync(string agentId, AgentUpdatePackage package, CancellationToken ct = default) => throw new NotSupportedException();
        public IObservable<AgentConnectionEvent> ConnectionEvents => Observable.Never<AgentConnectionEvent>();
    }

    private sealed class TestInventoryService : IInventoryService
    {
        public Task<Machine> AddAsync(MachineCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Machine> UpdateAsync(Guid id, MachineUpdateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BatchRemoveAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Machine?> GetAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PagedResult<Machine>> QueryAsync(MachineQuery query, CancellationToken ct = default) =>
            Task.FromResult(new PagedResult<Machine>(Array.Empty<Machine>(), 0, query.Page, query.PageSize));
        public Task<Machine> RefreshMetadataAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Machine>> DiscoverAsync(CidrRange range, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ImportAsync(Stream csvStream, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ExportAsync(MachineQuery query, Stream destination, ExportFormat format, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class TestSystemHealthService : ISystemHealthService
    {
        public Task<SystemHealthReport> CheckAsync(CancellationToken ct = default) =>
            Task.FromResult(new SystemHealthReport(HealthStatus.Healthy, Array.Empty<ComponentHealth>(), DateTime.UtcNow));
    }
}