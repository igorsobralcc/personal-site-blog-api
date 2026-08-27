# Flow: Editorial cross-aggregate journeys

- Status: Draft
- Owner: Igor
- Last updated: 2026-08-27

## Outcome

Complete editorial journeys across media, articles, revisions, series, public
reads, caching, and recovery remain consistent when every intermediate request
or dependency can fail, race, or be retried.

## In scope

- Author/publish, hide/archive/republish/recover, media-reference lifecycle,
  series assembly/projection, concurrency races, and slug-reuse restore conflict.
- Cross-aggregate invariants not provable from isolated endpoint tests.

## Out of scope

- New product behavior beyond the underlying Approved feature specifications.
- Distributed workflows with the Presentation API.

## Content and workflow

Journeys compose existing operations without weakening their local invariants.
The system must remain recoverable after failure at every step. A later request
never assumes a prior response succeeded merely because an external side effect
started. Administrative truth, immutable history, provider state, and public
projections are compared after each success and rejection.

## HTTP contract

Each operation retains its own documented status, ETag, Problem Details,
Location, and cache semantics. Cross-flow failures do not introduce ambiguous
`200` responses or leak internal orchestration state. Retried requests use
resource identity and ETags rather than blind replay.

## Data and integrations

Within one aggregate mutation, database state is transactional. Across separate
HTTP operations, compensation/retry/reconciliation produces eventual safe
consistency without distributed transactions. Provider objects are immutable;
article/series relationships use Blog IDs and restricted database references.

## Security and privacy

At every journey checkpoint, only active Published articles/series are public.
Failures, retries, cache validators, storage URLs, and membership changes cannot
reveal Draft/NotListed/Archived/deleted content or provider credentials.

## Acceptance scenarios

### Scenario: Author and publish a complete article

- Given an authenticated owner and healthy dependencies
- When media is uploaded, a Writing article references it, the article becomes
  Draft then Published, and feed/detail are read conditionally
- Then each mutation has one version/revision and valid references
- And public summary/detail/media/canonical/SEO/cache projections are consistent
- And no admin/provider-only field is public

### Scenario: Fail at every author-and-publish checkpoint

- Given failure injection before/after media upload, article create, Draft
  transition, publication commit, and public projection
- When the journey is attempted and safely retried
- Then no incomplete article becomes public
- And no orphan/dangling reference or duplicate revision is silently created
- And eventual recovery reaches exactly one coherent published outcome or a
  clearly private recoverable state

### Scenario: Hide, archive, republish, and recover

- Given a cached Published article linked into a Published series
- When it is hidden, archived through legal paths, republished, deleted,
  restored, and explicitly republished
- Then public article/feed/series visibility changes at each exact boundary
- And stable slug/first publication time survive
- And restore is Draft and never accidentally public

### Scenario: Reject illegal recovery paths

- Given the same lifecycle journey
- When forbidden transitions, stale ETags, post-publication slug edits, restore
  slug conflicts, and unusable media are attempted at each phase
- Then each fails with no partial root/revision/reference/cache mutation
- And the last valid state remains authoritative

### Scenario: Protect a shared media asset

- Given one asset is used in body/editorial/social positions across multiple
  active articles and immutable revisions
- When articles independently change/delete and media deletion/cleanup is tried
  after each step
- Then active references always return `409`
- And revision-only references retain physical bytes
- And deletion becomes eligible only under the exact logical/retention rules

### Scenario: Replace media without overwriting history

- Given a Published cached article uses asset A
- When asset B with equal or different bytes is uploaded and the article is
  patched to B
- Then B has a distinct ID/URL, A is unchanged and retained for history, the
  article gets one new revision/ETag, and old cache validators do not serve A as
  current content

### Scenario: Assemble and expose a mixed-state series

- Given active articles across all lifecycle states and creation-time ties
- When they are linked to a Draft series and the series is Published
- Then all memberships persist but only active Published members are returned
  oldest first
- And public member shape exactly matches the article feed summary

### Scenario: Change a member without changing membership

- Given a cached Published series
- When a member hides, republishes, edits public metadata/media, or is deleted
- Then membership and series revisions stay unchanged
- And public articles and series representation/ETag immediately reflect the
  new member state

### Scenario: Share one article between multiple series

- Given article A and Published series X/Y
- When A belongs to both and A changes visibility/content
- Then both independent memberships remain
- And both affected public representations invalidate correctly
- And failure updating one series never corrupts the other

### Scenario: Race two owners using the single-owner credential

- Given clients A/B read identical article, media, and series ETags
- When their PATCH/DELETE/restore operations race at controlled commit barriers
- Then each resource has exactly one winning mutation per version
- And losers receive safe precondition/concurrency failures
- And cross-aggregate projections reflect only committed winners

### Scenario: Race reference creation with media deletion

- Given active media and an article patch that will reference it
- When the article transaction races media deletion
- Then restricted relationships/locking produce either a committed reference
  with media active or a committed delete with article rejection
- And never an active dangling reference

### Scenario: Reuse a deleted slug and reject restore

- Given article/series A is deleted and active B claims A's slug with different
  casing
- When A restore uses its current ETag
- Then restore returns `409`, A remains deleted, and B remains authoritative
- And no restore revision/version/public cache change occurs

### Scenario: Recover after service restart

- Given committed media/article/series/revision/reference state and pending safe
  reconciliation work
- When the production process restarts
- Then durable state and work resume without duplication
- And caches/ETags remain representation-correct
- While the in-memory development adapter's expected data loss is explicitly
  isolated from production evidence

### Scenario: Maintain privacy during compound failure

- Given private content with sentinel metadata and simultaneous provider,
  persistence, cache, and logging failures
- When the request pipeline handles the failure
- Then no public body/header/error/log reveals the private values or credentials
- And operators retain a safe trace/correlation path for diagnosis

## Test evidence

- End-to-end HTTP journey tests using isolated app instances and controllable
  time.
- PostgreSQL transaction/race/restart suites with barriers and failure hooks.
- Provider fake plus Cloudinary sandbox contract tests.
- Cross-route public privacy and cache-consistency assertions after every step.
- State-model/property tests generating valid and invalid lifecycle sequences.

## Decisions and open questions

- Decision: journey tests assert state after every step, not only the final
  response, to localize partial-mutation defects.
- Decision: every cross-operation recovery is retry/compensation based; no
  distributed transaction is introduced.
- Open question: define operational ownership and retry schedule for persistent
  media reconciliation failures.
