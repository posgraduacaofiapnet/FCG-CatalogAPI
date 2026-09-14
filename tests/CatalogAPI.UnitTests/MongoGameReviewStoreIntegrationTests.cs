using MongoDB.Driver;

namespace CatalogAPI.UnitTests;

public sealed class MongoGameReviewStoreIntegrationTests
{
    [SkippableFact]
    public async Task Store_CreatesIndexInsertsAndListsNewestFirst()
    {
        var connectionString = Environment.GetEnvironmentVariable("FCG_TEST_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "FCG_TEST_MONGO_CONNECTION is not configured.");
        var client = new MongoClient(connectionString);
        var databaseName = $"fcg_catalog_tests_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var gameId = Guid.NewGuid();
        var sut = new MongoGameReviewStore(database);
        var older = new GameReview
        {
            GameId = gameId,
            UserId = Guid.NewGuid(),
            Rating = 3,
            Comment = "Older",
            CreatedAt = DateTime.UtcNow.AddMinutes(-1)
        };
        var newer = new GameReview
        {
            GameId = gameId,
            UserId = Guid.NewGuid(),
            Rating = 5,
            Comment = "Newer",
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            await sut.InsertAsync(older, CancellationToken.None);
            await sut.InsertAsync(newer, CancellationToken.None);

            var reviews = await sut.ListByGameIdAsync(gameId, CancellationToken.None);

            Assert.Equal(new[] { newer.Id, older.Id }, reviews.Select(review => review.Id));
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName);
        }
    }
}
