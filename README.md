# Personal Site Blog API

Reserved for the future technical-article service. It is intentionally outside
the first MVP so the portfolio can ship without carrying an unused publishing
system.

## Future responsibility

This service will own article drafts, publication workflow, slugs, tags, SEO
metadata, and rendered article content. The Presentation API will not store or
proxy blog posts, and the frontend will integrate with this service only when
the blog phase begins.

Likely future endpoints include a cacheable public article feed and protected
authoring operations. Their architecture and contract should be defined when
the publishing workflow, editor format, search, and hosting requirements are
known.

## Current rule

Do not add blog dependencies or shared database tables to the Presentation API.
The only planned integration is that the React application will eventually add
`/blog` and `/blog/:slug` routes backed by this service.

Every future feature in this repository must use spec-driven development. Its
feature specification and API contract must be approved before production
implementation begins. Development must also use incremental Conventional
Commits so each coherent change can be reverted safely. See
[CONTRIBUTING.md](CONTRIBUTING.md).
