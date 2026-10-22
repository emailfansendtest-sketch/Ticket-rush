using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using TicketRush.BuildingBlocks.Contracts.Security;
using TicketRush.Domain;
using TicketRush.Infrastructure;
using TicketRush.Inventory;

namespace TicketRush.Inventory.Api;

/// <summary>
/// HTTP boundary for Inventory. Phase 2.3o.2a keeps the existing Booking database
/// while removing Inventory reads/hold writes from the public Booking API process.
/// </summary>
public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddDbContext<BookingDbContext>(options =>
            options.UseNpgsql(builder.Configuration.GetConnectionString("BookingDb")
                ?? throw new InvalidOperationException("BookingDb connection string is required.")));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<IInventoryService, InventoryService>();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
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
        var authority = app.Configuration["Auth:Authority"];
        var audience = app.Configuration["Auth:Audience"];
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var issuerUri) ||
            (issuerUri.Scheme != Uri.UriSchemeHttp && issuerUri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(audience))
            throw new InvalidOperationException("Auth:Authority and Auth:Audience must be configured.");
        if (issuerUri.Scheme == Uri.UriSchemeHttp &&
            (!app.Environment.IsDevelopment() ||
             !bool.TryParse(app.Configuration["Auth:AllowInsecureHttpMetadata"], out bool allowHttp) ||
             !allowHttp))
            throw new InvalidOperationException("HTTP OIDC metadata requires Development and explicit opt-in.");

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHealthChecks("/health/live");
        app.MapGet("/events/{eventId:guid}/seats", async (Guid eventId,
            IInventoryService inventory, CancellationToken ct) =>
            Results.Ok(await inventory.ListSeatsAsync(eventId, ct)));
        app.MapGet("/holds/{holdId:guid}", async (Guid holdId,
            System.Security.Claims.ClaimsPrincipal user, IInventoryService inventory,
            CancellationToken ct) =>
        {
            var buyer = BuyerIdentity.FromPrincipal(user);
            if (buyer is null) return Results.NotFound();
            var hold = await inventory.GetHoldAsync(holdId, buyer.Value, ct);
            return hold is null ? Results.NotFound() : Results.Ok(hold);
        }).RequireAuthorization();
        app.MapPost("/events/{eventId:guid}/holds", async (Guid eventId,
            CreateInventoryHoldRequest request, System.Security.Claims.ClaimsPrincipal user,
            IInventoryService inventory, CancellationToken ct) =>
        {
            var buyer = BuyerIdentity.FromPrincipal(user);
            if (buyer is null || buyer.Value != request.BuyerId) return Results.Forbid();
            var result = await inventory.HoldAsync(eventId, buyer.Value,
                request.IdempotencyKey, request.SeatCodes, ct);
            return result.Kind switch
            {
                ResultKind.Created when result.Value is { } created => Results.Created(
                    $"/holds/{created.Id:D}", created),
                ResultKind.Replayed when result.Value is { } replayed => Results.Ok(replayed),
                ResultKind.Invalid => Results.BadRequest(new { error = result.Error }),
                ResultKind.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Conflict(new { error = result.Error })
            };
        }).RequireAuthorization();
        app.Run();
    }
}

public sealed record CreateInventoryHoldRequest(Guid BuyerId, string IdempotencyKey,
    string[] SeatCodes);
