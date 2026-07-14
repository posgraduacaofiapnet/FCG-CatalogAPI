using Bogus;
using CatalogAPI;
using FCG.Contracts;
using Microsoft.EntityFrameworkCore;

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

    public Task PublishOrderPlacedAsync(OrderPlacedEvent message, CancellationToken cancellationToken)
    {
        Published = message;
        return Task.CompletedTask;
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
    public async Task PurchaseAsync_CreatesPendingOrderAndPublishesEvent()
    {
        await using var db = fixture.CreateDbContext();
        var publisher = new FakeCatalogEventPublisher();
        var service = new CatalogService(db, publisher);
        var game = await service.CreateGameAsync(fixture.CreateGame(), CancellationToken.None);
        var userId = Guid.NewGuid();

        await service.PurchaseAsync(new PurchaseGameRequest(userId, game.Id), CancellationToken.None);

        var order = Assert.Single(await db.Orders.ToListAsync());
        Assert.Equal("Pending", order.Status);
        Assert.Equal(order.Id, publisher.Published?.OrderId);
        Assert.Equal(userId, publisher.Published?.UserId);
    }

    [Fact]
    public async Task ApprovedPayment_AddsGameToLibraryOnlyOnce()
    {
        await using var db = fixture.CreateDbContext();
        var service = new CatalogService(db, new FakeCatalogEventPublisher());
        var game = await service.CreateGameAsync(fixture.CreateGame(), CancellationToken.None);
        var userId = Guid.NewGuid();
        await service.PurchaseAsync(new PurchaseGameRequest(userId, game.Id), CancellationToken.None);
        var order = Assert.Single(await db.Orders.ToListAsync());
        var payment = new PaymentProcessedEvent(order.Id, userId, game.Id, game.Title, game.Price,
            PaymentStatuses.Approved, DateTime.UtcNow);

        await service.ProcessPaymentAsync(payment, CancellationToken.None);
        await service.ProcessPaymentAsync(payment, CancellationToken.None);

        Assert.Single(await db.LibraryItems.ToListAsync());
        Assert.Equal(PaymentStatuses.Approved, order.Status);
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
}
