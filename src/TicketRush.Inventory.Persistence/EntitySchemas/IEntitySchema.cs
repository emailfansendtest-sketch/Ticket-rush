using System.Linq.Expressions;

namespace TicketRush.Infrastructure.EntitySchemas;

/// <summary>
/// SQL identifiers for an EF Core entity, resolved from the finalized EF model.
/// Not an EF Core framework interface: this is TicketRush's own abstraction.
/// </summary>
public interface IEntitySchema
{
    /// <summary>A safely quoted, optionally schema-qualified table name.</summary>
    string Table { get; }

    /// <summary>All mapped columns, safely quoted, including provider columns such as xmin.</summary>
    string Columns { get; }
}

public interface IEntitySchema<TEntity> : IEntitySchema where TEntity : class
{
    /// <summary>The mapped SQL column identifier for a C# entity property.</summary>
    string Column<TProperty>(Expression<Func<TEntity, TProperty>> property);
}
