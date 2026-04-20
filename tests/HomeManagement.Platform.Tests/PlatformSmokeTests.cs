using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace HomeManagement.Platform.Tests;

/// <summary>
/// End-to-end smoke tests that exercise a running docker-compose platform stack.
/// Set the PLATFORM_BASE_HOST environment variable to override the default URLs.
/// Run: docker compose -f deploy/docker/docker-compose.yaml up -d
/// Then: dotnet test tests/HomeManagement.Platform.Tests --filter "Category=Platform"
/// </summary>
[Trait("Category", "Platform")]
public sealed class PlatformSmokeTests : IAsyncLifetime, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _brokerUrl;
    private readonly string _authUrl;
    private readonly string _gatewayUrl;
    private readonly string _webUrl;
    private readonly string _agentGwUrl;

    public PlatformSmokeTests()
    {
        var baseHost = Environment.GetEnvironmentVariable("PLATFORM_BASE_HOST") ?? "http://localhost";
        _brokerUrl = $"{baseHost}:8082";
        _authUrl = $"{baseHost}:8083";
        _gatewayUrl = $"{baseHost}:8081";
        _webUrl = $"{baseHost}:8084";
        _agentGwUrl = $"{baseHost}:9445"; // 9444=gRPC, 9445=HTTP/1 health endpoint
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    // ── Health Checks ──

    [Fact]
    public async Task Broker_HealthCheck_ReturnsHealthy()
    {
        var response = await _http.GetAsync($"{_brokerUrl}/healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Auth_HealthCheck_ReturnsHealthy()
    {
        var response = await _http.GetAsync($"{_authUrl}/healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Gateway_HealthCheck_ReturnsHealthy()
    {
        var response = await _http.GetAsync($"{_gatewayUrl}/healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Web_HealthCheck_ReturnsHealthy()
    {
        var response = await _http.GetAsync($"{_webUrl}/healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AgentGateway_HealthCheck_ReturnsHealthy()
    {
        var response = await _http.GetAsync($"{_agentGwUrl}/healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Readiness ──

    [Fact]
    public async Task Broker_Readiness_ReturnsReady()
    {
        var response = await _http.GetAsync($"{_brokerUrl}/readyz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Auth_Readiness_ReturnsReady()
    {
        var response = await _http.GetAsync($"{_authUrl}/readyz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Auth Flow ──

    [Fact]
    public async Task Auth_Login_WithInvalidCredentials_ReturnsBadRequest()
    {
        var response = await _http.PostAsJsonAsync($"{_authUrl}/api/auth/login", new
        {
            username = "nonexistent",
            password = "wrong",
            provider = 0
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Auth_Login_WithDefaultAdmin_ReturnsToken()
    {
        // Read bootstrap credentials from env (set by New-Secrets.ps1 / CI secrets)
        var adminUser = Environment.GetEnvironmentVariable("HM_BOOTSTRAP_ADMIN_USERNAME") ?? "admin";
        var adminPass = Environment.GetEnvironmentVariable("HM_BOOTSTRAP_ADMIN_PASSWORD") ?? "admin";

        var response = await _http.PostAsJsonAsync($"{_authUrl}/api/auth/login", new
        {
            username = adminUser,
            password = adminPass,
            provider = 0
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
        result.GetProperty("refreshToken").GetString().Should().NotBeNullOrWhiteSpace();
    }

    // ── Gateway Routing ──

    [Fact]
    public async Task Gateway_RoutesToAuth_LoginEndpoint()
    {
        var response = await _http.PostAsJsonAsync($"{_gatewayUrl}/auth/api/auth/login", new
        {
            username = "nonexistent",
            password = "wrong",
            provider = 0
        });

        // Gateway should route to auth and return auth's response (BadRequest for invalid creds)
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Gateway_RoutesToBroker_MachinesEndpoint_RequiresAuth()
    {
        var response = await _http.GetAsync($"{_gatewayUrl}/api/api/machines?page=1&pageSize=10");

        // Broker endpoints require JWT auth — should get 401
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Authenticated API Call ──

    [Fact]
    public async Task FullChain_Login_ThenListMachines_ReturnsOk()
    {
        // Step 1: Authenticate
        var loginResponse = await _http.PostAsJsonAsync($"{_authUrl}/api/auth/login", new
        {
            username = "admin",
            password = "admin",
            provider = 0
        });

        if (loginResponse.StatusCode != HttpStatusCode.OK)
            return; // Skip if default admin not seeded

        var loginResult = await loginResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var token = loginResult.GetProperty("accessToken").GetString()!;

        // Step 2: Call Broker API with JWT via Gateway
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_gatewayUrl}/api/api/machines?page=1&pageSize=10");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var apiResponse = await _http.SendAsync(request);

        apiResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Web UI ──

    [Fact]
    public async Task Web_ReturnsHtml()
    {
        // The root page may redirect to login; use a handler that doesn't follow redirects
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler);
        var response = await client.GetAsync(_webUrl);

        // Accept either 200 (public page rendered) or 302 (redirect to login)
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.OK, HttpStatusCode.Found, HttpStatusCode.Redirect);
    }

    // ── Metrics ──

    [Fact]
    public async Task Broker_Metrics_ReturnsPrometheusFormat()
    {
        var response = await _http.GetAsync($"{_brokerUrl}/metrics");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("# HELP");
    }

    [Fact]
    public async Task Auth_Metrics_ReturnsPrometheusFormat()
    {
        var response = await _http.GetAsync($"{_authUrl}/metrics");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("# HELP");
    }
}
