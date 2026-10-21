using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using TicketRush.BuildingBlocks.Contracts.Security;
using TicketRush.Ordering.Persistence;

namespace TicketRush.Ordering.Api;

// Independent API: only Ordering storage is referenced. No direct Booking
// database access and no transaction shared with Inventory.
public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddDbContext<OrderingDbContext>(options =>
            options.UseNpgsql(builder.Configuration.GetConnectionString("OrderingDb")
                ?? throw new InvalidOperationException("OrderingDb connection string is missing.")));
        builder.Services.AddScoped<CheckoutIntakeService>();
        builder.Services.AddScoped<CheckoutStatusReader>();
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
        app.MapCheckoutEndpoints();
        app.Run();
    }
}
