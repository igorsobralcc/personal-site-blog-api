# Flow: Series publication, public delivery, recovery, and history

- Status: Draft
- Owner: Igor
- Last updated: 2026-08-27

## Outcome

A series can be intentionally published, hidden, archived, deleted, and safely
restored while visitors see only its currently Published active articles in a
correctly invalidated cacheable representation.

## In scope

- Series lifecycle state machine and first-publication identity.
- `GET /api/v1/series/{slug}` member filtering/order/projection/caching.
- Series delete, restore, revision list/detail, slug conflicts, Draft-on-restore.

## Out of scope

- Membership authoring validation, covered separately.
- Manual part ordering and cascading article mutations.

## Content and workflow

Series use the article lifecycle matrix. First publication requires slug/title
but permits zero members, sets stable `publishedAt`, and freezes the slug.
Public detail filters stored membership at read time to active Published
articles, orders by article `createdAt ASC` then UUID ASC, and reuses the exact
article summary projection.

Delete writes one Deleted revision and removes public visibility without
changing membership. Restore revalidates the stable slug, forces Draft, and
writes one Restored revision. Revisions contain complete membership. Member
articles deleted while the series is deleted remain stored memberships and are
filtered from public projection under the approved relationship rules.

## HTTP contract

- `GET /api/v1/series/{slug}` -> `200`, exact-match `304`, or indistinguishable
  private/deleted/missing `404`.
- Admin DELETE/restore and revision list/detail follow shared authorization,
  ETag, idempotency, and Problem Details behavior.
- Public success includes strong representation ETag and required cache control.

## Data and integrations

The public ETag derives from the complete returned representation, including
series fields and all projected member fields/visibility/order. It must not use
collision-prone sums of platform hash codes. Article changes invalidate the
representation without writing a series revision.

## Security and privacy

Private series states and missing are indistinguishable. Private/deleted member
metadata never appears in body, ETag derivation observable content, errors, or
logs. Series revisions remain admin-only.

## Acceptance scenarios

### Scenario: Exercise the complete series lifecycle matrix

- Given a series in each state
- When every allowed and forbidden transition is attempted
- Then allowed changes commit one version/revision with correct visibility
- And forbidden changes return `400` with no root/membership/revision/cache
  mutation

### Scenario: Publish an empty series

- Given a Draft series with valid slug/title and no members
- When it becomes Published
- Then first publication fields/revision commit
- And public detail returns `articles: []`

### Scenario: Reject incomplete publication

- Given Draft series independently missing slug or title
- When publication is requested
- Then it returns `400`, remains private, has no `publishedAt`, and writes no
  revision

### Scenario: Preserve stable publication identity

- Given a published series hidden/archived and later republished through allowed
  transitions
- When slug edits are attempted before and after first publication
- Then only pre-publication edits succeed
- And the original slug and `publishedAt` remain stable thereafter

### Scenario: Make private series indistinguishable

- Given Writing, Draft, NotListed, Archived, deleted, and missing series slugs
- When each public route is requested
- Then all return the same `404` contract without existence/membership leakage

### Scenario: Filter and order members at read time

- Given a Published series links active/deleted articles across every lifecycle
  state with creation-time/UUID ties
- When public detail is requested
- Then only active Published members appear oldest first, then UUID
- And membership/revisions remain unchanged by filtering

### Scenario: Reuse the exact article summary schema

- Given members with topic/editorial image/reading-time variations
- When public series is read
- Then each member matches the feed summary schema and excludes body/admin fields
- And provider-specific media data never leaks

### Scenario: Invalidate cache for every returned change

- Given a cached series representation
- When series metadata/membership/lifecycle changes, or a member's visibility,
  title, summary, topic, image, reading time, or publication metadata changes
- Then the complete representation ETag changes or the series becomes `404`
- And the old ETag never returns `304` for changed content

### Scenario: Resist ETag collisions and ordering insensitivity

- Given distinct member sets/representations engineered to share GUID hash sums
  or permutations
- When ETags are calculated
- Then different returned representations never intentionally share an ETag
- And identical serialized representations do share it deterministically across
  processes

### Scenario: Delete and retry safely

- Given a Published series with membership
- When it is deleted and DELETE is retried
- Then exactly one version/timestamp/Deleted revision occurs
- And membership persists while public detail becomes `404`

### Scenario: Restore to Draft or reject slug conflict

- Given a deleted formerly Published series with intact membership
- When restore is attempted with no conflict, then in a separate case with an
  active case-insensitive slug conflict
- Then valid restore forces Draft and preserves `publishedAt`/membership
- And conflict returns `409` with the entire deleted state unchanged

### Scenario: Preserve deleted member relationships through restore

- Given a member article was deleted while its series was deleted
- When the series is restored
- Then restore succeeds to Draft without rewriting membership
- And the deleted member remains absent from any later public projection
- And an explicit membership patch is required to remove the relationship

### Scenario: Preserve and protect revision history

- Given creation, metadata/membership/lifecycle updates, delete, and restore
- When revisions are listed/read for active/deleted parents and invalid numbers
- Then successful revisions are sequential complete immutable snapshots
- And rejected/no-op requests create none
- And unauthorized/public clients cannot access them

## Test evidence

- Lifecycle/privacy/cache HTTP matrix tests.
- Deterministic representation hashing tests, including collision adversaries.
- PostgreSQL delete/restore/revision/membership integrity tests.
- Contract compatibility test against article summary schema.

## Decisions and open questions

- Decision: series ETag hashes the complete canonical public representation;
  current GUID hash-sum behavior must be replaced.
- Decision: article changes invalidate public series without creating a series
  revision.
- Decision: restore does not reject members deleted after they were linked;
  lifecycle filtering and explicit membership repair preserve the approved
  non-cascading relationship behavior.
