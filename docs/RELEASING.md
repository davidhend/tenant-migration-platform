# Releasing

The platform is **self-hosted**: each operator runs their own instance. A release
is a git tag + a matching pair of Docker images (`migration-api`, `migration-web`)
that an operator deploys via `docker-compose` or the `platform-app` Terraform
stack. There is no cloud CI — releases are cut locally with `scripts/release.sh`
(or `scripts/release.ps1` on Windows).

## Versioning

Semantic Versioning (`MAJOR.MINOR.PATCH`). One version spans the whole platform —
the API, the web app, **and the Azure Automation runbook are released together**:

| Where the version lives | How |
|---|---|
| `VERSION` (repo root) | source of truth; bumped by `release.sh` |
| API | `Services/PlatformVersion.Current`, exposed at `GET /api/version` |
| Runbook | `# RUNBOOK_VERSION: x.y.z` marker in `Invoke-SpoCrossTenantOperation.ps1` |

Because the API drives the runbook by name, a version mismatch means the deployed
runbook may lack operations the API expects. After deploying a new version, confirm
`GET /api/version` reports matching `version` and `runbookVersion` (the API's
`RunbookAutoPublisher` normally republishes the runbook to match on startup when it
holds Automation Contributor).

**Pre-1.0:** we are in `0.x` because a full end-to-end content move is validated
only up to Microsoft-side boundaries (see `CHANGELOG.md`). Under SemVer's `0.x`
rules, minor bumps may include breaking changes; `1.0.0` is reserved for a proven
full end-to-end migration.

## Cutting a release

```bash
# 1. Land all changes on main and fill in CHANGELOG.md's [Unreleased] section.

# 2. Cut the release (bump + changelog roll + commit + tag + build images):
scripts/release.sh --patch          # 0.9.0 -> 0.9.1   (or --minor / --major)
scripts/release.sh 0.9.1            # ...or an explicit version

# 3. Push (kept manual on purpose):
git push && git push --tags
```

`release.sh` will:
1. Refuse to run on a dirty tree (override: `--allow-dirty`) and warn if not on `main`.
2. Write the new `VERSION`.
3. Roll `CHANGELOG.md`'s `[Unreleased]` items into a dated `[X.Y.Z]` section.
4. `git commit` the bump and create an annotated tag `vX.Y.Z`.
5. Build `migration-api:X.Y.Z` + `:latest` and `migration-web:X.Y.Z` + `:latest`.
6. Print the git-push command — it never pushes for you.

Flags: `--push` (push images, needs `REGISTRY` + a prior login), `--no-build`
(tag only), `--dry-run` (show, change nothing), `--allow-dirty`.

## Publishing images to a registry

Images are local-only unless you set `REGISTRY` and pass `--push`. Log in to the
registry first, then:

```bash
# Azure Container Registry
az acr login --name myacr
REGISTRY=myacr.azurecr.io scripts/release.sh 0.9.1 --push

# Docker Hub (or any registry)
docker login
REGISTRY=docker.io/myorg scripts/release.sh 0.9.1 --push
```

This also tags/pushes `${REGISTRY}/migration-api:X.Y.Z` (+ `latest`) and the web
image. The `platform-app` Terraform stack takes the image reference as an input —
point it at `${REGISTRY}/migration-api:X.Y.Z` for a pinned, reproducible deploy.

## Deploying a release

- **Local / small**: `git checkout vX.Y.Z` then `./start.sh --build` (or `make up`).
- **Cloud**: push images to your registry (above), then set the image tag in the
  `platform-app` Terraform stack and `terraform apply`.

Pin to an explicit `X.Y.Z` tag in production; `latest` is a convenience for dev.

## Hotfixes

Branch from the tag, fix, then `scripts/release.sh --patch` on that branch and
`git push --tags`. Forward-port the fix to `main`.
