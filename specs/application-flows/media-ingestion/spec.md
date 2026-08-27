# Flow: Media ingestion and durable publication asset creation

- Status: Draft
- Owner: Igor
- Last updated: 2026-08-27

## Outcome

The owner can upload a safe image and receive one immutable provider-neutral
Blog asset, while malformed, hostile, oversized, or partially failed uploads
leave no visible record, unsafe public bytes, or unreconciled silent orphan.

## In scope

- `POST /api/v1/admin/media` multipart ingestion.
- Metadata validation, content detection/decoding, normalization, hashing,
  provider upload/verification, database commit, compensation, and reporting.

## Out of scope

- Video/audio/documents/SVG/animated content, client-side editing, responsive
  derivatives, and direct browser-to-provider upload.
- Media metadata patch/delete/restore.

## Content and workflow

The request supplies `file`, required default `alt` unless
`decorative=true`, and optional caption. The API enforces limits before costly
work, detects and safely decodes accepted JPEG/PNG/WebP, rejects animation and
more than 25 megapixels, applies orientation, removes metadata, bounds the long
edge to 1600 without upscaling, preserves PNG only for transparency and
otherwise normalizes to WebP.

It hashes the publication bytes, allocates UUIDv7, uploads under immutable key
`blog/media/{mediaId}/{digest}`, verifies the signed provider response and
versioned HTTPS URL, then commits the Blog record. Each accepted upload creates
a new logical asset even for duplicate bytes.

## HTTP contract

- Non-multipart or invalid form/content -> `400` Validation Problem Details.
- Maximum accepted file length is 10 MiB; server request limit behavior must be
  compatible and documented.
- Exact accepted input types: `image/jpeg`, `image/png`, `image/webp`.
- Provider unavailability -> `503` with no visible media record.
- Success -> `201`, `Location`, ETag, administrative representation with real
  types, dimensions, byte size, digest, metadata, timestamps, and opaque URL.

## Data and integrations

Provider upload precedes database commit. A database failure after provider
success triggers immediate idempotent deletion; failed compensation records
bounded reconciliation work keyed by media ID/object digest without secrets.
No record is visible before provider response verification and commit.

## Security and privacy

Original unsanitized bytes are never publicly served or retained after success.
Filename is reduced to a safe basename and never enters the provider public ID.
EXIF/XMP/GPS/camera data and unnecessary profiles/comments are removed. Provider
credentials, asset IDs, signatures, and original bytes never enter public
article responses or logs.

## Acceptance scenarios

### Scenario: Upload each accepted safe format

- Given valid baseline JPEG, opaque/transparent PNG, and WebP fixtures within
  byte/pixel limits
- When each is uploaded with valid metadata
- Then it is decoded, oriented, sanitized, normalized, hashed, uploaded once,
  verified, and committed
- And `201`, Location, ETag, real dimensions/type/size, digest, and immutable URL
  match the stored publication bytes

### Scenario: Upload an explicitly decorative image

- Given an accepted image and `decorative=true`
- When alt is empty after trimming
- Then upload succeeds with an explicit empty default
- But missing/false/malformed decorative with blank alt returns `400`

### Scenario: Enforce file and metadata boundaries

- Given missing, zero-byte, 10 MiB, over-10-MiB files; alt at 0/500/501; caption
  at 0/1000/1001; and duplicate/missing form parts
- When each form is submitted
- Then only legal combinations succeed
- And every rejection leaves provider/database/reconciliation unchanged

### Scenario: Reject declared-type attacks

- Given unsupported types, mixed-case/parameterized declarations, and bytes
  whose signature differs from declared JPEG/PNG/WebP
- When each is uploaded
- Then it returns `400`
- And no bytes are published or retained

### Scenario: Reject unsafe decoded content

- Given truncated, corrupt, polyglot, decompression-bomb, animated, excessive
  pixel, invalid orientation, and decoder-timeout fixtures
- When each is uploaded
- Then safe decode fails within resource bounds
- And no provider/database effect occurs
- And the response/log does not expose decoder internals or bytes

### Scenario: Normalize deterministically

- Given fixtures with orientation, GPS/EXIF/XMP/comments/profiles,
  transparency, long edges below/at/above 1600, and duplicate input bytes
- When uploaded repeatedly
- Then metadata is absent, orientation/dimensions are correct, small images are
  not upscaled, output policy is followed, and equal publication bytes hash
  equally
- And each upload still receives a distinct media ID and immutable URL

### Scenario: Sanitize adversarial filenames

- Given traversal, absolute, Unicode, control-character, extremely long, empty,
  and duplicate filenames
- When a valid image is uploaded
- Then the provider key contains only media ID/digest
- And stored display filename follows the documented safe basename/length rule
- And no filesystem path is accessed from the supplied name

### Scenario: Fail closed on provider errors

- Given timeout, DNS/TLS/auth/rate-limit, malformed response, invalid signature,
  missing version/URL, wrong object metadata, and cancellation from the provider
- When upload reaches that boundary
- Then the API returns mapped `503` or cancellation behavior
- And no media row is visible
- And credentials/provider response bodies are not leaked

### Scenario: Compensate a failed database commit

- Given provider upload and verification succeed
- When root/reference commit fails
- Then no media row becomes visible
- And immediate deletion is attempted exactly once with the immutable object
  identity
- And a safe retry creates a new independent asset

### Scenario: Reconcile a failed compensation

- Given database commit and immediate cleanup both fail after remote success
- When bounded reconciliation runs after the grace period
- Then only an unreferenced orphan is deleted idempotently
- And referenced/current/revision assets are retained
- And provider pagination/rate limits use bounded backoff

### Scenario: Prevent request cancellation ambiguity

- Given cancellation before decode, during provider upload, after provider
  success, and during database commit
- When the client disconnects
- Then each phase has a deterministic cleanup/commit outcome discoverable by
  media ID/correlation ID
- And no silently abandoned unsafe object remains

## Test evidence

- Multipart HTTP boundary tests and malicious image fixture suite.
- Deterministic image-normalization unit/golden-file tests.
- Failure-injectable provider contract tests for every remote phase.
- PostgreSQL/provider orchestration tests for commit compensation and
  reconciliation.
- Opt-in signed Cloudinary sandbox tests without exposing credentials.

## Decisions and open questions

- Decision: signature-only probing and 1x1 placeholder dimensions do not satisfy
  this flow and are characterized only as current-slice behavior.
- Decision: every provider failure is mapped deliberately; raw exceptions must
  not become generic `500` for expected outages.
- Open question: set CPU/memory/time limits for decoding and normalization.
