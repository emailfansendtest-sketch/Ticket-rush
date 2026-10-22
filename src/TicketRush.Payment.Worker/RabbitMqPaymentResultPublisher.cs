using System.Text;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using TicketRush.Payment.Persistence;
using TicketRush.BuildingBlocks.Contracts.Payment;

namespace TicketRush.Payment.Worker;

public sealed class RabbitMqPaymentResultPublisher(IConfiguration config) : IPaymentResultPublisher
{
    public const string Exchange = "ticket.events";
    public const string Queue = "ticket.ordering.payment-results.v1";
    public const string ApprovedKey = "payment.authorized.v1";
    public const string DeclinedKey = "payment.declined.v1";

    public async Task PublishAsync(PaymentOutboxMessage message, CancellationToken ct)
    {
        var routing = message.MessageType switch
        {
            nameof(PaymentAuthorizedV1) => ApprovedKey,
            nameof(PaymentDeclinedV1) => DeclinedKey,
            _ => throw new InvalidOperationException("Unsupported Payment outbox message type.")
        };
        var uri = config["RabbitMq:Uri"]
            ?? throw new InvalidOperationException("RabbitMq:Uri is missing.");
        var factory = new ConnectionFactory
        {
            Uri = new Uri(uri), AutomaticRecoveryEnabled = false,
            RequestedHeartbeat = TimeSpan.FromSeconds(15)
        };
        await using var connection = await factory.CreateConnectionAsync(ct);
        var options = new CreateChannelOptions(
            publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);
        await using var channel = await connection.CreateChannelAsync(options, ct);
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true,
            autoDelete: false, arguments: null, cancellationToken: ct);
        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false,
            autoDelete: false, arguments: null, cancellationToken: ct);
        await channel.QueueBindAsync(Queue, Exchange, ApprovedKey,
            arguments: null, cancellationToken: ct);
        await channel.QueueBindAsync(Queue, Exchange, DeclinedKey,
            arguments: null, cancellationToken: ct);
        var properties = new BasicProperties
        {
            MessageId = message.MessageId.ToString("D"),
            Type = message.MessageType, ContentType = "application/json", Persistent = true
        };
        await channel.BasicPublishAsync(exchange: Exchange, routingKey: routing,
            mandatory: true, basicProperties: properties,
            body: Encoding.UTF8.GetBytes(message.PayloadJson), cancellationToken: ct);
    }
}
