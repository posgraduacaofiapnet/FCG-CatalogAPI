using FCG.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace CatalogAPI;

public interface ICatalogEventPublisher
{
    Task PublishOrderPlacedAsync(OrderPlacedEvent message, CancellationToken cancellationToken);
}

public sealed class MassTransitCatalogEventPublisher(IPublishEndpoint publisher, CorrelationContext correlationContext) : ICatalogEventPublisher
{
    public Task PublishOrderPlacedAsync(OrderPlacedEvent message, CancellationToken cancellationToken)
    {
        return publisher.Publish(message, context =>
            context.Headers.Set(CorrelationId.HeaderName, correlationContext.Value), cancellationToken);
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

    public async Task<PagedResult<GameResponse>> GetGamesAsync(PaginationParameters pagination, CancellationToken cancellationToken)
    {
        var query = dbContext.Games
            .Where(game => game.IsActive)
            .OrderBy(game => game.Title);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .Select(game => Map(game))
            .ToListAsync(cancellationToken);

        return new PagedResult<GameResponse>(items, pagination.Page, pagination.PageSize, totalCount);
    }

    public async Task<GameResponse?> GetGameAsync(Guid id, CancellationToken cancellationToken)
    {
        var game = await dbContext.Games.FirstOrDefaultAsync(game => game.Id == id && game.IsActive, cancellationToken);
        return game is null ? null : Map(game);
    }

    public async Task<GameResponse?> UpdateGameAsync(Guid id, UpdateGameRequest request, CancellationToken cancellationToken)
    {
        var game = await dbContext.Games.FirstOrDefaultAsync(game => game.Id == id && game.IsActive, cancellationToken);
        if (game is null)
        {
            return null;
        }

        game.Title = request.Title;
        game.Description = request.Description;
        game.Price = request.Price;

        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(game);
    }

    public async Task<bool> DeleteGameAsync(Guid id, CancellationToken cancellationToken)
    {
        var game = await dbContext.Games.FirstOrDefaultAsync(game => game.Id == id && game.IsActive, cancellationToken);
        if (game is null)
        {
            return false;
        }

        game.IsActive = false;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IResult> PurchaseAsync(PurchaseGameRequest request, CancellationToken cancellationToken)
    {
        var game = await dbContext.Games.FirstOrDefaultAsync(game => game.Id == request.GameId && game.IsActive, cancellationToken);
        if (game is null)
        {
            return Results.NotFound(new { error = "Games.NotFound" });
        }

        var alreadyOwned = await dbContext.LibraryItems.AnyAsync(
            item => item.UserId == request.UserId && item.GameId == request.GameId,
            cancellationToken);

        if (alreadyOwned)
        {
            return Results.Conflict(new { error = "Library.GameAlreadyOwned" });
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
        var correlationId = CorrelationId.From(context.Headers);
        using var _ = Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId);
        logger.LogInformation("Pagamento {Status} recebido para pedido {OrderId}.", context.Message.Status, context.Message.OrderId);
        await catalogService.ProcessPaymentAsync(context.Message, context.CancellationToken);
    }
}
