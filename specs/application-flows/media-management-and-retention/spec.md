# Flow: Media management, reference protection, and restoration

- Status: Draft
- Owner: Igor
- Last updated: 2026-08-27

## Outcome

The owner can inspect and edit descriptive media metadata, safely remove unused
logical assets, and restore verifiably intact objects without breaking current
articles or immutable history.

## In scope

- Admin media list/get/patch/delete/restore.
- Metadata semantics, immutable binary identity, active/revision reference
  protection, provider existence/integrity, retention and physical cleanup.

## Out of scope

- Upload normalization details.
- Replacing bytes in place; replacements always create new media.

## Content and workflow

List/get expose administrative metadata and ETags. PATCH changes only default
alt/caption and never changes bytes, digest, dimensions, type, or URL. DELETE
soft-deletes only when no active article currently references the asset. Objects
needed by immutable revisions remain physically retained. Restore verifies the
remote object exists and matches its digest before clearing deletion.

Article image presentation metadata is captured into the article context during
article mutation. Later media-default edits do not silently rewrite Published
articles or historical revisions.

## HTTP contract

- `GET /api/v1/admin/media` and `GET /api/v1/admin/media/{id}`.
- `PATCH`, `DELETE`, and restore require shared ETag behavior.
- PATCH invalid metadata -> `400`; missing/deleted active target -> `404`.
- DELETE referenced active media -> `409`; success/idempotent repeat -> `204`.
- Restore active/missing target -> `404`; missing or integrity-failed object ->
  `409`; provider unavailable -> `503`; success -> `204`.

## Data and integrations

Current and revision media references use explicit restricted relationships,
not JSON inspection. Physical cleanup deletes only assets with no current or
revision reference after retention. Provider inspect/download checks are
bounded, authenticated, and cancellation-aware.

## Security and privacy

Administrative media exposes only approved Blog metadata. Public article
projections expose opaque URL, contextual alt/caption, and dimensions, never
provider management identifiers or credentials. Deleted/private article media
URLs are not redisclosed by the Blog API.

## Acceptance scenarios

### Scenario: List and read active/deleted media

- Given active and deleted assets with deterministic creation times
- When default/inclusive pages and item reads are requested
- Then list filtering/order/totals and item ETags are correct
- And missing IDs return protected `404`

### Scenario: Patch descriptive metadata

- Given active media with current ETag
- When alt/caption are omitted, set at boundaries, trimmed, replaced, or caption
  is explicitly cleared
- Then only intended metadata changes
- And bytes, URL, digest, dimensions, types, creation time, and provider object
  remain identical

### Scenario: Reject ambiguous or invalid metadata patches

- Given null/blank/over-limit/wrong-type alt/caption, conflicting
  `clearCaption`, and unknown/server-owned properties
- When each patch is submitted
- Then one documented semantic policy is applied
- And every rejection/no-op preserves version, update time, and public caches

### Scenario: Keep contextual article metadata stable

- Given a Published article captured default media alt/caption at mutation time
- When the media defaults are patched
- Then administrative media changes
- But article detail, article revision, and article ETag retain their contextual
  values until an explicit article mutation

### Scenario: Prevent deletion from every active reference position

- Given the asset is referenced by body, editorial, or social position, by one
  or several active articles
- When DELETE uses the current ETag
- Then it returns `409`
- And media/article/public projections remain valid and unchanged

### Scenario: Allow logical delete after current references leave

- Given all referencing articles are changed or soft-deleted
- When media DELETE succeeds
- Then the media becomes administratively deleted and its version increments
- And no current article has a dangling active reference
- And bytes remain when any immutable revision references them

### Scenario: Retry delete without duplicate effects

- Given an already deleted asset
- When DELETE is repeated with one arbitrary `If-Match`
- Then it returns `204`
- And version/timestamps/provider object/reference rows remain unchanged

### Scenario: Restore an intact object

- Given a soft-deleted media record whose immutable remote bytes exist and match
  the digest
- When restore uses the current ETag
- Then it becomes active with one version/time change
- And identity, URL, bytes, and creation metadata remain unchanged

### Scenario: Reject missing or corrupted object restoration

- Given remote object missing, wrong bytes/digest, wrong metadata, or unversioned
  URL
- When restore is attempted
- Then it returns `409`
- And the record remains deleted with unchanged version/time

### Scenario: Distinguish provider outage from integrity conflict

- Given provider timeout/auth/rate-limit versus a verified missing/corrupt object
- When restore inspects the object
- Then outage returns `503` and integrity result returns `409`
- And both preserve deleted state and permit safe retry

### Scenario: Retain historical assets during cleanup

- Given assets referenced currently, only by revisions, by both, or by neither,
  across active/deleted logical states and retention ages
- When garbage collection and reconciliation run
- Then only eligible unreferenced expired objects are deleted
- And races with new references are transactionally prevented or rechecked

### Scenario: Recover an article/media lifecycle safely

- Given an article was deleted, its logical media was deleted, and history still
  retains the object
- When article restore is attempted before and after media restore
- Then the first attempt fails atomically
- And restoring media then article produces an active Draft with valid references

## Test evidence

- HTTP patch/delete/restore matrix tests with failure-injectable storage.
- PostgreSQL restricted-reference and cleanup-race tests.
- Snapshot/cache tests across media-default and article-context changes.
- Provider integrity and outage contract tests.

## Decisions and open questions

- Decision: logical delete may occur for revision-only references, but physical
  bytes remain retained.
- Decision: media PATCH requires explicit, documented null/blank/decorative
  semantics before approval.
- Open question: define retention duration and the provider operation used for
  digest verification.
