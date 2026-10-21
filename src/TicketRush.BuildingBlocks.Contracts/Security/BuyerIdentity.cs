using System.Security.Claims;

namespace TicketRush.BuildingBlocks.Contracts.Security;

/// <summary>
/// The authenticated buyer is the issuer-assigned UUID in the access token subject.
/// Never infer buyer ownership from a request body's buyerId or a query parameter.
/// </summary>
public static class BuyerIdentity
{
    public static Guid? FromPrincipal(ClaimsPrincipal principal)
    {
        string? subject = principal.FindFirst("sub")?.Value;
        return Guid.TryParse(subject, out Guid id) && id != Guid.Empty ? id : null;
    }
}
