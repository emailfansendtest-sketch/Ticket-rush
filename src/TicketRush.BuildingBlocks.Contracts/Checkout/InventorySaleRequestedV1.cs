namespace TicketRush.BuildingBlocks.Contracts.Checkout;

/// <summary>
/// Command: attempt to sell the seats of an existing hold for an order.
/// Inventory must validate hold ownership, expiry and prior sale atomically.
/// </summary>
public sealed record InventorySaleRequestedV1(
    Guid MessageId,
    Guid OrderId,
    Guid HoldId,
    Guid BuyerId,
    DateTimeOffset RequestedAtUtc);
