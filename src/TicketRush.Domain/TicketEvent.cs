namespace TicketRush.Domain;

public sealed class TicketEvent
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset StartsAtUtc { get; set; }
}
