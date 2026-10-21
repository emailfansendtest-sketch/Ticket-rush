using Microsoft.Extensions.Configuration;
using System.Text;
using RabbitMQ.Client;
using TicketRush.Ordering.Persistence;

namespace TicketRush.Ordering.OutboxPublisher;

/// <summary>
/// Connection-per-attempt keeps recovery simple for this correctness-first
/// stage. Consider pooled channels with explicit reconnection in a measured
/// later optimization. All publishes require tracked broker confirmations.
/// </summary>
public sealed class RabbitMqOutboxPublisher(IConfiguration configuration) : IOutboxPublisher
{
    public const string Exchange = "ticket.events";
    public const string RoutingKey = "checkout.requested.v1";
    public const string Queue = "ticket.ordering.checkout-requested.v1";

    public async Task PublishAsync(OutboxMessage message, CancellationToken ct)
    {
        if (message.MessageType != "CheckoutRequestedV1")
            throw new InvalidOperationException($"Unknown outbox message type: {message.MessageType}");

        var uri = configuration["RabbitMq:Uri"]
            ?? throw new InvalidOperationException("RabbitMq:Uri is required.");
        var factory = new ConnectionFactory
        {
            Uri = new Uri(uri),
            AutomaticRecoveryEnabled = false,
            RequestedHeartbeat = TimeSpan.FromSeconds(15)
        };

        await using var connection = await factory.CreateConnectionAsync(ct);
        var channelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);
        await using var channel = await connection.CreateChannelAsync(channelOptions, ct);

        await channel.ExchangeDeclareAsync(
            exchange: Exchange, type: ExchangeType.Direct,
            durable: true, autoDelete: false, arguments: null, cancellationToken: ct);
        await channel.QueueDeclareAsync(
            queue: Queue, durable: true, exclusive: false,
            autoDelete: false, arguments: null, cancellationToken: ct);
        await channel.QueueBindAsync(
            queue: Queue, exchange: Exchange, routingKey: RoutingKey,
            arguments: null, cancellationToken: ct);

        var properties = new BasicProperties
        {
            MessageId = message.MessageId.ToString("D"),
            Type = message.MessageType,
            ContentType = "application/json",
            Persistent = true // persistent message in a durable queue
        };

        // In RabbitMQ.Client 7 tracked confirmations mean await completes only
        // on broker ACK; mandatory unroutable or NACK causes an exception.
        await channel.BasicPublishAsync(
            exchange: Exchange, routingKey: RoutingKey,
            mandatory: true, basicProperties: properties,
            body: Encoding.UTF8.GetBytes(message.PayloadJson), cancellationToken: ct);
    }
}
