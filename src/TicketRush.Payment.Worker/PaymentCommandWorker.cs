using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TicketRush.BuildingBlocks.Contracts.Payment;
using TicketRush.Payment.Persistence;

namespace TicketRush.Payment.Worker;

/// <summary>Development-only AMQP consumer; no real payment credentials or funds.</summary>
public sealed class PaymentCommandWorker(
    IServiceScopeFactory scopes, IConfiguration config, ILogger<PaymentCommandWorker> logger)
    : BackgroundService
{
    public const string Exchange = "ticket.events";
    public const string Queue = "ticket.payment.authorize-requested.v1";
    public const string RoutingKey = "payment.authorize-requested.v1";
    public const string DeadExchange = "ticket.dead";
    public const string DeadQueue = "ticket.payment.authorize-requested.dead.v1";
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
                    Uri = new Uri(uri), AutomaticRecoveryEnabled = false,
                    RequestedHeartbeat = TimeSpan.FromSeconds(15)
                };
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true,
                    autoDelete: false, arguments: null, cancellationToken: stoppingToken);
                await channel.ExchangeDeclareAsync(DeadExchange, ExchangeType.Direct, durable: true,
                    autoDelete: false, arguments: null, cancellationToken: stoppingToken);
                await channel.QueueDeclareAsync(DeadQueue, durable: true, exclusive: false,
                    autoDelete: false, arguments: null, cancellationToken: stoppingToken);
                await channel.QueueBindAsync(DeadQueue, DeadExchange, RoutingKey,
                    arguments: null, cancellationToken: stoppingToken);
                var arguments = new Dictionary<string, object?>
                {
                    ["x-dead-letter-exchange"] = DeadExchange,
                    ["x-dead-letter-routing-key"] = RoutingKey
                };
                await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false,
                    autoDelete: false, arguments: arguments, cancellationToken: stoppingToken);
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
                        if (delivery.BasicProperties.Type != nameof(PaymentAuthorizationRequestedV1))
                            throw new InvalidDataException("Unexpected Payment message type.");
                        var command = JsonSerializer.Deserialize<PaymentAuthorizationRequestedV1>(
                            delivery.Body.ToArray(), JsonOptions)
                            ?? throw new InvalidDataException("Empty Payment command.");
                        if (!Guid.TryParse(delivery.BasicProperties.MessageId, out var headerId) ||
                            headerId != command.MessageId)
                            throw new InvalidDataException("Payment MessageId does not match header.");
                        await using var scope = scopes.CreateAsyncScope();
                        var processor = scope.ServiceProvider.GetRequiredService<PaymentAuthorizationProcessor>();
                        await processor.ProcessAsync(command, stoppingToken);
                        await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
                    {
                        logger.LogWarning(ex, "Invalid Payment simulation command; dead-lettering");
                        await channel.BasicNackAsync(delivery.DeliveryTag, false, false, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        // Connection close causes unacknowledged delivery to be retried.
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Payment simulation command failed; retrying");
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
                logger.LogInformation("Payment simulation consumer subscribed to {Queue}", Queue);
                await disconnected.Task.WaitAsync(stoppingToken);
                throw new IOException("Payment AMQP channel disconnected.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Payment AMQP disconnected; reconnecting");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }
    }
}
