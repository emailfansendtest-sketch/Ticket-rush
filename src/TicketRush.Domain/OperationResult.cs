namespace TicketRush.Domain;

public sealed record OperationResult<T>( ResultKind Kind, T? Value = default, string? Error = null );
