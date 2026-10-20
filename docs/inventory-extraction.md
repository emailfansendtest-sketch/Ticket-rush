# Apply the Inventory extraction

The source baseline is the **corrected Catalog full solution**, which you reported building with **15 passing tests**. Commit or back up your local working tree before applying.

1. Extract the `TicketRush_Inventory_Extraction_Patch.zip` into the folder containing `TicketRush.slnx`. The archive has **no wrapper directory** and **no delete operations**.
2. `dotnet restore TicketRush.slnx`
3. `dotnet build TicketRush.slnx --no-restore`
4. `dotnet test TicketRush.slnx --no-build`
5. Check that both `GET /events/{eventId}/seats` and `POST /events/{eventId}/holds` still behave as before.

The existing concurrency tests were updated only to call `new InventoryService(db, clock).HoldAsync(...)` for holds, retaining `BookingService.ConfirmAsync(...)` for checkout. The 15 original tests remain; two additional Inventory tests should yield **17** total passing tests when successful.

No data migration is necessary. `Directory.Packages.props` and both security pins (`System.Security.Cryptography.Pkcs` and `.Xml` at `10.0.13`) are unchanged.

If your working tree contains newer changes than the 15-test baseline, review changes using `git diff` before applying; this patch is not a merge operation.
