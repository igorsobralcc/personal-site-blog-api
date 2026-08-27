# Blog API application flow map

This document maps the complete Blog API surface for test design. Business
behavior is emphasized, but platform, HTTP, storage, caching, and operational
flows are included because failures in those layers change observable business
outcomes.

## How to read this map

- **Contract** means behavior required by the approved specifications.
- **Current** means behavior implemented by the in-memory executable slice.
- **Gap** means the current implementation differs from, or does not yet
  implement, the approved production behavior.
- Priorities are test-planning priorities: **P0** protects publication,
  privacy, data integrity, and concurrency; **P1** protects validation and
  complete endpoint behavior; **P2** protects operational and contract quality.

The approved specifications and `docs/openapi.yaml` remain authoritative. This
map is an index for tests, not a replacement contract.

## 1. Request and dependency flow

```text
request
  -> HTTPS redirection
  -> CORS policy
  -> admin-key middleware (only /api/v1/admin/*)
       -> 401 and stop when missing, invalid, or unconfigured
  -> request logging middleware
  -> route selection and ASP.NET parameter/body binding
  -> endpoint handler
       -> shared paging/concurrency checks
       -> aggregate validation and business rules
       -> BlogStore mutation/read
       -> IMediaStorage call for upload/restore only
  -> response (JSON, Problem Details, 204, or 304)
  -> request completion log

unhandled exception
  -> exception handler
  -> framework-generated 500 Problem Details
```

Current dependencies are singleton `BlogStore`, singleton in-memory media
storage, `TimeProvider.System`, configuration, and health-check services. The
store is process-local and is lost at restart. Approved production flows add
PostgreSQL transactions in the Blog-owned `blog` schema and Cloudinary.

### Full route inventory

| Area | Operations | Main success results |
| --- | --- | --- |
| Operations | `GET /health/live`, `GET /health/ready` | `200` healthy |
| Public articles | feed and slug detail | `200`, conditional `304` |
| Public series | slug detail | `200`, conditional `304` |
| Admin articles | list, create, get, patch, delete, restore, revision list/detail | `200`, `201`, `204` |
| Admin media | list, upload, get, patch, delete, restore | `200`, `201`, `204` |
| Admin series | list, create, get, patch, delete, restore, revision list/detail | `200`, `201`, `204` |

## 2. Shared platform flows

### 2.1 Administrative authentication

All `/api/v1/admin/*` requests pass through the same gate before binding or
store access.

1. Read configured `AdminKey` and request `X-Admin-Key`.
2. If either is empty, return `401 application/problem+json`.
3. Compare UTF-8 bytes with `FixedTimeEquals`.
4. On mismatch, return the same `401` shape.
5. On match, continue to the endpoint.

Test branches: absent header, empty header, wrong value, wrong length, multiple
header values, absent configuration, valid value, public route without a key,
and confirmation that the credential never appears in response or logs. The
contract also requires no persistence operation on rejection.

**Current caveat:** authentication runs before request logging, so rejected
admin calls do not pass through `RequestLoggingMiddleware`.

### 2.2 Administrative pagination

Article, media, and series lists share header pagination.

- `X-Page`: default `1`, integer greater than zero.
- `X-Page-Size`: default `20`, integer from `1` through `100`.
- `X-Include-Deleted`: default `false`; parsed `true` includes active and
  deleted rows.
- Results sort by `createdAt DESC`, then use offset paging.
- Response contains `items`, `pageNumber`, `pageSize`, `totalItems`, and
  `totalPages` in the current implementation.

Exception branches: non-integer page or size, zero/negative page, zero/negative
size, size above 100 -> `400` validation Problem Details with `errors.headers`.
An invalid include-deleted value currently behaves as `false` rather than
returning validation failure. Pages after the end return an empty `items`
array. An empty collection currently has `totalPages = 0`.

**Contract drift to characterize:** the platform spec calls the field `page`,
while the shared record serializes `pageNumber`.

### 2.3 Optimistic concurrency and mutation idempotency

Item `GET`, create, and successful `PATCH` responses expose strong numeric ETags
such as `"1"`.

For active `PATCH`, first `DELETE`, and restore:

- no `If-Match` -> `428 Precondition Required`;
- multiple values, wildcard, weak, malformed, or stale value -> `412
  Precondition Failed`;
- exactly one current strong value -> evaluate business rules and mutate;
- rejected mutations preserve state, version, timestamps, and revisions;
- successful state changes increment version exactly once;
- no-op PATCH returns `200` with the same ETag and creates no revision;
- repeated DELETE requires exactly one `If-Match` header but intentionally
  ignores its value, returns `204`, and does not mutate again.

Order matters and should be asserted: missing/deleted lookup happens before
precondition evaluation for PATCH and restore; existing deleted resources take
the idempotent DELETE path.

### 2.4 Problem, routing, and binding flows

Handler-generated errors use `application/problem+json` and include `type`,
`title`, `status`, optional `detail`, `traceId`, and optional keyed `errors`.

| Result | Application meaning |
| --- | --- |
| `400` | validation, invalid cursor/limit, invalid patch value/shape, invalid upload |
| `401` | missing/invalid/unconfigured admin credential |
| `404` | missing admin resource; deleted resource where active is required; any non-public public resource |
| `409` | uniqueness, immutable slug, referenced media, missing remote object on restore |
| `412` | invalid or stale `If-Match` |
| `428` | missing `If-Match` |
| `500` | an exception not converted by a handler |
| `503` | approved dependency-unavailable behavior; not fully implemented |

Framework branches also need characterization tests: malformed JSON, empty
required JSON body, JSON type mismatch, unsupported content type, invalid GUID
or integer route values, unknown route, unsupported HTTP method, oversized
request, aborted request, and response content type/trace ID. Constrained route
values that do not parse currently miss route selection and normally return
framework `404`. `.Accepts(...)` adds endpoint metadata; tests must not assume it
enforces `application/merge-patch+json` at runtime.

### 2.5 Logging, CORS, HTTPS, and health

- Completed authorized/public calls log method, path, status, duration, and
  trace ID. Bodies, query cursor contents, keys, and content are not logged.
- CORS permits configured exact origins with any method/header and no
  credential support. Empty origin configuration grants no cross-origin access.
- HTTPS redirection precedes all application middleware when a target HTTPS
  port is known.
- Liveness always returns `{ "status": "Healthy" }` and calls no dependency.
- Current readiness is the default application health check and returns
  healthy without testing persistence.
- Contract production readiness performs a bounded PostgreSQL check and returns
  `503` when unavailable; production startup also fails closed for missing
  required secrets/origins.

## 3. Article business flows

### 3.1 Lifecycle state machine

Creation always produces `Writing`; clients cannot set creation status.

| From | Allowed targets (including no-op self) | Public? |
| --- | --- | --- |
| `Writing` | `Writing`, `Draft` | No |
| `Draft` | `Draft`, `Writing`, `Published`, `NotListed` | Only after `Published` |
| `Published` | `Published`, `NotListed`, `Archived` | Yes only while `Published` |
| `NotListed` | `NotListed`, `Draft`, `Published`, `Archived` | No |
| `Archived` | `Archived`, `Draft` | No |

Every other transition returns `400` without mutation. Publishing additionally
requires nonempty valid slug, title, summary, and at least one valid body block.
First publication sets `publishedAt`; hiding, archiving, and republication
preserve it. Once `publishedAt` exists, changing the slug returns `409`, even
while the article is private. Delete preserves the lifecycle state. Restore
always changes it to `Draft`.

### 3.2 Create article

`POST /api/v1/admin/articles`

1. Bind nullable authoring fields.
2. Trim strings; whitespace-only strings become null.
3. Default body/tags to empty and status to `Writing`.
4. Validate metadata, slug uniqueness, tags, body, and media references.
5. Derive reading time and complete current media-ID set.
6. Add immutable revision 1, operation `Created`, actor `site-owner`, request
   correlation ID, and complete resulting snapshot.
7. Insert article and return `201`, `Location`, ETag `"1"`, and admin view.

Exception branches (`400`): invalid/too-long slug; over-limit title, summary,
topic, SEO fields; too many, blank, too-long, or normalized-duplicate tags;
more than 500 blocks; body JSON above 1 MiB; invalid block; missing/deleted
media; or active case-insensitive slug duplicate. Rejection must create neither
article nor revision.

### 3.3 Structured body validation

All blocks require a string `type`.

| Block | Required business shape | Failure branches |
| --- | --- | --- |
| paragraph/heading/quote | nonblank `text` | absent, null, wrong type, blank |
| code | nonblank `code` | absent, null, wrong type, blank |
| list | nonempty `items`, every item a nonblank string | absent/empty array, null item, wrong type, blank item |
| image | GUID `mediaId` resolving to active media | absent/invalid GUID, missing asset, deleted asset |
| table | nonblank `caption`, nonempty `headers`, every row array length equals header count | missing/blank caption, no headers, non-array row, short/long row |
| unknown | never allowed | unknown/case-mismatched type |

Optional block properties are currently passed through rather than strictly
schema-validated. Tests should cover unknown properties and type-specific
optional values (`ordered`, `language`, contextual image `alt`/`caption`) to
decide whether permissive behavior is intentional.

### 3.4 Reading-time derivation

On creation and each state-changing PATCH:

- prose includes `text`, captions, list items, table headers, and table cells;
- title, summary, image alt, block metadata, and code are excluded from prose;
- Unicode letter/number words with internal apostrophes/hyphens count as one;
- prose contributes `words / 200` minutes;
- code contributes nonblank lines at `lines / 12` minutes;
- contributions are added, rounded up, minimum one minute.

Boundary tests: empty body, 1/200/201 words, 1/12/13 code lines, blank code
lines, mixed prose/code fractions, Unicode/apostrophe/hyphen words, captions
and table content, and repeatability.

### 3.5 Patch article

`PATCH /api/v1/admin/articles/{id}`

1. Require an existing active article (`404` otherwise).
2. Require the current ETag.
3. Clone current authoring state and apply JSON Merge Patch semantics.
4. Omitted scalars remain; explicit null clears nullable scalars; body/tags
   arrays replace the full collection; null body/tags becomes empty.
5. Validate the complete candidate aggregate.
6. Enforce published-slug immutability, lifecycle transition, and publication
   completeness, in that order.
7. If no observable change, return current view/ETag with no mutation.
8. On first publication, set `publishedAt`.
9. Copy candidate, increment version, update timestamp, recalculate reading
   time/media references, add `Updated` revision, and return `200` plus new ETag.

Patch parsing failures such as invalid enum, GUID, array, or scalar types are
caught by the handler and returned as `400` with exception text in `detail`.
Unknown patch properties are ignored. A null/non-value `status` is currently
treated as omission.

### 3.6 Publish and public article flows

Public feed `GET /api/v1/articles`:

1. Validate `limit` (default 8, allowed 1..50).
2. If supplied, decode URL-safe cursor; require version 1, exact shape, roundtrip
   timestamp, UUID, valid hex signature, and HMAC for the configured cursor key.
3. Select only non-deleted `Published` articles.
4. Order by `publishedAt DESC`, then UUID DESC.
5. Seek strictly after the cursor keys and take `limit + 1`.
6. Return summaries only; create `nextCursor` from the last returned item when
   another row exists.
7. Hash the complete `{items,next}` representation for a strong ETag, add public
   cache control, and return `304` for exact matching `If-None-Match`, else `200`.

Feed exception branches: limit below/above bounds, nonnumeric query binding,
bad base64/padding/UTF-8 shape, wrong cursor version, timestamp, UUID, signature
hex, signature, or signing key -> `400`. Private/deleted articles must neither
appear nor influence ordering/cursor. Cursor continuation remains valid if the
prior boundary article is hidden or deleted.

Public detail `GET /api/v1/articles/{slug}`:

1. Match exactly one active `Published` article by slug.
2. Missing, deleted, Writing, Draft, NotListed, and Archived all return the
   same public `404` shape.
3. Resolve editorial/social media and image blocks to provider-neutral URL,
   alt, dimensions, and caption; remove `mediaId` from public body blocks.
4. Return summary fields plus updated time, tags, body version/body, SEO
   fallbacks, and canonical URL derived from configured origin.
5. Use article version ETag and public cache control; exact matching
   `If-None-Match` returns bodyless `304`.

Test public projections for absence of authoring-only media IDs, deletion
metadata, revisions, actor/correlation data, internal storage fields, and body
in summaries. Test configured origin trimming and SEO explicit/fallback values.

### 3.7 Delete, restore, and revisions

Delete:

- missing ID -> `404`;
- active resource + missing/stale ETag -> `428`/`412`;
- active resource + current ETag -> set deletion/update time, increment version,
  append `Deleted` revision, return `204`, disappear from all public flows;
- already deleted + any single `If-Match` value -> idempotent `204`, no further
  timestamp/version/revision change.

Restore:

- missing or active resource -> `404`;
- missing/stale ETag -> `428`/`412`;
- active case-insensitive slug conflict -> `409`, remain deleted, no revision;
- otherwise clear deletion, force `Draft`, increment/update, append `Restored`
  revision, return `204`, remain private.

Revision list returns all revisions for an existing active or deleted article.
Revision detail returns an exact revision or `404`; missing parent and missing
revision intentionally share `404`. Revision numbers start at 1 and advance
only for state-changing mutations. Snapshots must not change after later edits.

## 4. Media business flows

### 4.1 Upload media

`POST /api/v1/admin/media` expects `multipart/form-data`.

1. Read `file`, `alt`, `decorative`, and optional `caption`.
2. Require a nonempty file no larger than 10 MiB.
3. Trim metadata; require alt unless explicitly decorative; enforce alt <= 500
   and caption <= 1000.
4. Require declared JPEG, PNG, or WebP.
5. Read bytes and validate leading/trailing signature for declared type.
6. Allocate UUIDv7, calculate lowercase SHA-256 digest, and upload through
   `IMediaStorage`.
7. Create record only after upload succeeds; return `201`, `Location`, ETag
   `"1"`, metadata, and immutable URL.

Current `400` branches: non-form request; missing/empty/oversized file; missing
non-decorative alt; long alt/caption; unsupported exact content type; signature
mismatch. Filename is reduced with `Path.GetFileName`.

Contract production flow also safely decodes the image, rejects malformed or
animated content and dimensions above 25 megapixels, applies orientation,
strips metadata, preserves PNG only for transparency, otherwise converts to
WebP, bounds the longest edge to 1600 without upscaling, verifies the signed
provider response, and persists real dimensions/output type/versioned URL.

Storage failure contract: return `503` with no visible row. If remote upload
succeeds but database commit fails, attempt idempotent cleanup and reconcile
orphans later. Current storage exceptions are unhandled and therefore become
`500`; current bytes are unchanged, dimensions are always 1x1, and output type
equals input type.

### 4.2 List, get, and patch media

List uses shared paging and hides deleted assets by default. Get returns active
or deleted records by ID with ETag; missing returns `404`.

Patch:

1. Require existing active media and current ETag.
2. Trim optional alt/caption; reject alt > 500 or caption > 1000.
3. Omitted alt/caption remain unchanged; `clearCaption=true` wins and clears
   caption; explicit caption otherwise replaces it.
4. No effective change returns `200` with unchanged ETag.
5. A change updates timestamp/version and returns `200` with new ETag; bytes,
   digest, dimensions, and URL remain immutable.

Branches to pin down: blank alt is currently allowed on PATCH; `alt: null`
means unchanged rather than cleared; `caption: null` means unchanged unless
`clearCaption` is true; unknown JSON fields are ignored; no media revisions are
created.

### 4.3 Delete and restore media

Delete active media requires current ETag. It returns `409` if any active
(non-deleted) article's derived media-ID set references it in body, editorial,
or social position. Otherwise it soft-deletes, increments version, and returns
`204`. Repeated delete is idempotent under shared rules.

Restore requires a deleted record and current ETag, then calls
`storage.Exists(url,digest)`. Missing/unverifiable object -> `409` without
mutation; present object -> clear deletion, increment/update, `204`.

Contract retention is broader than current deletion protection: immutable
revision references keep physical bytes available, even when the logical media
record may be soft-deleted. Physical garbage collection is a separate bounded
maintenance flow and is absent from the current slice.

## 5. Series business flows

### 5.1 Lifecycle and membership

Series use the identical lifecycle transition matrix as articles. Creation is
always `Writing`; first publication requires slug and title but may have zero
members. First `publishedAt` and slug immutability behave like articles. Restore
always results in `Draft`.

Membership is many-to-many:

- any active article state may be linked;
- missing or deleted article IDs are rejected;
- duplicate IDs in one series are rejected;
- one article may belong to multiple series;
- supplying `articleIds` replaces the complete ordered ID set;
- omitting it leaves membership unchanged;
- article lifecycle/delete changes never mutate membership or series revisions;
- public projection filters current article visibility at read time.

### 5.2 Create and patch series

Create trims slug/title/summary, defaults members empty, validates the complete
aggregate, writes `Created` revision 1, and returns `201`, `Location`, and ETag.
Validation rejects invalid/long slug, title over 200, summary over 500,
duplicate/missing/deleted members, and active case-insensitive slug conflicts.

Patch follows article lookup, ETag, clone/apply, validation, immutable slug,
lifecycle, publication completeness, no-op, first-publication, version/time,
and `Updated` revision sequencing. Invalid enum/GUID/array/scalar patch values
return handler `400`; unknown properties are ignored.

### 5.3 Public series

`GET /api/v1/series/{slug}` returns only an active `Published` series. Every
private/deleted/missing state uses the same `404` representation.

Members are resolved from stored membership, filtered to active `Published`
articles, ordered by article `createdAt ASC` then UUID ASC, and projected with
the exact public article-summary shape. A published series may legitimately
return an empty member array. Private member metadata must not leak.

The response receives public cache control and a strong representation ETag;
matching `If-None-Match` returns bodyless `304`. A series mutation, member
visibility change, or visible member edit must invalidate the representation.

### 5.4 Delete, restore, and revisions

Delete, idempotent repeated delete, restore lookup/concurrency, restore slug
conflict, Draft-on-restore, and revision list/detail mirror article behavior.
Membership stays intact through deletion and restore. Successful membership
replacement writes the full resulting member set in the next immutable
revision; rejected/no-op changes do not.

## 6. Cross-aggregate flows

These are the most important multi-step business paths for acceptance tests.

### A. Author and publish an article

Upload media -> create Writing article referencing media -> patch to Draft ->
patch to Published -> confirm first publication/revision -> confirm feed/detail
public projections and caches.

Failures: invalid media/body, missing/stale ETag, forbidden transition,
incomplete publication, conflicting slug, failed persistence/revision. No
partial state may become public.

### B. Hide, archive, republish, and recover an article

Published -> NotListed or Archived -> public 404/feed removal/series-member
removal -> allowed path back to Published -> preserve slug and `publishedAt` ->
soft delete -> restore to Draft -> explicitly republish.

Failures: direct forbidden transition, slug edit after first publication,
stale token, restore slug conflict, deleted media reference.

### C. Media reference lifecycle

Upload -> reference from article body/editorial/social -> reject media delete
while article active -> delete article -> allow logical media delete -> preserve
revision bytes -> restore media when object exists -> restore/revise article.

Tests must cover each reference position independently and multiple articles
sharing one media asset.

### D. Build and expose a series

Create articles in mixed lifecycle states -> create series membership -> publish
series -> expose only published active members oldest first -> change a member's
visibility/content -> representation and ETag change without membership revision
-> replace membership -> series revision changes.

### E. Concurrency race

Two clients GET the same resource -> client A changes it with current ETag ->
client B uses stale ETag -> `412` and A's state/revision survives. Repeat for
article, media, and series; include delete and restore races.

### F. Slug reuse and restore conflict

Create resource A -> delete A -> create active B with A's slug -> restore A ->
`409`, A remains deleted, no version/revision change. Cover article and series,
case-insensitive comparison, and reuse before/after first publication.

## 7. Known contract/implementation divergences to turn into tests

These are test discoveries, not approved behavior changes.

1. **P0 — Article no-op detection can mutate.** `Clone` does not copy
   `ReadingTimeMinutes`; an article whose derived value exceeds 1 compares as
   changed even for an empty/no-op patch, incrementing version and revision.
2. **P0 — Contextual image defaults are live, not snapshotted.** An image block
   with omitted alt/caption resolves the media defaults at public-read time.
   Later media metadata changes can alter a published article without changing
   its article/detail ETag or revision, contrary to the approved snapshot rule.
3. **P0 — Public series ETag misses visible member edits.** It combines series
   version with member GUID hash codes, not the returned member
   representations/versions. Title, summary, topic, image, reading time, or
   publication timestamp changes can incorrectly return `304` for stale data.
4. **P0 — Restored articles can retain deleted media.** Article restore does not
   revalidate body/editorial/social references. It can create an active Draft
   referencing deleted media; subsequent behavior depends on another patch or
   media restore and risks inconsistent projection paths.
5. **P0 — Store mutations are not aggregate-atomic under concurrency.** The
   singleton concurrent dictionaries protect dictionary operations, but
   check-then-mutate sequences and mutable entities are not locked. Concurrent
   same-slug creates or same-ETag mutations can both pass validation.
6. **P0 — Production transaction rollback is absent.** Root, memberships,
   references, and revisions are in-memory object operations; PostgreSQL
   atomicity, uniqueness, FK restrictions, and rollback flows are unimplemented.
7. **P1 — Media validation/normalization is only a signature probe.** Safe
   decode, animation/dimension checks, metadata removal, orientation, resize,
   WebP normalization, real dimensions, and provider signature verification
   are absent.
8. **P1 — Media provider failures become `500`.** The approved response is
   `503`, with cleanup/reconciliation for partial remote success.
9. **P1 — Pagination response name differs.** Runtime emits `pageNumber`; the
   approved platform contract says `page`.
10. **P1 — OpenAPI is incomplete.** Many request/response schemas, parameters,
    common errors, `If-Match` declarations, media patch content, health routes,
    and ETag/cache headers are absent or underspecified. There is no runtime
    drift test.
11. **P1 — Create and patch normalize tags differently.** Create validates
    trimmed tag values but retains original surrounding whitespace; patch
    stores trimmed values.
12. **P1 — Admin representations expose no `version` field.** Concurrency is
    available only through ETag; confirm this is the intended interpretation
    of the administrative contract.
13. **P1 — Public series lookup can cache-collide.** Summing GUID hash codes is
    order-insensitive and collision-prone in addition to missing member content.
14. **P2 — Readiness does not test persistence.** It cannot signal the required
    PostgreSQL outage behavior.
15. **P2 — Production configuration does not fail closed.** Cursor signing and
    public origin have development fallbacks, and empty production CORS/admin
    configuration does not stop startup.
16. **P2 — Unauthorized requests bypass application request logging.** Confirm
    whether platform security/audit requirements expect a sanitized rejection
    log.

## 8. Test backlog

### P0: business integrity and privacy

- `ART-LIFE-001`: exhaustive allowed/forbidden article transition matrix.
- `ART-PUB-001`: incomplete article cannot publish and creates no revision.
- `ART-PUB-002`: first publication sets immutable slug/time; republication
  preserves both.
- `ART-PRIV-001`: missing plus every private/deleted state has identical public
  404 and never enters feed/series.
- `ART-REV-001`: create/update/delete/restore revisions are sequential,
  complete, immutable, and correlated.
- `ART-NOOP-001`: empty/equivalent patch never changes version/time/revisions,
  including reading time greater than one.
- `ART-REST-001`: delete/restore forces Draft and handles slug/media conflicts
  without partial mutation.
- `ART-CURSOR-001`: stable multi-page seek ordering, boundary ties, hidden or
  deleted boundary row, and no duplicate/private item.
- `MEDIA-REF-001`: body/editorial/social references each prevent active-media
  delete; shared reference stays protected.
- `MEDIA-SNAP-001`: contextual alt/caption snapshot and article cache/revision
  behavior after media metadata changes.
- `SER-LIFE-001`: exhaustive allowed/forbidden series transition matrix.
- `SER-MEMBER-001`: replace membership, duplicates, missing/deleted articles,
  sharing, persistence through lifecycle/delete/restore.
- `SER-PRIV-001`: mixed member visibility filters without membership mutation
  or private leakage; deterministic oldest-first order.
- `SER-CACHE-001`: series ETag changes for series edits, member content edits,
  member visibility changes, and membership changes.
- `CONC-001`: missing/malformed/multiple/weak/wildcard/stale/current ETags for
  every mutation family.
- `CONC-002`: simultaneous same-token mutations and same-slug creation permit
  exactly one committed outcome in the production adapter.
- `TX-001`: induced failure at root/reference/membership/revision commit points
  rolls back the complete business mutation.

### P1: endpoint and validation completeness

- `ART-CREATE-001`: defaults, trimming, UUIDv7, UTC timestamps, ETag, Location,
  derived media set/reading time, and Created revision.
- `ART-VALID-001`: every metadata/tag/body limit at below/equal/above boundary.
- `ART-BLOCK-001`: valid and invalid branch for every block type and optional
  property.
- `ART-PATCH-001`: omission/null/replacement semantics, unknown fields, wrong
  JSON types, invalid enum, and validation atomicity.
- `ART-PROJ-001`: feed/detail field allowlists, SEO fallbacks, canonical URL,
  provider-neutral media, and no authoring leakage.
- `ART-CACHE-001`: feed/detail `200` headers and bodyless exact-match `304`;
  nonmatching/multiple/weak `If-None-Match` characterization.
- `ART-READ-001`: all reading-time inputs and rounding boundaries.
- `MEDIA-UP-001`: multipart/file/metadata/type/signature/size boundaries and
  filename sanitization.
- `MEDIA-IMG-001`: decode, malformed image, animation, megapixels,
  transparency, orientation, stripping, resizing/no-upscale, normalized output.
- `MEDIA-PATCH-001`: omission/null/blank/clearCaption/no-op/change and immutable
  binary fields.
- `MEDIA-REST-001`: present, missing, and digest-mismatched remote object.
- `MEDIA-FAIL-001`: provider unavailable, upload success plus database failure,
  cleanup failure, and reconciliation retention.
- `SER-CREATE-001`: defaults, all field/slug/member validation, revision,
  Location, ETag.
- `SER-PATCH-001`: merge semantics, publication completeness, no-op, immutable
  slug/time, and atomic rejection.
- `REV-READ-001`: list/detail for active/deleted/missing parent and missing,
  zero, negative revision numbers.
- `PAGE-001`: defaults, boundaries, invalid headers, deleted visibility,
  ordering, totals, empty/overflow pages, and response field names.
- `ERR-001`: exact Problem Details content type/shape/trace ID and keyed
  validation errors across every handler status.
- `BIND-001`: malformed/empty/wrong-type JSON, content types, route constraints,
  unsupported methods, request limits, and cancellation.
- `OAS-001`: runtime route/verb/request/response/status/header/content-type
  drift against OpenAPI 3.1 and generated-client smoke test.

### P2: security and operations

- `AUTH-001`: all key branches, no store access on failure, constant-time
  comparison integration boundary, and no credential logging.
- `LOG-001`: success/rejection/exception logs contain required safe metadata
  and exclude bodies, cursor payloads, secrets, and article content.
- `CORS-001`: allowed/disallowed/no origin, preflight methods/headers, no
  credentials, and production empty-config startup behavior.
- `HTTPS-001`: HTTP redirect and HTTPS request behavior without redirect loops.
- `HEALTH-001`: liveness dependency isolation and readiness PostgreSQL
  success/timeout/failure.
- `CONFIG-001`: production fail-closed AdminKey, CursorKey, origin, CORS,
  database, and provider settings; development-only fallbacks stay isolated.
- `STORE-001`: process restart behavior is documented for in-memory tests;
  PostgreSQL persistence, migrations, schema ownership/privileges, and no
  Presentation-schema access are integration-tested.
- `CONTAINER-001`: non-root runtime, pinned base, application port, liveness
  health check, and no embedded secrets/development settings.

## 9. Recommended implementation order for new tests

1. Build reusable HTTP fixtures for authenticated requests, ETag capture,
   article/media/series creation, lifecycle transitions, and isolated stores.
2. Add table-driven lifecycle, visibility, and concurrency tests first.
3. Add article body/reading-time/media-reference tests.
4. Add series membership/filtering/cache tests.
5. Add failure-injection fakes for media storage, time, and future persistence.
6. Add contract/OpenAPI and framework-boundary tests.
7. Keep production-adapter scenarios separately categorized so in-memory
   acceptance tests do not falsely claim PostgreSQL/Cloudinary guarantees.

The existing suite currently proves only admin-key rejection, one successful
article publish/conditional detail read, one missing-`If-Match` series case,
and a shallow Postman route-family check. All other flows above remain available
for explicit automated coverage.
