namespace TicketRush.Ordering.Persistence;

/// <summary>Ordering-owned checkout intent, not the Phase 1 TicketOrder entity.</summary>
public sealed class OrderIntent
{
    public Guid Id { get; set; }
    public Guid HoldId { get; set; }
    public Guid BuyerId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public CheckoutState State { get; set; } = CheckoutState.Pending;
    public DateTimeOffset RequestedAtUtc { get; set; }
    // False for pre-2.3l intents. New HTTP checkouts require sandbox authorization.
    public bool PaymentRequired { get; set; }
    public bool SimulatePaymentDecline { get; set; }
    // Recovery is operational progress, independent of Inventory's business outcome.
    public int RecoveryAttempts { get; set; }
    public DateTimeOffset? NextRecoveryAtUtc { get; set; }
    public bool RecoveryNeedsAttention { get; set; }
}

public enum CheckoutState
{
    Pending = 0,
    Completed = 1,
    Failed = 2
}
