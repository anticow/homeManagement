extern alias AgentClient;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;
using HomeManagement.Agent.Protocol;
using HomeManagement.AgentGateway.Host.Endpoints;
using HomeManagement.AgentGateway.Host.Services;
using HomeManagement.Auth;
using HomeManagement.Auth.Host;
using HomeManagement.Auth.Host.Endpoints;
using HomeManagement.Broker.Host.Endpoints;
using HomeManagement.Broker.Host.Hubs;
using HomeManagement.Core;
using HomeManagement.Data;
using HomeManagement.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Refit;

namespace HomeManagement.Web.Tests;

using AgentProtocol = AgentClient::HomeManagement.Agent.Protocol;

public sealed class WebBrokerAgentEndToEndTests
{
    private const string SharedSigningKey = "end-to-end-test-signing-key-that-is-long-enough-for-hmac-sha256!";
    private const string SharedIssuer = "end-to-end-tests";
    private const string SharedAudience = "homemanagement-api";
    private const string AdminUsername = "admin";
    private const string AdminPassword = "HomeManagement_TestAdmin1!";

    [Fact]
    public async Task PatchScan_FromWebSession_RoutesThroughBrokerGatewayAndAgent()
    {
        var machineId = Guid.NewGuid();
        var machine = CreateAgentMachine(machineId, "agent-e2e");

        await using var gatewayHost = await TestAgentGatewayHost.StartAsync();
        await using var agentSession = await TestAgentSession.ConnectAsync(gatewayHost.GrpcBaseAddress, gatewayHost.ApiKey, machine.Hostname.Value);
        await gatewayHost.WaitForAgentAsync(machine.Hostname.Value, CancellationToken.None);

        await using var brokerFactory = new BrokerHostWebApplicationFactory(gatewayHost.ControlBaseAddress, gatewayHost.ApiKey, machine);
        await using var authFactory = new AuthHostWebApplicationFactory();

        var authClient = authFactory.CreateClient();
        var authApi = RestService.For<IAuthApi>(authClient, new RefitSettings
        {
            ContentSerializer = new SystemTextJsonContentSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web))
        });
        var sessionState = new ServerSessionState();
        var authService = new WebSessionAuthService(authApi, sessionState);

        var brokerHttpClient = brokerFactory.CreateClient();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(BrokerApiClient.HttpClientName).Returns(brokerHttpClient);

        var brokerApi = new BrokerApiClient(httpClientFactory, sessionState, authService);

        var login = await authService.LoginAsync(AdminUsername, AdminPassword);
        login.Success.Should().BeTrue();
        sessionState.IsAuthenticated.Should().BeTrue();

        var patches = await brokerApi.ScanPatchesAsync(new HomeManagement.Web.Services.PatchScanRequest(machineId));

        patches.Should().HaveCount(2);
        patches.Select(patch => patch.PatchId).Should().BeEquivalentTo(["curl", "vim"]);
        agentSession.ReceivedCommands.Should().ContainSingle();
        agentSession.ReceivedCommands.Single().CommandText.Should().Contain("apt list --upgradable");
    }

    [Fact]
    public async Task JobSubmission_FromBrokerApi_RoutesThroughGatewayAndPersistsCompletion()
    {
        var machineId = Guid.NewGuid();
        var machine = CreateAgentMachine(machineId, "agent-job-e2e");

        await using var gatewayHost = await TestAgentGatewayHost.StartAsync();
        await using var agentSession = await TestAgentSession.ConnectAsync(gatewayHost.GrpcBaseAddress, gatewayHost.ApiKey, machine.Hostname.Value);
        await gatewayHost.WaitForAgentAsync(machine.Hostname.Value, CancellationToken.None);

        await using var brokerFactory = new BrokerHostWebApplicationFactory(gatewayHost.ControlBaseAddress, gatewayHost.ApiKey, machine);
        await using var authFactory = new AuthHostWebApplicationFactory();

        var authClient = authFactory.CreateClient();
        var login = await authClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(AdminUsername, AdminPassword, AuthProviderType.Local));
        login.EnsureSuccessStatusCode();
        var authResult = await login.Content.ReadFromJsonAsync<AuthResult>();
        authResult.Should().NotBeNull();
        authResult!.Success.Should().BeTrue();
        authResult.AccessToken.Should().NotBeNullOrWhiteSpace();

        var brokerClient = brokerFactory.CreateClient();
        brokerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authResult.AccessToken);

        var jobDefinition = new JobDefinition(
            Name: "Patch Scan Job",
            Type: JobType.PatchScan,
            TargetMachineIds: [machineId],
            Parameters: []);

        var submit = await brokerClient.PostAsJsonAsync("/api/jobs", jobDefinition);
        submit.EnsureSuccessStatusCode();
        var submitPayload = await submit.Content.ReadFromJsonAsync<JobSubmissionResponse>();
        submitPayload.Should().NotBeNull();

        JobStatus? job = null;
        var timeoutAt = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < timeoutAt)
        {
            job = await brokerClient.GetFromJsonAsync<JobStatus>($"/api/jobs/{submitPayload!.JobId}");
            if (job is not null && job.State is JobState.Completed or JobState.Failed)
            {
                break;
            }

            await Task.Delay(100);
        }

        job.Should().NotBeNull();
        job!.State.Should().Be(JobState.Completed);
        job.CompletedTargets.Should().Be(1);
        job.FailedTargets.Should().Be(0);
        job.MachineResults.Should().ContainSingle();
        job.MachineResults.Single().Success.Should().BeTrue();
        agentSession.ReceivedCommands.Should().ContainSingle();
        agentSession.ReceivedCommands.Single().CommandText.Should().Contain("apt list --upgradable");
    }

    private static Machine CreateAgentMachine(Guid machineId, string hostname)
    {
        return new Machine(
            machineId,
            Hostname.Create(hostname),
            null,
            [],
            OsType.Linux,
            "Ubuntu 24.04",
            MachineConnectionMode.Agent,
            TransportProtocol.Agent,
            9444,
            Guid.Empty,
            MachineState.Online,
            new Dictionary<string, string>().AsReadOnly(),
            null,
            DateTime.UtcNow,
            DateTime.UtcNow,
            DateTime.UtcNow);
    }

    private sealed class AuthHostWebApplicationFactory : WebApplicationFactory<AuthHostAssemblyMarker>
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        public AuthHostWebApplicationFactory()
        {
            _connection.Open();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:HomeManagement"] = "Data Source=:memory:",
                    ["Auth:JwtSigningKey"] = SharedSigningKey,
                    ["Auth:Issuer"] = SharedIssuer,
                    ["Auth:Audience"] = SharedAudience,
                    ["Auth:BootstrapAdmin:Enabled"] = "true",
                    ["Auth:BootstrapAdmin:Username"] = AdminUsername,
                    ["Auth:BootstrapAdmin:Password"] = AdminPassword,
                    ["Auth:BootstrapAdmin:DisplayName"] = "Integration Admin",
                    ["Auth:BootstrapAdmin:Email"] = "admin@test.local"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<HomeManagementDbContext>>();
                services.RemoveAll<HomeManagementDbContext>();

                services.AddDbContext<HomeManagementDbContext>(options => options.UseSqlite(_connection));
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class BrokerHostWebApplicationFactory : WebApplicationFactory<EventHub>
    {
        private readonly Machine _machine;
        private readonly string _databasePath;
        private readonly string _dataDirectory;
        private readonly string _gatewayApiKey;
        private readonly Uri _gatewayBaseAddress;

        public BrokerHostWebApplicationFactory(Uri gatewayBaseAddress, string gatewayApiKey, Machine machine)
        {
            _gatewayBaseAddress = gatewayBaseAddress;
            _gatewayApiKey = gatewayApiKey;
            _machine = machine;
            _dataDirectory = Path.Combine(Path.GetTempPath(), $"hm-broker-e2e-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dataDirectory);
            _databasePath = Path.Combine(_dataDirectory, "broker-tests.db");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:HomeManagement"] = $"Data Source={_databasePath}",
                    ["DataDirectory"] = _dataDirectory,
                    ["Auth:JwtSigningKey"] = SharedSigningKey,
                    ["Auth:Issuer"] = SharedIssuer,
                    ["Auth:Audience"] = SharedAudience,
                    ["AgentGateway:BaseUrl"] = _gatewayBaseAddress.ToString().TrimEnd('/'),
                    ["AgentGateway:ApiKey"] = _gatewayApiKey,
                    ["AgentGateway:PollIntervalSeconds"] = "1"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<HomeManagementDbContext>>();
                services.RemoveAll<HomeManagementDbContext>();
                services.RemoveAll<IInventoryService>();

                services.AddDbContext<HomeManagementDbContext>(options =>
                    options.UseSqlite($"Data Source={_databasePath}"));

                services.AddScoped<IInventoryService>(_ => new TestInventoryService(_machine));
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();

            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(_dataDirectory))
                    {
                        Directory.Delete(_dataDirectory, recursive: true);
                    }

                    return;
                }
                catch (IOException)
                {
                    if (attempt == 4)
                    {
                        break;
                    }

                    await Task.Delay(100);
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempt == 4)
                    {
                        break;
                    }

                    await Task.Delay(100);
                }
            }

            try
            {
                if (Directory.Exists(_dataDirectory))
                {
                    Directory.Delete(_dataDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class TestInventoryService : IInventoryService
    {
        private readonly Machine _machine;

        public TestInventoryService(Machine machine)
        {
            _machine = machine;
        }

        public Task<Machine> AddAsync(MachineCreateRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Machine> UpdateAsync(Guid id, MachineUpdateRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RemoveAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task BatchRemoveAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Machine?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<Machine?>(id == _machine.Id ? _machine : null);

        public Task<PagedResult<Machine>> QueryAsync(MachineQuery query, CancellationToken ct = default) =>
            Task.FromResult(new PagedResult<Machine>([_machine], 1, query.Page, query.PageSize));

        public Task<Machine> RefreshMetadataAsync(Guid id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Machine>> DiscoverAsync(CidrRange range, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ImportAsync(Stream csvStream, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ExportAsync(MachineQuery query, Stream destination, ExportFormat format, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestAgentGatewayHost : IAsyncDisposable
    {
        private const string HeaderName = "x-agent-gateway-api-key";

        private readonly WebApplication _app;
        private readonly HttpClient _controlClient;

        private TestAgentGatewayHost(WebApplication app, Uri controlBaseAddress, Uri grpcBaseAddress, string apiKey)
        {
            _app = app;
            ControlBaseAddress = controlBaseAddress;
            GrpcBaseAddress = grpcBaseAddress;
            ApiKey = apiKey;
            _controlClient = new HttpClient { BaseAddress = controlBaseAddress };
            _controlClient.DefaultRequestHeaders.Add(HeaderName, apiKey);
        }

        public Uri ControlBaseAddress { get; }

        public Uri GrpcBaseAddress { get; }

        public string ApiKey { get; }

        public static async Task<TestAgentGatewayHost> StartAsync()
        {
            var apiKey = $"test-agent-gateway-key-{Guid.NewGuid():N}";

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });

            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentGateway:ApiKey"] = apiKey
            });

            builder.Logging.AddFilter("Grpc.AspNetCore.Server", LogLevel.Warning);

            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http1;
                });

                options.Listen(IPAddress.Loopback, 0, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http2;
                });
            });

            builder.Services.AddLogging();
            builder.Services.AddSingleton<TestApiKeyInterceptor>();
            builder.Services.AddGrpc(options => options.Interceptors.Add<TestApiKeyInterceptor>());
            builder.Services.AddSingleton<StandaloneAgentGatewayService>();
            builder.Services.AddHealthChecks();

            var app = builder.Build();
            app.MapHealthChecks("/healthz");
            app.MapGet("/readyz", () => Results.Ok("ready"));
            app.MapGrpcService<AgentGatewayGrpcService>();
            app.MapControlPlaneEndpoints();

            await app.StartAsync();

            var addresses = app.Urls
                .Where(url => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                .ToList();

            return new TestAgentGatewayHost(
                app,
                new Uri(addresses[0], UriKind.Absolute),
                new Uri(addresses[1], UriKind.Absolute),
                apiKey);
        }

        public async Task WaitForAgentAsync(string agentId, CancellationToken ct)
        {
            var timeoutAt = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < timeoutAt)
            {
                var agents = await _controlClient.GetFromJsonAsync<List<ConnectedAgent>>("/internal/agents", ct) ?? [];
                if (agents.Any(agent => string.Equals(agent.AgentId, agentId, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                await Task.Delay(100, ct);
            }

            throw new TimeoutException($"Timed out waiting for agent '{agentId}' to connect.");
        }

        public async ValueTask DisposeAsync()
        {
            _controlClient.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class TestAgentSession : IAsyncDisposable
    {
        private readonly GrpcChannel _channel;
        private readonly AsyncDuplexStreamingCall<AgentProtocol.AgentMessage, AgentProtocol.ControlMessage> _call;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _readLoop;

        private TestAgentSession(
            GrpcChannel channel,
            AsyncDuplexStreamingCall<AgentProtocol.AgentMessage, AgentProtocol.ControlMessage> call)
        {
            _channel = channel;
            _call = call;
            _readLoop = Task.Run(ReadLoopAsync);
        }

        public ConcurrentQueue<AgentProtocol.CommandRequest> ReceivedCommands { get; } = new();

        public static async Task<TestAgentSession> ConnectAsync(Uri gatewayBaseAddress, string apiKey, string agentId)
        {
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            var channel = GrpcChannel.ForAddress(gatewayBaseAddress, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true
                }
            });

            var client = new AgentProtocol.AgentHub.AgentHubClient(channel);
            var headers = new Metadata
            {
                { "x-agent-api-key", apiKey }
            };

            var call = client.Connect(headers);
            await call.RequestStream.WriteAsync(new AgentProtocol.AgentMessage
            {
                Handshake = new AgentProtocol.Handshake
                {
                    AgentId = agentId,
                    Hostname = agentId,
                    AgentVersion = "1.0.0-test",
                    OsType = "Linux",
                    OsVersion = "Ubuntu 24.04",
                    Architecture = "x64",
                    ProtocolVersion = 1
                }
            });

            return new TestAgentSession(channel, call);
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (await _call.ResponseStream.MoveNext(_cts.Token))
                {
                    var message = _call.ResponseStream.Current;
                    if (message.PayloadCase != AgentProtocol.ControlMessage.PayloadOneofCase.CommandRequest)
                    {
                        continue;
                    }

                    var command = message.CommandRequest;
                    ReceivedCommands.Enqueue(command);

                    await _call.RequestStream.WriteAsync(new AgentProtocol.AgentMessage
                    {
                        CommandResponse = new AgentProtocol.CommandResponse
                        {
                            RequestId = command.RequestId,
                            ExitCode = 0,
                            Stdout = "curl/jammy-security 8.5.0 amd64 [upgradable from: 8.4.0]\nvim/jammy-updates 9.1.0 amd64 [upgradable from: 9.0.0]",
                            Stderr = string.Empty,
                            DurationMs = 25,
                            CorrelationId = command.CorrelationId
                        }
                    });
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }
            catch (RpcException ex) when (_cts.IsCancellationRequested && ex.StatusCode == StatusCode.Cancelled)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _call.RequestStream.CompleteAsync();
            }
            catch (InvalidOperationException)
            {
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
            {
            }

            try
            {
                await _readLoop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                _cts.Cancel();
            }

            _call.Dispose();

            try
            {
                await _readLoop;
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }

            _channel.Dispose();
            _cts.Dispose();
        }
    }

    private sealed class TestApiKeyInterceptor : Interceptor
    {
        public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
            IAsyncStreamReader<TRequest> requestStream,
            IServerStreamWriter<TResponse> responseStream,
            ServerCallContext context,
            DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        {
            var expectedKey = context.GetHttpContext().RequestServices
                .GetRequiredService<IConfiguration>()["AgentGateway:ApiKey"]
                ?? throw new InvalidOperationException("AgentGateway:ApiKey must be configured.");

            var suppliedKey = context.RequestHeaders.GetValue("x-agent-api-key");
            if (!string.Equals(expectedKey, suppliedKey, StringComparison.Ordinal))
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid API key."));
            }

            return continuation(requestStream, responseStream, context);
        }
    }

    private sealed record JobSubmissionResponse(Guid JobId);
}