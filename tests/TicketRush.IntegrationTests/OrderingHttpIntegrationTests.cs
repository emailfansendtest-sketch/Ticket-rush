using System.Net;
using TicketRush.BuildingBlocks.Contracts.Security;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketRush.BuildingBlocks.Contracts.Checkout;
using TicketRush.Ordering.Persistence;
using Xunit;

namespace TicketRush.IntegrationTests;

/// <summary>Real Ordering HTTP pipeline and migrations, backed by Testcontainers PostgreSQL.</summary>
public sealed class OrderingHttpIntegrationTests
{
    private static readonly Guid Hold = Guid.Parse("d5f1465e-6f1e-4a2f-a5a6-00986fc55d01");
    private static string Proof(Guid buyer, Guid hold) =>
        new HoldCheckoutProof(TestHoldProofKey.Value).Issue(hold, buyer,
            DateTimeOffset.UtcNow.AddMinutes(5));

    private static async Task<DbContextOptions<OrderingDbContext>> StartDbAsync(
        PostgreSqlContainer postgres)
    {
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<OrderingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using var db = new OrderingDbContext(options);
        await db.Database.MigrateAsync();
        return options;
    }

    private static OrderingApiFactory CreateFactory(PostgreSqlContainer postgres) =>
        new(postgres.GetConnectionString());

    private sealed class OrderingApiFactory(string connectionString) :
        WebApplicationFactory<TicketRush.Ordering.Api.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(FindContentRoot());
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(TestBuyerAuthentication.Configure);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:OrderingDb"] = connectionString,
                    ["HoldProof:KeyBase64"] = TestHoldProofKey.Value,
                    ["Auth:Authority"] = "http://keycloak:8080/realms/ticketrush",
                    ["Auth:Audience"] = "ticketrush-apis",
                    ["Auth:AllowInsecureHttpMetadata"] = "true"
                }));
        }

        private static string FindContentRoot()
        {
            for (DirectoryInfo? folder = new DirectoryInfo(AppContext.BaseDirectory);
                 folder is not null; folder = folder.Parent)
            {
                if (!File.Exists(Path.Combine(folder.FullName, "TicketRush.slnx")))
                    continue;
                var project = Path.Combine(folder.FullName, "src", "TicketRush.Ordering.Api");
                if (File.Exists(Path.Combine(project, "TicketRush.Ordering.Api.csproj")))
                    return project;
            }
            throw new InvalidOperationException("Could not locate TicketRush.Ordering.Api project root.");
        }
    }

    [Fact]
    public async Task Http_checkout_requires_authentication_and_rejects_subject_spoofing()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await StartDbAsync(postgres);
        using var factory = CreateFactory(postgres);
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        var request = new { holdId = Hold, buyerId = owner,
            idempotencyKey = "auth-test", holdProof = Proof(owner, Hold) };
        using var anonymous = await client.PostAsJsonAsync("/checkouts", request);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        TestBuyerAuthentication.As(client, attacker);
        using var forged = await client.PostAsJsonAsync("/checkouts", request);
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        TestBuyerAuthentication.As(client, owner);
        using var admitted = await client.PostAsJsonAsync("/checkouts", request);
        Assert.Equal(HttpStatusCode.Accepted, admitted.StatusCode);
        await using var db = new OrderingDbContext(options);
        Assert.Single(await db.OrderIntents.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Http_intake_returns_202_location_then_200_replay_without_duplicate_outbox()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await StartDbAsync(postgres);
        using var factory = CreateFactory(postgres);
        using var client = factory.CreateClient();
        var buyer = Guid.NewGuid();
        TestBuyerAuthentication.As(client, buyer);
        var request = new { holdId = Hold, buyerId = buyer, idempotencyKey = "http-intake-1", holdProof = Proof(buyer, Hold) };

        using var health = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var first = await client.PostAsJsonAsync("/checkouts", request);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var accepted = Assert.IsType<CheckoutStatusView>(
            await first.Content.ReadFromJsonAsync<CheckoutStatusView>());
        Assert.Equal(Hold, accepted.HoldId);
        Assert.Equal("Pending", accepted.State);
        Assert.Null(accepted.FailureReasonCode);
        Assert.Equal($"/checkouts/{accepted.OrderId:D}?buyerId={buyer:D}",
            first.Headers.Location?.OriginalString);

        using var get = await client.GetAsync(first.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("no-store", get.Headers.CacheControl?.ToString());
        Assert.Equal(accepted, await get.Content.ReadFromJsonAsync<CheckoutStatusView>());

        using var replay = await client.PostAsJsonAsync("/checkouts", request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(accepted, await replay.Content.ReadFromJsonAsync<CheckoutStatusView>());

        await using var verify = new OrderingDbContext(options);
        Assert.Single(await verify.OrderIntents.AsNoTracking().ToArrayAsync());
        var fact = Assert.Single(await verify.OutboxMessages.AsNoTracking().ToArrayAsync());
        Assert.Equal(nameof(CheckoutRequestedV1), fact.MessageType);
        Assert.Equal(accepted.OrderId, fact.OrderId);
    }

    [Fact]
    public async Task Http_intake_rejects_invalid_commands_and_conflicting_keys_or_holds()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await StartDbAsync(postgres);
        using var factory = CreateFactory(postgres);
        using var client = factory.CreateClient();
        var buyer = Guid.NewGuid();
        TestBuyerAuthentication.As(client, buyer);
        using var bad1 = await client.PostAsJsonAsync("/checkouts",
            new { holdId = Guid.Empty, buyerId = buyer, idempotencyKey = "valid", holdProof = Proof(buyer, Hold) });
        Assert.Equal(HttpStatusCode.BadRequest, bad1.StatusCode);
        using var bad2 = await client.PostAsJsonAsync("/checkouts",
            new { holdId = Hold, buyerId = buyer, idempotencyKey = " ", holdProof = Proof(buyer, Hold) });
        Assert.Equal(HttpStatusCode.BadRequest, bad2.StatusCode);
        using var bad3 = await client.PostAsJsonAsync("/checkouts",
            new { holdId = Hold, buyerId = buyer, idempotencyKey = new string('a', 101), holdProof = Proof(buyer, Hold) });
        Assert.Equal(HttpStatusCode.BadRequest, bad3.StatusCode);

        using var accepted = await client.PostAsJsonAsync("/checkouts",
            new { holdId = Hold, buyerId = buyer, idempotencyKey = "key-1", holdProof = Proof(buyer, Hold) });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        var changedHold = Guid.NewGuid();
        using var reusedKey = await client.PostAsJsonAsync("/checkouts",
            new { holdId = changedHold, buyerId = buyer, idempotencyKey = "key-1", holdProof = Proof(buyer, changedHold) });
        Assert.Equal(HttpStatusCode.Conflict, reusedKey.StatusCode);
        using var reusedHold = await client.PostAsJsonAsync("/checkouts",
            new { holdId = Hold, buyerId = buyer, idempotencyKey = "key-2", holdProof = Proof(buyer, Hold) });
        Assert.Equal(HttpStatusCode.Conflict, reusedHold.StatusCode);

        await using var verify = new OrderingDbContext(options);
        Assert.Single(await verify.OrderIntents.AsNoTracking().ToArrayAsync());
        Assert.Single(await verify.OutboxMessages.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Http_status_requires_matching_buyer_and_hides_unknown_orders()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        _ = await StartDbAsync(postgres);
        using var factory = CreateFactory(postgres);
        using var client = factory.CreateClient();
        var buyer = Guid.NewGuid();
        TestBuyerAuthentication.As(client, buyer);
        using var created = await client.PostAsJsonAsync("/checkouts",
            new { holdId = Hold, buyerId = buyer, idempotencyKey = "status-security", holdProof = Proof(buyer, Hold) });
        var receipt = Assert.IsType<CheckoutStatusView>(
            await created.Content.ReadFromJsonAsync<CheckoutStatusView>());

        using var missingBuyer = await client.GetAsync($"/checkouts/{receipt.OrderId:D}");
        Assert.Equal(HttpStatusCode.OK, missingBuyer.StatusCode);
        using var emptyBuyer = await client.GetAsync($"/checkouts/{receipt.OrderId:D}?buyerId={Guid.Empty:D}");
        Assert.Equal(HttpStatusCode.NotFound, emptyBuyer.StatusCode);
        using var wrongBuyer = await client.GetAsync($"/checkouts/{receipt.OrderId:D}?buyerId={Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, wrongBuyer.StatusCode);
        using var unknown = await client.GetAsync($"/checkouts/{Guid.NewGuid():D}?buyerId={buyer:D}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Http_status_reflects_saga_terminal_result_and_idempotent_replay(bool committed)
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await StartDbAsync(postgres);
        using var factory = CreateFactory(postgres);
        using var client = factory.CreateClient();
        var buyer = Guid.NewGuid();
        TestBuyerAuthentication.As(client, buyer);
        var request = new { holdId = Hold, buyerId = buyer, idempotencyKey = "status-terminal", holdProof = Proof(buyer, Hold) };
        using var created = await client.PostAsJsonAsync("/checkouts", request);
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var receipt = Assert.IsType<CheckoutStatusView>(
            await created.Content.ReadFromJsonAsync<CheckoutStatusView>());

        // Advance the real Ordering saga processor, not just raw SQL fields.
        await using (var db = new OrderingDbContext(options))
        {
            var fact = await db.OutboxMessages.SingleAsync(x => x.OrderId == receipt.OrderId);
            var checkout = JsonSerializer.Deserialize<CheckoutRequestedV1>(fact.PayloadJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(checkout);
            var saga = new OrderingSagaProcessor(db, TimeProvider.System);
            Assert.Equal(SagaProcessOutcome.Processed,
                await saga.ScheduleInventoryAsync(checkout, CancellationToken.None));
            if (committed)
            {
                Assert.Equal(SagaProcessOutcome.Processed,
                    await saga.RecordResultAsync(new InventorySaleCommittedV1(
                        Guid.NewGuid(), receipt.OrderId, Hold, DateTimeOffset.UtcNow), CancellationToken.None));
            }
            else
            {
                Assert.Equal(SagaProcessOutcome.Processed,
                    await saga.RecordResultAsync(new InventorySaleRejectedV1(
                        Guid.NewGuid(), receipt.OrderId, Hold, "HOLD_EXPIRED", DateTimeOffset.UtcNow), CancellationToken.None));
            }
        }

        using var fetched = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        var status = Assert.IsType<CheckoutStatusView>(
            await fetched.Content.ReadFromJsonAsync<CheckoutStatusView>());
        Assert.Equal(committed ? "Completed" : "Failed", status.State);
        Assert.Equal(committed ? null : "HOLD_EXPIRED", status.FailureReasonCode);

        using var replay = await client.PostAsJsonAsync("/checkouts", request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(status, await replay.Content.ReadFromJsonAsync<CheckoutStatusView>());
    }

    [Fact]
    public async Task Http_checkout_denies_missing_wrong_buyer_and_tampered_hold_proofs_without_side_effects()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await StartDbAsync(postgres);
        using var factory = CreateFactory(postgres);
        using var client = factory.CreateClient();
        var legitimateBuyer = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        var proof = Proof(legitimateBuyer, Hold);
        TestBuyerAuthentication.As(client, legitimateBuyer);

        using var missing = await client.PostAsJsonAsync("/checkouts",
            new { holdId = Hold, buyerId = attacker, idempotencyKey = "claim-1" });
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        using var forgedBuyer = await client.PostAsJsonAsync("/checkouts",
            new { holdId = Hold, buyerId = attacker, idempotencyKey = "claim-2", holdProof = proof });
        Assert.Equal(HttpStatusCode.Forbidden, forgedBuyer.StatusCode);
        using var forgedHold = await client.PostAsJsonAsync("/checkouts",
            new { holdId = Guid.NewGuid(), buyerId = legitimateBuyer,
                idempotencyKey = "claim-3", holdProof = proof });
        Assert.Equal(HttpStatusCode.Forbidden, forgedHold.StatusCode);
        using var forgedSignature = await client.PostAsJsonAsync("/checkouts",
            new { holdId = Hold, buyerId = legitimateBuyer, idempotencyKey = "claim-4",
                holdProof = proof + "a" });
        Assert.Equal(HttpStatusCode.Forbidden, forgedSignature.StatusCode);

        await using (var verify = new OrderingDbContext(options))
        {
            Assert.Empty(await verify.OrderIntents.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.OutboxMessages.AsNoTracking().ToArrayAsync());
        }

        // An invalid attempt must not lock the hold's unique key for its owner.
        TestBuyerAuthentication.As(client, attacker);
        using var stolen = await client.PostAsJsonAsync("/checkouts",
            new { holdId = Hold, buyerId = legitimateBuyer,
                idempotencyKey = "stolen", holdProof = proof });
        Assert.Equal(HttpStatusCode.Forbidden, stolen.StatusCode);
        TestBuyerAuthentication.As(client, legitimateBuyer);
        using var actual = await client.PostAsJsonAsync("/checkouts",
            new { holdId = Hold, buyerId = legitimateBuyer, idempotencyKey = "owner-checkout", holdProof = proof });
        Assert.Equal(HttpStatusCode.Accepted, actual.StatusCode);
    }

    [Fact]
    public async Task Http_checkout_denies_expired_proof_before_admission()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await StartDbAsync(postgres);
        using var factory = CreateFactory(postgres);
        using var client = factory.CreateClient();
        var buyer = Guid.NewGuid();
        TestBuyerAuthentication.As(client, buyer);
        var expired = new HoldCheckoutProof(TestHoldProofKey.Value).Issue(
            Hold, buyer, DateTimeOffset.UtcNow.AddSeconds(-1));
        using var response = await client.PostAsJsonAsync("/checkouts",
            new { holdId = Hold, buyerId = buyer, idempotencyKey = "expired", holdProof = expired });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var verify = new OrderingDbContext(options);
        Assert.Empty(await verify.OrderIntents.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Http_concurrent_identical_requests_admit_only_one_checkout()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await StartDbAsync(postgres);
        using var factory = CreateFactory(postgres);
        using var client = factory.CreateClient();
        var buyer = Guid.NewGuid();
        TestBuyerAuthentication.As(client, buyer);
        var request = new { holdId = Hold, buyerId = buyer, idempotencyKey = "concurrent-http", holdProof = Proof(buyer, Hold) };
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> SubmitAsync()
        {
            await start.Task;
            return await client.PostAsJsonAsync("/checkouts", request);
        }
        var taskA = SubmitAsync();
        var taskB = SubmitAsync();
        start.SetResult(true);
        var responses = await Task.WhenAll(taskA, taskB);
        try
        {
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Accepted);
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
            var states = await Task.WhenAll(responses.Select(async response =>
                await response.Content.ReadFromJsonAsync<CheckoutStatusView>()));
            var firstState = states[ 0 ];
            var secondState = states[ 1 ];

            Assert.NotNull( firstState );
            Assert.NotNull(secondState);
            Assert.Equal(firstState.OrderId, secondState.OrderId);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
        await using var db = new OrderingDbContext(options);
        Assert.Single(await db.OrderIntents.AsNoTracking().ToArrayAsync());
        Assert.Single(await db.OutboxMessages.AsNoTracking().ToArrayAsync());
    }
}
