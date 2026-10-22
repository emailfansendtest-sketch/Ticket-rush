using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TicketRush.IntegrationTests;

/// <summary>In-process test identity only. Production uses real JwtBearer validation.</summary>
internal static class TestBuyerAuthentication
{
    public const string Scheme = "TestBuyer";

    public static void Configure(IServiceCollection services) =>
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = Scheme;
            options.DefaultChallengeScheme = Scheme;
            options.DefaultForbidScheme = Scheme;
        }).AddScheme<AuthenticationSchemeOptions, Handler>(Scheme, _ => { });

    public static void As(HttpClient client, Guid buyer) =>
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", buyer.ToString("D"));

    public sealed class Handler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public Handler(IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParse(header[7..], out Guid buyer) || buyer == Guid.Empty)
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity(
                new[] { new Claim("sub", buyer.ToString("D")) }, TestBuyerAuthentication.Scheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestBuyerAuthentication.Scheme)));
        }
    }
}
