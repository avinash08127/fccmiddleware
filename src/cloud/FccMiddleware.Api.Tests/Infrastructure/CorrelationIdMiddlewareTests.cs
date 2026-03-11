using FccMiddleware.Api.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace FccMiddleware.Api.Tests.Infrastructure;

public sealed class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task Invoke_UsesIncomingHeaderAndEchoesItOnResponse()
    {
        var correlationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = correlationId.ToString();

        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);

        await middleware.Invoke(context);

        CorrelationIdMiddleware.GetCorrelationId(context).Should().Be(correlationId);
        context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString().Should().Be(correlationId.ToString());
        context.TraceIdentifier.Should().Be(correlationId.ToString());
    }

    [Fact]
    public async Task Invoke_GeneratesCorrelationIdWhenHeaderMissing()
    {
        var context = new DefaultHttpContext();
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);

        await middleware.Invoke(context);

        var correlationId = CorrelationIdMiddleware.GetCorrelationId(context);
        correlationId.Should().NotBe(Guid.Empty);
        context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString().Should().Be(correlationId.ToString());
    }
}
