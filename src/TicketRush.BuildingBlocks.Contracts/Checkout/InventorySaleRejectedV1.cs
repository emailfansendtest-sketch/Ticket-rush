namespace TicketRush.BuildingBlocks.Contracts.Checkout;

/// <summary>
/// Fact: Inventory did not commit the sale. ReasonCode is machine-readable;
/// do not rely on its display text for retries or compensation policy.
/// </summary>
public sealed record InventorySaleRejectedV1(
    Guid MessageId,
    Guid OrderId,
    Guid HoldId,
    string ReasonCode,
    DateTimeOffset RejectedAtUtc);
