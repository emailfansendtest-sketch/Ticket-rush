using Microsoft.EntityFrameworkCore;

namespace TicketRush.Ordering.Persistence;

/// <summary>Ordering-owned projection. A CheckoutStatus is not a Booking TicketOrder.</summary>
public sealed record CheckoutStatusView(
    Guid OrderId, Guid HoldId, string State, DateTimeOffset RequestedAtUtc,
    string? FailureReasonCode, int RecoveryAttempts = 0,
    bool RecoveryNeedsAttention = false, string CompensationStatus = "NotRequired");

public sealed class CheckoutStatusReader(OrderingDbContext db)
{
    public async Task<CheckoutStatusView?> GetAsync(Guid orderId, Guid buyerId, CancellationToken ct)
    {
        if (orderId == Guid.Empty || buyerId == Guid.Empty)
            return null;

        // One database statement observes the state and optional rejection reason
        // from a consistent PostgreSQL statement-level snapshot.
        var projection = await (
            from order in db.OrderIntents.AsNoTracking()
            join receipt in db.ResultInbox.AsNoTracking()
                on order.Id equals receipt.OrderId into receipts
            from receipt in receipts.DefaultIfEmpty()
            join payment in db.PaymentInbox.AsNoTracking()
                on order.Id equals payment.OrderId into payments
            from payment in payments.DefaultIfEmpty()
            join voidReceipt in db.VoidInbox.AsNoTracking()
                on order.Id equals voidReceipt.OrderId into voidReceipts
            from voidReceipt in voidReceipts.DefaultIfEmpty()
            where order.Id == orderId && order.BuyerId == buyerId
            select new
            {
                order.Id, order.HoldId, order.State, order.RequestedAtUtc,
                order.RecoveryAttempts, order.RecoveryNeedsAttention,
                RequiresVoid = order.PaymentRequired &&
                    payment != null && payment.MessageType == "PaymentAuthorizedV1" &&
                    receipt != null && receipt.MessageType == "InventorySaleRejectedV1",
                VoidConfirmed = voidReceipt != null,
                FailureReasonCode = receipt == null ?
                    (payment == null ? null : payment.ReasonCode) : receipt.ReasonCode
            }).SingleOrDefaultAsync(ct);

        return projection is null ? null : new CheckoutStatusView(
            projection.Id, projection.HoldId, projection.State.ToString(),
            projection.RequestedAtUtc,
            projection.State == CheckoutState.Failed ? projection.FailureReasonCode : null,
            projection.RecoveryAttempts, projection.RecoveryNeedsAttention,
            !projection.RequiresVoid ? "NotRequired" :
                projection.VoidConfirmed ? "Voided" :
                projection.RecoveryNeedsAttention ? "NeedsAttention" : "Pending");
    }
}
