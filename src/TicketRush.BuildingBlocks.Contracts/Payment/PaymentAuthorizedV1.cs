namespace TicketRush.BuildingBlocks.Contracts.Payment;

/// <summary>Simulated authorization only; no money is charged or captured.</summary>
public sealed record PaymentAuthorizedV1(Guid MessageId, Guid OrderId,
    long AmountMinor, string Currency, DateTimeOffset AuthorizedAtUtc);
