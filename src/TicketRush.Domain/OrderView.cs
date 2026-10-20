namespace TicketRush.Domain;
public sealed record OrderView( Guid Id, Guid HoldId, Guid BuyerId, DateTimeOffset CreatedAtUtc );
