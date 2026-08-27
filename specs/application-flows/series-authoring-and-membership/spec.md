# Flow: Series authoring and membership

- Status: Draft
- Owner: Igor
- Last updated: 2026-08-27

## Outcome

The owner can create and revise a named series and its many-to-many membership
as one atomic aggregate, while duplicate, missing, deleted, stale, or malformed
member changes cannot corrupt membership or history.

## In scope

- Admin series list, create, get, and merge-patch.
- Slug/title/summary validation, membership replacement/sharing, creation/update
  revisions, no-op behavior, and active-article relationship rules.

## Out of scope

- Public visibility/caching and series delete/restore/revision reads.
- Manual part numbers or membership ordering controls.

## Content and workflow

Creation trims fields, defaults `articleIds` empty, starts Writing, validates
the complete aggregate, writes revision 1, and returns Location/ETag.
Membership may reference active articles in any lifecycle state. IDs are unique
within a series; one article may belong to multiple series.

PATCH omission preserves membership; supplied `articleIds` replaces the entire
set. Root, membership rows, and complete series revision commit atomically.
Article lifecycle/delete changes do not mutate stored membership or series
revision history.

## HTTP contract

- `GET/POST /api/v1/admin/series`
- `GET/PATCH /api/v1/admin/series/{id}`
- Invalid fields/membership/lifecycle -> `400`; active slug conflict or stable
  slug edit -> `409`; missing/deleted patch target -> `404`.
- Create -> `201`, Location, ETag; PATCH -> `200` and current/new ETag.

## Data and integrations

Membership has unique `(series_id, article_id)` and restricted foreign keys.
Production persistence serializes concurrent replacement and article deletion
so no committed membership points to a deleted/missing article at mutation time.

## Security and privacy

Admin views/revisions are protected. Linking a private article does not make any
of its metadata public. Validation errors identify invalid membership without
returning private article content.

## Acceptance scenarios

### Scenario: Create an empty Writing series

- Given an authenticated owner
- When a minimum valid creation request is submitted
- Then a Writing series with empty membership, UUIDv7, UTC timestamps, ETag,
  Location, and complete Created revision commits

### Scenario: Create a series with mixed private memberships

- Given active Writing, Draft, Published, NotListed, and Archived articles
- When their unique IDs are supplied
- Then all memberships commit regardless of lifecycle
- And no member becomes public merely because it was linked

### Scenario: Validate series fields pessimistically

- Given slug/title/summary values below/at/above limits plus blank, invalid,
  wrong-type, and case-insensitive duplicate slugs
- When create and patch are attempted
- Then only valid candidates commit
- And every failure preserves root, membership, and revision state

### Scenario: Reject invalid membership arrays

- Given duplicate, missing, deleted, malformed-GUID, null, wrong-type, and very
  large article-ID arrays
- When create or replacement is attempted
- Then it returns keyed `400`
- And no partial subset or revision commits

### Scenario: Replace the complete membership

- Given a series with articles A/B and active articles B/C
- When PATCH supplies B/C
- Then A is removed, B retained once, C added, and one complete Updated revision
  records B/C
- And the mutation increments version exactly once

### Scenario: Distinguish omitted, empty, and null membership

- Given a populated series
- When `articleIds` is omitted, `[]`, or explicit null
- Then omission preserves membership
- And empty/null follow the documented merge-patch clearing semantics
- And generated clients express each intent unambiguously

### Scenario: Share one article across series

- Given article A belongs to series X
- When A is added to series Y
- Then both relationships exist without duplicating or changing A
- And each series owns only its own membership revision

### Scenario: Keep article lifecycle changes independent

- Given a series links article A
- When A changes lifecycle or is deleted/restored
- Then the membership row and series version/revision remain unchanged
- And only the public series projection changes when visibility changes

### Scenario: Prevent membership/delete races

- Given an active article and a pending series membership replacement
- When article deletion and membership commit race
- Then the restricted relationship/transaction policy yields one consistent
  outcome
- And no committed active membership points to a deleted article unexpectedly

### Scenario: Keep equivalent patch inert

- Given current fields and membership
- When a reordered/same membership or equivalent scalar patch is submitted
- Then semantic order policy determines equivalence consistently
- And a no-op preserves ETag/time/revision/public cache

### Scenario: Roll back a membership write failure

- Given a valid replacement affecting multiple rows
- When failure is injected after removals, during additions, revision write, or
  commit
- Then a fresh read observes the exact original membership and revision
- And retry with the original ETag can succeed once

## Test evidence

- HTTP validation and merge-patch matrix tests.
- PostgreSQL many-to-many, restricted FK, uniqueness, rollback, and race tests.
- Complete immutable membership-snapshot comparisons.
- Cross-series sharing and article-lifecycle independence tests.

## Decisions and open questions

- Decision: `articleIds` is logically a set for equality/uniqueness; public
  order always comes from article creation time, not supplied array order.
- Open question: explicitly settle null membership merge semantics in OpenAPI.
