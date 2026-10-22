using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using TicketRush.Infrastructure;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class LegacyCheckoutRetirementTests
{
    [Fact]
    public async Task Normal_gateway_exposes_no_booking_database_and_retires_legacy_checkout()
    {
        using var factory = new GatewayOnlyFactory();
        using var client = factory.CreateClient();
        Assert.Null(factory.Services.GetService<DbContextOptions<BookingDbContext>>());

        // Authentication is still required to discover the retired endpoints.
        using var anonymous = await client.PostAsync("/orders", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        TestBuyerAuthentication.As(client, Guid.NewGuid());
        using var retiredPost = await client.PostAsync("/orders", null);
        Assert.Equal(HttpStatusCode.Gone, retiredPost.StatusCode);
        var response = await retiredPost.Content.ReadAsStringAsync();
        Assert.Contains("/checkouts", response, StringComparison.Ordinal);

        using var retiredGet = await client.GetAsync($"/orders/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.Gone, retiredGet.StatusCode);
    }

    private sealed class GatewayOnlyFactory : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseContentRoot(FindApiContentRoot());
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:BookingDb"] = null,
                    ["LegacyBooking:Enabled"] = "false",
                    ["Inventory:BaseUrl"] = "http://inventory-api:8080/",
                    ["HoldProof:KeyBase64"] = TestHoldProofKey.Value,
                    ["Auth:Authority"] = "http://keycloak:8080/realms/ticketrush",
                    ["Auth:Audience"] = "ticketrush-apis",
                    ["Auth:AllowInsecureHttpMetadata"] = "true"
                }));
            builder.ConfigureTestServices(TestBuyerAuthentication.Configure);
        }
    }

    private static string FindApiContentRoot()
    {
        for (DirectoryInfo? folder = new DirectoryInfo(AppContext.BaseDirectory);
            folder is not null; folder = folder.Parent)
        {
            if (!File.Exists(Path.Combine(folder.FullName, "TicketRush.slnx"))) continue;
            string path = Path.Combine(folder.FullName, "src", "TicketRush.Api");
            if (File.Exists(Path.Combine(path, "TicketRush.Api.csproj"))) return path;
        }
        throw new InvalidOperationException("Could not locate TicketRush.Api project.");
    }
}
