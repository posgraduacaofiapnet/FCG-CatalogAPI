using Bogus;
using CatalogAPI;
using FCG.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CatalogAPI.UnitTests;

public sealed class CatalogFixture
{
    public Faker Faker { get; } = new("pt_BR");

    public CatalogDbContext CreateDbContext() => new(new DbContextOptionsBuilder<CatalogDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    public CreateGameRequest CreateGame() => new(
        Faker.Commerce.ProductName(),
        Faker.Commerce.ProductDescription(),
        decimal.Parse(Faker.Commerce.Price(10, 300)));
}

public sealed class FakeCatalogEventPublisher : ICatalogEventPublisher
{
    public OrderPlacedEvent? Published { get; private set; }
    public Exception? Exception { get; init; }

    public Task PublishOrderPlacedAsync(OrderPlacedEvent message, CancellationToken cancellationToken)
    {
        Published = message;
        var exception = Exception;
        return exception is null ? Task.CompletedTask : Task.FromException(exception);
    }
}

public sealed class CatalogServiceTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    [Fact]
    public async Task CreateAndGetGame_PersistsActiveGame()
    {
        await using var db = fixture.CreateDbContext();
        var service = new CatalogService(db, new FakeCatalogEventPublisher());
        var request = fixture.CreateGame();

        var created = await service.CreateGameAsync(request, CancellationToken.None);
        var result = await service.GetGameAsync(created.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(request.Title, result.Title);
        Assert.Equal(request.Price, result.Price);
    }

    [Fact]
    public async Task PurchaseAsync_CreatesPendingOrderOutboxAndPaymentEvent()
    {
        await using var db = fixture.CreateDbContext();
        var publisher = new FakeCatalogEventPublisher();
        var service = new CatalogService(db, publisher);
        var game = await service.CreateGameAsync(fixture.CreateGame(), CancellationToken.None);
        var userId = Guid.NewGuid();

        await service.PurchaseAsync(new PurchaseGameRequest(userId, game.Id), "user@example.com", CancellationToken.None);

        var order = Assert.Single(await db.Orders.ToListAsync());
        Assert.Equal("Pending", order.Status);
        Assert.Equal(order.Id, publisher.Published?.OrderId);
        Assert.Equal(userId, publisher.Published?.UserId);
        var outbox = Assert.Single(await db.OutboxMessages.ToListAsync());
        Assert.Equal(NotificationEventTypes.OrderPlaced, outbox.EventType);
        Assert.Contains(order.Id.ToString(), outbox.Payload);
        Assert.Equal(0, outbox.Attempts);
    }

    [Fact]
    public async Task ApprovedPayment_AddsGameToLibraryOnlyOnce()
    {
        await using var db = fixture.CreateDbContext();
        var service = new CatalogService(db, new FakeCatalogEventPublisher());
        var game = await service.CreateGameAsync(fixture.CreateGame(), CancellationToken.None);
        var userId = Guid.NewGuid();
        await service.PurchaseAsync(new PurchaseGameRequest(userId, game.Id), "user@example.com", CancellationToken.None);
        var order = Assert.Single(await db.Orders.ToListAsync());
        var payment = new PaymentProcessedEvent(order.Id, userId, game.Id, game.Title, game.Price,
            PaymentStatuses.Approved, DateTime.UtcNow);

        await service.ProcessPaymentAsync(payment, CancellationToken.None);
        await service.ProcessPaymentAsync(payment, CancellationToken.None);

        Assert.Single(await db.LibraryItems.ToListAsync());
        Assert.Equal(PaymentStatuses.Approved, order.Status);
        var outbox = await db.OutboxMessages.ToListAsync();
        Assert.Equal(2, outbox.Count);
        Assert.Single(outbox, message => message.EventType == NotificationEventTypes.PaymentProcessed);
    }

    [Fact]
    public async Task PurchaseAsync_WhenBusOutboxRejectsMessage_DoesNotPersistOrderOrNotification()
    {
        await using var db = fixture.CreateDbContext();
        var publisher = new FakeCatalogEventPublisher
        {
            Exception = new InvalidOperationException("Bus outbox unavailable")
        };
        var service = new CatalogService(db, publisher);
        var game = await service.CreateGameAsync(fixture.CreateGame(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PurchaseAsync(
            new PurchaseGameRequest(Guid.NewGuid(), game.Id),
            "user@example.com",
            CancellationToken.None));

        Assert.Empty(await db.Orders.ToListAsync());
        Assert.Empty(await db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task CreateGameValidator_RejectsNonPositivePrice()
    {
        var result = await new CreateGameRequestValidator().ValidateAsync(
            new CreateGameRequest(fixture.Faker.Commerce.ProductName(), "description", 0));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(CreateGameRequest.Price));
    }

    [Fact]
    public void CorrelationId_PreservesValidValue() =>
        Assert.Equal("catalog-flow", CorrelationId.Normalize("catalog-flow"));

    [Fact]
    public async Task GetGamesAsync_UsesCacheOnSecondCall()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new FakeGameCatalogCache();
        var service = new CatalogService(db, new FakeCatalogEventPublisher(), gameListCache: cache);
        await service.CreateGameAsync(fixture.CreateGame(), CancellationToken.None);
        var pagination = PaginationParameters.From(1, 10);

        var first = await service.GetGamesAsync(pagination, CancellationToken.None);
        db.Games.RemoveRange(db.Games);
        await db.SaveChangesAsync();
        var second = await service.GetGamesAsync(pagination, CancellationToken.None);

        Assert.Equal(1, cache.Misses);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(first.TotalCount, second.TotalCount);
        Assert.Equal(first.Items[0].Id, second.Items[0].Id);
    }

    [Fact]
    public async Task CreateGameAsync_InvalidatesCachedList()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new FakeGameCatalogCache();
        var service = new CatalogService(db, new FakeCatalogEventPublisher(), gameListCache: cache);
        await service.CreateGameAsync(fixture.CreateGame(), CancellationToken.None);
        var pagination = PaginationParameters.From(1, 10);

        await service.GetGamesAsync(pagination, CancellationToken.None);
        var created = await service.CreateGameAsync(fixture.CreateGame(), CancellationToken.None);
        var listed = await service.GetGamesAsync(pagination, CancellationToken.None);

        Assert.True(cache.Invalidations >= 2);
        Assert.Equal(2, listed.TotalCount);
        Assert.Contains(listed.Items, game => game.Id == created.Id);
    }

    [Fact]
    public async Task DistributedCache_InvalidateChangesVersionAndMisses()
    {
        var memory = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var cache = new DistributedGameCatalogCache(memory, NullLogger<DistributedGameCatalogCache>.Instance);
        var pagination = PaginationParameters.From(1, 10);
        var page = new PagedResult<GameResponse>(
            [new GameResponse(Guid.NewGuid(), "Cyber FIAP", "Demo", 99.9m)],
            1,
            10,
            1);

        await cache.SetListAsync(pagination, page, CancellationToken.None);
        var hit = await cache.TryGetListAsync(pagination, CancellationToken.None);
        await cache.InvalidateListAsync(CancellationToken.None);
        var afterInvalidate = await cache.TryGetListAsync(pagination, CancellationToken.None);

        Assert.NotNull(hit);
        Assert.Equal("Cyber FIAP", hit.Items[0].Title);
        Assert.Null(afterInvalidate);
    }
}

public sealed class FakeGameCatalogCache : IGameCatalogCache
{
    private readonly Dictionary<string, PagedResult<GameResponse>> _store = [];
    private int _version = 1;

    public int Hits { get; private set; }
    public int Misses { get; private set; }
    public int Invalidations { get; private set; }

    public Task<PagedResult<GameResponse>?> TryGetListAsync(PaginationParameters pagination, CancellationToken cancellationToken)
    {
        if (_store.TryGetValue(Key(pagination), out var value))
        {
            Hits++;
            return Task.FromResult<PagedResult<GameResponse>?>(value);
        }

        Misses++;
        return Task.FromResult<PagedResult<GameResponse>?>(null);
    }

    public Task SetListAsync(PaginationParameters pagination, PagedResult<GameResponse> value, CancellationToken cancellationToken)
    {
        _store[Key(pagination)] = value;
        return Task.CompletedTask;
    }

    public Task InvalidateListAsync(CancellationToken cancellationToken)
    {
        Invalidations++;
        _version++;
        _store.Clear();
        return Task.CompletedTask;
    }

    private string Key(PaginationParameters pagination) => $"{_version}:{pagination.Page}:{pagination.PageSize}";
}
