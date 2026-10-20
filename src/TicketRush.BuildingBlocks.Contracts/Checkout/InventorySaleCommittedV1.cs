namespace TicketRush.BuildingBlocks.Contracts.Checkout;

/// <summary>
/// Fact: Inventory has durably assigned the hold's seats to OrderId.
/// </summary>
public sealed record InventorySaleCommittedV1(
    Guid MessageId,
    Guid OrderId,
    Guid HoldId,
    DateTimeOffset CommittedAtUtc);
