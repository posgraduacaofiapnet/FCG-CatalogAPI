namespace FCG.Contracts;

public sealed record UserCreatedEvent(
    Guid UserId,
    string Name,
    string Email,
    DateTime CreatedAt);

public sealed record OrderPlacedEvent(
    Guid OrderId,
    Guid UserId,
    Guid GameId,
    string GameTitle,
    decimal Price,
    DateTime PlacedAt);

public sealed record PaymentProcessedEvent(
    Guid OrderId,
    Guid UserId,
    Guid GameId,
    string GameTitle,
    decimal Price,
    string Status,
    DateTime ProcessedAt);

public static class PaymentStatuses
{
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}
