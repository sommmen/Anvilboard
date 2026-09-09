using Anvilboard.Application.Automation;

namespace Anvilboard.Application.Tests.Automation;

public sealed class CorrelationContextTests
{
    [Fact]
    public void FromHeaderOrNew_ClientSupplied_UsesSuppliedValue()
    {
        var context = CorrelationContext.FromHeaderOrNew("client-correlation-id");

        Assert.Equal("client-correlation-id", context.CorrelationId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromHeaderOrNew_MissingOrBlank_GeneratesNewGuid(string? clientSupplied)
    {
        var context = CorrelationContext.FromHeaderOrNew(clientSupplied);

        Assert.True(Guid.TryParse(context.CorrelationId, out _));
    }

    [Fact]
    public void FromHeaderOrNew_CalledTwiceWithNoInput_GeneratesDistinctValues()
    {
        var first = CorrelationContext.FromHeaderOrNew(null);
        var second = CorrelationContext.FromHeaderOrNew(null);

        Assert.NotEqual(first.CorrelationId, second.CorrelationId);
    }
}
