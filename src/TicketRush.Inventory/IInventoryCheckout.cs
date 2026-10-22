namespace TicketRush.Inventory;

/// <summary>
/// A checkout-specific port within the modular monolith. Implementations must use
/// the caller's InventoryDbContext transaction; they may not save or commit.
/// The result describes a prepared (tracked) seat/hold transition.
/// </summary>
public interface IInventoryCheckout
{
    Task<CheckoutPreparation> PrepareCheckoutAsync(
        Guid holdId, Guid buyerId, Guid orderId, CancellationToken ct);
}

public enum CheckoutPreparationStatus
{
    Ready,
    NotFound,
    AlreadyConfirmed,
    AlreadyCheckedOut,
    Expired
}

/// <summary>
/// CheckedAtUtc is present only for Ready; it is captured after both row locks
/// are acquired, and becomes the order creation timestamp.
/// </summary>
public sealed record CheckoutPreparation(
    CheckoutPreparationStatus Status,
    DateTimeOffset? CheckedAtUtc = null);
