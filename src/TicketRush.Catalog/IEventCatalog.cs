namespace TicketRush.Catalog;

/// <summary>Public read boundary for event discovery.</summary>
public interface IEventCatalog
{
    Task<IReadOnlyList<EventSummary>> ListEventsAsync(CancellationToken ct);
}
