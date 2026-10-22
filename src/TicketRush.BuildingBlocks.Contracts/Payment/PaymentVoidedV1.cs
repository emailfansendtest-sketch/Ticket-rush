namespace TicketRush.BuildingBlocks.Contracts.Payment;

/// <summary>Acknowledges a simulated authorization void; no refund or actual transfer.</summary>
public sealed record PaymentVoidedV1(
    Guid MessageId, Guid OrderId, Guid VoidCommandId,
    Guid AuthorizationCommandId, DateTimeOffset VoidedAtUtc);
