using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CatalogAPI.UnitTests;

public sealed class GlobalExceptionHandlerTests
{
    [Theory]
    [InlineData(true, StatusCodes.Status400BadRequest)]
    [InlineData(false, StatusCodes.Status500InternalServerError)]
    public async Task TryHandleAsync_MapsKnownAndUnknownFailures(bool badRequest, int expectedStatus)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        Exception exception = badRequest
            ? new BadHttpRequestException("invalid")
            : new InvalidOperationException("failure");
        var sut = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);

        var handled = await sut.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(expectedStatus, context.Response.StatusCode);
    }
}
