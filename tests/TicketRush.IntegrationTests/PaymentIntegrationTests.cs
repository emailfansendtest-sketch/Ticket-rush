using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketRush.BuildingBlocks.Contracts.Payment;
using TicketRush.Payment.Persistence;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class PaymentIntegrationTests
{
    private static async Task<DbContextOptions<PaymentDbContext>> StartAsync(PostgreSqlContainer postgres)
    {
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<PaymentDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using var db = new PaymentDbContext(options);
        await db.Database.MigrateAsync();
        return options;
    }

    private static PaymentAuthorizationRequestedV1 NewRequest(bool decline = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1250, "EUR", decline,
            new DateTimeOffset(2026, 10, 22, 2, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Simulation_creates_one_receipt_and_one_matching_outbox(bool decline)
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await StartAsync(postgres);
        var request = NewRequest(decline);
        await using (var db = new PaymentDbContext(options))
        {
            var result = await new PaymentAuthorizationProcessor(db, TimeProvider.System)
                .ProcessAsync(request, CancellationToken.None);
            Assert.Equal(PaymentProcessOutcome.Processed, result);
        }
        await using var verify = new PaymentDbContext(options);
        var receipt = Assert.Single(await verify.Receipts.AsNoTracking().ToListAsync());
        var outbox = Assert.Single(await verify.Outbox.AsNoTracking().ToListAsync());
        Assert.Equal(request.MessageId, receipt.MessageId);
        Assert.Equal(request.OrderId, outbox.OrderId);
        Assert.Equal(receipt.ResultMessageId, outbox.MessageId);
        Assert.Null(outbox.PublishedAtUtc);
        if (decline)
        {
            Assert.Equal(nameof(PaymentDeclinedV1), outbox.MessageType);
            var result = JsonSerializer.Deserialize<PaymentDeclinedV1>(outbox.PayloadJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(result);
            Assert.Equal("SimulatedDecline", result.ReasonCode);
        }
        else
        {
            Assert.Equal(nameof(PaymentAuthorizedV1), outbox.MessageType);
            var result = JsonSerializer.Deserialize<PaymentAuthorizedV1>(outbox.PayloadJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(result);
            Assert.Equal(request.AmountMinor, result.AmountMinor);
            Assert.Equal(request.Currency, result.Currency);
        }
    }

    [Fact]
    public async Task Identical_replay_creates_no_new_rows()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await StartAsync(postgres);
        var request = NewRequest() with { RequestedAtUtc = NewRequest().RequestedAtUtc.AddTicks(1) };
        await using (var db = new PaymentDbContext(options))
        {
            var processor = new PaymentAuthorizationProcessor(db, TimeProvider.System);
            Assert.Equal(PaymentProcessOutcome.Processed,
                await processor.ProcessAsync(request, CancellationToken.None));
            Assert.Equal(PaymentProcessOutcome.Duplicate,
                await processor.ProcessAsync(request, CancellationToken.None));
        }
        await using var verify = new PaymentDbContext(options);
        Assert.Single(await verify.Receipts.ToListAsync());
        Assert.Single(await verify.Outbox.ToListAsync());
    }

    [Fact]
    public async Task Reusing_message_or_order_id_for_a_different_authorization_is_rejected()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await StartAsync(postgres);
        var request = NewRequest();
        await using var db = new PaymentDbContext(options);
        var processor = new PaymentAuthorizationProcessor(db, TimeProvider.System);
        await processor.ProcessAsync(request, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => processor.ProcessAsync(
            request with { BuyerId = Guid.NewGuid() }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => processor.ProcessAsync(
            request with { MessageId = Guid.NewGuid() }, CancellationToken.None));
        Assert.Single(await db.Receipts.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Replaying_confirmed_result_rearms_same_outbox_message()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await StartAsync(postgres);
        var request = NewRequest();
        Guid resultId;
        await using (var db = new PaymentDbContext(options))
        {
            await new PaymentAuthorizationProcessor(db, TimeProvider.System)
                .ProcessAsync(request, CancellationToken.None);
            var result = Assert.Single(await db.Outbox.ToArrayAsync());
            resultId = result.MessageId;
            var publishedAt = DateTimeOffset.UtcNow;
            result.PublishedAtUtc = publishedAt.AddTicks(-(publishedAt.Ticks % 10));
            await db.SaveChangesAsync();
        }
        await using (var db = new PaymentDbContext(options))
        {
            Assert.Equal(PaymentProcessOutcome.Duplicate,
                await new PaymentAuthorizationProcessor(db, TimeProvider.System)
                    .ProcessAsync(request, CancellationToken.None));
        }
        await using var verify = new PaymentDbContext(options);
        var outbox = Assert.Single(await verify.Outbox.AsNoTracking().ToArrayAsync());
        Assert.Equal(resultId, outbox.MessageId);
        Assert.Null(outbox.PublishedAtUtc);
    }

    [Fact]
    public async Task Missing_result_outbox_is_a_persistence_failure_not_a_bad_command()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await StartAsync(postgres);
        var request = NewRequest();
        await using (var db = new PaymentDbContext(options))
        {
            await new PaymentAuthorizationProcessor(db, TimeProvider.System)
                .ProcessAsync(request, CancellationToken.None);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM payment_outbox WHERE order_id = {request.OrderId}
                """);
        }
        await using (var db = new PaymentDbContext(options))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new PaymentAuthorizationProcessor(db, TimeProvider.System)
                    .ProcessAsync(request, CancellationToken.None));
        }
        await using var verify = new PaymentDbContext(options);
        Assert.Single(await verify.Receipts.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.Outbox.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Outbox_insert_failure_rolls_back_receipt()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await StartAsync(postgres);
        await using (var db = new PaymentDbContext(options))
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_payment_outbox() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'Injected Payment outbox failure'; END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_payment_outbox_insert BEFORE INSERT ON payment_outbox
                FOR EACH ROW EXECUTE FUNCTION reject_payment_outbox();
                """);
        }
        await using (var db = new PaymentDbContext(options))
        {
            var request = NewRequest();
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                new PaymentAuthorizationProcessor(db, TimeProvider.System)
                    .ProcessAsync(request, CancellationToken.None));
        }
        await using var verify = new PaymentDbContext(options);
        Assert.Empty(await verify.Receipts.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.Outbox.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Failed_publication_is_deferred_without_losing_original_result()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await StartAsync(postgres);
        await using (var db = new PaymentDbContext(options))
            await new PaymentAuthorizationProcessor(db, TimeProvider.System)
                .ProcessAsync(NewRequest(), CancellationToken.None);
        await using (var db = new PaymentDbContext(options))
        {
            var result = await new PaymentOutboxDispatcher(db, new FailingPublisher(), TimeProvider.System)
                .DispatchNextAsync(CancellationToken.None);
            Assert.Equal(PaymentDispatchOutcome.Deferred, result);
        }
        await using var verify = new PaymentDbContext(options);
        var item = Assert.Single(await verify.Outbox.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, item.PublishAttempts);
        Assert.NotNull(item.NextAttemptAtUtc);
        Assert.Null(item.PublishedAtUtc);
    }

    [Fact]
    public async Task Concurrent_same_command_creates_only_one_simulated_authorization()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await StartAsync(postgres);
        var request = NewRequest();
        await using var first = new PaymentDbContext(options);
        await using var second = new PaymentDbContext(options);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<PaymentProcessOutcome> ExecuteAsync(PaymentDbContext context)
        {
            await start.Task;
            return await new PaymentAuthorizationProcessor(context, TimeProvider.System)
                .ProcessAsync(request, CancellationToken.None);
        }
        var a = ExecuteAsync(first);
        var b = ExecuteAsync(second);
        start.SetResult(true);
        var outcomes = await Task.WhenAll(a, b);
        Assert.Single(outcomes, x => x == PaymentProcessOutcome.Processed);
        Assert.Single(outcomes, x => x == PaymentProcessOutcome.Duplicate);
        await using var verify = new PaymentDbContext(options);
        Assert.Single(await verify.Receipts.AsNoTracking().ToArrayAsync());
        Assert.Single(await verify.Outbox.AsNoTracking().ToArrayAsync());
    }

    private sealed class FailingPublisher : IPaymentResultPublisher
    {
        public Task PublishAsync(PaymentOutboxMessage message, CancellationToken ct) =>
            Task.FromException(new IOException("Simulated RabbitMQ outage"));
    }
}
