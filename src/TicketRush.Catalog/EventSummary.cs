namespace TicketRush.Catalog;

/// <summary>Catalog read model, not an EF entity.</summary>
public sealed record EventSummary(Guid Id, string Name, DateTimeOffset StartsAtUtc);
