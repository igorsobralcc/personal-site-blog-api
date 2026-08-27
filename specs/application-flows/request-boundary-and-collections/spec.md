# Flow: Request boundary and administrative collections

- Status: Draft
- Owner: Igor
- Last updated: 2026-08-27

## Outcome

Every request reaches business code only after transport, origin, authorization,
routing, and binding rules succeed. Administrative collections return stable,
bounded pages, while every rejected request is safe, indistinguishable where
required, and free of persistence side effects.

## In scope

- HTTPS redirection, CORS, admin-key authentication, routing, binding, shared
  Problem Details, request logging, and administrative pagination.
- Article, media, and series list flows and the shared boundary behavior for
  every `/api/v1/admin/*` operation.
- Framework-originated failures before endpoint execution.

## Out of scope

- Aggregate-specific validation and lifecycle rules.
- ETag mutation behavior, specified by Optimistic concurrency.
- Production identity systems beyond the approved single admin key.

## Content and workflow

Requests pass through HTTPS redirection and CORS before the admin-key gate.
Only paths below `/api/v1/admin` require `X-Admin-Key`. Missing, empty, invalid,
and unconfigured keys produce the same `401` before binding or persistence.
Authorized requests proceed to logging, routing, binding, and the handler.

Administrative lists read `X-Page`, `X-Page-Size`, and
`X-Include-Deleted`, order by `createdAt DESC`, and return a bounded page.
Deleted rows are excluded by default and included only by an explicitly valid
request. An overrun page is successful and empty rather than an error.

## HTTP contract

- Admin authentication failures return `401 application/problem+json` with
  `type`, `title`, `status`, and `traceId`.
- `X-Page` defaults to 1 and must be a positive integer.
- `X-Page-Size` defaults to 20 and must be an integer from 1 through 100.
- `X-Include-Deleted` defaults to false and accepts explicit boolean values.
- Invalid paging returns `400` with `errors.headers`.
- Collection responses expose `items`, `page`, `pageSize`, `totalItems`, and
  `totalPages`; OpenAPI and runtime naming must agree before approval.
- Unknown routes return `404`; unsupported methods return `405`; invalid
  constrained route values do not enter a handler.
- Malformed/empty/wrong-type bodies and unsupported content types use the
  documented framework Problem Details response and never become `500`.

## Data and integrations

Authentication, routing, and binding failures perform no store/database/provider
operation. Paging is read-only. Each HTTP acceptance test uses isolated state so
parallel test execution cannot change counts or order.

## Security and privacy

- Admin-key comparison is constant-time and the key never enters logs,
  exceptions, metrics, traces, or responses.
- Public routes never require or reflect an admin key.
- CORS permits only exact configured origins, requested allowed methods/headers,
  and no credentials.
- Logs exclude request/response bodies, cursor payloads, and authoring content.

## Acceptance scenarios

### Scenario: Accept an authenticated administrative request

- Given the Blog admin key is configured
- When a request supplies exactly the valid key
- Then it reaches the selected endpoint
- And the completion log contains method, path, outcome, duration, and trace ID
- And the log does not contain the key or body

### Scenario: Reject every invalid credential shape

- Given a protected endpoint and instrumented persistence/provider spies
- When the key is absent, empty, wrong, wrong-length, duplicated, or the server
  key is unconfigured
- Then each request returns the same `401` Problem Details contract
- And no binder, endpoint, persistence, or provider operation occurs
- And no supplied credential appears in logs or response

### Scenario: Leave public routes anonymous

- Given the admin key is absent or incorrect
- When a public article, public series, liveness, or readiness route is requested
- Then the admin-key middleware does not reject it
- And only that route's own public rules determine the result

### Scenario: Return the default administrative page

- Given more than 20 active records with deterministic creation times
- When the collection is requested without pagination headers
- Then page 1 contains the newest 20 records
- And totals describe the complete active result set
- And the response contains no deleted record

### Scenario: Return boundary and overrun pages

- Given a known active/deleted data set
- When page sizes 1, 20, and 100 and the last and first overrun pages are read
- Then each result has deterministic order, item count, and totals
- And `X-Include-Deleted=true` changes only deletion filtering

### Scenario: Reject invalid paging without reading data

- Given a collection endpoint with a store-read spy
- When page or size is nonnumeric, zero, negative, duplicated, or size exceeds
  100, or include-deleted is not a valid boolean
- Then the request returns `400` with `errors.headers`
- And the store is not enumerated
- And no record changes

### Scenario: Contain malformed request bodies

- Given each JSON or multipart endpoint
- When the body is empty, truncated, malformed, wrong-shaped, wrong-typed,
  unsupported in content type, over the request limit, or canceled during read
- Then the documented `400`, `413`, `415`, or cancellation behavior occurs
- And business code makes no partial mutation
- And no internal exception text or content is leaked

### Scenario: Handle routing failures safely

- Given invalid GUID/revision route values, an unknown path, and an unsupported
  HTTP method
- When each request is sent
- Then routing returns the documented `404` or `405`
- And authorization behavior follows the selected path boundary
- And no endpoint mutation occurs

### Scenario: Enforce CORS pessimistically

- Given one configured frontend origin
- When allowed, unlisted, opaque/null, malformed, and lookalike origins send
  simple and preflight requests
- Then only the exact configured origin receives allow headers
- And credentials are never enabled
- And denial does not expose protected data

### Scenario: Preserve failure observability

- Given a handler returns each application error or throws unexpectedly
- When the response completes
- Then a trace ID correlates the response and sanitized completion/error log
- And secrets, bodies, private content, and provider credentials are absent

## Test evidence

- Table-driven HTTP tests for every authentication and header branch.
- Route/binder characterization tests for every endpoint body and constraint.
- Log-capture tests with sentinel secrets and content.
- OpenAPI/runtime assertions for statuses, headers, content types, and page
  field names.

## Decisions and open questions

- Decision: tests require invalid `X-Include-Deleted` to fail rather than
  silently behave as false; reconcile current implementation before approval.
- Decision: use the approved response field `page`; resolve current
  `pageNumber` drift through contract review.
- Open question: define the exact cancellation response when the client aborts
  before a response can be written.
