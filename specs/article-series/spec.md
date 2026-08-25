# Feature: Article series

- Status: Approved
- Owner: Igor
- Last updated: 2026-08-25

## Outcome

The site owner can group a subject that is too long for one post into a named
series, and visitors can read its published articles from oldest to newest.

## In scope

- Administrative series CRUD, lifecycle transitions, soft deletion, restore,
  and many-to-many article membership.
- Public series lookup by stable slug.
- Immutable creation-time member ordering.
- Complete series revision history, including membership changes.
- Compatibility with the approved Platform foundation and Article publishing
  contracts.

## Out of scope

- Cross-schema Presentation relationships, manually reorderable parts,
  automatic article splitting, and cascading article changes.

## Content and workflow

A series uses the same `Writing`, `Draft`, `Published`, `NotListed`, and
`Archived` visibility meanings and allowed transitions as an article. Creation
starts in `Writing`. Publishing requires a slug and title but does not require a
member, so a series may be published before its first article is ready.

The series slug may change before first publication and becomes immutable once
the series has ever been Published. First publication records an immutable
`publishedAt`; later hiding, archiving, and republication preserve it.

Changing an article's lifecycle or deleting it does not change series
membership. Public projection filters members at read time. Restoring a deleted
series places it in `Draft` so restore cannot accidentally republish it.

## HTTP contract

- `GET /api/v1/series/{slug}` returns one Published series and its Published,
  non-deleted articles ordered by `createdAt ASC`, then UUID.
- A missing, deleted, Writing, Draft, NotListed, or Archived series returns the
  same `404` representation.
- Articles in Writing, Draft, NotListed, Archived, or deleted state never appear
  publicly, even when linked to a Published series.
- Protected management operations are:
  - `GET` and `POST /api/v1/admin/series`
  - `GET`, `PATCH`, and `DELETE /api/v1/admin/series/{id}`
  - `POST /api/v1/admin/series/{id}/restore`
  - `GET /api/v1/admin/series/{id}/revisions`
  - `GET /api/v1/admin/series/{id}/revisions/{revisionNumber}`
- Series fields are `slug`, `title`, nullable `summary`, `status`, and
  `articleIds[]`.
- The public `articles[]` collection reuses the complete Article publishing feed
  summary schema: `id`, `slug`, `title`, `summary`, `publishedAt`,
  `readingTimeMinutes`, nullable `topic`, and nullable editorial `image`. Series
  adds no alternate article-summary type.
- PATCH uses JSON Merge Patch. Supplying `articleIds` replaces the complete
  membership set; omission leaves membership unchanged.
- Mutations require `If-Match` and use `428`/`412` precondition behavior.
- Management authorization, Problem Details, pagination, ETags, JSON Merge
  Patch, and idempotent-delete behavior follow Platform foundation.
- Successful public responses emit a strong representation ETag and
  `Cache-Control: public, max-age=60, stale-while-revalidate=300`; matching
  `If-None-Match` returns `304` without a body.

## Data and integrations

- Tables are `blog.article_series`, `blog.article_series_memberships`, and
  `blog.article_series_revisions`.
- One logical database is shared with the Presentation API, but this feature is
  owned exclusively by the Blog API principal and migration history.
- Membership has a unique `(series_id, article_id)` key and restricted foreign
  keys. An article can belong to zero, one, or multiple series.
- A membership mutation may link any active article lifecycle state but rejects
  a missing or soft-deleted article ID. Lifecycle changes after linking do not
  change the membership row.
- Series and articles have immutable `created_at`, soft-deletion timestamps,
  and explicit incrementing concurrency versions.
- Series restore changes its lifecycle to Draft and records that resulting state
  in the restore revision.
- Active slugs are case-insensitively unique. A once-published slug is immutable;
  restore fails on an active slug conflict.
- No database relationship cascades. Membership changes are applied explicitly
  in the series mutation transaction.
- Creation writes series revision 1 with operation `Created`. Each later
  successful state-changing mutation writes the next immutable sequential
  revision with the complete series and membership snapshot, operation,
  `changed_at`, actor `site-owner`, and request correlation ID in the same
  transaction. A no-op PATCH and repeated idempotent DELETE write no revision
  and do not increment the ETag version.

## Security and privacy

- Management operations require the Blog API's protected authoring credential.
- The credential and HTTPS behavior are defined by Platform foundation.
- Public routes disclose only Published series and Published member articles.
- Every non-public state, including NotListed, is indistinguishable from a
  missing resource on public routes.
- The Blog API never reads or writes the `presentation` schema.

## Failure and operational behavior

- Reject duplicate article IDs, missing articles, and invalid lifecycle values
  with Validation Problem Details.
- A stale concurrency token returns `412`; missing `If-Match` returns `428`.
- Deleting an already deleted series is idempotent and returns `204` when
  `If-Match` is present.
- A failed membership or revision write rolls back the complete mutation.
- A Published series with no Published members may be returned with an empty
  `articles` collection; private member metadata is never exposed.
- Article lifecycle changes invalidate any affected public series
  representation without mutating the membership or series revision history.

## Acceptance scenarios

### Scenario: Read a published series

- Given a Published series contains Published articles created on different
  dates
- When its public slug is requested
- Then the series is returned with those articles ordered oldest first

### Scenario: Hide a NotListed member

- Given a Published series contains Published and NotListed articles
- When its public slug is requested
- Then only the Published articles appear and no NotListed metadata is exposed

### Scenario: Share an article between series

- Given an article already belongs to one series
- When it is added to a second series
- Then both memberships exist without duplicating the article

### Scenario: Preserve a membership revision

- Given a series membership changes
- When the transaction commits
- Then a new revision records the complete resulting membership and actor
  `site-owner`

### Scenario: Restore without accidental publication

- Given a previously Published series is soft-deleted
- When it is restored with the current ETag and no slug conflict exists
- Then it becomes Draft and remains absent from the public route

## Test evidence

- PostgreSQL integration tests for many-to-many membership, restricted foreign
  keys, lifecycle privacy, ordering, soft deletion, restore conflicts,
  concurrency, transaction rollback, and immutable revisions.
- API integration tests proving all private states share the public `404`
  representation.
- Contract tests for revision routes, conditional public reads, and compatibility
  with the Article publishing summary projection.

## Decisions and open questions

- Decision: creation date, not a manual part number, determines series order.
- Decision: NotListed is private on every public route.
- Decision: authentication and shared HTTP behavior come from Platform
  foundation rather than being redefined by this feature.
- Decision: public series members reuse the complete Article publishing feed
  summary so generated clients and frontend caches share one schema.
