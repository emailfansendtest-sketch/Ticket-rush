namespace TicketRush.BuildingBlocks.Contracts.Payment;

/// <summary>Sandbox-only authorization request. Amount is minor units, not trusted checkout pricing.</summary>
public sealed record PaymentAuthorizationRequestedV1(
    Guid MessageId, Guid OrderId, Guid BuyerId, long AmountMinor,
    string Currency, bool SimulateDecline, DateTimeOffset RequestedAtUtc);
