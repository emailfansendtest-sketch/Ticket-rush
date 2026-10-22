using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using TicketRush.Domain;
using TicketRush.Infrastructure;
using Xunit;

namespace TicketRush.IntegrationTests;

/// <summary>Exercises the new independently hosted Inventory routes with real PostgreSQL.</summary>
public sealed class InventoryApiIntegrationTests
{
    [Fact]
    public async Task Inventory_api_requires_valid_buyer_and_preserves_idempotency()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();
        string connection = postgres.GetConnectionString();
        var options = new DbContextOptionsBuilder<BookingDbContext>().UseNpgsql(connection).Options;
        await TestDatabaseInitializer.InitializeAsync(options);
        using var factory = new InventoryFactory(connection);
        using var client = factory.CreateClient();
        Guid buyer = Guid.NewGuid();
        var request = new { buyerId = buyer, idempotencyKey = "inventory-api-1", seatCodes = new[] { "A-067" } };
        string url = $"/events/{DemoDataSeeder.DemoEventId:D}/holds";
        using var anonymous = await client.PostAsJsonAsync(url, request);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        TestBuyerAuthentication.As(client, Guid.NewGuid());
        using var forged = await client.PostAsJsonAsync(url, request);
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        TestBuyerAuthentication.As(client, buyer);
        using var created = await client.PostAsJsonAsync(url, request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var hold = await created.Content.ReadFromJsonAsync<HoldView>();
        Assert.NotNull(hold);
        Assert.Equal(buyer, hold.BuyerId);
        using var replay = await client.PostAsJsonAsync(url, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await replay.Content.ReadFromJsonAsync<HoldView>();
        Assert.NotNull(replayed);
        Assert.Equal(hold.Id, replayed.Id);
        Assert.Equal(hold.ExpiresAtUtc, replayed.ExpiresAtUtc);
        Assert.Equal(hold.SeatCodes, replayed.SeatCodes);
        using var read = await client.GetAsync($"/holds/{hold.Id:D}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        TestBuyerAuthentication.As(client, Guid.NewGuid());
        using var notOwned = await client.GetAsync($"/holds/{hold.Id:D}");
        Assert.Equal(HttpStatusCode.NotFound, notOwned.StatusCode);
        await using var db = new BookingDbContext(options);
        Assert.Equal(1, await db.Holds.CountAsync());
    }

    private sealed class InventoryFactory(string connectionString) :
        WebApplicationFactory<TicketRush.Inventory.Api.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseContentRoot(FindContentRoot());
            builder.ConfigureTestServices(TestBuyerAuthentication.Configure);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:InventoryDb"] = connectionString,
                    ["Auth:Authority"] = "http://keycloak:8080/realms/ticketrush",
                    ["Auth:Audience"] = "ticketrush-apis",
                    ["Auth:AllowInsecureHttpMetadata"] = "true"
                }));
        }

        private static string FindContentRoot()
        {
            for (DirectoryInfo? folder = new(AppContext.BaseDirectory);
                 folder is not null; folder = folder.Parent)
            {
                if (!File.Exists(Path.Combine(folder.FullName, "TicketRush.slnx"))) continue;
                string path = Path.Combine(folder.FullName, "src", "TicketRush.Inventory.Api");
                if (File.Exists(Path.Combine(path, "TicketRush.Inventory.Api.csproj"))) return path;
            }
            throw new InvalidOperationException("Could not find Inventory API project content root.");
        }
    }
}
