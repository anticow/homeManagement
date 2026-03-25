using FluentAssertions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HomeManagement.Core.Tests;

public class SystemHealthServiceTests
{
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();
    private readonly IResiliencePipeline _resilience = Substitute.For<IResiliencePipeline>();

    [Fact]
    public async Task CheckAsync_WhenDatabaseUnavailable_ReturnsUnhealthy()
    {
        // Arrange — scope factory throws when getting DbContext
        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(HomeManagementDbContext)).Returns(_ => throw new InvalidOperationException("No DB"));
        scope.ServiceProvider.Returns(provider);
        _scopeFactory.CreateScope().Returns(scope);
        _resilience.GetCircuitState("*").Returns(CircuitState.Closed);

        var sut = new SystemHealthService(_scopeFactory, _resilience, NullLogger<SystemHealthService>.Instance);

        var report = await sut.CheckAsync();

        report.OverallStatus.Should().Be(HealthStatus.Unhealthy);
        report.Components.Should().Contain(c => c.ComponentName == "Database" && c.Status == HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task CheckAsync_WhenCircuitOpen_ReturnsUnhealthy()
    {
        // Arrange — DB ok, circuit open
        var dbOptions = new DbContextOptionsBuilder<HomeManagementDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var db = new HomeManagementDbContext(dbOptions);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();

        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(HomeManagementDbContext)).Returns(db);
        scope.ServiceProvider.Returns(provider);
        _scopeFactory.CreateScope().Returns(scope);
        _resilience.GetCircuitState("*").Returns(CircuitState.Open);

        var sut = new SystemHealthService(_scopeFactory, _resilience, NullLogger<SystemHealthService>.Instance);

        var report = await sut.CheckAsync();

        report.Components.Should().Contain(c => c.ComponentName == "Transport" && c.Status == HealthStatus.Unhealthy);

        await db.Database.CloseConnectionAsync();
        await db.DisposeAsync();
    }

    [Fact]
    public async Task CheckAsync_WhenCircuitHalfOpen_ReturnsDegraded()
    {
        var dbOptions = new DbContextOptionsBuilder<HomeManagementDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var db = new HomeManagementDbContext(dbOptions);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();

        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(HomeManagementDbContext)).Returns(db);
        scope.ServiceProvider.Returns(provider);
        _scopeFactory.CreateScope().Returns(scope);
        _resilience.GetCircuitState("*").Returns(CircuitState.HalfOpen);

        var sut = new SystemHealthService(_scopeFactory, _resilience, NullLogger<SystemHealthService>.Instance);

        var report = await sut.CheckAsync();

        report.OverallStatus.Should().Be(HealthStatus.Degraded);
        report.Components.Should().Contain(c => c.ComponentName == "Transport" && c.Status == HealthStatus.Degraded);

        await db.Database.CloseConnectionAsync();
        await db.DisposeAsync();
    }

    [Fact]
    public async Task CheckAsync_AllHealthy_ReturnsHealthy()
    {
        var dbOptions = new DbContextOptionsBuilder<HomeManagementDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var db = new HomeManagementDbContext(dbOptions);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();

        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(HomeManagementDbContext)).Returns(db);
        scope.ServiceProvider.Returns(provider);
        _scopeFactory.CreateScope().Returns(scope);
        _resilience.GetCircuitState("*").Returns(CircuitState.Closed);

        var sut = new SystemHealthService(_scopeFactory, _resilience, NullLogger<SystemHealthService>.Instance);

        var report = await sut.CheckAsync();

        report.OverallStatus.Should().Be(HealthStatus.Healthy);
        report.Components.Should().HaveCount(2);
        report.CheckedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

        await db.Database.CloseConnectionAsync();
        await db.DisposeAsync();
    }
}
