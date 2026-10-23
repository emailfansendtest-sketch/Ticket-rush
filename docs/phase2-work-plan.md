# Phase 2 implementation history — updated at Phase 2.3r

The repository evolved from a modular monolith into a set of independently hosted components with PostgreSQL-owned persistence, a RabbitMQ-based durable checkout saga and simulated Payment/Notification services. The current API, fresh-deployment instructions and tests are in the [main README](../README.md). Individual root-level `README-STEP*.md` notes are historical records of the corresponding patches.

| Milestone | Delivered capability |
| --- | --- |
| 2.1–2.2 | Versioned message contracts; Catalog host and independent Catalog PostgreSQL |
| 2.3a–2.3d | Ordering-owned checkout intake/outbox, RabbitMQ publication and initial Inventory message handling |
| 2.3e–2.3h | Persistent saga/result processing, Ordering `/checkouts`, watchdog and missing-result recovery |
| 2.3i–2.3j | Hold-proof admission and Keycloak JWT buyer identity checks |
| 2.3k–2.3m | Simulated Payment authorization, saga gate and authorization-void compensation |
| 2.3n | Simulated Notification worker with durable deduplicated receipts |
| 2.3o.1–2.3o.2c | Inventory sale ledger, HTTP service boundary, retirement of synchronous checkout and independent Inventory database/cutover |
| 2.3p | Consumer outage and duplicate Inventory-command replay smoke test |
| 2.3q | RabbitMQ outage and transactional outbox recovery smoke test |
| 2.3r | Original CheckoutRequested event replay after terminal checkout; duplicate-effect checks |

**Validated status:** the user reported all tests and relevant PowerShell scripts passing through Phase 2.3r and pushed the changes. These are user-run local results; no CI or independent production environment is implied.

**Outstanding for future phases:** throughput/latency and memory measurements; Redis waiting room; active seat-map push; Kubernetes/Helm replicas and readiness; OpenTelemetry/observability; real PSP or notification integrations. Do not claim MassTransit: this snapshot uses the RabbitMQ.Client library and custom orchestration.
