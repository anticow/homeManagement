using System.Net;
using FluentAssertions;
using HomeManagement.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace HomeManagement.Web.Tests;

public sealed class WebStaticAssetsIntegrationTests
{
    [Fact]
    public async Task BlazorFrameworkScript_IsServed()
    {
        await using var factory = new WebStaticAssetsApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/_framework/blazor.web.js");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/javascript");
    }

    private sealed class WebStaticAssetsApplicationFactory : WebApplicationFactory<WebSessionAuthService>
    {
        public WebStaticAssetsApplicationFactory()
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ReloadStaticAssetsAtRuntime"] = "false"
                }));
        }
    }
}
