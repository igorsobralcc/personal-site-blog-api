# Feature: Platform foundation

- Status: Approved
- Owner: Igor
- Last updated: 2026-08-25

## Outcome

Blog features share one secure, observable HTTP and persistence baseline while
remaining operationally and structurally independent from the Presentation API.

## In scope

- An ASP.NET Core API on .NET 10 with PostgreSQL persistence in the `blog`
  schema.
- A Blog-only database principal and EF Core migration history.
- UUIDv7 identifiers, UTC timestamps, soft deletion, optimistic concurrency,
  Problem Details, pagination, health checks, CORS, and structured logging.
- A checked-in OpenAPI 3.1 contract that is the source of generated clients.
- Protected single-owner management operations and anonymous public reads.

## Out of scope

- Blog author accounts, multi-author permissions, OIDC, public registration,
  and an administrative web editor.
- Presentation tables, cross-schema foreign keys, shared migrations, generic
  repositories, MediatR, and distributed transactions.
- Article-specific content, publication, search, media, or revision behavior.

## Content and workflow

This specification defines shared infrastructure only. Feature specifications
own their lifecycle states and transitions. Production implementation does not
begin until this specification and the initial feature contract are Approved.

`docs/openapi.yaml` is the canonical HTTP contract. Runtime route metadata and
the checked-in document must be tested for drift. Breaking contract changes use
a new URL version; additive compatible changes retain `/api/v1`.

## HTTP contract

- JSON properties use `camelCase`; database objects use `snake_case`.
- Public and management routes live below `/api/v1`; liveness and readiness are
  exposed at `GET /health/live` and `GET /health/ready`.
- Protected operations require `X-Admin-Key` over HTTPS. The Blog key is
  configured independently from the Presentation API key even if the same site
  owner initially controls both services.
- Administrative collections accept optional `X-Page` (default `1`),
  `X-Page-Size` (default `20`, maximum `100`), and `X-Include-Deleted` (default
  `false`) headers. Responses contain `items`, `page`, `pageSize`, `totalItems`,
  and `totalPages`.
- Public seek-paginated operations use feature-defined opaque cursors and
  bounded `limit` query parameters. A cursor never contains private content or
  reveals unpublished records.
- Mutable item responses emit a strong ETag derived from the positive integer
  `version`.
- `PATCH`, `DELETE`, and restore operations require exactly one strong
  `If-Match` value. Absence returns `428`; malformed, wildcard, multiple, or
  stale values return `412`.
- PATCH uses `application/merge-patch+json`. Omitted properties remain
  unchanged, explicit `null` clears nullable properties, and supplied aggregate
  arrays replace those arrays completely.
- Errors use `application/problem+json` and include `type`, `title`, `status`,
  and `traceId`. Validation errors also include an `errors` object keyed by
  JSON property or header name.

## Data and integrations

- Every independently managed record has a UUIDv7 `id`, immutable `created_at`,
  `updated_at`, nullable `deleted_at`, and positive integer `version`.
- EF Core uses a global soft-delete filter. Only explicitly authorized
  administrative queries may opt into deleted rows.
- Every foreign key uses `RESTRICT` or `NO ACTION`; database cascades are
  forbidden. Aggregate handlers perform child and join changes explicitly in
  one transaction.
- Each successful state-changing mutation increments `version`. No-op or
  rejected requests do not increment it.
- The Blog migration history is stored in the `blog` schema and never creates,
  reads, changes, or drops Presentation-owned objects.
- The service uses the shared logical PostgreSQL database only as a deployment
  choice. Its runtime principal has no privileges on the `presentation` schema.

## Security and privacy

- Compare the configured administration key in constant time and never log it.
- Missing, invalid, and unconfigured keys return the same `401` representation
  without a database operation.
- CORS uses an explicit configured origin allowlist. Credentialed cross-origin
  requests are disabled unless a later approved authentication design requires
  them.
- Local secrets use .NET user-secrets or process environment variables.
  Production receives the Blog administration key, PostgreSQL connection
  string, and provider credentials through the hosting platform's encrypted
  secret injection; repository files and container images contain no secret
  values.
- `Cors:Origins` is a required production array populated with the deployed
  frontend origin. Local development permits only the explicit Vite development
  origin configured for that environment. Empty production configuration fails
  startup rather than enabling a wildcard origin.
- Public projections never expose deletion metadata, concurrency versions,
  revision actor data, draft content, or administrative-only fields.
- Request bodies, content bodies, credentials, and connection strings are not
  written to application logs.

## Failure and operational behavior

- Validation returns `400`; missing active resources return `404`; uniqueness,
  relationship, and restore conflicts return `409`.
- An already deleted resource makes DELETE return `204` when `If-Match` is
  present; no additional mutation occurs and the supplied value is not checked.
- Transactions roll back the root, child, join, and revision changes together.
- Logs identify operation, resource type, resource ID when known, outcome,
  duration, and trace ID without recording secrets or article content.
- Liveness does not call dependencies. Readiness checks PostgreSQL with a
  bounded timeout and returns `503` when unavailable.
- The production container runs as a non-root user, exposes only the application
  port, has a liveness health check, and uses a pinned runtime image.

## Acceptance scenarios

### Scenario: Reject an unauthenticated management request

- Given a protected Blog endpoint
- When the request omits or supplies an invalid `X-Admin-Key`
- Then it returns `401` Problem Details and performs no database operation

### Scenario: Prevent a lost update

- Given a Blog resource changed after an administrator read it
- When PATCH supplies the old ETag
- Then the API returns `412` and preserves the newer state

### Scenario: Isolate the Blog schema

- Given the Blog API runtime database principal
- When it attempts to access an object in the `presentation` schema
- Then PostgreSQL denies the operation

### Scenario: Report database readiness

- Given PostgreSQL is unavailable
- When readiness is requested
- Then the API returns `503` without making liveness unhealthy

## Test evidence

- Contract tests for checked-in OpenAPI drift, route versioning, content types,
  Problem Details, authentication, ETags, merge patch, and pagination.
- PostgreSQL integration tests for schema privileges, migration-history
  isolation, soft-delete filters, restricted foreign keys, transactions, and
  optimistic concurrency.
- Production build and container checks for non-root execution, health checks,
  pinned images, and absence of development settings.

## Decisions and open questions

- Decision: mirror the proven Presentation API platform conventions where Blog
  requirements do not require a deliberate difference.
- Decision: use a distinct Blog administration secret and database principal.
- Decision: OpenAPI 3.1 is checked in and treated as the client-generation
  contract.
- Decision: exact origins are deployment configuration, while this contract
  requires explicit environment-specific values and forbids wildcard fallback.
- Decision: local development uses user-secrets; production secrets come from
  the hosting platform's encrypted secret injection and are never baked into
  artifacts.
