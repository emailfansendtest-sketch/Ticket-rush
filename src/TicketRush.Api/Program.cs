using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using TicketRush.BuildingBlocks.Contracts.Security;
using TicketRush.Api.Endpoints;
using TicketRush.Catalog;
using TicketRush.Infrastructure;
using TicketRush.Inventory;
using TicketRush.Ordering;
using TicketRush.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<BookingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("BookingDb")
        ?? throw new InvalidOperationException("BookingDb connection string is missing.")));

// Catalog is independently hosted. Never open its database from the Booking API.
builder.Services.AddHttpClient<IEventCatalog, HttpEventCatalog>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Catalog:BaseUrl"]
        ?? "http://localhost:8090/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
// The legacy synchronous /orders path still needs a shared local transaction.
// The public Inventory read/hold operations are proxied to the independent host
// when configured. Without Inventory:BaseUrl (existing in-process tests), retain
// the Phase-1 implementation to avoid changing existing test contract behavior.
builder.Services.AddScoped<InventoryService>();
builder.Services.AddScoped<IInventoryCheckout>(sp => sp.GetRequiredService<InventoryService>());
if (builder.Configuration["Inventory:BaseUrl"] is { Length: > 0 } inventoryBaseUrl)
{
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddHttpClient<IInventoryService, HttpInventoryService>(client =>
    {
        client.BaseAddress = new Uri(inventoryBaseUrl);
        client.Timeout = TimeSpan.FromSeconds(15);
    });
}
else
{
    builder.Services.AddScoped<IInventoryService>(sp => sp.GetRequiredService<InventoryService>());
}
builder.Services.AddScoped<IOrderingService, OrderingService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<HoldCheckoutProof>(services => new HoldCheckoutProof(
    services.GetRequiredService<IConfiguration>()["HoldProof:KeyBase64"]));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
// Configure after WebApplicationFactory's app-configuration overrides have run.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IConfiguration>((options, config) =>
    {
        options.Authority = config["Auth:Authority"];
        options.Audience = config["Auth:Audience"];
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "sub";
        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.ValidateAudience = true;
        options.RequireHttpsMetadata = !bool.TryParse(
            config["Auth:AllowInsecureHttpMetadata"], out bool insecure) || !insecure;
    });
builder.Services.AddAuthorization();
builder.Services.AddHealthChecks();

var app = builder.Build();
// Validate the final configuration at startup, including test-host overrides.
_ = app.Services.GetRequiredService<HoldCheckoutProof>();
// Validate configuration after test-host overrides, not during service registration.
var authority = app.Configuration["Auth:Authority"];
var audience = app.Configuration["Auth:Audience"];
if (!Uri.TryCreate(authority, UriKind.Absolute, out var issuerUri) ||
    issuerUri is null ||
    (issuerUri.Scheme != Uri.UriSchemeHttp && issuerUri.Scheme != Uri.UriSchemeHttps) ||
    string.IsNullOrWhiteSpace(audience))
    throw new InvalidOperationException("Auth:Authority and Auth:Audience must be configured.");
if (issuerUri.Scheme == Uri.UriSchemeHttp &&
    (!app.Environment.IsDevelopment() ||
     !bool.TryParse(app.Configuration["Auth:AllowInsecureHttpMetadata"], out bool allowHttp) ||
     !allowHttp))
    throw new InvalidOperationException("HTTP OIDC metadata is permitted only in Development with an explicit opt-in.");
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health/live");

app.MapCatalogEndpoints();
app.MapInventoryEndpoints();
app.MapOrderingEndpoints();

app.Run();

// Buyer JWT authentication is provided by the configured OIDC issuer.
// This local HTTP setup is never suitable for a public deployment.
public partial class Program { }
