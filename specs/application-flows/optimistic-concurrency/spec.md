# Flow: Optimistic concurrency and atomic mutation

- Status: Draft
- Owner: Igor
- Last updated: 2026-08-27

## Outcome

Concurrent or retried authoring requests never overwrite a newer decision,
duplicate a revision, violate uniqueness, or leave a partially changed
aggregate.

## In scope

- Strong ETags, `If-Match`, no-op PATCH, idempotent DELETE, stale updates,
  simultaneous requests, transactional rollback, and retry behavior.
- Article, media, and series mutations.

## Out of scope

- Public `If-None-Match` caching.
- Distributed multi-service transactions.

## Content and workflow

Create and item reads expose a strong ETag derived from a positive integer
version. Active PATCH, first DELETE, and restore require exactly one current
strong `If-Match`. Validation and persistence operate on one candidate snapshot
and commit root, children, references/memberships, and revision atomically.

No-op PATCH does not increment version, update timestamps, or write a revision.
Repeated DELETE is idempotent: one `If-Match` value must be present, but its
value is ignored after deletion is already established.

## HTTP contract

- Missing `If-Match` -> `428`.
- Multiple, wildcard, weak, malformed, or stale `If-Match` -> `412`.
- Missing/deleted active target -> `404` before active mutation evaluation.
- Current ETag plus valid state change -> endpoint success and a new ETag where
  a body is returned.
- Rejected operations use Problem Details and never return the candidate state.

## Data and integrations

Production tests use PostgreSQL concurrency and real transactions. Unique slug,
membership, media-reference, and revision constraints are part of the same
commit. Failures are injected before the root write, between child writes,
before revision insertion, during commit, and after provider success where
applicable.

## Security and privacy

ETags reveal only resource version semantics, not private content. A stale
request never receives the newer private representation in its error response.

## Acceptance scenarios

### Scenario: Commit one current mutation

- Given an active resource at version N
- When PATCH supplies ETag N and a valid state change
- Then exactly one aggregate transaction commits
- And the version becomes N+1
- And timestamps, references/memberships, and revision state agree

### Scenario: Reject a missing precondition

- Given an active resource
- When PATCH, first DELETE, or restore omits `If-Match`
- Then it returns `428`
- And state, version, timestamp, revision count, and external effects are
  byte-for-byte/logically unchanged

### Scenario: Reject every invalid precondition shape

- Given an active resource at version N
- When `If-Match` is stale, weak, wildcard, malformed, empty, or has multiple
  values
- Then it returns `412`
- And no validation-dependent or persistence side effect occurs

### Scenario: Prevent a lost update

- Given clients A and B read ETag N
- When A commits a mutation and B then submits a different mutation with N
- Then A remains the only committed outcome
- And B receives `412`
- And there is one new revision, not two

### Scenario: Serialize simultaneous current mutations

- Given two requests start with the same current ETag and are released at the
  commit boundary together
- When both attempt valid changes
- Then exactly one succeeds and one fails with `412` or a mapped concurrency
  conflict
- And the final aggregate/revision reflects only the winner

### Scenario: Keep a no-op truly inert

- Given each aggregate, including an article with reading time above one minute
- When an empty or semantically equivalent PATCH uses the current ETag
- Then it returns `200` and the same ETag
- And version, updated time, derived values, revision count, and public cache
  representation remain unchanged

### Scenario: Repeat deletion safely

- Given a resource was deleted once
- When DELETE is retried with any one `If-Match` value
- Then it returns `204`
- And deletion/update timestamps, version, revision count, references, and
  external objects remain unchanged
- But a missing or multiple header still follows the precondition contract

### Scenario: Roll back every partial database failure

- Given a valid aggregate mutation
- When an injected failure occurs at each root, child, join, reference,
  revision, or commit boundary
- Then the request returns the mapped failure
- And a fresh transaction observes the exact pre-request state
- And a retry with the original current ETag can safely succeed once

### Scenario: Enforce concurrent slug uniqueness

- Given no active resource owns a candidate slug
- When two article creates, two series creates, or restore/create combinations
  race for that slug case-insensitively
- Then at most one active resource commits with it
- And the loser returns a mapped `409` without orphan children or revisions

## Test evidence

- Shared table-driven HTTP precondition tests for every mutation route.
- Deterministic concurrency tests using barriers around the persistence commit.
- PostgreSQL integration tests for concurrency tokens, unique constraints, and
  transaction rollback.
- Revision/reference/membership counts asserted before and after all failures.

## Decisions and open questions

- Decision: total atomicity is a production-adapter claim and cannot be proven
  by singleton in-memory acceptance tests.
- Decision: repeated DELETE keeps the specified ignored-value behavior but
  still requires exactly one header.
- Open question: standardize the mapped response when the database detects a
  concurrency conflict after handler precondition validation.
