using CatalogAPI;
using FluentValidation;

namespace CatalogAPI.UnitTests;

public sealed class FakeGameReviewStore : IGameReviewStore
{
    private readonly List<GameReview> _reviews = [];

    public Task InsertAsync(GameReview review, CancellationToken cancellationToken)
    {
        _reviews.Add(review);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GameReview>> ListByGameIdAsync(Guid gameId, CancellationToken cancellationToken)
    {
        IReadOnlyList<GameReview> items = _reviews
            .Where(review => review.GameId == gameId)
            .OrderByDescending(review => review.CreatedAt)
            .ToList();
        return Task.FromResult(items);
    }
}

public sealed class GameReviewServiceTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    [Fact]
    public async Task CreateAsync_PersistsReviewWhenGameIsActive()
    {
        await using var db = fixture.CreateDbContext();
        var catalog = new CatalogService(db, new FakeCatalogEventPublisher());
        var game = await catalog.CreateGameAsync(fixture.CreateGame(), CancellationToken.None);
        var store = new FakeGameReviewStore();
        var service = new GameReviewService(db, store);
        var userId = Guid.NewGuid();

        var created = await service.CreateAsync(
            game.Id,
            userId,
            new CreateGameReviewRequest(5, "  Excelente campanha.  "),
            CancellationToken.None);

        Assert.NotNull(created);
        Assert.Equal(game.Id, created.GameId);
        Assert.Equal(userId, created.UserId);
        Assert.Equal(5, created.Rating);
        Assert.Equal("Excelente campanha.", created.Comment);
    }

    [Fact]
    public async Task CreateAsync_ReturnsNullWhenGameDoesNotExist()
    {
        await using var db = fixture.CreateDbContext();
        var service = new GameReviewService(db, new FakeGameReviewStore());

        var created = await service.CreateAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new CreateGameReviewRequest(4, "Bom"),
            CancellationToken.None);

        Assert.Null(created);
    }

    [Fact]
    public async Task CreateAsync_ReturnsNullWhenGameIsInactive()
    {
        await using var db = fixture.CreateDbContext();
        var catalog = new CatalogService(db, new FakeCatalogEventPublisher());
        var game = await catalog.CreateGameAsync(fixture.CreateGame(), CancellationToken.None);
        await catalog.DeleteGameAsync(game.Id, CancellationToken.None);
        var service = new GameReviewService(db, new FakeGameReviewStore());

        var created = await service.CreateAsync(
            game.Id,
            Guid.NewGuid(),
            new CreateGameReviewRequest(3, "Ok"),
            CancellationToken.None);

        Assert.Null(created);
    }

    [Fact]
    public async Task GetByGameIdAsync_ReturnsReviewsNewestFirst()
    {
        await using var db = fixture.CreateDbContext();
        var catalog = new CatalogService(db, new FakeCatalogEventPublisher());
        var game = await catalog.CreateGameAsync(fixture.CreateGame(), CancellationToken.None);
        var store = new FakeGameReviewStore();
        var older = new GameReview
        {
            GameId = game.Id,
            UserId = Guid.NewGuid(),
            Rating = 2,
            Comment = "Antiga",
            CreatedAt = DateTime.UtcNow.AddMinutes(-1)
        };
        var newer = new GameReview
        {
            GameId = game.Id,
            UserId = Guid.NewGuid(),
            Rating = 5,
            Comment = "Nova",
            CreatedAt = DateTime.UtcNow
        };
        await store.InsertAsync(older, CancellationToken.None);
        await store.InsertAsync(newer, CancellationToken.None);
        var service = new GameReviewService(db, store);

        var list = await service.GetByGameIdAsync(game.Id, CancellationToken.None);

        Assert.NotNull(list);
        Assert.Equal(2, list.Count);
        Assert.Equal(newer.Id, list[0].Id);
        Assert.Equal(older.Id, list[1].Id);
    }

    [Fact]
    public async Task GetByGameIdAsync_ReturnsEmptyListWhenGameHasNoReviews()
    {
        await using var db = fixture.CreateDbContext();
        var catalog = new CatalogService(db, new FakeCatalogEventPublisher());
        var game = await catalog.CreateGameAsync(fixture.CreateGame(), CancellationToken.None);
        var service = new GameReviewService(db, new FakeGameReviewStore());

        var list = await service.GetByGameIdAsync(game.Id, CancellationToken.None);

        Assert.NotNull(list);
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetByGameIdAsync_ReturnsNullWhenGameDoesNotExist()
    {
        await using var db = fixture.CreateDbContext();
        var service = new GameReviewService(db, new FakeGameReviewStore());

        var list = await service.GetByGameIdAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(list);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Validator_RejectsRatingOutsideRange(int rating)
    {
        var validator = new CreateGameReviewRequestValidator();
        var result = validator.Validate(new CreateGameReviewRequest(rating, "Comentario valido"));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validator_RejectsEmptyComment()
    {
        var validator = new CreateGameReviewRequestValidator();
        var result = validator.Validate(new CreateGameReviewRequest(5, ""));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validator_RejectsCommentLongerThan500()
    {
        var validator = new CreateGameReviewRequestValidator();
        var result = validator.Validate(new CreateGameReviewRequest(5, new string('a', 501)));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validator_AcceptsValidReview()
    {
        var validator = new CreateGameReviewRequestValidator();
        var result = validator.Validate(new CreateGameReviewRequest(1, "Ruim"));
        Assert.True(result.IsValid);
    }
}
