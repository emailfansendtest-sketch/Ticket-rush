using System.Text;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using TicketRush.Infrastructure;

namespace TicketRush.Inventory.ResultPublisher;

/// <summary>Publishes Inventory result events with mandatory routing and broker confirms.</summary>
public sealed class RabbitMqInventoryResultPublisher(IConfiguration configuration)
    : IInventoryResultPublisher
{
    public const string Exchange = "ticket.events";
    public const string Queue = "ticket.ordering.inventory-results.v1";
    public const string CommittedRoutingKey = "inventory.sale-committed.v1";
    public const string RejectedRoutingKey = "inventory.sale-rejected.v1";

    public async Task PublishAsync(InventoryResultOutbox message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var routingKey = message.MessageType switch
        {
            "InventorySaleCommittedV1" => CommittedRoutingKey,
            "InventorySaleRejectedV1" => RejectedRoutingKey,
            _ => throw new InvalidOperationException(
                $"Unsupported Inventory outbox message type: {message.MessageType}")
        };

        var uri = configuration["RabbitMq:Uri"]
            ?? throw new InvalidOperationException("RabbitMq:Uri is required.");
        var factory = new ConnectionFactory
        {
            Uri = new Uri(uri),
            AutomaticRecoveryEnabled = false,
            RequestedHeartbeat = TimeSpan.FromSeconds(15)
        };
        await using var connection = await factory.CreateConnectionAsync(ct);
        var options = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);
        await using var channel = await connection.CreateChannelAsync(options, ct);

        await channel.ExchangeDeclareAsync(
            exchange: Exchange, type: ExchangeType.Direct,
            durable: true, autoDelete: false, arguments: null, cancellationToken: ct);
        await channel.QueueDeclareAsync(
            queue: Queue, durable: true, exclusive: false,
            autoDelete: false, arguments: null, cancellationToken: ct);
        await channel.QueueBindAsync(
            queue: Queue, exchange: Exchange, routingKey: CommittedRoutingKey,
            arguments: null, cancellationToken: ct);
        await channel.QueueBindAsync(
            queue: Queue, exchange: Exchange, routingKey: RejectedRoutingKey,
            arguments: null, cancellationToken: ct);

        var properties = new BasicProperties
        {
            MessageId = message.MessageId.ToString("D"),
            Type = message.MessageType,
            ContentType = "application/json",
            Persistent = true
        };
        // With both confirmation flags set, BasicPublishAsync awaits the ACK.
        // A mandatory basic.return or broker NACK throws, leaving the outbox pending.
        await channel.BasicPublishAsync(
            exchange: Exchange, routingKey: routingKey,
            mandatory: true, basicProperties: properties,
            body: Encoding.UTF8.GetBytes(message.PayloadJson), cancellationToken: ct);
    }
}
