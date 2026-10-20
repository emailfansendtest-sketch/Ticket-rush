using Microsoft.EntityFrameworkCore;

namespace TicketRush.Infrastructure.EntitySchemas;

public static class EntitySchemaExtensions
{
    /// <summary>
    /// Resolve SQL names from the actual EF Core model (not hard-coded constants).
    /// Names are PostgreSQL-quoted; SQL values must still be passed as parameters.
    /// </summary>
    public static IEntitySchema<TEntity> GetEntitySchema<TEntity>(this DbContext db)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(db);
        return new EfEntitySchema<TEntity>(db);
    }
}
