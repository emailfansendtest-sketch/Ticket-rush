namespace TicketRush.BuildingBlocks.Contracts.Payment;

/// <summary>Sandbox compensation for one authorized (never captured) payment.</summary>
public sealed record PaymentVoidRequestedV1(
    Guid MessageId, Guid OrderId, Guid BuyerId,
    Guid AuthorizationCommandId, DateTimeOffset RequestedAtUtc);
