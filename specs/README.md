# Feature specifications

Create one directory per independently deliverable behavior and use
`_template/spec.md`. Specifications are permanent product documentation and
evolve through `Draft`, `Approved`, and `Implemented` states.

Current Approved specifications:

```text
specs/
  platform-foundation/spec.md
  article-media/spec.md
  article-publishing/spec.md
  article-series/spec.md
```

Contract and implementation dependency order:

1. Platform foundation
2. Article media
3. Article publishing
4. Article series

Article publishing depends on the media identity and public projection. Article
series then depends on the article identity, lifecycle, public projection, and
authorization rules established by the preceding specifications.

The current decision summary and approval checklist are recorded in
[review-2026-08-25.md](review-2026-08-25.md).
