using FluentAssertions;
using HomeManagement.Abstractions.CrossCutting;

namespace HomeManagement.Abstractions.Tests.CrossCutting;

public sealed class CorrelationContextTests
{
    [Fact]
    public void CorrelationId_WithNoScope_ReturnsNewGuidEachCall()
    {
        var ctx = new CorrelationContext();
        // Without a scope set, each access generates a fresh ID
        var id1 = ctx.CorrelationId;
        var id2 = ctx.CorrelationId;
        id1.Should().NotBe(id2);
    }

    [Fact]
    public void BeginScope_SetsCorrelationId()
    {
        var ctx = new CorrelationContext();
        using (ctx.BeginScope("test-correlation-id"))
        {
            ctx.CorrelationId.Should().Be("test-correlation-id");
        }
    }

    [Fact]
    public void BeginScope_AutoGeneratesIdWhenNull()
    {
        var ctx = new CorrelationContext();
        using (ctx.BeginScope())
        {
            var id = ctx.CorrelationId;
            id.Should().NotBeNullOrEmpty();
            // Should remain stable within the scope
            ctx.CorrelationId.Should().Be(id);
        }
    }

    [Fact]
    public void Dispose_RestoresPreviousScope()
    {
        var ctx = new CorrelationContext();
        using (ctx.BeginScope("outer"))
        {
            ctx.CorrelationId.Should().Be("outer");

            using (ctx.BeginScope("inner"))
            {
                ctx.CorrelationId.Should().Be("inner");
            }

            ctx.CorrelationId.Should().Be("outer");
        }
    }

    [Fact]
    public async Task Scope_FlowsAcrossAsyncCalls()
    {
        var ctx = new CorrelationContext();
        using (ctx.BeginScope("async-test"))
        {
            await Task.Yield();
            ctx.CorrelationId.Should().Be("async-test");

            await Task.Run(() =>
            {
                ctx.CorrelationId.Should().Be("async-test");
            });
        }
    }
}
