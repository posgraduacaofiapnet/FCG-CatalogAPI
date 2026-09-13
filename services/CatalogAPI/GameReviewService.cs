using Microsoft.EntityFrameworkCore;

namespace CatalogAPI;

public sealed class GameReviewService(CatalogDbContext dbContext, IGameReviewStore store)
{
    public async Task<GameReviewResponse?> CreateAsync(
        Guid gameId,
        Guid userId,
        CreateGameReviewRequest request,
        CancellationToken cancellationToken)
    {
        var gameExists = await dbContext.Games.AnyAsync(
            game => game.Id == gameId && game.IsActive,
            cancellationToken);
        if (!gameExists)
        {
            return null;
        }

        var review = new GameReview
        {
            GameId = gameId,
            UserId = userId,
            Rating = request.Rating,
            Comment = request.Comment.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        await store.InsertAsync(review, cancellationToken);
        return Map(review);
    }

    public async Task<IReadOnlyList<GameReviewResponse>?> GetByGameIdAsync(
        Guid gameId,
        CancellationToken cancellationToken)
    {
        var gameExists = await dbContext.Games.AnyAsync(
            game => game.Id == gameId && game.IsActive,
            cancellationToken);
        if (!gameExists)
        {
            return null;
        }

        var reviews = await store.ListByGameIdAsync(gameId, cancellationToken);
        return reviews.Select(Map).ToList();
    }

    private static GameReviewResponse Map(GameReview review) =>
        new(review.Id, review.GameId, review.UserId, review.Rating, review.Comment, review.CreatedAt);
}
