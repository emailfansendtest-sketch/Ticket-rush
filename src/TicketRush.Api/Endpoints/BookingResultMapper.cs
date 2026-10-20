using TicketRush.Domain;

namespace TicketRush.Api.Endpoints;

internal static class BookingResultMapper
{
    public static IResult MapResult<T>(OperationResult<T> result, Func<T, string> location)
        where T : class => result.Kind switch
        {
            ResultKind.Created when result.Value is not null =>
                Results.Created(location(result.Value), result.Value),
            ResultKind.Replayed when result.Value is not null => Results.Ok(result.Value),
            ResultKind.NotFound => Results.NotFound(new { error = result.Error }),
            ResultKind.Invalid => Results.BadRequest(new { error = result.Error }),
            ResultKind.Expired => Results.Conflict(new { error = result.Error }),
            _ => Results.Conflict(new { error = result.Error })
        };
}
