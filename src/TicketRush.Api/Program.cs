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
// Transitional compatibility mode is restricted to local development and legacy tests.
// The normal Booking API is now an HTTP gateway and NEVER opens the Booking database.
// Service registration uses startup configuration. WebApplicationFactory can add
// app settings later, so its legacy test host registers local services explicitly.
bool registerLegacyServices = bool.TryParse(
    builder.Configuration["LegacyBooking:Enabled"], out var enabled) && enabled;
if (registerLegacyServices)
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException(
            "Legacy synchronous checkout is allowed only in Development.");

    builder.Services.AddDbContext<BookingDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("BookingDb")
            ?? throw new InvalidOperationException(
                "Legacy checkout requires ConnectionStrings:BookingDb.")));
    // In legacy mode, reuse the SAME scoped context instance for Inventory and Ordering.
    // Never register a second InventoryDbContext against the legacy database.
    builder.Services.AddScoped<TicketRush.Inventory.Persistence.InventoryDbContext>(
        sp => sp.GetRequiredService<BookingDbContext>());
    builder.Services.AddScoped<InventoryService>();
    builder.Services.AddScoped<IInventoryCheckout>(sp => sp.GetRequiredService<InventoryService>());
    builder.Services.AddScoped<IOrderingService, OrderingService>();
}

// Catalog is independently hosted. Never open its database from the Booking API.
builder.Services.AddHttpClient<IEventCatalog, HttpEventCatalog>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Catalog:BaseUrl"]
        ?? "http://localhost:8090/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
// Inventory operations are independent HTTP calls in the default deployment.
// Only explicit local compatibility mode uses the old in-process InventoryService.
// Always register the Inventory service, even for gateway-only hosts without an
// Inventory URL. Minimal API must see this as DI, never an inferred GET body.
// Configure its address on resolution so late WebApplicationFactory overrides
// are visible, not during initial service registration.
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<IInventoryService, HttpInventoryService>((services, client) =>
{
    var configuredUrl = services.GetRequiredService<IConfiguration>()["Inventory:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(configuredUrl))
    {
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Inventory:BaseUrl must be an absolute HTTP(S) URL.");
        client.BaseAddress = baseUri;
    }
    client.Timeout = TimeSpan.FromSeconds(15);
});

// The explicit local compatibility mode uses in-process services for its
// original synchronous Booking workflow (when an Inventory URL is absent).
if (registerLegacyServices &&
    string.IsNullOrWhiteSpace(builder.Configuration["Inventory:BaseUrl"]))
{
    builder.Services.AddScoped<IInventoryService>(
        sp => sp.GetRequiredService<InventoryService>());
}

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

// Route choice must use the final configuration (including test-host app settings).
// Database services are registered separately at startup or explicitly by the
// Phase-1 compatibility test host.
bool exposeLegacyCheckout = bool.TryParse(
    app.Configuration["LegacyBooking:Enabled"], out var exposeLegacy) && exposeLegacy;
if (exposeLegacyCheckout && !app.Environment.IsDevelopment())
    throw new InvalidOperationException(
        "Legacy synchronous checkout is allowed only in Development.");

app.MapCatalogEndpoints();
app.MapInventoryEndpoints();
if (exposeLegacyCheckout)
    app.MapOrderingEndpoints();
else
    app.MapRetiredLegacyOrderingEndpoints();

app.Run();

// Buyer JWT authentication is provided by the configured OIDC issuer.
// This local HTTP setup is never suitable for a public deployment.
public partial class Program { }
