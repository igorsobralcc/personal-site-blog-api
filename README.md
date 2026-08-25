# Personal Site Blog API

A .NET 10 API for drafting, publishing, and organizing technical articles for
the personal site. It provides cacheable public article and series reads,
protected authoring operations, structured content blocks, media references,
immutable revisions, optimistic concurrency, health checks, tests, and
container delivery.

Presentation content remains owned by the separate Presentation API. This
service never reads or writes the `presentation` schema.

## Architecture

The service is a .NET 10 Minimal API organized as small feature slices. Shared
HTTP behavior lives in `Infrastructure`, while media, articles, and series own
their routes, validation, lifecycle, and projections.

```text
HTTP client
    |
    +-- GET /api/v1/articles ------------ public feed and article detail
    +-- GET /api/v1/series/{slug} -------- public series detail
    |
    +-- /api/v1/admin/* ----------------- admin key + ETag concurrency
    |                                             |
    +-- /health/live and /health/ready            |
                                                  v
                                               BlogStore
                                                  |
                                      in-memory development state
```

```text
src/BlogApi/
  Infrastructure/  authentication, pagination, concurrency, errors, logging
  Articles.cs       article lifecycle, blocks, revisions, feed, and detail
  Media.cs          upload validation, metadata, storage boundary, references
  Series.cs         series lifecycle, memberships, revisions, public detail
tests/               HTTP acceptance tests
specs/               approved behavior and acceptance scenarios
docs/                OpenAPI contract and Postman collection
.github/             CI, deployment, and dependency automation
```

The checked-in [OpenAPI contract](docs/openapi.yaml) and approved
[specifications](specs/README.md) define observable behavior. Public media
projections contain only an opaque URL, contextual alternative text, intrinsic
dimensions, and an optional caption; provider identifiers do not cross the
public API boundary.

### Current persistence boundary

The approved production design uses PostgreSQL objects in a Blog-owned `blog`
schema and Cloudinary behind `IMediaStorage`. The current executable slice uses
singleton in-memory adapters for both boundaries. It is suitable for contract
integration and local API development, but process restarts lose all records
and uploaded bytes.

Do not treat the current container as a durable production deployment. Before
production launch, replace `BlogStore` with the Blog-specific EF Core/Npgsql
implementation and `InMemoryMediaStorage` with the signed Cloudinary adapter,
then add migrations, database readiness, image normalization, reconciliation,
and provider integration tests required by the approved specifications.

## Business rules

### Shared resource behavior

- Independently managed records use UUIDv7 identifiers, UTC timestamps, soft
  deletion, and a positive integer concurrency version.
- JSON uses `camelCase`; the planned PostgreSQL schema uses `snake_case`.
- Admin requests require `X-Admin-Key`, compared in constant time and excluded
  from logs.
- Admin collections accept `X-Page` (default `1`), `X-Page-Size` (default `20`,
  maximum `100`), and `X-Include-Deleted` (default `false`).
- Mutable responses emit strong ETags. PATCH, DELETE, and restore require one
  strong `If-Match`; missing and stale values return `428` and `412`.
- PATCH uses `application/merge-patch+json`. Supplied aggregate arrays replace
  their complete collection.
- Errors use `application/problem+json` and include a request trace ID.

### Articles

- Articles move through `Writing`, `Draft`, `Published`, `NotListed`, and
  `Archived`; creation always starts in `Writing`.
- Only active `Published` articles appear in anonymous responses. Every private,
  deleted, or missing state returns the same public `404` shape.
- First publication fixes `publishedAt` and makes the slug immutable. Restore
  always returns an article to `Draft`.
- Body version 1 supports paragraph, heading, quote, list, code, image, and
  table blocks. Text is data rather than HTML or Markdown.
- Reading time combines prose at 200 words per minute and nonblank code at 12
  lines per minute, rounded up to at least one minute.
- Every successful state-changing mutation writes a full immutable revision.
- The public feed uses a signed opaque seek cursor and orders by publication
  time and UUID descending.

### Media

- Management supports upload, lookup, listing, descriptive metadata changes,
  soft deletion, and restoration.
- Uploads accept declared JPEG, PNG, or WebP files up to 10 MiB and validate the
  leading file signature.
- Media referenced by an active article cannot be deleted.
- Public article responses resolve a Blog media ID into provider-neutral image
  metadata; the API does not proxy public image bytes.
- The current adapter generates local development placeholder URLs under
  `https://assets.invalid`; Cloudinary upload, normalization, and durable
  delivery remain production integration work.

### Series

- Series share the article lifecycle meanings and Draft-on-restore behavior.
- Membership is many-to-many. Supplying `articleIds` in a patch replaces the
  complete set and rejects duplicate, missing, or deleted articles.
- Public series return only Published members ordered by article creation time
  and UUID ascending, using the same summary projection as the article feed.
- Membership changes create complete immutable series revisions.

### Public caching

Article feed, article detail, and series detail responses use strong ETags and
`Cache-Control: public, max-age=60, stale-while-revalidate=300`. A matching
`If-None-Match` returns `304` without a response body.

## HTTP endpoints

| Area | Routes |
| --- | --- |
| Public articles | `GET /api/v1/articles`, `GET /api/v1/articles/{slug}` |
| Public series | `GET /api/v1/series/{slug}` |
| Article admin | collection create/list; item get/patch/delete/restore; revision list/detail under `/api/v1/admin/articles` |
| Media admin | collection upload/list; item get/patch/delete/restore under `/api/v1/admin/media` |
| Series admin | collection create/list; item get/patch/delete/restore; revision list/detail under `/api/v1/admin/series` |
| Operations | `GET /health/live`, `GET /health/ready` |

Import the
[Postman collection](docs/PersonalSite.Blog.Api.postman_collection.json), set
`baseUrl` and `adminKey`, and run requests in folder order. Creation and read
requests capture resource IDs and ETags for subsequent mutations; the public
feed captures `nextCursor` for continuation.

## Run locally

### Prerequisites

- .NET SDK 10
- Optional: Postman and Docker

### 1. Restore and build

```powershell
git clone https://github.com/igorsobralcc/personal-site-blog-api.git
Set-Location personal-site-blog-api
dotnet restore
dotnet build --configuration Release --no-restore --warnaserror
```

### 2. Configure local secrets

Do not place administration or cursor-signing keys in tracked settings.

```powershell
dotnet user-secrets init --project src/BlogApi
dotnet user-secrets set "AdminKey" "<local-admin-key>" --project src/BlogApi
dotnet user-secrets set "CursorKey" "<local-cursor-signing-key>" --project src/BlogApi
dotnet user-secrets set "PublicSiteOrigin" "https://localhost:5173" --project src/BlogApi
dotnet user-secrets set "Cors:Origins:0" "https://localhost:5173" --project src/BlogApi
dotnet dev-certs https --trust
```

### 3. Run and verify

The checked-in development launch profile serves HTTPS on port `7146` and HTTP
on port `5026`:

```powershell
dotnet run --launch-profile https --project src/BlogApi
```

In another terminal:

```powershell
Invoke-RestMethod https://localhost:7146/health/live
Invoke-RestMethod https://localhost:7146/health/ready
Invoke-RestMethod "https://localhost:7146/api/v1/articles?limit=8"
```

Liveness reports that the process can serve requests. Readiness currently
reports application readiness only; it must include a bounded PostgreSQL check
when durable persistence is wired.

### 4. Run tests

```powershell
dotnet test PersonalSite.BlogApi.slnx --configuration Release --no-restore
```

The HTTP acceptance suite covers administrative authentication, article
publication and conditional public reads, and required mutation preconditions.

## Configuration

| Key | Purpose | Secret |
| --- | --- | --- |
| `AdminKey` | Protects `/api/v1/admin/*` | Yes |
| `CursorKey` | Signs opaque public feed cursors | Yes |
| `PublicSiteOrigin` | Derives canonical article URLs | No |
| `Cors:Origins` | Explicit allowed browser origins | No |

The current code falls back to `AdminKey` for cursor signing and to placeholder
development origins when settings are absent. Production hardening must fail
startup when required configuration is missing; no production environment
should rely on those development fallbacks.

## CI/CD and supply-chain controls

The [CI workflow](.github/workflows/ci.yml) runs on pull requests and `main`:

- Conventional Commit policy validation and validator self-tests.
- Workflow linting with a checksum-verified, pinned `actionlint` binary.
- Full-history Gitleaks scanning.
- .NET restore, Release build with warnings as errors, and acceptance tests.
- Docker production-image build after policy, secret scan, and tests pass.
- Immutable `sha-<commit>` plus convenience `latest` GHCR publication on
  successful `main` pushes.

The manually triggered [deployment workflow](.github/workflows/deploy.yml)
targets a protected `production` environment on a dedicated self-hosted runner.
It validates runtime settings, deploys an immutable GHCR image, waits for the
container health check, and restores the prior container on failure. Because
the current service is not durable, enable this workflow only after PostgreSQL
and Cloudinary production adapters are complete.

[Dependabot](.github/dependabot.yml) checks GitHub Actions, NuGet, and Docker
dependencies weekly. Actions and container bases are pinned so automated
updates remain explicit reviewable changes.

Protect `main` with pull requests, blocked force-push/deletion, and the required
`Commit policy`, `Secret scan`, `Build and test`, and `Build container` checks.

## Key trade-offs

| Decision | Benefit | Trade-off / alternative |
| --- | --- | --- |
| Structured versioned blocks | Safe semantic rendering and generated-client unions | Markdown is easier to author but needs a sanitization/rendering contract. |
| Static admin key | Small first-release authentication surface | OIDC provides identity and rotation but adds provider and authorization complexity. |
| Soft deletion and revisions | Recovery and complete editorial history | Retains more data and keeps referenced media alive longer. |
| Signed seek cursor | Stable continuation without exposing private rows | Offset pagination is simpler but drifts during publication changes. |
| Provider-neutral media projection | Storage migration does not change public article schemas | Requires an API-side resolution layer. |
| In-memory adapters for the contract slice | Fast local contract integration | Not durable or horizontally scalable; PostgreSQL and Cloudinary are required for production. |

## Development workflow

Behavior changes are spec-driven: update an approved file under `specs/`,
update `docs/openapi.yaml` before HTTP implementation, add acceptance tests,
implement, and verify. Commits must follow Conventional Commits and remain
small, coherent, buildable, secret-free, and independently revertible. See
[CONTRIBUTING.md](CONTRIBUTING.md).
