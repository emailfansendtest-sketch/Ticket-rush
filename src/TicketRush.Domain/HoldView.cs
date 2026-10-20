namespace TicketRush.Domain;

public sealed record HoldView( Guid Id, Guid EventId, Guid BuyerId, string[] SeatCodes,
    DateTimeOffset ExpiresAtUtc, string Status );
