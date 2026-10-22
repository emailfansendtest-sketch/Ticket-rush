using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TicketRush.BuildingBlocks.Contracts.Notification;
using TicketRush.Notification.Persistence;

namespace TicketRush.Notification.Worker;

/// <summary>
/// Simulated notification receiver. ACK only after a durable inbox commit;
/// no email, SMS, payment, or external provider is contacted.
/// </summary>
public sealed class NotificationWorker(
    IServiceScopeFactory scopes, IConfiguration config, ILogger<NotificationWorker> logger)
    : BackgroundService
{
    public const string Exchange = "ticket.events";
    public const string DeadExchange = "ticket.dead";
    public const string RoutingKey = "notification.checkout-finalized.v1";
    public const string Queue = "ticket.notification.checkout-finalized.v1";
    public const string DeadQueue = "ticket.notification.checkout-finalized.dead.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var uri = config["RabbitMq:Uri"]
                    ?? throw new InvalidOperationException("RabbitMq:Uri is required.");
                var factory = new ConnectionFactory
                {
                    Uri = new Uri(uri),
                    AutomaticRecoveryEnabled = false,
                    RequestedHeartbeat = TimeSpan.FromSeconds(15)
                };
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(
                    cancellationToken: stoppingToken);
                await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct,
                    durable: true, autoDelete: false, arguments: null,
                    cancellationToken: stoppingToken);
                await channel.ExchangeDeclareAsync(DeadExchange, ExchangeType.Direct,
                    durable: true, autoDelete: false, arguments: null,
                    cancellationToken: stoppingToken);
                await channel.QueueDeclareAsync(DeadQueue, durable: true, exclusive: false,
                    autoDelete: false, arguments: null, cancellationToken: stoppingToken);
                await channel.QueueBindAsync(DeadQueue, DeadExchange, RoutingKey,
                    arguments: null, cancellationToken: stoppingToken);
                var queueArgs = new Dictionary<string, object?>
                {
                    ["x-dead-letter-exchange"] = DeadExchange,
                    ["x-dead-letter-routing-key"] = RoutingKey
                };
                await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false,
                    autoDelete: false, arguments: queueArgs, cancellationToken: stoppingToken);
                await channel.QueueBindAsync(Queue, Exchange, RoutingKey,
                    arguments: null, cancellationToken: stoppingToken);
                await channel.BasicQosAsync(0, 1, false, stoppingToken);

                var disconnected = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                channel.ChannelShutdownAsync += (_, _) =>
                {
                    disconnected.TrySetResult(true);
                    return Task.CompletedTask;
                };
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.UnregisteredAsync += (_, _) =>
                {
                    disconnected.TrySetResult(true);
                    return Task.CompletedTask;
                };
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    try
                    {
                        if (delivery.BasicProperties.Type != nameof(CheckoutNotificationRequestedV1))
                            throw new InvalidDataException("Unexpected notification message type.");
                        var message = JsonSerializer.Deserialize<CheckoutNotificationRequestedV1>(
                            delivery.Body.ToArray(), JsonOptions)
                            ?? throw new InvalidDataException("Empty notification message.");
                        if (!Guid.TryParse(delivery.BasicProperties.MessageId, out var headerId) ||
                            headerId != message.MessageId)
                            throw new InvalidDataException("Notification MessageId/header mismatch.");
                        await using var scope = scopes.CreateAsyncScope();
                        var processor = scope.ServiceProvider.GetRequiredService<NotificationDeliveryProcessor>();
                        var result = await processor.ProcessAsync(message, stoppingToken);
                        logger.LogInformation(
                            "Simulated notification {Result}: Order {OrderId}, Outcome {Outcome}",
                            result, message.OrderId, message.Outcome);
                        await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
                    {
                        logger.LogWarning(ex, "Invalid notification command; dead-lettering");
                        await channel.BasicNackAsync(delivery.DeliveryTag, false, false, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        // Closing the connection requeues unacknowledged messages.
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Notification consumer failed; requeueing");
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                            await channel.BasicNackAsync(delivery.DeliveryTag, false, true, stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                    }
                };
                await channel.BasicConsumeAsync(
                    queue: Queue, autoAck: false, consumerTag: string.Empty,
                    noLocal: false, exclusive: false, arguments: null,
                    consumer: consumer, cancellationToken: stoppingToken);
                logger.LogInformation("Notification consumer subscribed to {Queue}", Queue);
                await disconnected.Task.WaitAsync(stoppingToken);
                throw new IOException("Notification AMQP channel disconnected.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Notification AMQP disconnected; reconnecting");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }
    }
}
