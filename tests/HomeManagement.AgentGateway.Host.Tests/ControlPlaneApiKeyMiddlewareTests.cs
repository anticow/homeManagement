using FluentAssertions;
using HomeManagement.AgentGateway.Host.Endpoints;
using HomeManagement.AgentGateway.Host.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace HomeManagement.AgentGateway.Host.Tests;

public sealed class ControlPlaneApiKeyMiddlewareTests
{
    private static readonly string ValidApiKey = Convert.ToBase64String(
        SHA256.HashData(Encoding.UTF8.GetBytes("HomeManagement test-only control-plane key")));

    [Fact]
    public async Task InvokeAsync_ControlPlaneRequestWithoutKey_ReturnsUnauthorized()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext("/internal/agents");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        nextCalled.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_ControlPlaneRequestWithInvalidKey_ReturnsUnauthorized()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext("/internal/agents/agent-01/commands");
        context.Request.Headers[ControlPlaneApiKeyMiddleware.HeaderName] = "invalid-api-key";

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        nextCalled.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_ControlPlaneRequestWithNonCanonicalKey_ReturnsUnauthorized()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext("/internal/agents");
        context.Request.Headers[ControlPlaneApiKeyMiddleware.HeaderName] = CreateNonCanonicalEquivalent(ValidApiKey);

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        nextCalled.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_ControlPlaneRequestWithValidKey_InvokesEndpoint()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext("/internal/agents");
        context.Request.Headers[ControlPlaneApiKeyMiddleware.HeaderName] = ValidApiKey;

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_NonControlPlaneRequest_DoesNotRequireKey()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateContext("/healthz");

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("short-key")]
    [InlineData("CHANGE-ME-control-plane-api-key-000000")]
    [InlineData("changeme-control-plane-api-key-0000000")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8AA==")]
    public void HasValidControlPlaneApiKey_WithUnsafeValue_ReturnsFalse(string apiKey)
    {
        var options = new AgentGatewayHostOptions { ApiKey = apiKey };

        AgentGatewayHostOptions.HasValidControlPlaneApiKey(options).Should().BeFalse();
    }

    [Fact]
    public void HasValidControlPlaneApiKey_WithStrongValue_ReturnsTrue()
    {
        var options = new AgentGatewayHostOptions { ApiKey = ValidApiKey };

        AgentGatewayHostOptions.HasValidControlPlaneApiKey(options).Should().BeTrue();
    }

    [Theory]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh9=")]
    [InlineData(" AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=")]
    public void HasValidControlPlaneApiKey_WithNonCanonicalValue_ReturnsFalse(string apiKey)
    {
        var options = new AgentGatewayHostOptions { ApiKey = apiKey };

        AgentGatewayHostOptions.HasValidControlPlaneApiKey(options).Should().BeFalse();
    }

    private static ControlPlaneApiKeyMiddleware CreateMiddleware(RequestDelegate next)
    {
        return new ControlPlaneApiKeyMiddleware(
            next,
            Options.Create(new AgentGatewayHostOptions { ApiKey = ValidApiKey }),
            NullLogger<ControlPlaneApiKeyMiddleware>.Instance);
    }

    private static DefaultHttpContext CreateContext(string path)
    {
        return new DefaultHttpContext
        {
            Request = { Path = path },
            Response = { Body = new MemoryStream() }
        };
    }

    private static string CreateNonCanonicalEquivalent(string canonicalKey)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var characters = canonicalKey.ToCharArray();
        var canonicalIndex = alphabet.IndexOf(characters[^2]);
        characters[^2] = alphabet[(canonicalIndex & 0x30) | ((canonicalIndex + 1) & 0x0f)];
        return new string(characters);
    }
}
