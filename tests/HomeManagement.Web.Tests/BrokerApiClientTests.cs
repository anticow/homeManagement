using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Web.Services;
using NSubstitute;

namespace HomeManagement.Web.Tests;

public sealed class BrokerApiClientTests
{
    [Fact]
    public async Task GetMachinesAsync_AuthenticatedRequest_ForwardsBearerToken()
    {
        var session = new ServerSessionState();
        var initialToken = CreateToken("alice", ["Viewer"]);
        session.SetSession(initialToken, "refresh-a");

        var handler = new QueueHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new PagedResult<Machine>([], 0, 1, 25))
        });

        var httpClientFactory = CreateFactory(handler);
        var authService = Substitute.For<IWebSessionAuthService>();
        var client = new BrokerApiClient(httpClientFactory, session, authService);

        var result = await client.GetMachinesAsync();

        result.Items.Should().BeEmpty();
        handler.Requests.Should().ContainSingle();
        var authorization = handler.Requests[0].Headers.Authorization;
        authorization.Should().NotBeNull();
        authorization!.Scheme.Should().Be("Bearer");
        authorization.Parameter.Should().Be(initialToken);
    }

    [Fact]
    public async Task GetMachinesAsync_Unauthorized_RefreshesAndRetries()
    {
        var session = new ServerSessionState();
        session.SetSession(CreateToken("alice", ["Viewer"], expiresUtc: DateTime.UtcNow.AddMinutes(-5)), "refresh-a");

        var refreshedToken = CreateToken("alice", ["Viewer"]);
        var handler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PagedResult<Machine>([], 0, 1, 25))
            });

        var httpClientFactory = CreateFactory(handler);
        var authService = Substitute.For<IWebSessionAuthService>();
        authService.RefreshAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                session.SetSession(refreshedToken, "refresh-b");
                return Task.FromResult(true);
            });

        var client = new BrokerApiClient(httpClientFactory, session, authService);
        var result = await client.GetMachinesAsync();

        result.Items.Should().BeEmpty();
        await authService.Received(1).RefreshAsync(Arg.Any<CancellationToken>());
        var authorization = handler.Requests[0].Headers.Authorization;
        authorization.Should().NotBeNull();
        authorization!.Parameter.Should().Be(refreshedToken);
    }

    [Fact]
    public async Task GetMachinesAsync_RefreshFails_ClearsSessionAndThrows()
    {
        var session = new ServerSessionState();
        session.SetSession(CreateToken("alice", ["Viewer"], expiresUtc: DateTime.UtcNow.AddMinutes(-5)), "refresh-a");

        var handler = new QueueHttpMessageHandler();
        var httpClientFactory = CreateFactory(handler);
        var authService = Substitute.For<IWebSessionAuthService>();
        authService.RefreshAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));

        var client = new BrokerApiClient(httpClientFactory, session, authService);

        var act = () => client.GetMachinesAsync();

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        session.IsAuthenticated.Should().BeFalse();
    }

    private static IHttpClientFactory CreateFactory(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(BrokerApiClient.HttpClientName)
            .Returns(new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:8082")
            });
        return factory;
    }

    private static string CreateToken(string username, IReadOnlyList<string> roles, DateTime? expiresUtc = null)
    {
        var claims = new List<System.Security.Claims.Claim>
        {
            new(System.Security.Claims.ClaimTypes.Name, username)
        };

        claims.AddRange(roles.Select(role => new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role)));

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            claims: claims,
            expires: expiresUtc ?? DateTime.UtcNow.AddMinutes(15));

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public QueueHttpMessageHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No queued HTTP responses were configured.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }
}