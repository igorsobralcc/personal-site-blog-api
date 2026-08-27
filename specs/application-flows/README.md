# Application-flow specifications

These Draft specifications translate the approved feature contracts into
observable end-to-end flows for automated testing. They complement, and do not
override, the Approved platform, media, article-publishing, and article-series
specifications.

## Pessimistic testing standard

Tests assume that every input, state transition, dependency, concurrency token,
and commit boundary can fail. A flow is covered only when its successful result
and its failure invariants are both asserted.

Every rejection or injected failure must verify, where applicable:

1. the exact status, content type, Problem Details shape, and trace ID;
2. no root, child, membership, reference, or revision row was partially changed;
3. version, ETag, lifecycle timestamps, and publication timestamps are unchanged;
4. no private/deleted content became public and no public cache incorrectly
   validates stale content;
5. no external object was leaked, overwritten, or deleted incorrectly;
6. retries are safe and do not duplicate mutations or revisions;
7. logs contain the safe operation context but no credential, body, media bytes,
   provider secret, or private authoring content.

For successful mutations, tests assert the inverse: exactly one atomic state
change, exactly one version increment, the expected revision/reference changes,
and immediate consistency across administrative and public projections.

## Flow index and route traceability

| Flow spec | Routes and responsibilities | Primary backlog IDs |
| --- | --- | --- |
| [Request boundary and collections](request-boundary-and-collections/spec.md) | all middleware/binding; admin list/get/create entry behavior | `AUTH`, `PAGE`, `ERR`, `BIND`, `CORS`, `HTTPS`, `LOG` |
| [Optimistic concurrency](optimistic-concurrency/spec.md) | all PATCH/DELETE/restore preconditions and races | `CONC-001`, `CONC-002`, `TX-001` |
| [Article authoring](article-authoring/spec.md) | admin article list/create/get/patch; blocks, metadata, reading time | `ART-CREATE`, `ART-VALID`, `ART-BLOCK`, `ART-PATCH`, `ART-READ` |
| [Article publication and delivery](article-publication-delivery/spec.md) | article lifecycle; public feed/detail/cursors/caching | `ART-LIFE`, `ART-PUB`, `ART-PRIV`, `ART-CURSOR`, `ART-PROJ`, `ART-CACHE` |
| [Article recovery and history](article-recovery-and-history/spec.md) | article delete/restore/revision list/detail | `ART-REV`, `ART-NOOP`, `ART-REST`, `REV-READ` |
| [Media ingestion](media-ingestion/spec.md) | admin media upload and provider commit boundary | `MEDIA-UP`, `MEDIA-IMG`, `MEDIA-FAIL` |
| [Media management and retention](media-management-and-retention/spec.md) | media list/get/patch/delete/restore and reference protection | `MEDIA-PATCH`, `MEDIA-REF`, `MEDIA-REST`, `MEDIA-SNAP` |
| [Series authoring and membership](series-authoring-and-membership/spec.md) | admin series list/create/get/patch and membership | `SER-CREATE`, `SER-PATCH`, `SER-MEMBER` |
| [Series publication and recovery](series-publication-and-recovery/spec.md) | public series; lifecycle/delete/restore/revisions/caching | `SER-LIFE`, `SER-PRIV`, `SER-CACHE`, `REV-READ` |
| [Operations and configuration](operations-and-configuration/spec.md) | health, startup configuration, persistence isolation, container | `HEALTH`, `CONFIG`, `STORE`, `CONTAINER`, `OAS` |
| [Editorial cross-aggregate journeys](editorial-cross-aggregate-journeys/spec.md) | complete article/media/series journeys and combined failures | cross-feature P0 scenarios |

Together these specifications cover all 27 mapped route operations and the
middleware, persistence, provider, cache, and operational paths surrounding
them.

### Endpoint coverage matrix

Shared authorization, routing/binding, Problem Details, logging, and
concurrency cases apply in addition to the feature spec named below.

| Method and route | Owning flow spec |
| --- | --- |
| `GET /health/live` | Operations and configuration |
| `GET /health/ready` | Operations and configuration |
| `GET /api/v1/articles` | Article publication and public delivery |
| `GET /api/v1/articles/{slug}` | Article publication and public delivery |
| `GET /api/v1/series/{slug}` | Series publication and recovery |
| `GET /api/v1/admin/articles` | Request boundary/collections + Article authoring |
| `POST /api/v1/admin/articles` | Article authoring |
| `GET /api/v1/admin/articles/{id}` | Article authoring |
| `PATCH /api/v1/admin/articles/{id}` | Article authoring + Optimistic concurrency |
| `DELETE /api/v1/admin/articles/{id}` | Article recovery/history + Optimistic concurrency |
| `POST /api/v1/admin/articles/{id}/restore` | Article recovery/history + Optimistic concurrency |
| `GET /api/v1/admin/articles/{id}/revisions` | Article recovery and history |
| `GET /api/v1/admin/articles/{id}/revisions/{revisionNumber}` | Article recovery and history |
| `GET /api/v1/admin/media` | Request boundary/collections + Media management/retention |
| `POST /api/v1/admin/media` | Media ingestion |
| `GET /api/v1/admin/media/{id}` | Media management and retention |
| `PATCH /api/v1/admin/media/{id}` | Media management/retention + Optimistic concurrency |
| `DELETE /api/v1/admin/media/{id}` | Media management/retention + Optimistic concurrency |
| `POST /api/v1/admin/media/{id}/restore` | Media management/retention + Optimistic concurrency |
| `GET /api/v1/admin/series` | Request boundary/collections + Series authoring/membership |
| `POST /api/v1/admin/series` | Series authoring and membership |
| `GET /api/v1/admin/series/{id}` | Series authoring and membership |
| `PATCH /api/v1/admin/series/{id}` | Series authoring/membership + Optimistic concurrency |
| `DELETE /api/v1/admin/series/{id}` | Series publication/recovery + Optimistic concurrency |
| `POST /api/v1/admin/series/{id}/restore` | Series publication/recovery + Optimistic concurrency |
| `GET /api/v1/admin/series/{id}/revisions` | Series publication and recovery |
| `GET /api/v1/admin/series/{id}/revisions/{revisionNumber}` | Series publication and recovery |

## Test levels

- **HTTP acceptance:** real middleware, routing, binding, serialization, and
  in-process application with isolated state.
- **Domain/handler:** exhaustive validation and state-machine matrices with a
  controllable clock.
- **Persistence integration:** real PostgreSQL schema, constraints,
  transactions, concurrency, migrations, and privilege isolation.
- **Provider contract:** failure-injectable media adapter plus opt-in Cloudinary
  sandbox verification.
- **Contract:** OpenAPI/runtime drift and generated-client compatibility.
- **Operational:** production configuration, container, logging, health, and
  restart behavior.

No in-memory acceptance test may be cited as evidence for a PostgreSQL,
Cloudinary, cleanup, or production-startup guarantee.
