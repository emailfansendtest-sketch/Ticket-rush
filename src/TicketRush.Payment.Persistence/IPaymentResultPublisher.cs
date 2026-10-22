namespace TicketRush.Payment.Persistence;

public interface IPaymentResultPublisher
{
    Task PublishAsync(PaymentOutboxMessage message, CancellationToken ct);
}
