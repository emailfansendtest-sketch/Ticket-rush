using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;
using TicketRush.Catalog;
using TicketRush.Catalog.Storage;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class CatalogServiceHttpTests
{
    [Fact]
    public async Task Catalog_host_and_booking_gateway_work_without_booking_database()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();
        string catalogConnection = postgres.GetConnectionString();
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(catalogConnection).Options;
        await using (var setup = new CatalogDbContext(options))
        {
            await setup.Database.MigrateAsync();
            await CatalogDemoDataSeeder.SeedAsync(setup);
        }

        using var catalogHost = new CatalogApiFactory(catalogConnection);
        using var catalogClient = catalogHost.CreateClient();
        using var direct = await catalogClient.GetAsync("/events");
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
        var catalogEvents = Assert.IsType<EventSummary[]>(
            await direct.Content.ReadFromJsonAsync<EventSummary[]>());
        Assert.Contains(catalogEvents, e => e.Id == CatalogDemoDataSeeder.DemoEventId);

        // Not an accessible booking database. /events must use Catalog HTTP instead.
        using var bookingHost = new BookingApiGatewayFactory(catalogHost);
        using var bookingClient = bookingHost.CreateClient();
        using var forwarded = await bookingClient.GetAsync("/events");
        Assert.Equal(HttpStatusCode.OK, forwarded.StatusCode);
        Assert.Equal(catalogEvents, Assert.IsType<EventSummary[]>(
            await forwarded.Content.ReadFromJsonAsync<EventSummary[]>()));
    }

    [Fact]
    public async Task Booking_gateway_returns_503_when_catalog_transport_fails()
    {
        using var bookingHost = new BookingApiUnavailableCatalogFactory();
        using var client = bookingHost.CreateClient();
        using var response = await client.GetAsync("/events");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Catalog_database_migration_can_be_reapplied_without_duplicating_event()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using var db = new CatalogDbContext(options);
        await db.Database.MigrateAsync();
        await CatalogDemoDataSeeder.SeedAsync(db);
        await db.Database.MigrateAsync();
        await CatalogDemoDataSeeder.SeedAsync(db);
        Assert.Single(await db.Events.AsNoTracking().ToArrayAsync());
        Assert.Single(await db.Database.GetAppliedMigrationsAsync());
    }

    private sealed class CatalogApiFactory(string connectionString) :
        WebApplicationFactory<TicketRush.Catalog.Api.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(FindContentRoot("TicketRush.Catalog.Api"));
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:CatalogDb"] = connectionString
                }));
        }
    }

    private sealed class BookingApiGatewayFactory(CatalogApiFactory catalogHost) :
        WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(FindContentRoot("TicketRush.Api"));
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:BookingDb"] =
                        "Host=127.0.0.1;Port=1;Database=not_running;Username=none;Password=none",
                    ["HoldProof:KeyBase64"] = TestHoldProofKey.Value,
                    ["Auth:Authority"] = "http://keycloak:8080/realms/ticketrush",
                    ["Auth:Audience"] = "ticketrush-apis",
                    ["Auth:AllowInsecureHttpMetadata"] = "true"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEventCatalog>();
                services.AddScoped<IEventCatalog>(_ => new HttpEventCatalog(
                    new HttpClient(catalogHost.Server.CreateHandler())
                    {
                        BaseAddress = new Uri("http://catalog-test/")
                    }));
            });
        }
    }

    private sealed class BookingApiUnavailableCatalogFactory :
        WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(FindContentRoot("TicketRush.Api"));
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:BookingDb"] =
                        "Host=127.0.0.1;Port=1;Database=not_running;Username=none;Password=none",
                    ["HoldProof:KeyBase64"] = TestHoldProofKey.Value,
                    ["Auth:Authority"] = "http://keycloak:8080/realms/ticketrush",
                    ["Auth:Audience"] = "ticketrush-apis",
                    ["Auth:AllowInsecureHttpMetadata"] = "true"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEventCatalog>();
                services.AddScoped<IEventCatalog>(_ => new HttpEventCatalog(
                    new HttpClient(new FailingCatalogHandler())
                    {
                        BaseAddress = new Uri("http://catalog-test/")
                    }));
            });
        }
    }

    private sealed class FailingCatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException("Simulated Catalog connection failure."));
    }

    private static string FindContentRoot(string projectName)
    {
        for (DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "TicketRush.slnx")))
                continue;
            string project = Path.Combine(directory.FullName, "src", projectName);
            if (File.Exists(Path.Combine(project, projectName + ".csproj")))
                return project;
        }
        throw new InvalidOperationException($"Could not find {projectName} content root.");
    }
}
