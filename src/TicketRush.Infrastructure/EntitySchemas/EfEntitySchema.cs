using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace TicketRush.Infrastructure.EntitySchemas;

/// <summary>
/// PostgreSQL identifier adapter over EF Core's finalized model. No table/column
/// mappings are repeated here: the IEntityTypeConfiguration classes own them.
/// </summary>
internal sealed class EfEntitySchema<TEntity> : IEntitySchema<TEntity> where TEntity : class
{
    private readonly IReadOnlyDictionary<string, string> _columns;

    public EfEntitySchema(DbContext db)
    {
        var entity = db.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not mapped by the DbContext.");

        var tableName = entity.GetTableName()
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not mapped to a table.");

        var schemaName = entity.GetSchema();
        var table = StoreObjectIdentifier.Table(tableName, schemaName);

        // Only values originating in the EF model are used as SQL identifiers.
        // Escaping double quotes makes the identifiers valid for PostgreSQL.
        Table = schemaName is null
            ? Quote(tableName)
            : $"{Quote(schemaName)}.{Quote(tableName)}";

        var mappedColumns = entity.GetProperties().Select(property =>
        {
            string mappedName = property.GetColumnName(table)
                ?? throw new InvalidOperationException(
                    $"Property {typeof(TEntity).Name}.{property.Name} has no mapped column.");
            return (PropertyName: property.Name, SqlName: Quote(mappedName));
        }).ToArray();

        _columns = mappedColumns.ToDictionary(
            x => x.PropertyName, x => x.SqlName, StringComparer.Ordinal);

        // Using SELECT * would omit PostgreSQL system columns (notably xmin),
        // which EF Core requires when materializing the SeatAllocation rowversion.
        Columns = string.Join(", ", mappedColumns.Select(x => x.SqlName));
    }

    public string Table { get; }
    public string Columns { get; }

    public string Column<TProperty>(Expression<Func<TEntity, TProperty>> property)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (property.Body is not MemberExpression { Member: PropertyInfo info } ||
            !_columns.TryGetValue(info.Name, out string? identifier))
        {
            throw new ArgumentException("Expected a directly mapped entity property.", nameof(property));
        }

        return identifier;
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
