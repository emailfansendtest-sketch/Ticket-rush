# Run from repository root AFTER extracting the patch, before building.
$ErrorActionPreference = 'Stop'
if (-not (Test-Path '.\TicketRush.slnx')) { throw 'Run from TicketRush repository root.' }
if (-not (Test-Path '.\src\TicketRush.Inventory.Persistence\InventoryDbContext.cs')) {
  throw 'Extract Phase 2.3o.2c patch before applying cleanup.'
}
$paths = @(
  '.\src\TicketRush.Infrastructure\InventoryInboxMessage.cs',
  '.\src\TicketRush.Infrastructure\InventoryResultOutbox.cs',
  '.\src\TicketRush.Infrastructure\InventorySaleRecord.cs',
  '.\src\TicketRush.Infrastructure\InventoryResultOutboxDispatcher.cs',
  '.\src\TicketRush.Infrastructure\IInventoryResultPublisher.cs',
  '.\src\TicketRush.Infrastructure\EntitySchemas\EfEntitySchema.cs',
  '.\src\TicketRush.Infrastructure\EntitySchemas\EntitySchemaExtensions.cs',
  '.\src\TicketRush.Infrastructure\EntitySchemas\IEntitySchema.cs',
  '.\src\TicketRush.Infrastructure\DbConfigurations\InventoryInboxConfiguration.cs',
  '.\src\TicketRush.Infrastructure\DbConfigurations\InventoryResultOutboxConfiguration.cs',
  '.\src\TicketRush.Infrastructure\DbConfigurations\InventorySaleRecordConfiguration.cs',
  '.\src\TicketRush.Infrastructure\DbConfigurations\SeatAllocationConfiguration.cs',
  '.\src\TicketRush.Infrastructure\DbConfigurations\SeatHoldConfiguration.cs',
  '.\src\TicketRush.Infrastructure\DbConfigurations\TicketEventConfiguration.cs'
)
foreach ($path in $paths) {
  if (Test-Path -LiteralPath $path) {
    Remove-Item -LiteralPath $path -Force
    Write-Host "Moved out of legacy Infrastructure: $path"
  }
}
Write-Host 'Phase 2.3o.2c source cleanup complete.' -ForegroundColor Green
