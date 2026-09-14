using System.Text.Json;

namespace CatalogAPI;

public static class NotificationEventTypes
{
    public const string OrderPlaced = "OrderPlaced";
    public const string PaymentProcessed = "PaymentProcessed";
}

public static class OrderStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

public sealed record OrderPlacedNotification(
    Guid OrderId,
    Guid UserId,
    Guid GameId,
    string GameTitle,
    decimal Price,
    string UserEmail,
    DateTimeOffset PlacedAt);

public sealed record PaymentProcessedNotification(
    Guid OrderId,
    Guid UserId,
    Guid GameId,
    string GameTitle,
    decimal Price,
    string Status,
    string UserEmail,
    DateTimeOffset ProcessedAt);

public sealed class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EventType { get; set; } = string.Empty;
    public bool IsSuccessful { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset? NextAttemptAt { get; set; }
    public int Attempts { get; set; }

    public static OutboxMessage Create<T>(string eventType, T payload, JsonSerializerOptions options) => new()
    {
        EventType = eventType,
        Payload = JsonSerializer.Serialize(payload, options)
    };
}
