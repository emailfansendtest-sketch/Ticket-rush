namespace TicketRush.BuildingBlocks.Contracts.Checkout;

/// <summary>
/// Fact: Ordering durably accepted a request to check out an existing hold.
/// The message is NOT evidence that payment succeeded or seats were sold.
/// </summary>
public sealed record CheckoutRequestedV1(
    Guid MessageId,
    Guid OrderId,
    Guid HoldId,
    Guid BuyerId,
    DateTimeOffset RequestedAtUtc);
