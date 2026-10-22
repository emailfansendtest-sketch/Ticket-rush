namespace TicketRush.BuildingBlocks.Contracts.Notification;

/// <summary>
/// Ordering's durable final business outcome. The receiver simulates delivery;
/// this contract does not claim that email/SMS was sent or payment was captured.
/// </summary>
public sealed record CheckoutNotificationRequestedV1(
    Guid MessageId,
    Guid OrderId,
    Guid BuyerId,
    string Outcome,
    string? ReasonCode,
    DateTimeOffset OccurredAtUtc);
