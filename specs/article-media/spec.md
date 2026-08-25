# Feature: Article media

- Status: Approved
- Owner: Igor
- Last updated: 2026-08-25

## Outcome

The site owner can upload an image once and safely use it in article content,
editorial cards, and social metadata through stable public URLs without making
the Blog API process image traffic on every reader request.

## In scope

- Protected image upload, metadata lookup, administrative listing, soft
  deletion, and restoration.
- Server-side file validation, metadata removal, safe decoding, deterministic
  dimensions, and durable remote object storage.
- Immutable public asset URLs on a dedicated configured asset origin.
- Reference protection for images used by active or deleted articles and their
  revisions.
- A storage-provider boundary so the public article contract does not expose a
  provider-specific file identifier or API URL.

## Out of scope

- Video, audio, documents, animated images, SVG, arbitrary binary files, and
  user-generated uploads.
- Browser-side image editing, cropping, focal-point controls, AI generation,
  bulk migration, and a general-purpose digital asset manager.
- Runtime image transformation through query-string parameters.
- Google Drive sharing or download URLs as the production public image origin.
  Drive may be used manually as a private source archive or backup only.

## Content and workflow

The initial workflow is intentionally administration-only and API-mediated:

1. The site owner sends one image and its required alt text to the protected
   Blog media endpoint.
2. The API enforces request limits, checks the declared type and file signature,
   safely decodes the image, applies orientation, removes embedded metadata,
   and rejects unsupported or malformed content.
3. The API creates a sanitized publication asset, calculates its SHA-256 digest,
   and uploads it to Cloudinary through an authenticated server-side request
   under a new immutable public ID.
4. After the signed Cloudinary response is verified, the API commits the media
   record and returns its identifier, dimensions, type, digest, and versioned
   HTTPS delivery URL.
5. Administrative article writes reference the returned `mediaId`. Public
   article projections resolve that reference to a stable URL, width, height,
   and alt text rather than exposing the storage key or provider.

The first slice accepts JPEG, PNG, and WebP input. Animated content is rejected.
Output preserves PNG only when transparency is required; other accepted images
are normalized to WebP. The publication asset keeps its natural aspect ratio
and is bounded to 1600 pixels on its longest edge, which covers the initial
760-pixel reading column at approximately 2x density. Smaller images are never
upscaled.

The first slice stores and delivers one normalized publication asset per upload.
It does not create responsive derivatives. This contains Cloudinary Free
storage, transformation, and bandwidth usage while still producing a bounded
source suitable for the current article layout. Responsive variants require a
later measured performance change to both the media and public article
contracts.

Alt text is required at upload time as a reusable default. Each article image
reference may override it because the appropriate description depends on the
article context. Decorative article images use an explicit empty override; an
omitted override copies the current media default into the article reference
during that article mutation. Later media-default changes do not silently alter
published articles or historical revisions.

## HTTP contract

Protected operations are:

- `GET /api/v1/admin/media` with Platform foundation pagination headers
- `POST /api/v1/admin/media` using `multipart/form-data`
- `GET /api/v1/admin/media/{id}`
- `PATCH /api/v1/admin/media/{id}` for mutable descriptive metadata only
- `DELETE /api/v1/admin/media/{id}`
- `POST /api/v1/admin/media/{id}/restore`

The upload form contains required `file` and `alt` parts and optional `caption`.
The maximum request size is 10 MiB. Accepted request media types are
`image/jpeg`, `image/png`, and `image/webp`; the detected file signature must
match the declared type. Decoded source dimensions may not exceed 25 megapixels.
Alt text accepts 0 through 500 characters after validation, but an empty upload
default requires an explicit `decorative=true` form part; caption accepts at
most 1,000 characters. Non-decorative alt text cannot be blank.

POST returns `201` with `Location`, ETag, and an administrative media
representation containing `id`, original filename, detected input type, output
type, byte size, `width`, `height`, SHA-256 `digest`, default `alt`, nullable
default `caption`, `createdAt`, `updatedAt`, and resolved `url`.

PATCH can change only default `alt` and `caption`; it never changes the stored
object or public URL. DELETE and restore follow Platform foundation ETag,
`If-Match`, `428`, and `412` behavior.

There is no Blog API endpoint that proxies public image bytes. Readers fetch
the resolved immutable versioned URL directly from Cloudinary's CDN. The public
Blog schema contains no provider discriminator or Cloudinary identifier; the
resolved URL is treated as opaque and may use another origin after a separately
specified migration.

## Data and integrations

- `blog.media_assets` stores the Blog identifier, Cloudinary `asset_id`,
  immutable `public_id`, asset version, sanitized metadata, content digest,
  lifecycle timestamps, and concurrency version. Provider fields remain
  administrative persistence details and never enter public article contracts.
- `blog.article_media_references` and
  `blog.article_revision_media_references` provide restricted relational foreign
  keys for every current and historical reference. They are updated from the
  validated structured document in the same transaction as the article or
  revision snapshot; reference safety never depends on querying inside `jsonb`.
- The Cloudinary public ID is `blog/media/{mediaId}/{digest}`. It never contains
  the original filename, article title, alt text, or Cloudinary account
  identifier. Upload requests explicitly disable overwriting and filename-based
  identifiers.
- Public IDs and versioned delivery URLs are immutable. Replacing image bytes
  creates a new media asset; it never overwrites an existing Cloudinary asset.
- Database commit occurs only after the Cloudinary upload succeeds and the
  response signature is verified. If the database
  commit then fails, the API attempts immediate idempotent deletion. A scheduled
  reconciliation operation uses the bounded Cloudinary Admin API to detect and
  remove any unreferenced asset left by an interrupted request after the
  retention grace period.
- The provider adapter exposes upload, inspect, download-for-integrity-check,
  versioned-URL generation, bounded listing for reconciliation, and delete
  operations. The first implementation uses the official Cloudinary .NET SDK
  for signed server-side operations.
- Cloudinary configuration consists of `cloudName`, `apiKey`, and `apiSecret`.
  The secret is injected at runtime, never stored in source control, and never
  sent to the frontend. Configuration fails closed when any required value is
  missing.
- The initial public origin is
  `https://res.cloudinary.com/{cloudName}`. Every stored delivery URL includes
  the explicit asset version returned by the upload response for immediate
  cache busting and deterministic delivery.
- Public asset responses use HTTPS and the correct image `Content-Type` and are
  cacheable through Cloudinary's CDN. The Blog API never generates an
  unversioned delivery URL.
- A media asset may be referenced by article body blocks, editorial images, or
  social images. Database relationships use `RESTRICT` or `NO ACTION` and are
  changed explicitly with the article aggregate.
- Article revisions retain the resolved media identifier and presentation
  metadata required to understand the historical article. Stored objects are
  retained while any current article or immutable revision references them.

## Security and privacy

- Upload and management inherit Platform foundation authorization and HTTPS
  requirements.
- Validation uses decoded content rather than trusting the extension or
  caller-supplied media type.
- EXIF, XMP, ICC comments not required for correct display, GPS coordinates,
  camera details, and other embedded metadata are removed from publication
  assets.
- SVG and active-content formats are rejected. Original unsanitized bytes are
  not publicly served or retained after successful normalization.
- Public knowledge of one immutable asset URL does not grant Cloudinary API or
  Media Library access, reveal credentials or other public IDs, or disclose
  draft article metadata.
- Standard Cloudinary delivery URLs are publicly retrievable by anyone who
  learns the URL, even when the referencing article is private. Media IDs and
  URLs for private articles are not exposed by the Blog API, but the site owner
  must not upload confidential or personally sensitive images to this public
  media pipeline.
- The Cloudinary API key and secret, asset ID, public ID, Media Library metadata,
  and original upload name never appear as public response fields. The delivery
  URL necessarily contains the public Cloudinary cloud name and asset path and
  must be treated as public information.

## Failure and operational behavior

- Missing files, invalid metadata, type mismatch, decode failure, animation,
  excessive byte size, or excessive dimensions return Validation Problem
  Details without a committed database row.
- Remote storage unavailability returns `503` Problem Details and leaves no
  visible media record. Retriable cleanup work is logged by media ID and object
  key digest, not by credentials or image content.
- Every accepted upload creates a new logical media record and immutable
  Cloudinary asset, even when its digest matches existing bytes. The digest
  proves integrity; it is not a global deduplication key.
- Deleting media that is referenced by a current article returns `409`. Media
  referenced only by immutable revisions may be soft-deleted from management
  views, but its object remains retrievable so historical integrity is not
  silently destroyed.
- Restoring an asset fails with `409` if its remote object is missing or fails
  digest verification.
- Physical garbage collection is a separate approved maintenance operation. It
  deletes only objects with no current or revision reference after a documented
  retention period.
- Cloudinary Free usage is reviewed over its rolling 30-day window. The initial
  operating budget is 25 combined credits across stored bytes, delivered image
  bandwidth, and transformations. Usage at 60% is logged as a warning and usage
  at 80% requires an explicit capacity decision before adding derived variants
  or increasing upload limits.
- Reconciliation respects the Cloudinary Free Admin API allowance and uses
  bounded pages and backoff; it never scans the account on a public request.

## Acceptance scenarios

### Scenario: Upload a safe article image

- Given a valid JPEG within the byte and dimension limits
- When the site owner uploads it with alt text
- Then the API removes embedded metadata, stores a sanitized immutable asset,
  and returns `201` with its media ID, dimensions, URL, and ETag

### Scenario: Reject a disguised file

- Given an upload declares `image/png` but its bytes are not a valid PNG
- When the site owner uploads it
- Then the API returns `400` and commits neither a media row nor a public object

### Scenario: Render a referenced image

- Given a Published article contains a valid media reference
- When its public detail is requested
- Then the image block contains the resolved public URL, contextual alt text,
  width, and height without exposing the storage provider key

### Scenario: Preserve a referenced asset

- Given an article or immutable revision references a media asset
- When cleanup evaluates the stored object
- Then the object is retained

### Scenario: Prevent deletion of active media

- Given an active article references a media asset
- When the site owner attempts to delete that asset with the current ETag
- Then the API returns `409` and the article image remains available

### Scenario: Keep immutable URLs stable

- Given a media asset is already publicly cached
- When a different image is uploaded as its replacement
- Then the replacement receives a new media ID and URL and the old bytes are not
  overwritten at the existing URL

## Test evidence

- Contract tests for multipart upload, size limits, management representations,
  ETags, article media references, and absence of a public byte-proxy route.
- Provider contract tests use an in-memory fake; opt-in Cloudinary sandbox tests
  cover signed upload-response verification, versioned URL generation, remote
  failure, cleanup recovery, reconciliation bounds, and reference protection.
- Image fixture tests for valid JPEG/PNG/WebP, mismatched signatures, malformed
  files, animation, transparency, orientation, dimension bounding, metadata
  removal, and no upscaling.
- Public HTTP tests for immutable caching, content type, `nosniff`, stable URL,
  unavailable storage behavior, and no provider metadata leakage.

## Decisions and open questions

- Decision: Google Drive is not the production asset server because its sharing,
  download, thumbnail, identity-disclosure, caching, and URL semantics are not
  a stable image-delivery contract.
- Decision: the Blog API mediates the initial low-volume administrative upload;
  direct-to-storage signed uploads may be specified later if scale requires
  them.
- Decision: immutable object keys eliminate cache-purge requirements when image
  content changes.
- Decision: article writes reference Blog media IDs, while public reads expose
  provider-neutral resolved image data.
- Decision: Cloudinary Free is the initial storage and CDN provider. Its
  provider identifiers remain behind the media adapter and administrative data
  boundary so migration does not change article records or public schemas.
- Decision: use signed server-side uploads through the official .NET SDK and
  explicit versioned delivery URLs; unsigned browser uploads and unversioned
  URLs are forbidden in the first slice.
- Decision: the first slice emits one normalized asset bounded to 1600 pixels;
  responsive derivatives remain out of scope until real performance and
  Cloudinary credit usage justify their contract and storage cost.
