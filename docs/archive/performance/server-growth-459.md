# Server inventory and OAuth replay growth (459)

## Decision and scope

Retain the current collection and OAuth rules; add no retention job, inventory cap or paging protocol. Local synthetic sizing does not demonstrate an operational need for them. Worker identities and project definitions are authoritative configuration, not disposable history. Used OAuth credentials are replay evidence for a live grant, including used authorization codes after their five-minute exchange lifetime. Deleting those records by credential expiry would prevent family revocation on replay.

This is a reproducible local sizing study, not a production performance claim. No deployment database, inventory counts or observed churn were supplied or accessed. Current production growth and latency remain unknown. Measurements below describe the current schema with explicit synthetic workloads; they do not assert a supported maximum deployment size.

## Current growth, expiry and access

- `SqliteRegistryStore.RegisterWorkerAsync` upserts by Worker ID. Reconnects update registration/last-seen state; they do not append registrations. New identities grow the collection. Heartbeat age/offline status does not revoke identity or remove scheduling, authorization, credential assignment or recovery relationships. There is no automatic Worker expiry.
- Projects have one current configuration per ID and unique repository/name indexes. Updates replace configuration with revision checks. Explicit removal checks revision and refuses queued/assigned/running execution references. No age-based project retention is justified.
- `GetWorkersAsync` reads all registered Workers in ID order, with execution counts and update state. `GetProjectsAsync` reads all definitions ordered by lower-case display name and ID. Scheduling/eligibility and execution decoration consume complete collections, as do dashboard inventory reads. These reads grow with identities/configuration size, not execution duration. Worker query subqueries also depend on execution/update history; the synthetic projection below does not measure that cost.
- MCP collection tools sort by immutable ID and provide bounded offset slices; internal Worker page reads also exist. Neither offset paging nor a new last-ID cursor alone establishes a consistent snapshot across concurrent insertions/deletions. This change makes no new pagination stability claim and does not truncate scheduler inputs.
- `McpOAuthStore` stores at most 1,024 grants; new authorization rejects additional grants rather than evicting active ones. Grants last 90 days, authorization codes five minutes and access tokens ten minutes. Refresh lifetime is the original grant expiry, not a sliding window. PKCE plaintext verifiers are not persisted; the grant stores the challenge. Replay evidence is the credential digest plus `used` flag.
- Initial code exchange leaves three credential rows (used code, access, refresh). Each successful refresh removes the previous access row, marks the old refresh used and adds access/refresh: after R rotations, R+3 rows remain. Used code/refresh replay revokes the whole family in a serialized write transaction. Lookup is indexed by digest; family operations use the grant index. Access validation does not scan all credentials.
- Authorization removes grants at `expires <= now` and their orphan credentials transactionally. Expiry denies access even without cleanup. With no further authorizations, expired rows remain on disk, but no new rows accumulate indefinitely through refresh because grant validity ends. Explicit revocation removes the family. OAuth list reads are limited to 1,024 and filter invalid grants. Backup excludes OAuth authority. SQLite deletion makes pages reusable; it need not shrink the file. No periodic vacuum or uncertain-operation replay is introduced.

## Reproducible measurements

Run from the checkout root:

```sh
python3 docs/benchmarks/server-growth-459/measure.py
```

Captured on 2026-11-03, Linux, Python 3.12.3 / SQLite 3.45.1. Raw results: `docs/benchmarks/server-growth-459/results.jsonl`. The script extracts the current Worker/project table definitions and OAuth tables/index from source into a temporary local database. Inventory uses small synthetic registrations without capability inventory, heartbeat, execution, authorization or update rows, and small project configurations. Reads project just stored JSON; bytes are stored JSON, not HTTP responses or managed allocations. Times are single observations with a warm local cache, not percentiles. Database size is page count times page size, including indexes and prior fixtures/free pages. OAuth rows use a short synthetic grant ID and empty grant body; sizes are indicative, not exact production projections. All data are synthetic and the database is discarded.

| Workers + projects (each) | Stored JSON bytes | Database bytes | Combined projection read ms |
| --- | ---: | ---: | ---: |
| 10 | 3,930 | 40,960 | 0.109 |
| 100 | 39,390 | 106,496 | 0.300 |
| 1,000 | 394,890 | 720,896 | 1.681 |

Repeated registration of the same identity has zero row-count growth (covered through the real registry API regression). At 5-second refresh intervals, 1,000 of each small fixture entails about 79 KB/s of stored JSON per reader before response overhead and dependent queries; this is an estimate, not measured network traffic. Larger capabilities/configuration and simultaneous tabs must be measured separately.

| Refreshes in one 90-day family | Cadence assumption | Credential rows | Total fixture database bytes | Indexed digest lookup ms |
| ---: | --- | ---: | ---: | ---: |
| 100 | Small churn sample | 103 | 741,376 | 0.053 |
| 2,160 | Hourly | 2,163 | 1,126,400 | 0.035 |
| 129,600 | Every minute | 129,603 | 25,677,824 | 0.335 |

The hourly case grows roughly 0.4 MB per family above the inventory fixture; minute rotation roughly 25 MB. Extrapolating 1,024 simultaneously minute-rotated grants gives roughly 25 GB, not a measured workload. A grant-count cap is not a credential-row cap. Rotation rate is not intrinsically bounded here; unusually frequent refreshes deserve investigation rather than silent loss of replay evidence. Bulk expiry/revocation holds a write transaction and may delay writers; indexed lookup measurements do not measure cleanup latency.

## Operational review thresholds

Use these as investigation triggers, not enforced limits or promised service levels:

- At 1,000 registered Workers or 1,000 projects, or inventory responses above 1 MiB, capture actual API serialization/allocations and p50/p95 latency with representative execution/update history and concurrent dashboard readers. Revisit bounded *presentation* pages if inventory p95 exceeds 250 ms or repeated inventory transfer exceeds 1 MiB per refresh. Keep complete authoritative scheduling reads unless separately redesigned and tested.
- At 100,000 credential rows per grant, one million total credential rows, 1 GiB OAuth storage, or cleanup writer stalls above 250 ms, capture rotation rates, active/expired row counts and transactional cleanup latency. Check clients refreshing far more often than the ten-minute access lifetime.
- If cleanup lag is material, consider deleting only expired grant families in bounded transactions with an expiry index and explicit cancellation/locking tests. Never delete used digests belonging to a live family, extend grant lifetime, or evict active grants to satisfy storage bounds. Rate/admission policy requires a separate explicit product decision; this study does not change OAuth availability semantics.
- If paging becomes necessary, specify stable immutable ordering, cursor validation and concurrent-change/snapshot semantics first. Do not silently impose a limit on existing complete-list consumers or assume offsets prevent skipped/repeated rows under churn.

## Regression coverage and validation

`ServerGrowthTests.InventoryGrowthRetainsAllIdentitiesAcrossReregistrationAndRestart` uses the real registry APIs at 10/100/1,000 Workers and projects, verifies complete unique collections after re-registration/reopening, and emits read timings, serialized bytes and file size without timing assertions.

`McpOAuthTests.RotationGrowthRetainsReplayEvidenceUntilGrantExpiryAndCleansOnlyExpiredFamilies` uses the existing fake clock and local SQLite seam: 100 refreshes yield exactly 103 credential rows; replay of an expired used code revokes the live family; exact older-grant expiry cleanup retains a younger family's refresh authority. No wall-clock delays or external service are needed.

Focused command:

```sh
dotnet test tests/CodexWorker.Tests --filter 'FullyQualifiedName~ServerGrowthTests|FullyQualifiedName~RotationGrowthRetains' --logger 'console;verbosity=detailed'
```

The Python sizing command completed successfully (six result rows). The focused .NET command remained at `Determining projects to restore...` and was interrupted without reaching build or test execution. A second attempt with `--no-restore` exited 1 with `NETSDK1004`: `tests/CodexWorker.Tests/obj/project.assets.json` is absent. Package restore is unavailable for this local check; the underlying feed/network cause was not established. No .NET test pass is claimed. Worker-configured validation remains authoritative and separate. Diff review confirmed no production lifecycle, authority, configuration or version changes.
