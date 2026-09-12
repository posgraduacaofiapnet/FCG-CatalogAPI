using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace CatalogAPI;

public interface IOrderPaidQueuePublisher
{
    Task PublishAsync(Guid orderId, Guid userId, Guid gameId, CancellationToken cancellationToken);
}

public sealed class SqsOrderPaidQueuePublisher(
    IAmazonSQS sqs,
    IConfiguration configuration,
    CorrelationContext correlationContext,
    ILogger<SqsOrderPaidQueuePublisher> logger) : IOrderPaidQueuePublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task PublishAsync(Guid orderId, Guid userId, Guid gameId, CancellationToken cancellationToken)
    {
        var queueUrl = configuration["Sqs:NotificationsQueueUrl"];
        if (string.IsNullOrWhiteSpace(queueUrl))
        {
            logger.LogWarning("Sqs:NotificationsQueueUrl nao configurada. Notificacao serverless ignorada.");
            return;
        }

        var body = JsonSerializer.Serialize(new
        {
            orderId,
            userId,
            gameIds = new[] { gameId },
            correlationId = correlationContext.Value,
            timestamp = DateTime.UtcNow
        }, JsonOptions);

        try
        {
            await sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = body
            }, cancellationToken);

            logger.LogInformation(
                "OrderPaid enviado para SQS. OrderId={OrderId} UserId={UserId} CorrelationId={CorrelationId}",
                orderId,
                userId,
                correlationContext.Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao publicar OrderPaid no SQS. OrderId={OrderId}", orderId);
        }
    }
}
