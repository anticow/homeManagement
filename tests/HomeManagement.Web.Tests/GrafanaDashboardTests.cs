using System.Net;
using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;
using HomeManagement.Web.Components.Shared;
using HomeManagement.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Web.Tests;

public sealed class GrafanaDashboardTests
{
    [Theory]
    [InlineData(OsType.Windows, "9182")]
    [InlineData(OsType.Linux, "9100")]
    public void Create_ScopesEveryGraphToExactFqdnAndSharedTimeRange(OsType osType, string port)
    {
        var options = new GrafanaOptions { BaseUrl = "https://grafana.example.test/grafana/" };
        var links = GrafanaDashboardLinks.Create(options, CreateMachine(osType), "now-24h");

        links.Instance.Should().Be($"node.example.test:{port}");
        links.Panels.Should().HaveCount(4);
        foreach (var url in links.Panels.Select(panel => panel.Url).Append(links.DashboardUrl))
        {
            var uri = new Uri(url);
            uri.AbsolutePath.Should().StartWith("/grafana/d");
            var query = QueryHelpers.ParseQuery(uri.Query);
            query["var-instance"].ToString().Should().Be(links.Instance);
            query["var-job"].ToString().Should().Be("homemanagement");
            query["from"].ToString().Should().Be("now-24h");
            query["to"].ToString().Should().Be("now");
            query["refresh"].ToString().Should().Be("30s");
            query.Should().NotContainKey("auth_token");
        }
        links.Panels.Select(panel => QueryHelpers.ParseQuery(new Uri(panel.Url).Query)["panelId"].ToString())
            .Should().Equal("1", "2", "3", "4");
    }

    [Fact]
    public void Create_InstanceOverrideIsEncodedAndCannotInjectAdditionalVariables()
    {
        var machine = CreateMachine(OsType.Windows) with
        {
            Tags = new Dictionary<string, string> { ["prometheus:instance"] = "[::1]:19182&var-job=other" }
        };
        var links = GrafanaDashboardLinks.Create(
            new GrafanaOptions { BaseUrl = "https://grafana.example.test" }, machine, "now-1h");

        var query = QueryHelpers.ParseQuery(new Uri(links.DashboardUrl).Query);
        query["var-instance"].ToString().Should().Be("[::1]:19182&var-job=other");
        query["var-job"].ToString().Should().Be("homemanagement");
        query["var-job"].Count.Should().Be(1);
    }

    [Fact]
    public void Create_MissingFqdnUsesHostnameAndHonorsCustomTemplate()
    {
        var options = new GrafanaOptions
        {
            BaseUrl = "https://grafana.example.test",
            InstanceLabelTemplate = "{FQDN}:19100",
            ScrapeJob = "custom-job"
        };
        var machine = CreateMachine(OsType.Linux) with { Fqdn = null };

        var links = GrafanaDashboardLinks.Create(options, machine, "now-7d");

        links.Instance.Should().Be("node:19100");
        QueryHelpers.ParseQuery(new Uri(links.DashboardUrl).Query)["var-job"].ToString().Should().Be("custom-job");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://viewer:password@grafana.example.test")]
    [InlineData("https://grafana.example.test?auth_token=secret")]
    [InlineData("https://grafana.example.test/#fragment")]
    public void Options_RejectUnsafeBaseUrls(string baseUrl)
    {
        var options = new GrafanaOptions { BaseUrl = baseUrl };

        options.IsValid.Should().BeFalse();
        var action = () => GrafanaDashboardLinks.Create(options, CreateMachine(OsType.Linux), "now-3h");
        action.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("now-1y")]
    public void Create_RejectsUnsupportedTimeRange(string from)
    {
        var action = () => GrafanaDashboardLinks.Create(
            new GrafanaOptions { BaseUrl = "https://grafana.example.test" }, CreateMachine(OsType.Linux), from);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("", "Grafana graphs are not configured")]
    [InlineData("https://grafana.example.test", "Load graphs")]
    public async Task Component_ExplainsSetupAndDoesNotEagerlyLoadGrafana(string baseUrl, string expectedText)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<GrafanaOptions>(options => options.BaseUrl = baseUrl);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<NodeMetricsPanels>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(NodeMetricsPanels.Machine)] = CreateMachine(OsType.Linux)
                }));
            return component.ToHtmlString();
        });

        html.Should().Contain(expectedText).And.NotContain("<iframe").And.NotContain("auth_token");
        if (baseUrl.Length > 0)
            html.Should().Contain("node.example.test:9100").And.Contain("No data means");
    }

    private static Machine CreateMachine(OsType osType) => new(
        Guid.NewGuid(), Hostname.Create("node"), "node.example.test", [IPAddress.Loopback], osType,
        "test", MachineConnectionMode.Agent, TransportProtocol.Agent, 0, Guid.Empty,
        MachineState.Online, new Dictionary<string, string>(), null, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow);
}
