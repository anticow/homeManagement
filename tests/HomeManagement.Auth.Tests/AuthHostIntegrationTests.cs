using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HomeManagement.Auth.Tests;

public sealed class AuthHostIntegrationTests : IClassFixture<AuthHostWebApplicationFactory>
{
    private readonly AuthHostWebApplicationFactory _factory;

    public AuthHostIntegrationTests(AuthHostWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Login_WithBootstrapAdmin_ReturnsTokens()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "admin",
            password = "HomeManagement_TestAdmin1!",
            provider = 0
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<AuthResult>();
        payload!.Success.Should().BeTrue();
        payload.AccessToken.Should().NotBeNullOrEmpty();
        payload.RefreshToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AdminUsers_RequiresAdminBearerToken()
    {
        using var anonymousClient = _factory.CreateClient();
        var anonymousResponse = await anonymousClient.GetAsync("/api/admin/users");
        anonymousResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "admin",
            password = "HomeManagement_TestAdmin1!",
            provider = 0
        });

        var loginPayload = await loginResponse.Content.ReadFromJsonAsync<AuthResult>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginPayload!.AccessToken);

        var response = await client.GetAsync("/api/admin/users");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

public sealed class AuthHostWebApplicationFactory : WebApplicationFactory<Program>, IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public AuthHostWebApplicationFactory()
    {
        _connection.Open();
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:HomeManagement"] = "Data Source=:memory:",
                ["Auth:JwtSigningKey"] = "integration-test-signing-key-that-is-long-enough-for-hmac-sha256!",
                ["Auth:Issuer"] = "test-issuer",
                ["Auth:Audience"] = "test-audience",
                ["Auth:BootstrapAdmin:Enabled"] = "true",
                ["Auth:BootstrapAdmin:Username"] = "admin",
                ["Auth:BootstrapAdmin:Password"] = "HomeManagement_TestAdmin1!",
                ["Auth:BootstrapAdmin:DisplayName"] = "Integration Admin",
                ["Auth:BootstrapAdmin:Email"] = "admin@test.local"
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<HomeManagementDbContext>>();
            services.RemoveAll<HomeManagementDbContext>();

            services.AddDbContext<HomeManagementDbContext>(options =>
                options.UseSqlite(_connection));
        });
    }

    public new void Dispose()
    {
        base.Dispose();
        _connection.Dispose();
    }
}
