# Entity schema / SQL locking queries refactor

The TicketRush-specific `IEntitySchema` and `IEntitySchema<TEntity>` interfaces expose quoted SQL identifiers from the **finalized EF Core model**. `EfEntitySchema<TEntity>` is the implementation. `IEntitySchema` is a project abstraction, **not** a built-in EF Core type.

## Source of truth

`IEntityTypeConfiguration<TEntity>` still defines `ToTable()` and `HasColumnName()` mappings. To avoid duplicating those names in `BookingService`, retrieve the schema from `BookingDbContext`:

```csharp
var schema = db.GetEntitySchema<SeatAllocation>();
string sql = $"""
    SELECT {schema.Columns}
    FROM {schema.Table}
    WHERE {schema.Column(x => x.EventId)} = {{0}}
      AND {schema.Column(x => x.SeatCode)} = ANY ({{1}})
    ORDER BY {schema.Column(x => x.SeatCode)} FOR UPDATE
    """;
var rows = await db.Seats.FromSqlRaw(sql, eventId, codes).ToListAsync(ct);
```

The interface returns quoted identifiers generated from the EF metadata; the query still uses indexed **database parameters** (`{0}`, `{1}`) for user/request values. PostgreSQL keywords `ORDER BY ... FOR UPDATE` are intentionally kept as SQL, because these rows must be locked in the ongoing transaction.

## Why not `SELECT *`?

PostgreSQL `SELECT *` omits hidden system columns such as `xmin`. Since `SeatAllocation.Version` is mapped with `IsRowVersion()` to `xmin`, the query needs to fetch it so the entity can be tracked and concurrency checks work. `schema.Columns` enumerates mapped columns (including `xmin`) from the EF model.

## Other mapping fixes

- Removed `.HasName("id")` on primary keys. That named the **primary-key constraint** (`id`), not the column, and produces conflicting names during EF migration generation. Columns remain mapped using `HasColumnName("id")`.
- Replaced the no-op `Property(x => x.HoldId)` in `SeatAllocationConfiguration` with `HasIndex(x => x.HoldId)` to match the existing hand-written schema index.

## Verification

`EntitySchemaTests` cover the finalized metadata including `xmin` and reject non-property expressions. The existing Testcontainers tests exercise the two PostgreSQL `FOR UPDATE` queries end-to-end. Run:

```powershell
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx
dotnet test TicketRush.slnx
```

**No change to database schema or booking business behavior is intended.** The current checked-in `001_schema.sql` continues to bootstrap the test database; moving to EF migrations remains a separate task.
