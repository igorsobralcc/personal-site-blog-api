# Feature: Article publishing

- Status: Approved
- Owner: Igor
- Last updated: 2026-08-25

## Outcome

The site owner can safely draft, revise, publish, hide, archive, and recover a
technical article, while visitors can browse and read only published content
through stable, cacheable contracts.

## In scope

- Protected article creation, listing, lookup, merge-patch updates, soft
  deletion, restoration, and immutable revision history.
- A public cursor-paginated article feed and public lookup by stable slug.
- Structured content blocks, derived reading time, topics and tags, SEO
  metadata, conditional requests, and deterministic publication ordering.
- A complete OpenAPI contract for article management and public reads after
  this specification is Approved.

## Out of scope

- Multiple authors, comments, reactions, newsletters, analytics, full-text
  search, tag archive routes, and related-article recommendations.
- Scheduled publication, preview tokens, automatic translation, collaborative
  editing, and external syndication.
- Media upload, transformation, object retention, and delivery rules, which are
  defined by the Article media specification.
- Arbitrary HTML, Markdown rendering, executable embeds, iframes, and client
  scripts in article content.

## Content and workflow

An article has `Writing`, `Draft`, `Published`, `NotListed`, or `Archived`
status. `Writing` is incomplete author work; `Draft` is complete but private;
`Published` is anonymously readable; `NotListed` is intentionally withheld
from every anonymous route; and `Archived` is retained history that is also
private.

Allowed transitions are:

- `Writing` to `Draft`
- `Draft` to `Writing`, `Published`, or `NotListed`
- `Published` to `NotListed` or `Archived`
- `NotListed` to `Draft`, `Published`, or `Archived`
- `Archived` to `Draft`

Creation always starts in `Writing`. Publishing requires a valid slug, title,
summary, and at least one body block. `publishedAt` is assigned on first
publication and remains stable through later hiding, archiving, and
republication. Restoring a soft-deleted article places it in `Draft` so restore
cannot accidentally make content public.

The authoring and delivery format is a versioned structured-block document.
Version 1 supports:

- `paragraph`: required plain `text`
- `heading`: required plain `text`, rendered as a level-two section heading
- `quote`: required plain `text`
- `list`: non-empty plain-text `items[]` and optional `ordered`
- `code`: required plain `code` and optional language identifier
- `image`: required Blog `mediaId`, optional contextual `alt` override, and
  optional plain-text `caption` override
- `table`: required plain-text `caption`, non-empty `headers[]`, and `rows[]`
  whose cell count matches the header count

Unknown block types are rejected on management writes. Public responses include
`bodyVersion: 1`, allowing a future additive block type or format version to be
contracted deliberately. All text is data, not markup; consumers render it
with semantic components and never inject it as HTML.

Article metadata uses these initial limits after trimming surrounding
whitespace: slug 1 through 160 characters, title 1 through 200, summary 1 through
500, nullable topic 1 through 80, nullable SEO title 1 through 70, and nullable
SEO description 1 through 180. A slug matches
`^[a-z0-9]+(?:-[a-z0-9]+)*$`. An article has at most 20 tags, each 1 through 40
characters after trimming. A structured body has at most 500 blocks and its
serialized JSON representation cannot exceed 1 MiB. Writing articles may have
an empty body; publication requires at least one valid non-empty block.

## HTTP contract

Anonymous operations are:

- `GET /api/v1/articles?cursor={cursor}&limit={limit}`
- `GET /api/v1/articles/{slug}`

The feed defaults `limit` to `8` and accepts `1` through `50`. It returns
`items[]` and nullable `nextCursor`. Items contain `id`, `slug`, `title`,
`summary`, `publishedAt`, `readingTimeMinutes`, nullable `topic`, and nullable
editorial `image`; they never contain the body. Results order by `publishedAt
DESC`, then UUID `DESC`. The opaque cursor implements seek pagination from
those keys and cannot be combined with unpublished rows. It is a URL-safe,
versioned payload containing only the last public ordering keys plus an HMAC.
It does not expire, does not require the prior article to remain public, and is
rejected when its version, shape, timestamp, UUID, or signature is invalid.

Article detail contains the summary fields plus `updatedAt`, `tags[]`,
`bodyVersion`, `body[]`, resolved `canonicalUrl`, resolved `seoTitle`, resolved
`seoDescription`, and nullable `socialImage`. SEO title falls back to `title`,
SEO description falls back to `summary`. Canonical URL is not author-writable in
the first slice; the API always derives it from the configured public site
origin plus `/articles/{slug}`.

Administrative image fields contain a Blog `mediaId`. Public image fields are
resolved through Article media and contain `url`, contextual `alt`, intrinsic
`width` and `height`, and nullable `caption`. Editorial and social images follow
the same administrative-reference and public-projection boundary.

Protected management operations are:

- `GET` and `POST /api/v1/admin/articles`
- `GET`, `PATCH`, and `DELETE /api/v1/admin/articles/{id}`
- `POST /api/v1/admin/articles/{id}/restore`
- `GET /api/v1/admin/articles/{id}/revisions`
- `GET /api/v1/admin/articles/{id}/revisions/{revisionNumber}`

Administrative representations include `status`, authoring body, unresolved
SEO and media fields, lifecycle timestamps, and the ETag, but not internal
normalized values. PATCH uses JSON Merge Patch. Supplying `body` or `tags`
replaces that complete collection; omission leaves it unchanged. Status changes
are requested through PATCH and validated as lifecycle transitions.

POST returns `201` with the representation, `Location`, and ETag. Successful
PATCH returns `200` with the representation and new ETag. DELETE and restore
return `204`. Mutations follow the platform `If-Match`, `428`, and `412`
behavior.

The slug is lowercase ASCII words separated by single hyphens. It may change
before first publication, but becomes immutable once `publishedAt` exists.
Attempts to reuse an active case-insensitive slug or change a stable published
slug return `409` Problem Details.

## Data and integrations

- Tables are `blog.articles`, `blog.article_tags`,
  `blog.article_tag_memberships`, and `blog.article_revisions`.
- Structured bodies and complete revision snapshots use PostgreSQL `jsonb` and
  include an explicit `body_version`. The API validates their complete shape
  before persistence.
- Active slugs and normalized tag names are case-insensitively unique. Tag
  display text is preserved, while matching trims whitespace and ignores case.
- An article has zero or one primary `topic` and zero or more tags. Tags supplied
  in an article mutation replace the complete membership set in the same
  transaction; duplicate normalized tags are rejected.
- `readingTimeMinutes` is server-derived and cannot be written by clients. The
  version 1 algorithm counts body headings, paragraphs, quotes, list items,
  image captions, table captions, headers, and cells as prose at 200 words per
  minute. A word is one invariant Unicode letter-or-number sequence with
  internal apostrophes or hyphens retained. Code contributes its non-blank lines
  at 12 lines per minute. Title, summary, image alt text, and markup metadata do
  not contribute. The API adds prose and code durations, divides by one minute,
  rounds up, and returns a minimum of one.
- Creation writes revision 1 with operation `Created`. Each later successful
  state-changing mutation writes the next immutable sequential revision with
  the complete resulting article and tag snapshot, operation, `changedAt`, actor
  `site-owner`, and request correlation ID in the same transaction. A no-op
  PATCH and an idempotent repeated DELETE write no revision and do not increment
  the ETag version.
- Revision rows and unused tags are never publicly exposed. Deleting an article
  does not delete its revisions or shared tag records.
- Media references use restricted foreign keys to Article media. An article
  mutation validates every referenced asset and applies current and revision
  reference-row changes in the article transaction; it never deletes a media
  asset implicitly or relies on a foreign key embedded in `jsonb`.
- Article series membership is owned by the Article series feature and is not
  changed implicitly by article lifecycle mutations.

## Security and privacy

- Management operations inherit the Platform foundation authorization policy.
- Only active `Published` articles appear in anonymous responses. A missing,
  deleted, Writing, Draft, NotListed, or Archived article returns the identical
  public `404` representation.
- Structured blocks contain no raw HTML. Code remains inert text; text fields
  are serialized as JSON data and escaped by renderers. Public image URLs come
  only from validated Blog media records.
- Image alt text and intrinsic dimensions are required to prevent inaccessible
  or layout-unstable public content.
- Revision content is sensitive authoring data and is available only through
  protected management routes.

## Failure and operational behavior

- Invalid blocks, lifecycle transitions, cursors, limits, media references,
  duplicate tags, and publication-incomplete articles return Validation Problem
  Details.
- A stale concurrency token returns `412`; missing `If-Match` returns `428`.
- A slug or restore conflict returns `409`. A restore conflict leaves the
  article deleted and writes no revision.
- A malformed cursor or unsupported cursor version returns `400`; it never
  falls back to the first page because that could duplicate already rendered
  entries.
- Public feed and detail responses send strong representation ETags and
  `Cache-Control: public, max-age=60, stale-while-revalidate=300`. Matching
  `If-None-Match` returns `304` without a body.
- A publication, hide, archive, deletion, or visible edit invalidates the
  affected detail representation and subsequent feed representation. Private
  edits do not change a public cache key until publication.
- A failed root, body, tag membership, or revision write rolls back the complete
  mutation.
- Within `/api/v1`, published response properties are not removed, renamed, or
  given narrower meanings. New optional metadata may be additive. A new required
  field, incompatible cursor payload, or structured block that existing
  generated clients cannot safely consume requires coordinated frontend support
  and either a new body version or `/api/v2` as appropriate.

## Acceptance scenarios

### Scenario: Publish and read an article

- Given a Draft article has valid metadata and structured body blocks
- When the site owner changes its status to Published with the current ETag
- Then the mutation records `publishedAt` and a revision atomically
- And anonymous feed and slug requests expose the published representation

### Scenario: Keep private states indistinguishable

- Given articles exist in Writing, Draft, NotListed, Archived, deleted, and
  missing states
- When each corresponding slug is requested anonymously
- Then every request returns the same `404` Problem Details shape

### Scenario: Continue a stable article feed

- Given more Published articles exist than the requested limit
- When the next page is requested with `nextCursor`
- Then it continues after the last prior ordering key without duplicates
- And no private article contributes to the cursor or result

### Scenario: Reject unsafe content

- Given an article patch contains an HTML block, unknown media ID, or reference
  to deleted media
- When the site owner submits the patch
- Then validation returns `400` and no article or revision changes

### Scenario: Preserve a revision

- Given an article title, body, or tags change successfully
- When the transaction commits
- Then the next sequential revision stores the complete resulting snapshot and
  actor `site-owner`

### Scenario: Restore without accidental publication

- Given a previously Published article is soft-deleted
- When it is restored with the current ETag and no slug conflict exists
- Then it becomes Draft, remains absent from public routes, and records a
  restore revision

### Scenario: Reuse a cached article

- Given a client has the ETag for a Published article
- When it requests the article with matching `If-None-Match`
- Then the API returns `304` without a body

### Scenario: Derive deterministic reading time

- Given an article body contains prose, a caption, and code with blank lines
- When the article is created or its body changes
- Then reading time uses the version 1 prose-word and non-blank-code-line rates
- And repeated calculation for the same body returns the same whole minutes

## Test evidence

- OpenAPI contract tests for every route, schema, status, content type, header,
  structured block discriminator, and generated TypeScript client shape.
- PostgreSQL integration tests for lifecycle transitions, first-publication
  timestamp stability, slug uniqueness and immutability, tag normalization,
  seek ordering, soft deletion, restore, concurrency, rollback, and immutable
  revisions.
- API integration tests for public-state privacy, summary-versus-detail fields,
  malformed cursors, conditional requests, cache invalidation, SEO fallbacks,
  derived reading time, and rejection of unsafe or unknown blocks.
- Frontend compatibility tests mapping the OpenAPI-generated summary, page,
  detail, and block unions to the Articles index, Home, and Article reader
  features.
- Article media integration tests proving administrative media IDs resolve to
  provider-neutral public URLs with intrinsic dimensions.

## Decisions and open questions

- Decision: use structured blocks rather than Markdown or HTML so the API and
  frontend share a safe, versioned rendering contract.
- Decision: NotListed content is private on every anonymous route; it is not an
  unlisted-by-link publication mode.
- Decision: the first publication timestamp and slug remain stable across later
  lifecycle changes.
- Decision: scheduling, full-text search, and public tag routes remain separate
  future specifications; upload and delivery behavior comes from Article media.
- Decision: reading time version 1 combines prose at 200 words per minute and
  non-blank code at 12 lines per minute, rounded up to at least one minute.
- Decision: version 1 compatibility preserves existing public fields and block
  meanings; incompatible schemas use explicit body or HTTP versioning.
