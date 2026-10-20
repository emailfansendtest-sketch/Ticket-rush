using TicketRush.Domain;

namespace TicketRush.Inventory;

/// <summary>The public seat projection retains the original API's three JSON fields.</summary>
public sealed record SeatSummary(
    string SeatCode,
    SeatStatus Status,
    DateTimeOffset? HeldUntilUtc);
