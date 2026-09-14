using System.Data;
using System.Text.Json;
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

public sealed class CatalogService(
    CatalogDbContext dbContext,
    ICatalogEventPublisher publisher,
    IGameCatalogCache? gameListCache = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
        await InvalidateGameListCacheAsync(cancellationToken);
        return Map(game);
    }

    public async Task<PagedResult<GameResponse>> GetGamesAsync(PaginationParameters pagination, CancellationToken cancellationToken)
    {
        if (gameListCache is not null)
        {
            var cached = await gameListCache.TryGetListAsync(pagination, cancellationToken);
            if (cached is not null)
            {
                return cached;
            }
        }

        var query = dbContext.Games
            .Where(game => game.IsActive)
            .OrderBy(game => game.Title);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .Select(game => Map(game))
            .ToListAsync(cancellationToken);

        var result = new PagedResult<GameResponse>(items, pagination.Page, pagination.PageSize, totalCount);

        if (gameListCache is not null)
        {
            await gameListCache.SetListAsync(pagination, result, cancellationToken);
        }

        return result;
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
        await InvalidateGameListCacheAsync(cancellationToken);
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
        await InvalidateGameListCacheAsync(cancellationToken);
        return true;
    }

    public async Task<IResult> PurchaseAsync(
        PurchaseGameRequest request,
        string userEmail,
        CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

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

        var placedAt = DateTimeOffset.UtcNow;
        var order = new PurchaseOrder
        {
            UserId = request.UserId,
            GameId = game.Id,
            GameTitle = game.Title,
            UserEmail = userEmail,
            Price = game.Price,
            Status = OrderStatuses.Pending,
            CreatedAt = placedAt.UtcDateTime
        };

        var notification = new OrderPlacedNotification(
            order.Id,
            order.UserId,
            order.GameId,
            order.GameTitle,
            order.Price,
            order.UserEmail,
            placedAt);

        dbContext.Orders.Add(order);
        dbContext.OutboxMessages.Add(OutboxMessage.Create(NotificationEventTypes.OrderPlaced, notification, JsonOptions));
        await publisher.PublishOrderPlacedAsync(
            new OrderPlacedEvent(
                order.Id,
                order.UserId,
                order.GameId,
                order.GameTitle,
                order.Price,
                order.CreatedAt),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return Results.Accepted($"/api/orders/{order.Id}", new { order.Id, order.Status });
    }

    public async Task ProcessPaymentAsync(PaymentProcessedEvent payment, CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        var order = await dbContext.Orders.FirstOrDefaultAsync(order => order.Id == payment.OrderId, cancellationToken);
        if (order is null)
        {
            return;
        }

        if (order.Status != OrderStatuses.Pending)
        {
            return;
        }

        if (order.UserId != payment.UserId
            || order.GameId != payment.GameId
            || order.GameTitle != payment.GameTitle
            || order.Price != payment.Price)
        {
            throw new InvalidOperationException($"Payment data does not match order {payment.OrderId}.");
        }

        if (payment.Status is not (PaymentStatuses.Approved or PaymentStatuses.Rejected))
        {
            throw new InvalidOperationException($"Unsupported payment status '{payment.Status}'.");
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

        var processedAt = new DateTimeOffset(DateTime.SpecifyKind(payment.ProcessedAt, DateTimeKind.Utc));
        var notification = new PaymentProcessedNotification(
            order.Id,
            order.UserId,
            order.GameId,
            order.GameTitle,
            order.Price,
            order.Status,
            order.UserEmail,
            processedAt);

        dbContext.OutboxMessages.Add(OutboxMessage.Create(
            NotificationEventTypes.PaymentProcessed,
            notification,
            JsonOptions));

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
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

    private Task InvalidateGameListCacheAsync(CancellationToken cancellationToken) =>
        gameListCache?.InvalidateListAsync(cancellationToken) ?? Task.CompletedTask;

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
