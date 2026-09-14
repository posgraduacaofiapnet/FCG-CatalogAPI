using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.EntityFrameworkCore;

namespace CatalogAPI;

public sealed record NotificationProcessingStatusResponse(
    Guid Id,
    string EventType,
    DateTimeOffset CreatedAt,
    bool OutboxIsSuccessful,
    int Attempts,
    DateTimeOffset? NextAttemptAt,
    bool LambdaProcessed,
    string? DynamoStatus,
    DateTimeOffset? DynamoExpiresAt);

public interface INotificationProcessingStatusReader
{
    Task<IReadOnlyList<NotificationProcessingStatusResponse>> GetRecentAsync(
        int? limit,
        CancellationToken cancellationToken);

    Task<NotificationProcessingStatusResponse?> GetByIdAsync(
        Guid eventId,
        CancellationToken cancellationToken);
}

public sealed class DynamoDbNotificationProcessingStatusReader(
    CatalogDbContext dbContext,
    IAmazonDynamoDB dynamoDb,
    IConfiguration configuration) : INotificationProcessingStatusReader
{
    private const int DefaultLimit = 20;
    private const int MaximumLimit = 50;
    private readonly string _tableName = configuration["Notifications:DynamoDbTableName"]
        ?? "fcg-notification-idempotency";

    public async Task<IReadOnlyList<NotificationProcessingStatusResponse>> GetRecentAsync(
        int? limit,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaximumLimit);
        var outboxMessages = await dbContext.OutboxMessages
            .AsNoTracking()
            .OrderByDescending(message => message.CreatedAt)
            .ThenByDescending(message => message.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        if (outboxMessages.Count == 0)
        {
            return [];
        }

        var response = await dynamoDb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new Dictionary<string, KeysAndAttributes>
            {
                [_tableName] = new()
                {
                    ConsistentRead = true,
                    Keys = outboxMessages.Select(message => Key(message.Id)).ToList()
                }
            }
        }, cancellationToken);

        var dynamoItems = response.Responses.TryGetValue(_tableName, out var items)
            ? items.ToDictionary(ItemEventId)
            : new Dictionary<Guid, Dictionary<string, AttributeValue>>();

        return outboxMessages
            .Select(message => Map(message, dynamoItems.GetValueOrDefault(message.Id)))
            .ToList();
    }

    public async Task<NotificationProcessingStatusResponse?> GetByIdAsync(
        Guid eventId,
        CancellationToken cancellationToken)
    {
        var outboxMessage = await dbContext.OutboxMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(message => message.Id == eventId, cancellationToken);
        if (outboxMessage is null)
        {
            return null;
        }

        var response = await dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = Key(eventId),
            ConsistentRead = true
        }, cancellationToken);

        return Map(outboxMessage, response.IsItemSet ? response.Item : null);
    }

    private static NotificationProcessingStatusResponse Map(
        OutboxMessage outboxMessage,
        Dictionary<string, AttributeValue>? dynamoItem)
    {
        var dynamoStatus = ReadString(dynamoItem, "Status");
        return new NotificationProcessingStatusResponse(
            outboxMessage.Id,
            outboxMessage.EventType,
            outboxMessage.CreatedAt,
            outboxMessage.IsSuccessful,
            outboxMessage.Attempts,
            outboxMessage.NextAttemptAt,
            string.Equals(dynamoStatus, "Completed", StringComparison.OrdinalIgnoreCase),
            dynamoStatus,
            ReadUnixTimestamp(dynamoItem, "ExpiresAt"));
    }

    private static Dictionary<string, AttributeValue> Key(Guid eventId) => new()
    {
        ["EventId"] = new() { S = eventId.ToString("D") }
    };

    private static Guid ItemEventId(Dictionary<string, AttributeValue> item) =>
        Guid.Parse(item["EventId"].S);

    private static string? ReadString(
        IReadOnlyDictionary<string, AttributeValue>? item,
        string attributeName) =>
        item is not null && item.TryGetValue(attributeName, out var attribute)
            ? attribute.S
            : null;

    private static DateTimeOffset? ReadUnixTimestamp(
        IReadOnlyDictionary<string, AttributeValue>? item,
        string attributeName) =>
        item is not null
        && item.TryGetValue(attributeName, out var attribute)
        && long.TryParse(attribute.N, out var unixTime)
            ? DateTimeOffset.FromUnixTimeSeconds(unixTime)
            : null;
}
