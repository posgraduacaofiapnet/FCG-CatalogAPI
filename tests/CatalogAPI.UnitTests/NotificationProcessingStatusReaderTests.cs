using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using CatalogAPI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace CatalogAPI.UnitTests;

public sealed class NotificationProcessingStatusReaderTests
{
    [Fact]
    public async Task GetRecentAsync_MergesOutboxAndCompletedDynamoStatus()
    {
        await using var dbContext = CreateDbContext();
        var completed = NewOutboxMessage(NotificationEventTypes.PaymentProcessed, true, 1);
        var pending = NewOutboxMessage(NotificationEventTypes.OrderPlaced, true, 1);
        dbContext.OutboxMessages.AddRange(completed, pending);
        await dbContext.SaveChangesAsync();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds();
        var dynamoDb = new Mock<IAmazonDynamoDB>();
        dynamoDb
            .Setup(client => client.BatchGetItemAsync(It.IsAny<BatchGetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BatchGetItemResponse
            {
                Responses = new Dictionary<string, List<Dictionary<string, AttributeValue>>>
                {
                    ["notifications-test"] =
                    [
                        new()
                        {
                            ["EventId"] = new() { S = completed.Id.ToString("D") },
                            ["Status"] = new() { S = "Completed" },
                            ["ExpiresAt"] = new() { N = expiresAt.ToString() }
                        }
                    ]
                }
            });
        var sut = CreateReader(dbContext, dynamoDb.Object);

        var result = await sut.GetRecentAsync(20, CancellationToken.None);

        Assert.Equal(2, result.Count);
        var completedResult = Assert.Single(result, item => item.Id == completed.Id);
        Assert.True(completedResult.LambdaProcessed);
        Assert.Equal("Completed", completedResult.DynamoStatus);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(expiresAt), completedResult.DynamoExpiresAt);
        var pendingResult = Assert.Single(result, item => item.Id == pending.Id);
        Assert.False(pendingResult.LambdaProcessed);
        Assert.Null(pendingResult.DynamoStatus);
    }

    [Fact]
    public async Task GetRecentAsync_WithNoOutboxMessages_DoesNotCallDynamoDb()
    {
        await using var dbContext = CreateDbContext();
        var dynamoDb = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        var sut = CreateReader(dbContext, dynamoDb.Object);

        var result = await sut.GetRecentAsync(20, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsCompletedStatus()
    {
        await using var dbContext = CreateDbContext();
        var message = NewOutboxMessage(NotificationEventTypes.OrderPlaced, true, 1);
        dbContext.OutboxMessages.Add(message);
        await dbContext.SaveChangesAsync();
        var dynamoDb = new Mock<IAmazonDynamoDB>();
        dynamoDb
            .Setup(client => client.GetItemAsync(It.Is<GetItemRequest>(request =>
                    request.Key["EventId"].S == message.Id.ToString("D")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse
            {
                Item = new Dictionary<string, AttributeValue>
                {
                    ["EventId"] = new() { S = message.Id.ToString("D") },
                    ["Status"] = new() { S = "Completed" }
                }
            });
        var sut = CreateReader(dbContext, dynamoDb.Object);

        var result = await sut.GetByIdAsync(message.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.LambdaProcessed);
    }

    [Fact]
    public async Task GetByIdAsync_WithUnknownOutboxMessage_ReturnsNullWithoutCallingDynamoDb()
    {
        await using var dbContext = CreateDbContext();
        var dynamoDb = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        var sut = CreateReader(dbContext, dynamoDb.Object);

        var result = await sut.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    private static CatalogDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<CatalogDbContext>()
            .UseInMemoryDatabase($"notification-status-{Guid.NewGuid():N}")
            .Options);

    private static DynamoDbNotificationProcessingStatusReader CreateReader(
        CatalogDbContext dbContext,
        IAmazonDynamoDB dynamoDb) =>
        new(dbContext, dynamoDb, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Notifications:DynamoDbTableName"] = "notifications-test"
            })
            .Build());

    private static OutboxMessage NewOutboxMessage(string eventType, bool successful, int attempts) => new()
    {
        Id = Guid.NewGuid(),
        EventType = eventType,
        CreatedAt = DateTimeOffset.UtcNow,
        IsSuccessful = successful,
        Attempts = attempts,
        Payload = "{}"
    };
}
