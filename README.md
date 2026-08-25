# Personal Site Blog API

Specification-first technical-article service for the personal site. The
portfolio shipped without a publishing dependency; the Blog API is now in its
contract-design phase and has no production implementation yet.

## Responsibility

This service owns article drafts, publication workflow, slugs, tags, SEO
metadata, and rendered article content. The Presentation API does not store or
proxy blog posts, and the frontend will integrate with this service when the
first Blog contract is approved and implemented.

The first planned slice provides a cacheable public article feed, stable article
lookup, protected authoring operations, structured content blocks, publication
lifecycle, media upload, and revision history. Search, scheduling, and other
later capabilities remain outside that slice.

## Current phase

Approved specifications will be contracted and implemented in this order:

1. [Platform foundation](specs/platform-foundation/spec.md)
2. [Article media](specs/article-media/spec.md)
3. [Article publishing](specs/article-publishing/spec.md)
4. [Article series](specs/article-series/spec.md)

These approved specifications now drive the versioned OpenAPI contract before
production code is added.

Cloudinary Free is the selected initial media storage and CDN provider. Article
records and public contracts remain provider-neutral so a later storage
migration does not require rewriting article content.

## Service boundary

Do not add blog dependencies or shared database tables to the Presentation API.
The only planned integration is through the Blog API when the React application
enables its existing `/articles` and `/articles/:slug` routes.

Every feature in this repository uses spec-driven development. Its feature
specification and API contract must be approved before production implementation
begins. Development also uses incremental Conventional Commits so each coherent
change can be reverted safely. See
[CONTRIBUTING.md](CONTRIBUTING.md).
