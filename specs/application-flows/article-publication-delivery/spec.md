# Flow: Article publication and public delivery

- Status: Draft
- Owner: Igor
- Last updated: 2026-08-27

## Outcome

Only intentionally Published articles become anonymously visible, through a
stable feed and detail contract whose ordering, media resolution, SEO metadata,
and conditional caching never leak or serve stale private content.

## In scope

- Complete article lifecycle transition matrix.
- First publication, hide/archive/republish behavior.
- Public feed cursor pagination and article detail.
- Public projections, media resolution, canonical/SEO fields, ETags and caching.

## Out of scope

- Soft-delete restoration and revision read routes.
- Scheduled publication, preview tokens, search, or tag archives.

## Content and workflow

Allowed transitions are Writing->Draft; Draft->Writing/Published/NotListed;
Published->NotListed/Archived; NotListed->Draft/Published/Archived; and
Archived->Draft. Self transitions are semantic no-ops. Every other transition
is rejected.

First publication requires slug, title, summary, and at least one valid block;
sets `publishedAt`; and permanently freezes the slug. Later private/public
transitions retain that timestamp and slug.

The feed selects active Published articles ordered by `publishedAt DESC`, then
UUID DESC, and uses a signed opaque seek cursor. Detail resolves public media,
canonical URL, and SEO fallbacks. Both use strong representation ETags and
`Cache-Control: public, max-age=60, stale-while-revalidate=300`.

## HTTP contract

- `GET /api/v1/articles?cursor={cursor}&limit={limit}`
- `GET /api/v1/articles/{slug}`
- Limit defaults to 8 and accepts 1..50.
- Invalid cursor/limit returns `400`; no fallback page is returned.
- Missing, Writing, Draft, NotListed, Archived, and deleted detail all return
  identical `404` Problem Details.
- Exact matching `If-None-Match` returns bodyless `304`.

## Data and integrations

Publication time, lifecycle, version, references, and revision commit in one
transaction. Cursor signing uses a dedicated configured key. Public images are
resolved from Blog media to opaque URL, contextual alt, dimensions, and caption;
provider identifiers never cross the public boundary.

## Security and privacy

Public summaries exclude bodies and all administrative fields. Public detail
excludes media IDs, deletion/version data, revisions, actors, correlation IDs,
and provider metadata. Every private state is indistinguishable from missing.

## Acceptance scenarios

### Scenario: Exercise every allowed lifecycle transition

- Given one article in each lifecycle state with a current ETag
- When every allowed outgoing transition is requested
- Then it succeeds exactly once, writes the expected revision, and has the
  documented visibility
- And first-publication fields are set only on the first Published transition

### Scenario: Reject every forbidden lifecycle transition

- Given the full from/to state matrix
- When each forbidden transition is requested
- Then it returns `400`
- And status, publication time, version, timestamp, references, revisions, feed,
  detail, and series projections remain unchanged

### Scenario: Reject incomplete publication

- Given Draft articles independently missing slug, title, summary, or body
- When each is patched to Published
- Then it returns `400`
- And none becomes visible or receives `publishedAt`
- And no update revision is written

### Scenario: Preserve first-publication identity

- Given an article is published, hidden, archived through allowed paths, and
  later republished
- When its slug is also patched before and after first publication
- Then pre-publication changes are allowed
- And every post-publication slug change returns `409`
- And the first `publishedAt` never changes

### Scenario: Make all private detail states indistinguishable

- Given unique slugs in Writing, Draft, NotListed, Archived, deleted, and
  missing states
- When each public detail route is requested
- Then status, content type, Problem Details fields, and disclosure are equal
- And no response reveals whether the article exists

### Scenario: Return only safe feed summaries

- Given public and private articles mixed across timestamps and UUID ties
- When the feed is read
- Then only active Published articles appear in deterministic descending order
- And summaries omit body and administrative/private fields

### Scenario: Continue a stable feed pessimistically

- Given more public rows than the limit
- When every page is followed using only returned opaque cursors
- Then each article appears exactly once in correct order
- And hiding/deleting the prior boundary row does not invalidate continuation
- And inserted newer rows do not duplicate rows already passed

### Scenario: Reject every malformed cursor

- Given truncated/invalid base64, wrong UTF-8 shape, wrong field count/version,
  invalid timestamp/UUID/hex, changed ordering keys, changed signature, and a
  cursor signed by another key
- When each cursor is requested
- Then it returns `400` with no items or fallback page
- And no private row influenced the cursor result

### Scenario: Validate public detail projection

- Given explicit/fallback SEO fields, configured origin with/without trailing
  slash, editorial/social media, every body block, and contextual decorative
  image metadata
- When detail is requested
- Then canonical and SEO values are correct
- And media IDs/provider fields are replaced by the exact public image shape
- And code/text remain inert JSON data

### Scenario: Snapshot contextual media presentation

- Given an article image omits an override and is then published
- When the media default alt/caption changes later
- Then the published article retains the contextual values captured by its last
  article mutation
- And its revision and ETag remain internally consistent

### Scenario: Reuse valid public caches

- Given current feed/detail ETags
- When exact matching, nonmatching, weak, wildcard, and multiple
  `If-None-Match` forms are sent
- Then only the documented match form returns bodyless `304`
- And every `200`/`304` contains the required cache and ETag headers

### Scenario: Invalidate every visible change

- Given cached feed/detail/series representations
- When an article publishes, hides, archives, deletes, republishes, or changes
  any publicly projected field/media/body
- Then every affected representation receives a new ETag or becomes `404`
- And the old ETag never validates stale public content

### Scenario: Contain projection dependency inconsistency

- Given corrupted/inconsistent persistence points a Published article at missing
  or deleted media
- When public detail/feed is requested
- Then the API returns a deliberate safe failure and sanitized diagnostic
- And it never emits a partial body, private ID, or unhandled null exception

## Test evidence

- Table-driven lifecycle and public-privacy HTTP tests.
- Cursor unit/property tests plus multi-page PostgreSQL integration tests.
- Projection allowlist tests and generated-client deserialization.
- Cache invalidation tests spanning article, media, and series representations.

## Decisions and open questions

- Decision: contextual image defaults are captured during article mutation, not
  resolved live from mutable media metadata.
- Decision: cursor invalidity is always a hard `400`, never first-page fallback.
- Open question: document supported RFC semantics for multiple/weak/wildcard
  public `If-None-Match` values; current exact-string behavior is narrower.
