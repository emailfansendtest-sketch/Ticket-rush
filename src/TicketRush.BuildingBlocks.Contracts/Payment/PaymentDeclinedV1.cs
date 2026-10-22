namespace TicketRush.BuildingBlocks.Contracts.Payment;

public sealed record PaymentDeclinedV1(Guid MessageId, Guid OrderId,
    string ReasonCode, DateTimeOffset DeclinedAtUtc);
