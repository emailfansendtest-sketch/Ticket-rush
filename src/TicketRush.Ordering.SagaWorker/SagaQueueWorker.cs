using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TicketRush.BuildingBlocks.Contracts.Checkout;
using TicketRush.BuildingBlocks.Contracts.Payment;
using TicketRush.Ordering.Persistence;

namespace TicketRush.Ordering.SagaWorker;

/// <summary>
/// Two independent subscriptions share the same consumer lifecycle and
/// acknowledgment rules. Both existing queues are declared WITHOUT x-arguments;
/// changing their arguments would break already deployed durable queues.
/// Invalid messages are published with confirmations to a quarantine queue
/// before their originals are acknowledged.
/// </summary>
public abstract class SagaQueueWorker(
    IServiceScopeFactory scopes, IConfiguration config, ILogger logger) : BackgroundService
{
    protected abstract string Queue { get; }
    protected abstract Task HandleAsync(OrderingSagaProcessor saga, string type,
        byte[] body, Guid messageId, CancellationToken ct);
    private const string Exchange = "ticket.events";
    private const string QuarantineExchange = "ticket.saga.quarantine";
    private const string QuarantineQueue = "ticket.saga.quarantine.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };
    protected static T Deserialize<T>(string type, byte[] body, Guid messageId)
        where T : class
    {
        if (type != typeof(T).Name)
            throw new InvalidDataException("Unexpected event type on Ordering saga queue.");
        var value = JsonSerializer.Deserialize<T>(body, JsonOptions)
            ?? throw new InvalidDataException("Empty event body.");
        var payloadId = value switch
        {
            CheckoutRequestedV1 v => v.MessageId,
            InventorySaleCommittedV1 v => v.MessageId,
            InventorySaleRejectedV1 v => v.MessageId,
            PaymentAuthorizedV1 v => v.MessageId,
            PaymentDeclinedV1 v => v.MessageId,
            _ => throw new InvalidDataException("Unknown saga event payload.")
        };
        if (payloadId != messageId)
            throw new InvalidDataException("MessageId header/payload mismatch.");
        return value;
    }

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
                    Uri = new Uri(uri), AutomaticRecoveryEnabled = false,
                    RequestedHeartbeat = TimeSpan.FromSeconds(15)
                };
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                var options = new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true);
                await using var channel = await connection.CreateChannelAsync(options, stoppingToken);
                await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct,
                    durable: true, autoDelete: false, arguments: null, cancellationToken: stoppingToken);
                await channel.ExchangeDeclareAsync(QuarantineExchange, ExchangeType.Direct,
                    durable: true, autoDelete: false, arguments: null, cancellationToken: stoppingToken);
                await channel.QueueDeclareAsync(QuarantineQueue, durable: true, exclusive: false,
                    autoDelete: false, arguments: null, cancellationToken: stoppingToken);
                await channel.QueueBindAsync(QuarantineQueue, QuarantineExchange, "invalid",
                    arguments: null, cancellationToken: stoppingToken);
                await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false,
                    autoDelete: false, arguments: null, cancellationToken: stoppingToken);
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
                        if (!Guid.TryParse(delivery.BasicProperties.MessageId, out var id))
                            throw new InvalidDataException("Missing MessageId header.");
                        var type = delivery.BasicProperties.Type
                            ?? throw new InvalidDataException("Missing message type header.");
                        await using var scope = scopes.CreateAsyncScope();
                        var saga = scope.ServiceProvider.GetRequiredService<OrderingSagaProcessor>();
                        await HandleAsync(saga, type, delivery.Body.ToArray(), id, stoppingToken);
                        await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException)
                    {
                        logger.LogWarning(ex, "Invalid Ordering saga event; quarantining from {Queue}", Queue);
                        try
                        {
                            // ACK only after durable, broker-confirmed quarantine publish.
                            var properties = new BasicProperties
                            {
                                MessageId = delivery.BasicProperties.MessageId,
                                Type = delivery.BasicProperties.Type,
                                ContentType = "application/json",
                                Persistent = true
                            };
                            await channel.BasicPublishAsync(
                                exchange: QuarantineExchange, routingKey: "invalid", mandatory: true,
                                basicProperties: properties, body: delivery.Body,
                                cancellationToken: stoppingToken);
                            await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                        }
                        catch (Exception pubException)
                        {
                            disconnected.TrySetException(pubException);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        // Closing channel will requeue any unacknowledged delivery.
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Transient Ordering saga failure in {Queue}", Queue);
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                            await channel.BasicNackAsync(delivery.DeliveryTag, false, true, stoppingToken);
                        }
                        catch (Exception nackException)
                        {
                            disconnected.TrySetException(nackException);
                        }
                    }
                };
                await channel.BasicConsumeAsync(
                    queue: Queue, autoAck: false, consumerTag: string.Empty,
                    noLocal: false, exclusive: false, arguments: null,
                    consumer: consumer, cancellationToken: stoppingToken);
                logger.LogInformation("Ordering saga listening on {Queue}", Queue);
                await disconnected.Task.WaitAsync(stoppingToken);
                throw new IOException("RabbitMQ Ordering saga subscription was canceled.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ordering saga connection failed; reconnecting for {Queue}", Queue);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }
    }
}
