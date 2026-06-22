using FCG.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace CatalogAPI;

public interface ICatalogEventPublisher
{
    Task PublishOrderPlacedAsync(OrderPlacedEvent message, CancellationToken cancellationToken);
}

public sealed class MassTransitCatalogEventPublisher(IPublishEndpoint publisher) : ICatalogEventPublisher
{
    public Task PublishOrderPlacedAsync(OrderPlacedEvent message, CancellationToken cancellationToken)
    {
        return publisher.Publish(message, cancellationToken);
    }
}

public sealed class CatalogService(CatalogDbContext dbContext, ICatalogEventPublisher publisher)
{
    public async Task<GameResponse> CreateGameAsync(CreateGameRequest request, CancellationToken cancellationToken)
    {
        var game = new Game
        {
            Title = request.Title,
            Description = request.Description,
            Price = request.Price
        };

        dbContext.Games.Add(game);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(game);
    }

    public async Task<IReadOnlyList<GameResponse>> GetGamesAsync(CancellationToken cancellationToken)
    {
        return await dbContext.Games
            .Where(game => game.IsActive)
            .OrderBy(game => game.Title)
            .Select(game => Map(game))
            .ToListAsync(cancellationToken);
    }

    public async Task<GameResponse?> GetGameAsync(Guid id, CancellationToken cancellationToken)
    {
        var game = await dbContext.Games.FirstOrDefaultAsync(game => game.Id == id && game.IsActive, cancellationToken);
        return game is null ? null : Map(game);
    }

    public async Task<IResult> PurchaseAsync(PurchaseGameRequest request, CancellationToken cancellationToken)
    {
        var game = await dbContext.Games.FirstOrDefaultAsync(game => game.Id == request.GameId && game.IsActive, cancellationToken);
        if (game is null)
        {
            return Results.NotFound(new { error = "Games.NotFound" });
        }

        var order = new PurchaseOrder
        {
            UserId = request.UserId,
            GameId = game.Id,
            GameTitle = game.Title,
            Price = game.Price,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };

        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync(cancellationToken);

        await publisher.PublishOrderPlacedAsync(new OrderPlacedEvent(order.Id, order.UserId, order.GameId, order.GameTitle, order.Price, order.CreatedAt), cancellationToken);

        return Results.Accepted($"/api/orders/{order.Id}", new { order.Id, order.Status });
    }

    public async Task ProcessPaymentAsync(PaymentProcessedEvent payment, CancellationToken cancellationToken)
    {
        var order = await dbContext.Orders.FirstOrDefaultAsync(order => order.Id == payment.OrderId, cancellationToken);
        if (order is null)
        {
            return;
        }

        order.Status = payment.Status;

        if (payment.Status == PaymentStatuses.Approved)
        {
            var alreadyOwned = await dbContext.LibraryItems.AnyAsync(
                item => item.UserId == payment.UserId && item.GameId == payment.GameId,
                cancellationToken);

            if (!alreadyOwned)
            {
                dbContext.LibraryItems.Add(new LibraryItem
                {
                    UserId = payment.UserId,
                    GameId = payment.GameId,
                    AcquiredAt = DateTime.UtcNow
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LibraryGameResponse>> GetLibraryAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await dbContext.LibraryItems
            .Where(item => item.UserId == userId)
            .Join(dbContext.Games,
                item => item.GameId,
                game => game.Id,
                (item, game) => new { item, game })
            .OrderBy(result => result.game.Title)
            .Select(result => new LibraryGameResponse(result.game.Id, result.game.Title, result.game.Price, result.item.AcquiredAt))
            .ToListAsync(cancellationToken);
    }

    private static GameResponse Map(Game game)
    {
        return new GameResponse(game.Id, game.Title, game.Description, game.Price);
    }
}

public sealed class PaymentProcessedConsumer(CatalogService catalogService, ILogger<PaymentProcessedConsumer> logger)
    : IConsumer<PaymentProcessedEvent>
{
    public async Task Consume(ConsumeContext<PaymentProcessedEvent> context)
    {
        logger.LogInformation("Pagamento {Status} recebido para pedido {OrderId}.", context.Message.Status, context.Message.OrderId);
        await catalogService.ProcessPaymentAsync(context.Message, context.CancellationToken);
    }
}
