using HomeManagement.Integration.Prometheus;
using Xunit;

namespace HomeManagement.Integration.Tests;

public class PromQLTests
{
    private const string TestHostname = "test-host";
    private const string TestScrapeLabel = "homemanagement";

    [Fact]
    public void LinuxCpuUsagePercent_GeneratesValidQuery()
    {
        var query = PromQL.LinuxCpuUsagePercent(TestHostname, TestScrapeLabel);
        Assert.Contains("mode=\"idle\"", query);
        Assert.Contains("rate(", query);
        Assert.Contains("100 -", query);
        Assert.DoesNotContain("null", query);
    }

    [Fact]
    public void WindowsCpuUsagePercent_GeneratesValidQuery()
    {
        var query = PromQL.WindowsCpuUsagePercent(TestHostname, TestScrapeLabel);
        Assert.Contains("windows_cpu_time_total", query);
        Assert.Contains("mode=\"idle\"", query);
        Assert.Contains("100 -", query);
    }

    [Fact]
    public void MacOsCpuUsagePercent_MatchesLinuxQuery()
    {
        var linux = PromQL.LinuxCpuUsagePercent(TestHostname, TestScrapeLabel);
        var macos = PromQL.MacOsCpuUsagePercent(TestHostname, TestScrapeLabel);
        Assert.Equal(linux, macos);
    }

    [Fact]
    public void LinuxMemoryUsagePercent_GeneratesValidQuery()
    {
        var query = PromQL.LinuxMemoryUsagePercent(TestHostname, TestScrapeLabel);
        Assert.Contains("node_memory_MemAvailable_bytes", query);
        Assert.Contains("node_memory_MemTotal_bytes", query);
        Assert.Contains("* 100", query);
        Assert.Contains("(1 -", query);
    }

    [Fact]
    public void WindowsMemoryUsagePercent_GeneratesValidQuery()
    {
        var query = PromQL.WindowsMemoryUsagePercent(TestHostname, TestScrapeLabel);
        Assert.Contains("windows_memory_available_bytes", query);
        Assert.Contains("windows_cs_physical_memory_bytes", query);
        Assert.Contains("* 100", query);
    }

    [Fact]
    public void MacOsMemoryUsagePercent_MatchesLinuxQuery()
    {
        var linux = PromQL.LinuxMemoryUsagePercent(TestHostname, TestScrapeLabel);
        var macos = PromQL.MacOsMemoryUsagePercent(TestHostname, TestScrapeLabel);
        Assert.Equal(linux, macos);
    }

    [Fact]
    public void LinuxDiskUsagePercent_GeneratesValidQuery()
    {
        var query = PromQL.LinuxDiskUsagePercent(TestHostname, TestScrapeLabel);
        Assert.Contains("node_filesystem_avail_bytes", query);
        Assert.Contains("node_filesystem_size_bytes", query);
        Assert.Contains("mountpoint=\"/\"", query);
        Assert.Contains("* 100", query);
    }

    [Fact]
    public void WindowsDiskUsagePercent_GeneratesValidQuery()
    {
        var query = PromQL.WindowsDiskUsagePercent(TestHostname, TestScrapeLabel);
        Assert.Contains("windows_logical_disk_free_bytes", query);
        Assert.Contains("windows_logical_disk_size_bytes", query);
        Assert.Contains("volume=\"C:\"", query);
        Assert.Contains("* 100", query);
    }

    [Fact]
    public void MacOsDiskUsagePercent_MatchesLinuxQuery()
    {
        var linux = PromQL.LinuxDiskUsagePercent(TestHostname, TestScrapeLabel);
        var macos = PromQL.MacOsDiskUsagePercent(TestHostname, TestScrapeLabel);
        Assert.Equal(linux, macos);
    }

    [Fact]
    public void EndpointUp_GeneratesValidQuery()
    {
        var query = PromQL.EndpointUp(TestHostname, TestScrapeLabel);
        Assert.Contains("up{", query);
        Assert.Contains("job=\"homemanagement\"", query);
        Assert.DoesNotContain("null", query);
    }

    [Fact]
    public void HostnameWithSpecialCharacters_EscapesCorrectly()
    {
        var hostnameWithQuotes = "host\"with\"quotes";
        var query = PromQL.EndpointUp(hostnameWithQuotes, TestScrapeLabel);
        // Should escape quotes for PromQL
        Assert.Contains("\\\"", query);
    }

    [Fact]
    public void AllHealthMetrics_ReturnNonEmptyStrings()
    {
        var cpuLinux = PromQL.LinuxCpuUsagePercent(TestHostname, TestScrapeLabel);
        var cpuWindows = PromQL.WindowsCpuUsagePercent(TestHostname, TestScrapeLabel);
        var cpuMacos = PromQL.MacOsCpuUsagePercent(TestHostname, TestScrapeLabel);

        var memLinux = PromQL.LinuxMemoryUsagePercent(TestHostname, TestScrapeLabel);
        var memWindows = PromQL.WindowsMemoryUsagePercent(TestHostname, TestScrapeLabel);
        var memMacos = PromQL.MacOsMemoryUsagePercent(TestHostname, TestScrapeLabel);

        var diskLinux = PromQL.LinuxDiskUsagePercent(TestHostname, TestScrapeLabel);
        var diskWindows = PromQL.WindowsDiskUsagePercent(TestHostname, TestScrapeLabel);
        var diskMacos = PromQL.MacOsDiskUsagePercent(TestHostname, TestScrapeLabel);

        var up = PromQL.EndpointUp(TestHostname, TestScrapeLabel);

        Assert.NotEmpty(cpuLinux);
        Assert.NotEmpty(cpuWindows);
        Assert.NotEmpty(cpuMacos);
        Assert.NotEmpty(memLinux);
        Assert.NotEmpty(memWindows);
        Assert.NotEmpty(memMacos);
        Assert.NotEmpty(diskLinux);
        Assert.NotEmpty(diskWindows);
        Assert.NotEmpty(diskMacos);
        Assert.NotEmpty(up);
    }
}
