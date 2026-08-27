# Flow: Article deletion, recovery, and immutable history

- Status: Draft
- Owner: Igor
- Last updated: 2026-08-27

## Outcome

The owner can remove and safely recover an article without accidental
publication, while a complete immutable history survives successful mutations
and failed/retried requests never fabricate revisions.

## In scope

- `DELETE /api/v1/admin/articles/{id}`
- `POST /api/v1/admin/articles/{id}/restore`
- Article revision list and detail routes.
- Soft-deletion visibility, Draft-on-restore, slug/media conflicts, idempotency,
  and snapshot immutability.

## Out of scope

- Physical history deletion and retention expiration.
- Restoring an old revision as current content.

## Content and workflow

First DELETE soft-deletes the aggregate, increments version, updates time, and
writes a `Deleted` revision. Repeated DELETE is idempotent. Delete never removes
tags, media references needed by current/history retention, revisions, or series
membership.

Restore requires a deleted article and current ETag, revalidates every invariant
needed for an active Draft, rejects active slug or unusable media conflicts,
clears deletion, forces Draft, increments version/time, and writes a `Restored`
revision. It never republishes automatically.

## HTTP contract

- Missing DELETE target -> `404`; successful/idempotent DELETE -> `204`.
- Missing or active restore target -> `404`.
- Restore slug/media relationship conflict -> `409`.
- Revision list/detail remain admin-protected for active and deleted parents.
- Missing parent or revision -> indistinguishable `404` within the protected
  route; revision number must satisfy the documented positive range.

## Data and integrations

Every revision has a sequential number, operation, UTC changed time, actor
`site-owner`, request correlation ID, and complete resulting snapshot. Revision
rows and their media-reference rows are immutable and transactional with the
root mutation.

## Security and privacy

Deletion removes the article immediately from feed, detail, and public series.
Revision content is never public. Restore remains private Draft even if the
article had previously been Published.

## Acceptance scenarios

### Scenario: Soft-delete an active article

- Given an active article with current ETag and public/series visibility where
  applicable
- When DELETE is requested
- Then it returns `204`, increments version once, records deletion/update time
  and one Deleted revision
- And it vanishes from every public projection without losing history or
  membership

### Scenario: Delete missing and stale targets safely

- Given missing and active article IDs
- When DELETE is missing `If-Match`, uses stale/invalid values, or targets the
  missing ID
- Then it returns the specified `428`, `412`, or `404`
- And no timestamp, version, revision, membership, or media retention changes

### Scenario: Retry delete idempotently

- Given an already deleted article
- When DELETE is repeated once and concurrently with one header value
- Then every accepted retry returns `204`
- And exactly one Deleted revision and one deletion timestamp exist

### Scenario: Restore safely to Draft

- Given a formerly Published deleted article with valid current references and
  no slug conflict
- When restore uses the current ETag
- Then deletion clears, status becomes Draft, version increments once, and a
  Restored revision captures that state
- And feed/detail/series remain private until explicit republication
- And original `publishedAt` and stable slug are preserved

### Scenario: Reject restore slug conflict atomically

- Given another active article owns the deleted article's slug in any case
  variation
- When restore is attempted
- Then it returns `409`
- And the article remains deleted with unchanged version/time/revisions

### Scenario: Reject restore with unusable media

- Given a deleted article references media that was subsequently soft-deleted,
  physically lost, or fails integrity verification
- When restore is attempted
- Then the documented conflict is returned
- And no active article is created with an invalid reference
- And restoring the media first permits a safe retry

### Scenario: Preserve sequential complete revisions

- Given create, several updates, lifecycle changes, delete, and restore
- When revisions are listed
- Then numbers are contiguous from 1, operations/timestamps/correlation IDs
  match their requests, and every snapshot is the complete resulting state

### Scenario: Keep old snapshots immutable

- Given revision N and later root/tag/body/media changes
- When revision N is reread repeatedly and after process/database restart
- Then its JSON and reference set are identical to the original committed value

### Scenario: Avoid revisions for rejected, failed, and no-op requests

- Given the current revision count
- When validation, precondition, relationship, persistence, or provider checks
  fail, or a patch/delete is a no-op
- Then the revision count and last snapshot remain unchanged

### Scenario: Read revisions pessimistically

- Given active, deleted, and missing parents and existing/missing/zero/negative/
  malformed revision numbers
- When list/detail routes are requested with valid and invalid authorization
- Then only authorized valid existing revisions are returned
- And no public route can infer revision existence or content

## Test evidence

- HTTP lifecycle/revision tests with a controllable clock and stable snapshots.
- PostgreSQL tests for transactional root/revision/reference writes and
  immutability constraints.
- Concurrent/idempotent delete and restore-conflict tests.
- Public privacy tests before/after delete and restore.

## Decisions and open questions

- Decision: restore revalidates all current media relationships before making
  the article active; current behavior must be hardened.
- Decision: revision media retention remains even if the logical asset is
  deleted.
- Open question: specify whether a media integrity failure during article
  restore is `409` or dependency `503` when the provider cannot be reached.
