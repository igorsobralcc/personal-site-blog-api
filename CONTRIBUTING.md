# Contributing

## Required method: spec-driven development

Every feature, bug fix that changes behavior, and breaking refactor starts with
a version-controlled specification under `specs/<feature-name>/spec.md`.
Because the Blog API is a future project, its first implementation feature must
also establish the versioned OpenAPI contract rather than inferring a contract
from completed code.

### Workflow

1. **Specify** — copy `specs/_template/spec.md`, describe the publishing outcome
   and mark the specification `Draft`.
2. **Review** — settle content-format, workflow, storage, search, and operational
   decisions that affect the feature, then mark it `Approved`.
3. **Contract** — define or update OpenAPI before production implementation.
4. **Prove** — add tests mapped to the acceptance scenarios and initially
   failing for the missing behavior.
5. **Implement** — deliver the smallest slice that satisfies the specification.
6. **Verify** — run contract, integration, migration, and production-build checks.
7. **Reconcile** — update and reapprove the specification when decisions change.
8. **Complete** — mark the specification `Implemented` and record test evidence.

### Blog specification requirements

In addition to the HTTP contract, a blog feature specification must address the
relevant parts of authoring format, draft/public visibility, slug stability,
revisions, publication scheduling, sanitization, media, SEO, search, caching,
and backward compatibility. Unneeded capabilities should explicitly remain out
of scope rather than being implemented speculatively.

### Pull request gate

A feature is incomplete unless its pull request links its specification,
acceptance scenarios map to automated tests, contracts and migrations match the
implementation, and relevant checks pass.

## Required method: Conventional Commits

Development must be recorded as a sequence of small, atomic commits using the
[Conventional Commits](https://www.conventionalcommits.org/) format:

```text
<type>(<optional-scope>): <imperative summary>
```

Allowed types are `feat`, `fix`, `docs`, `refactor`, `test`, `build`, `ci`,
`chore`, `perf`, `style`, and `revert`. Use `!` and a `BREAKING CHANGE:` footer
for breaking changes.

Examples:

```text
docs(publishing): approve article lifecycle spec
test(publishing): cover scheduled publication
feat(publishing): add publish transition
fix(slugs): reject duplicate published slugs
```

Commits must be dispersed throughout development at meaningful, working
checkpoints. Do not wait until the end of a feature and place the entire change
in one commit. A normal sequence is specification, OpenAPI contract, failing
tests, implementation, migration, and focused refinement.

Each commit must:

- Represent one coherent reason for change
- Avoid unrelated formatting, cleanup, or feature work
- Keep the repository buildable whenever practical
- Keep a schema migration with the model change that requires it
- Include tests with the behavior they prove, or in an immediately preceding
  test commit during the red-green cycle
- Be independently understandable and safely revertible
- Never contain secrets, generated local state, or temporary debugging changes

Use a `revert:` commit to undo shared history. Do not rewrite published history
to conceal intermediate development.
