# Flow: Operations, configuration, contract, and deployment safety

- Status: Draft
- Owner: Igor
- Last updated: 2026-08-27

## Outcome

Operators and automation can distinguish a live process from a dependency-ready
service, deploy only fail-closed production configuration, detect contract
drift, and preserve Blog/Presentation isolation under success and failure.

## In scope

- Liveness/readiness, startup configuration, CORS origins, signing/admin keys,
  persistence/provider settings, OpenAPI drift, logging, restart behavior,
  PostgreSQL schema isolation, and production container checks.

## Out of scope

- Deployment platform provisioning and Cloudinary capacity purchasing.
- Business aggregate semantics covered by other flow specs.

## Content and workflow

Liveness reports only whether the process can serve requests and calls no
dependency. Readiness checks PostgreSQL with a bounded timeout and reports
unready on dependency failure without changing liveness. Production startup
validates all required settings and fails before serving when unsafe/missing.

The checked-in OpenAPI 3.1 contract is compared with runtime routes, methods,
requests, statuses, content types, headers, and schemas. The container runs
non-root from pinned images, exposes only the app port, contains no secrets or
development settings, and uses liveness for container health.

## HTTP contract

- `GET /health/live` -> `200` healthy while the request pipeline is operational.
- `GET /health/ready` -> `200` when required dependencies are ready, `503` when
  PostgreSQL is unavailable/timeout/misconfigured.
- Health responses never disclose connection strings, credentials, host
  topology, stack traces, or private state.
- Every documented route and response remains below the approved versioned
  boundary; breaking changes require explicit versioning.

## Data and integrations

The Blog principal owns/accesses only the `blog` schema and its migration
history; Presentation access is denied. Migrations and readiness use bounded
database operations. Current in-memory adapters are allowed only in explicit
development/test configuration and lose data on restart.

## Security and privacy

Production requires distinct nonempty AdminKey/CursorKey, HTTPS public origin,
explicit HTTPS CORS origins, PostgreSQL configuration, and media-provider
credentials. Secrets come from runtime injection, never repository/image/log.
Startup errors identify the missing setting without printing its value.

## Acceptance scenarios

### Scenario: Report liveness without dependencies

- Given PostgreSQL/provider are healthy, slow, unavailable, or misconfigured
- When liveness is requested repeatedly
- Then it returns healthy while the process can serve
- And no dependency call is made

### Scenario: Report readiness accurately

- Given PostgreSQL success, authentication failure, DNS/TLS failure, pool
  exhaustion, slow response beyond timeout, migration mismatch, and cancellation
- When readiness is requested
- Then only bounded successful access returns `200`
- And every dependency failure returns `503` without making liveness unhealthy
- And no sensitive connection detail leaks

### Scenario: Fail production startup closed

- Given each required setting is independently absent, empty, malformed,
  insecure, duplicated where separation is required, or still a development
  placeholder
- When the process starts in Production
- Then startup fails before binding a serving port
- And the diagnostic names the setting but not any secret value

### Scenario: Permit explicit development adapters only

- Given Development/Test and Production environments
- When in-memory store/storage or fallback cursor/origin values are selected
- Then they work only in explicit non-production environments
- And Production rejects them

### Scenario: Isolate database ownership

- Given the Blog runtime/migration principal
- When CRUD/migration access targets Blog objects and read/write/DDL targets
  Presentation objects
- Then permitted Blog operations succeed
- And every Presentation operation is denied and audited safely

### Scenario: Roll migrations forward safely

- Given empty, current, prior, partially failed, and incompatible databases
- When migration/startup runs
- Then history exists only in `blog`, successful upgrades are atomic/idempotent,
  and failures do not alter Presentation or leave silent partial schema

### Scenario: Detect every OpenAPI drift class

- Given runtime and checked-in contract
- When routes, verbs, security, parameters, merge-patch/multipart bodies,
  success/error statuses, content types, ETag/cache/Location headers, page fields,
  discriminated body blocks, or health endpoints differ
- Then contract validation fails with an actionable diff
- And generated TypeScript client smoke tests cover all public schemas

### Scenario: Keep logs safe in success and failure

- Given sentinel values in keys, connection strings, provider credentials,
  cursors, article bodies, alt text, filenames, and media bytes
- When success, validation, auth, provider, persistence, cancellation, and
  unexpected exceptions occur
- Then logs include safe operation/outcome/duration/trace/resource identity
- And contain none of the sentinel secrets/content

### Scenario: Verify production container hardening

- Given the built production image
- When inspected and run
- Then bases/actions are pinned, runtime is non-root, only intended files/port
  exist, health invokes liveness, and repository/user secrets are absent
- And read-only/restricted filesystem behavior does not break serving

### Scenario: Document and replace ephemeral restart behavior

- Given current in-memory and production PostgreSQL configurations
- When the process/container restarts after writes
- Then development tests explicitly observe data loss
- And production integration observes durable records, versions, histories, and
  references with no duplicate singleton state

### Scenario: Preserve deployment rollback

- Given a previous healthy container and a candidate image that fails startup or
  health within the bounded window
- When deployment runs
- Then the candidate is stopped and the previous container is restored
- And immutable image identity and sanitized logs identify the outcome

## Test evidence

- Health tests with failure-injectable bounded database checks.
- Production configuration matrix startup tests.
- PostgreSQL privilege/migration/restart integration tests.
- Runtime-versus-OpenAPI and generated-client contract tests.
- Container structure/runtime/secret scanning and deployment-script tests.

## Decisions and open questions

- Decision: current always-healthy readiness is development-only and cannot
  satisfy production readiness.
- Decision: production uses no AdminKey cursor fallback or placeholder origin.
- Open question: select and document the exact readiness timeout and migration
  compatibility policy.
