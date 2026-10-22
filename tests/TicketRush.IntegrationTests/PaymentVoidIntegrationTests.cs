using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketRush.BuildingBlocks.Contracts.Payment;
using TicketRush.Payment.Persistence;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class PaymentVoidIntegrationTests
{
    private static async Task<DbContextOptions<PaymentDbContext>> OpenAsync(PostgreSqlContainer pg)
    {
        await pg.StartAsync();
        var options = new DbContextOptionsBuilder<PaymentDbContext>()
            .UseNpgsql(pg.GetConnectionString()).Options;
        await using var db = new PaymentDbContext(options);
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        return options;
    }
    private static PaymentAuthorizationRequestedV1 Authorization(bool decline = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1000, "USD", decline,
            new DateTimeOffset(2026, 10, 22, 3, 0, 0, TimeSpan.Zero));
    private static PaymentVoidRequestedV1 Void(PaymentAuthorizationRequestedV1 a) =>
        new(Guid.NewGuid(), a.OrderId, a.BuyerId, a.MessageId, a.RequestedAtUtc);

    [Fact]
    public async Task Authorized_payment_is_voided_atomically_and_replay_rearms_same_result()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await OpenAsync(pg);
        var auth = Authorization();
        var command = Void(auth);
        await using (var db = new PaymentDbContext(options))
        {
            await new PaymentAuthorizationProcessor(db, TimeProvider.System)
                .ProcessAsync(auth, CancellationToken.None);
            Assert.Equal(PaymentProcessOutcome.Processed,
                await new PaymentVoidProcessor(db, TimeProvider.System)
                    .ProcessAsync(command, CancellationToken.None));
        }
        Guid resultId;
        await using (var db = new PaymentDbContext(options))
        {
            Assert.NotNull((await db.Receipts.SingleAsync()).VoidedAtUtc);
            Assert.Single(await db.VoidReceipts.ToArrayAsync());
            Assert.Equal(2, await db.Outbox.CountAsync());
            var result = await db.Outbox.SingleAsync(x => x.MessageType == nameof(PaymentVoidedV1));
            resultId = result.MessageId;
            var stamp = DateTimeOffset.UtcNow;
            result.PublishedAtUtc = stamp.AddTicks(-(stamp.Ticks % 10));
            await db.SaveChangesAsync();
        }
        await using (var db = new PaymentDbContext(options))
        {
            Assert.Equal(PaymentProcessOutcome.Duplicate,
                await new PaymentVoidProcessor(db, TimeProvider.System)
                    .ProcessAsync(command, CancellationToken.None));
        }
        await using var verify = new PaymentDbContext(options);
        var outbox = await verify.Outbox.SingleAsync(x => x.MessageType == nameof(PaymentVoidedV1));
        Assert.Equal(resultId, outbox.MessageId);
        Assert.Null(outbox.PublishedAtUtc);
        Assert.Equal(2, await verify.Outbox.CountAsync());
    }

    [Fact]
    public async Task Declined_and_mismatched_authorizations_cannot_be_voided()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await OpenAsync(pg);
        var auth = Authorization(decline: true);
        await using var db = new PaymentDbContext(options);
        await new PaymentAuthorizationProcessor(db, TimeProvider.System)
            .ProcessAsync(auth, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PaymentVoidProcessor(db, TimeProvider.System).ProcessAsync(Void(auth), CancellationToken.None));
        Assert.Empty(await db.VoidReceipts.AsNoTracking().ToArrayAsync());
        Assert.Single(await db.Outbox.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Wrong_identity_and_conflicting_replay_are_rejected()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await OpenAsync(pg);
        var auth = Authorization();
        var command = Void(auth);
        await using (var db = new PaymentDbContext(options))
            await new PaymentAuthorizationProcessor(db, TimeProvider.System)
                .ProcessAsync(auth, CancellationToken.None);
        await using (var db = new PaymentDbContext(options))
        {
            var processor = new PaymentVoidProcessor(db, TimeProvider.System);
            await Assert.ThrowsAsync<InvalidDataException>(() => processor.ProcessAsync(
                command with { BuyerId = Guid.NewGuid() }, CancellationToken.None));
            Assert.Equal(PaymentProcessOutcome.Processed,
                await processor.ProcessAsync(command, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() => processor.ProcessAsync(
                command with { MessageId = Guid.NewGuid() }, CancellationToken.None));
        }
        await using var verify = new PaymentDbContext(options);
        Assert.Single(await verify.VoidReceipts.ToArrayAsync());
    }

    [Fact]
    public async Task Void_outbox_failure_rolls_back_receipt_and_voided_state()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await OpenAsync(pg);
        var auth = Authorization();
        await using (var db = new PaymentDbContext(options))
        {
            await new PaymentAuthorizationProcessor(db, TimeProvider.System)
                .ProcessAsync(auth, CancellationToken.None);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION fail_void_outbox() RETURNS trigger AS $$
                BEGIN IF NEW.message_type = 'PaymentVoidedV1' THEN
                  RAISE EXCEPTION 'Injected void result failure';
                END IF; RETURN NEW; END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER fail_void_outbox BEFORE INSERT ON payment_outbox
                  FOR EACH ROW EXECUTE FUNCTION fail_void_outbox();
                """);
        }
        await using (var db = new PaymentDbContext(options))
            await Assert.ThrowsAnyAsync<Exception>(() =>
                new PaymentVoidProcessor(db, TimeProvider.System)
                    .ProcessAsync(Void(auth), CancellationToken.None));
        await using var verify = new PaymentDbContext(options);
        Assert.Null((await verify.Receipts.SingleAsync()).VoidedAtUtc);
        Assert.Empty(await verify.VoidReceipts.ToArrayAsync());
        Assert.Single(await verify.Outbox.ToArrayAsync());
    }
}
