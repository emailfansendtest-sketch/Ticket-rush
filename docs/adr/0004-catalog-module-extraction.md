# ADR 0004 — Own Catalog queries within the Catalog module

**Status:** Proposed (pending developer local build and integration-test validation).

## Decision

Place `IEventCatalog`, `EventSummary`, and the EF-backed `EfEventCatalog` implementation in `TicketRush.Catalog`, rather than splitting its implementation across shared Infrastructure. Preserve the existing PostgreSQL context, route, and JSON contract.

Dependency direction: `Api → Catalog → Infrastructure`; shared Infrastructure must not depend on Catalog. The Api continues to reference Infrastructure for remaining Phase-1 booking endpoints.

## Tradeoff

Catalog currently depends on shared persistence. This is acceptable for an incremental Phase-1 modular monolith but is **not** independent database ownership. The transactional booking logic remains untouched while we establish Inventory/Ordering boundaries.
