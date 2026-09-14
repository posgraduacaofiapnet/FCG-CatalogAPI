using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace CatalogAPI;

public sealed class GameReview
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [BsonElement("gameId")]
    [BsonRepresentation(BsonType.String)]
    public Guid GameId { get; set; }

    [BsonElement("userId")]
    [BsonRepresentation(BsonType.String)]
    public Guid UserId { get; set; }

    [BsonElement("rating")]
    public int Rating { get; set; }

    [BsonElement("comment")]
    public string Comment { get; set; } = string.Empty;

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public interface IGameReviewStore
{
    Task InsertAsync(GameReview review, CancellationToken cancellationToken);
    Task<IReadOnlyList<GameReview>> ListByGameIdAsync(Guid gameId, CancellationToken cancellationToken);
}

public sealed class MongoGameReviewStore : IGameReviewStore
{
    private readonly IMongoCollection<GameReview> _reviews;

    public MongoGameReviewStore(IMongoDatabase database)
    {
        _reviews = database.GetCollection<GameReview>("reviews");
        _reviews.Indexes.CreateOne(
            new CreateIndexModel<GameReview>(
                Builders<GameReview>.IndexKeys.Ascending(review => review.GameId)));
    }

    public async Task InsertAsync(GameReview review, CancellationToken cancellationToken)
    {
        await _reviews.InsertOneAsync(review, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<GameReview>> ListByGameIdAsync(Guid gameId, CancellationToken cancellationToken)
    {
        return await _reviews
            .Find(review => review.GameId == gameId)
            .SortByDescending(review => review.CreatedAt)
            .ToListAsync(cancellationToken);
    }
}
