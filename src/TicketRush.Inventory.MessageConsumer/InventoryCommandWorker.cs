using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TicketRush.BuildingBlocks.Contracts.Checkout;
using TicketRush.Inventory;

namespace TicketRush.Inventory.MessageConsumer;

/// <summary>RabbitMQ manual-ACK consumer: acknowledge only after durable Inbox commit.</summary>
public sealed class InventoryCommandWorker(
    IServiceScopeFactory scopes, IConfiguration config, ILogger<InventoryCommandWorker> logger)
    : BackgroundService
{
    public const string Exchange = "ticket.events";
    public const string Queue = "ticket.inventory.sale-requested.v1";
    public const string RoutingKey = "inventory.sale-requested.v1";
    public const string DeadExchange = "ticket.dead";
    public const string DeadQueue = "ticket.inventory.sale-requested.dead.v1";

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
                    ?? throw new InvalidOperationException("RabbitMq:Uri is missing.");
                var factory = new ConnectionFactory
                {
                    Uri = new Uri(uri),
                    AutomaticRecoveryEnabled = false,
                    RequestedHeartbeat = TimeSpan.FromSeconds(15)
                };
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

                await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct,
                    durable: true, autoDelete: false, arguments: null, cancellationToken: stoppingToken);
                await channel.ExchangeDeclareAsync(DeadExchange, ExchangeType.Direct,
                    durable: true, autoDelete: false, arguments: null, cancellationToken: stoppingToken);
                await channel.QueueDeclareAsync(DeadQueue, durable: true, exclusive: false,
                    autoDelete: false, arguments: null, cancellationToken: stoppingToken);
                await channel.QueueBindAsync(DeadQueue, DeadExchange, RoutingKey,
                    arguments: null, cancellationToken: stoppingToken);
                var args = new Dictionary<string, object?>
                {
                    ["x-dead-letter-exchange"] = DeadExchange,
                    ["x-dead-letter-routing-key"] = RoutingKey
                };
                await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false,
                    autoDelete: false, arguments: args, cancellationToken: stoppingToken);
                await channel.QueueBindAsync(Queue, Exchange, RoutingKey,
                    arguments: null, cancellationToken: stoppingToken);
                await channel.BasicQosAsync(0, 1, false, stoppingToken);

                var disconnected = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                channel.ChannelShutdownAsync += (_, reason) =>
                {
                    disconnected.TrySetResult(true);
                    return Task.CompletedTask;
                };
                var consumer = new AsyncEventingBasicConsumer(channel);
                // A broker may cancel a consumer (for example, after queue deletion)
                // without closing the channel. Re-enter the setup/reconnect loop.
                consumer.UnregisteredAsync += (_, _) =>
                {
                    disconnected.TrySetResult(true);
                    return Task.CompletedTask;
                };
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    try
                    {
                        if (delivery.BasicProperties.Type != nameof(InventorySaleRequestedV1))
                            throw new InvalidDataException("Unexpected message type.");
                        var command = JsonSerializer.Deserialize<InventorySaleRequestedV1>(
                            delivery.Body.ToArray(), JsonOptions)
                            ?? throw new InvalidDataException("Empty sale command.");
                        if (!Guid.TryParse(delivery.BasicProperties.MessageId, out var headerId)
                            || headerId != command.MessageId)
                            throw new InvalidDataException("MessageId header does not match payload.");

                        await using var scope = scopes.CreateAsyncScope();
                        var processor = scope.ServiceProvider
                            .GetRequiredService<InventorySaleCommandProcessor>();
                        await processor.ProcessAsync(command, stoppingToken);
                        await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
                    {
                        logger.LogWarning(ex, "Discarding invalid Inventory command into dead-letter queue");
                        await channel.BasicNackAsync(delivery.DeliveryTag, false, false, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        // No ACK: broker requeues when the connection closes.
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Inventory command failed; delivery will be retried");
                        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                        await channel.BasicNackAsync(delivery.DeliveryTag, false, true, stoppingToken);
                    }
                };
                await channel.BasicConsumeAsync(
                    queue: Queue, autoAck: false, consumerTag: string.Empty,
                    noLocal: false, exclusive: false, arguments: null,
                    consumer: consumer, cancellationToken: stoppingToken);
                logger.LogInformation("Inventory consumer started for {Queue}", Queue);
                // Return to reconnect loop if the broker closes this channel.
                await disconnected.Task.WaitAsync(stoppingToken);
                throw new IOException("RabbitMQ channel was closed.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Inventory consumer connection failed; reconnecting");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}
