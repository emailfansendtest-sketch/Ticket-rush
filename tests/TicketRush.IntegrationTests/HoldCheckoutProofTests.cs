using TicketRush.BuildingBlocks.Contracts.Security;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class HoldCheckoutProofTests
{
    [Fact]
    public void Valid_signed_receipt_binds_buyer_and_hold()
    {
        var verifier = new HoldCheckoutProof(TestHoldProofKey.Value);
        var hold = Guid.NewGuid();
        var buyer = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var receipt = verifier.Issue(hold, buyer, now.AddMinutes(10));
        Assert.True(verifier.Verify(receipt, hold, buyer, now));
        Assert.False(verifier.Verify(receipt, Guid.NewGuid(), buyer, now));
        Assert.False(verifier.Verify(receipt, hold, Guid.NewGuid(), now));
        Assert.False(verifier.Verify(receipt, hold, buyer, now.AddMinutes(10)));
    }

    [Fact]
    public void Tampered_or_malformed_receipts_are_rejected()
    {
        var verifier = new HoldCheckoutProof(TestHoldProofKey.Value);
        var hold = Guid.NewGuid();
        var buyer = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var receipt = verifier.Issue(hold, buyer, now.AddMinutes(10));
        Assert.False(verifier.Verify(receipt + "a", hold, buyer, now));
        Assert.False(verifier.Verify("invalid", hold, buyer, now));
        Assert.False(verifier.Verify("", hold, buyer, now));
        Assert.False(verifier.Verify(null, hold, buyer, now));
        Assert.False(verifier.Verify(new string('a', 600), hold, buyer, now));
    }

    [Fact]
    public void Signing_key_must_be_strong_and_isolated()
    {
        Assert.Throws<InvalidOperationException>(() => new HoldCheckoutProof(null));
        Assert.Throws<InvalidOperationException>(() => new HoldCheckoutProof("AQID"));
        var original = new HoldCheckoutProof(TestHoldProofKey.Value);
        var different = new HoldCheckoutProof(Convert.ToBase64String(new byte[32]));
        var buyer = Guid.NewGuid();
        var hold = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Assert.False(different.Verify(original.Issue(hold, buyer, now.AddMinutes(1)), hold, buyer, now));
    }
}
