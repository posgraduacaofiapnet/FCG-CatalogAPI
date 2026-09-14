using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CatalogAPI;
using MassTransit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CatalogAPI.UnitTests;

public sealed class ProgramTests : IClassFixture<CatalogApiFactory>
{
    private readonly CatalogApiFactory _factory;

    public ProgramTests(CatalogApiFactory factory) => _factory = factory;

    [Fact]
    public async Task PublicEndpointsAndMassTransitConfiguration_AreAvailable()
    {
        using var client = _factory.CreateClient();
        _ = _factory.Services.GetRequiredService<IBus>();

        (await client.GetAsync("/health")).EnsureSuccessStatusCode();
        (await client.GetAsync("/api/games?page=1&pageSize=10")).EnsureSuccessStatusCode();
        var missing = await client.GetAsync($"/api/games/{Guid.NewGuid()}");
        var metrics = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        metrics.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task AdminCanCreateUpdateAndDeleteGame()
    {
        using var client = AuthenticatedClient("Admin", Guid.NewGuid(), "admin@example.com");
        var created = await client.PostAsJsonAsync("/api/games", new
        {
            title = "FCG Test Game",
            description = "Integration test",
            price = 49.90m
        });
        var game = await created.Content.ReadFromJsonAsync<GameResponse>();
        var updated = await client.PutAsJsonAsync($"/api/games/{game!.Id}", new
        {
            title = "FCG Updated",
            description = "Updated",
            price = 59.90m
        });
        var deleted = await client.DeleteAsync($"/api/games/{game.Id}");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task UserCanPurchaseListLibraryAndCreateReview()
    {
        var userId = Guid.NewGuid();
        using var admin = AuthenticatedClient("Admin", Guid.NewGuid(), "admin@example.com");
        var created = await admin.PostAsJsonAsync("/api/games", new
        {
            title = "Reviewable Game",
            description = "Integration test",
            price = 10m
        });
        var game = await created.Content.ReadFromJsonAsync<GameResponse>();
        using var user = AuthenticatedClient("User", userId, "user@example.com");

        var purchase = await user.PostAsJsonAsync("/api/library/purchase", new { userId, gameId = game!.Id });
        var library = await user.GetAsync($"/api/library/{userId}");
        var review = await user.PostAsJsonAsync($"/api/games/{game.Id}/reviews", new { rating = 5, comment = "Excelente" });
        var reviews = await user.GetAsync($"/api/games/{game.Id}/reviews");

        Assert.Equal(HttpStatusCode.Accepted, purchase.StatusCode);
        Assert.Equal(HttpStatusCode.OK, library.StatusCode);
        Assert.Equal(HttpStatusCode.Created, review.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reviews.StatusCode);
    }

    [Fact]
    public async Task InvalidAndCrossUserRequests_AreRejected()
    {
        var userId = Guid.NewGuid();
        using var user = AuthenticatedClient("User", userId, "user@example.com");

        var invalidReview = await user.PostAsJsonAsync($"/api/games/{Guid.NewGuid()}/reviews", new { rating = 8, comment = "" });
        var forbiddenLibrary = await user.GetAsync($"/api/library/{Guid.NewGuid()}");
        var forbiddenPurchase = await user.PostAsJsonAsync("/api/library/purchase", new
        {
            userId = Guid.NewGuid(),
            gameId = Guid.NewGuid()
        });

        Assert.Equal(HttpStatusCode.BadRequest, invalidReview.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenLibrary.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenPurchase.StatusCode);
    }

    [Fact]
    public async Task AdminCanReadLambdaProcessingStatus()
    {
        using var admin = AuthenticatedClient("Admin", Guid.NewGuid(), "admin@example.com");

        var response = await admin.GetAsync("/api/notifications/status?limit=20");
        var statuses = await response.Content.ReadFromJsonAsync<List<NotificationProcessingStatusResponse>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(statuses);
        Assert.Single(statuses);
        Assert.True(statuses[0].LambdaProcessed);
    }

    [Fact]
    public async Task NotificationStatus_RejectsNonAdminAndReturnsNotFoundForUnknownEvent()
    {
        using var user = AuthenticatedClient("User", Guid.NewGuid(), "user@example.com");
        using var admin = AuthenticatedClient("Admin", Guid.NewGuid(), "admin@example.com");

        var forbidden = await user.GetAsync("/api/notifications/status");
        var missing = await admin.GetAsync($"/api/notifications/status/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private HttpClient AuthenticatedClient(string role, Guid userId, string email)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        client.DefaultRequestHeaders.Add("X-Test-UserId", userId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Email", email);
        return client;
    }
}

public sealed class CatalogApiFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "integration-test-key-with-at-least-thirty-two-characters";
    private readonly string _databaseName = $"catalog-api-tests-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Server=unused",
                ["ConnectionStrings:MongoDB"] = "mongodb://127.0.0.1:27017",
                ["ConnectionStrings:Redis"] = "",
                ["Jwt:Key"] = JwtKey,
                ["Jwt:Issuer"] = "UsersAPI",
                ["Jwt:Audience"] = "FCG"
            }));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<CatalogDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<IDbContextOptionsConfiguration<CatalogDbContext>>();
            services.RemoveAll<IDatabaseProvider>();
            services.RemoveAll<CatalogDbContext>();
            services.AddDbContext<CatalogDbContext>(options => options.UseInMemoryDatabase(_databaseName));
            services.RemoveAll<IGameReviewStore>();
            services.AddSingleton<IGameReviewStore, FakeGameReviewStore>();
            services.RemoveAll<ICatalogEventPublisher>();
            services.AddScoped<ICatalogEventPublisher, FakeCatalogEventPublisher>();
            services.RemoveAll<INotificationProcessingStatusReader>();
            services.AddSingleton<INotificationProcessingStatusReader, FakeNotificationProcessingStatusReader>();
            services.RemoveAll<IHostedService>();
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthenticationHandler.AuthenticationSchemeName;
                options.DefaultChallengeScheme = TestAuthenticationHandler.AuthenticationSchemeName;
                options.DefaultForbidScheme = TestAuthenticationHandler.AuthenticationSchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                TestAuthenticationHandler.AuthenticationSchemeName,
                _ => { });
        });
    }
}

public sealed class FakeNotificationProcessingStatusReader : INotificationProcessingStatusReader
{
    private static readonly NotificationProcessingStatusResponse CompletedStatus = new(
        Guid.Parse("19938a4c-e93b-44bc-8027-0bf1cc9808d1"),
        NotificationEventTypes.PaymentProcessed,
        DateTimeOffset.Parse("2026-09-14T12:00:00Z"),
        true,
        1,
        null,
        true,
        "Completed",
        DateTimeOffset.Parse("2026-09-21T12:00:00Z"));

    public Task<IReadOnlyList<NotificationProcessingStatusResponse>> GetRecentAsync(
        int? limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NotificationProcessingStatusResponse>>([CompletedStatus]);

    public Task<NotificationProcessingStatusResponse?> GetByIdAsync(
        Guid eventId,
        CancellationToken cancellationToken) =>
        Task.FromResult<NotificationProcessingStatusResponse?>(
            eventId == CompletedStatus.Id ? CompletedStatus : null);
}

public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string AuthenticationSchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-UserId", out var userId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new[]
        {
            new Claim("user_id", userId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Email, Request.Headers["X-Test-Email"].ToString()),
            new Claim(ClaimTypes.Role, Request.Headers["X-Test-Role"].ToString())
        };
        var identity = new ClaimsIdentity(claims, AuthenticationSchemeName, ClaimTypes.Name, ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), AuthenticationSchemeName)));
    }
}
