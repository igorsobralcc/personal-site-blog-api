# Flow: Article authoring and validation

- Status: Draft
- Owner: Igor
- Last updated: 2026-08-27

## Outcome

The site owner can create and revise a structurally safe article while invalid
metadata, content, tags, media, or patch shapes are rejected without changing
the article or its immutable history.

## In scope

- Admin article list, create, get, and merge-patch.
- Metadata normalization/limits, structured body version 1, tags, media
  references, derived reading time, UUID/timestamps, and creation/update
  revisions.

## Out of scope

- Public feed/detail visibility and caching.
- Delete, restore, and revision reads.
- Media byte ingestion.

## Content and workflow

Creation trims nullable strings, converts whitespace-only strings to null,
defaults body/tags empty, assigns UUIDv7 and UTC timestamps, and always starts
in `Writing`. It validates the complete aggregate, derives reading time and
current media references, writes revision 1 (`Created`), then returns `201`,
`Location`, ETag, and the administrative representation.

PATCH clones the current active aggregate, applies JSON Merge Patch, validates
the entire candidate, detects a semantic no-op, and otherwise commits exactly
one new version and `Updated` revision. Omitted fields remain unchanged;
explicit null clears nullable fields; supplied body/tags arrays replace their
complete collections.

## HTTP contract

- `GET/POST /api/v1/admin/articles`
- `GET/PATCH /api/v1/admin/articles/{id}`
- Create validation returns keyed `400` errors and creates no resource.
- Patch requires the current strong ETag and returns `200` with the current or
  next ETag.
- Missing item, or deleted item for PATCH, returns `404`.
- Administrative reads may retrieve a deleted item by ID and expose its ETag.

## Data and integrations

Article root, tag memberships, current media references, and immutable revision
commit in one transaction. Media IDs must resolve to active Blog media. Reading
time is derived server-side and is not client-writable.

Body version 1 permits paragraph, heading, quote, list, code, image, and table
blocks only. Its serialized JSON is at most 1 MiB and contains at most 500
blocks.

## Security and privacy

Blocks contain inert JSON data, never arbitrary HTML, Markdown execution,
iframes, or scripts. Administrative authoring content and revision correlation
data remain protected. Validation errors do not echo complete bodies or secrets.

## Acceptance scenarios

### Scenario: Create the minimum Writing article

- Given an authenticated owner and an empty store
- When an empty valid creation document is submitted
- Then a Writing article with empty body/tags, reading time 1, UUIDv7, UTC
  timestamps, ETag `"1"`, Location, and Created revision is returned
- And no public route exposes it

### Scenario: Create a complete structured article

- Given active editorial, social, and body media assets
- When all metadata and every supported block type are supplied at legal limits
- Then the trimmed/normalized aggregate, derived media-reference set, and
  deterministic reading time commit atomically
- And revision 1 is a complete immutable snapshot

### Scenario: Reject every metadata boundary violation

- Given independent values just below, at, and above each limit
- When slug, title, summary, topic, SEO title, or SEO description is created or
  patched with blank/invalid/over-limit content
- Then only legal values commit
- And each rejection identifies the correct property and preserves all state

### Scenario: Reject conflicting active slugs

- Given an active article with a slug
- When another create or pre-publication patch requests the same slug with any
  case variation
- Then it returns `409`
- And neither article, tag, media reference, nor revision is partially changed

### Scenario: Validate tags pessimistically

- Given tag arrays at 0, 20, and 21 entries and values at 1 and 40 trimmed
  characters
- When blank, null, wrong-type, over-limit, whitespace-variant duplicate, and
  case-variant duplicate tags are submitted
- Then valid display values follow one consistent trimming rule
- And every invalid array returns `400` with no membership/revision change

### Scenario: Accept each supported body block

- Given valid active media where required
- When paragraph, heading, quote, list, code, image, and table blocks are
  submitted individually and together
- Then their semantic data is retained without execution or HTML interpretation
- And body version remains 1

### Scenario: Reject malformed blocks exhaustively

- Given each block discriminator
- When required properties are absent, null, wrong-type, blank, empty, or table
  rows do not match headers, or the discriminator is absent/unknown/wrong-case
- Then the request returns `400` keyed to body
- And root, body, tags, references, derived values, and revisions are unchanged

### Scenario: Reject invalid body media

- Given missing, malformed-GUID, deleted, and valid media IDs
- When each is used in an image block or top-level editorial/social field
- Then only active valid media commits
- And failure creates no dangling current or revision reference

### Scenario: Enforce body size boundaries

- Given bodies at 500/501 blocks and serialized sizes just below/at/above 1 MiB
- When they are created and patched
- Then only contract-compliant bodies commit
- And oversized request/framework behavior remains distinct from body validation

### Scenario: Apply merge-patch semantics

- Given a populated Writing or Draft article
- When properties are omitted, nullable scalars are set null, and body/tags are
  omitted, null, or replaced
- Then omission preserves, null clears where legal, and arrays replace fully
- And wrong scalar/array/GUID/enum types return safe `400` without mutation

### Scenario: Ignore or reject unknown patch properties consistently

- Given an otherwise valid patch containing an unknown property
- When it is submitted
- Then runtime and OpenAPI behavior follow one documented policy
- And an unknown property can never write a server-derived/internal field

### Scenario: Derive reading time at every boundary

- Given empty content; 1, 200, and 201 prose words; 1, 12, and 13 nonblank code
  lines; mixed fractional durations; Unicode/apostrophe/hyphen words; and blank
  code lines
- When each body is created or changed
- Then title/summary/alt/metadata are excluded, specified prose fields are
  included, contributions add before ceiling, and the minimum is one
- And identical bodies always produce identical results

### Scenario: Keep failed and no-op patches inert

- Given a current article, including reading time above one
- When validation fails or the patch is semantically equivalent
- Then the original ETag, update time, derived fields, references, and revision
  count remain unchanged

## Test evidence

- Boundary/mutation theory tests for all scalar, tag, and body rules.
- HTTP tests for representations, Location, ETag, Problem Details, and merge
  patch binding.
- PostgreSQL integration tests for aggregate transaction, normalized tag/slug
  uniqueness, restricted media references, and immutable snapshots.
- Property-based reading-time tests for deterministic Unicode tokenization and
  rounding invariants.

## Decisions and open questions

- Decision: creation and patch must store tags with the same normalization;
  current create/patch divergence must be resolved before approval.
- Decision: unknown patch properties require one explicit policy across runtime
  and OpenAPI.
- Open question: define validation limits for optional block properties such as
  code language and contextual image caption/alt.
