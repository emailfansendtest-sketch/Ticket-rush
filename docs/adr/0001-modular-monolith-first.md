# ADR 0001 — Start as a modular monolith

**Status:** Accepted for initial development.

## Context

Ticketing needs transactional inventory correctness before distribution. Starting immediately with several services would obscure correctness bugs behind network failures, retries and at-least-once delivery.

## Decision

Build Catalog, Inventory and Ordering as separate *logical* modules in one .NET 10 host for Phase 1. PostgreSQL enforces seat invariants; initially there is no queue/Redis/payment integration. Introduce independently deployable services in Phase 2 after integration tests prove the transaction semantics.

## Consequences

- Correctness remains testable using one real database without simulated distributed workflows.
- Clear extraction candidates emerge naturally; modular boundaries must be enforced by ownership and contract discipline before splitting.
- Phase 1 is **not** a microservices implementation. The README and resume must say so.
