#!/usr/bin/env bash
#
# release.sh — cut a versioned release of the migration platform.
#
# Bumps VERSION, rolls CHANGELOG's [Unreleased] into a dated version section,
# commits + annotated-tags, and builds the api/web Docker images tagged with
# the version and `latest`. Does NOT push git or images automatically — it
# prints the exact push commands so the operator stays in control.
#
# Usage:
#   scripts/release.sh <version>       # explicit, e.g. scripts/release.sh 0.9.1
#   scripts/release.sh --patch         # 0.9.0 -> 0.9.1
#   scripts/release.sh --minor         # 0.9.0 -> 0.10.0
#   scripts/release.sh --major         # 0.9.0 -> 1.0.0
#
# Flags:
#   --push          also `docker push` the images (needs REGISTRY + a prior login)
#   --no-build      skip the Docker image build (tag + changelog only)
#   --allow-dirty   permit release with uncommitted changes in the tree
#   --dry-run       show what would happen, change nothing
#
# Registry (optional):
#   REGISTRY=myacr.azurecr.io scripts/release.sh --patch --push
#   -> also tags/pushes ${REGISTRY}/migration-api:X.Y.Z and :latest (+ web).
#   Run `az acr login -n <acr>` or `docker login` first.
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

VERSION_FILE="$ROOT/VERSION"
CHANGELOG="$ROOT/CHANGELOG.md"
REGISTRY="${REGISTRY:-}"

PUSH=false
BUILD=true
ALLOW_DIRTY=false
DRY_RUN=false
BUMP=""
EXPLICIT=""

die()  { echo "❌ $*" >&2; exit 1; }
info() { echo "→  $*"; }
run()  { if $DRY_RUN; then echo "   [dry-run] $*"; else eval "$*"; fi; }

# ── Parse args ───────────────────────────────────────────────────────────────
for arg in "$@"; do
  case "$arg" in
    --patch|--minor|--major) BUMP="${arg#--}" ;;
    --push)        PUSH=true ;;
    --no-build)    BUILD=false ;;
    --allow-dirty) ALLOW_DIRTY=true ;;
    --dry-run)     DRY_RUN=true ;;
    -h|--help)     sed -n '2,40p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    [0-9]*.[0-9]*.[0-9]*) EXPLICIT="$arg" ;;
    *) die "unknown argument: $arg (see --help)" ;;
  esac
done

[[ -n "$BUMP" && -n "$EXPLICIT" ]] && die "give either a version OR a --patch/--minor/--major bump, not both"
[[ -z "$BUMP" && -z "$EXPLICIT" ]] && die "specify a version (e.g. 0.9.1) or a bump (--patch/--minor/--major). See --help."

# ── Determine new version ────────────────────────────────────────────────────
CURRENT="0.9.0"
[[ -f "$VERSION_FILE" ]] && CURRENT="$(tr -d '[:space:]' < "$VERSION_FILE")"

if [[ -n "$EXPLICIT" ]]; then
  NEW="$EXPLICIT"
else
  IFS='.' read -r MA MI PA <<< "$CURRENT"
  case "$BUMP" in
    patch) PA=$((PA + 1)) ;;
    minor) MI=$((MI + 1)); PA=0 ;;
    major) MA=$((MA + 1)); MI=0; PA=0 ;;
  esac
  NEW="${MA}.${MI}.${PA}"
fi
[[ "$NEW" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "'$NEW' is not a valid semver X.Y.Z"
TAG="v${NEW}"

info "Current: ${CURRENT}   →   New: ${NEW}   (tag ${TAG})"

# ── Guard rails ──────────────────────────────────────────────────────────────
BRANCH="$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo '?')"
[[ "$BRANCH" == "main" ]] || echo "⚠️  not on 'main' (on '${BRANCH}') — continuing anyway."

if ! $ALLOW_DIRTY && [[ -n "$(git status --porcelain)" ]]; then
  die "working tree is dirty. Commit/stash first, or pass --allow-dirty."
fi
if git rev-parse "$TAG" >/dev/null 2>&1; then
  die "tag ${TAG} already exists."
fi

# ── VERSION ──────────────────────────────────────────────────────────────────
info "Writing VERSION = ${NEW}"
run "printf '%s\n' '${NEW}' > '${VERSION_FILE}'"

# ── CHANGELOG: roll [Unreleased] into a dated version section ────────────────
if [[ -f "$CHANGELOG" ]]; then
  DATE="$(date +%Y-%m-%d)"
  info "Rolling CHANGELOG [Unreleased] → [${NEW}] - ${DATE}"
  if ! $DRY_RUN; then
    awk -v ver="$NEW" -v date="$DATE" '
      /^## \[Unreleased\]/ && !done {
        print "## [Unreleased]"; print "";
        print "## [" ver "] - " date;
        done = 1; next
      }
      { print }
    ' "$CHANGELOG" > "$CHANGELOG.tmp" && mv "$CHANGELOG.tmp" "$CHANGELOG"
  fi
else
  echo "⚠️  no CHANGELOG.md — skipping changelog roll."
fi

# ── Commit + tag ─────────────────────────────────────────────────────────────
info "Committing + tagging ${TAG}"
run "git add '${VERSION_FILE}' '${CHANGELOG}' 2>/dev/null || git add '${VERSION_FILE}'"
run "git commit -m 'release: ${TAG}'"
run "git tag -a '${TAG}' -m 'Release ${TAG}'"

# ── Build images ─────────────────────────────────────────────────────────────
build_image() {
  local name="$1" ctx="$2"
  info "Building ${name}:${NEW} (+ latest)"
  run "docker build -t '${name}:${NEW}' -t '${name}:latest' '${ctx}'"
  if [[ -n "$REGISTRY" ]]; then
    run "docker tag '${name}:${NEW}' '${REGISTRY}/${name}:${NEW}'"
    run "docker tag '${name}:latest' '${REGISTRY}/${name}:latest'"
    if $PUSH; then
      info "Pushing ${REGISTRY}/${name}:${NEW} (+ latest)"
      run "docker push '${REGISTRY}/${name}:${NEW}'"
      run "docker push '${REGISTRY}/${name}:latest'"
    fi
  fi
}

if $BUILD; then
  command -v docker >/dev/null 2>&1 || die "docker not found — install Docker Desktop (with WSL integration), or pass --no-build."
  build_image migration-api "$ROOT/apps/api"
  build_image migration-web "$ROOT/apps/web"
else
  info "Skipping image build (--no-build)."
fi

if $PUSH && [[ -z "$REGISTRY" ]]; then
  echo "⚠️  --push ignored: set REGISTRY=<host> and run 'az acr login'/'docker login' first."
fi

# ── Done — print manual next steps ───────────────────────────────────────────
cat <<EOF

✅ Release ${TAG} prepared locally.

Next (manual, on purpose):
  git push && git push --tags
$( [[ -n "$REGISTRY" ]] && ! $PUSH && echo "  # push images:  REGISTRY=${REGISTRY} $0 ${NEW} --no-build --push  (or docker push ${REGISTRY}/migration-{api,web}:${NEW})" )
$( [[ -z "$REGISTRY" ]] && echo "  # images are local only (migration-api:${NEW}, migration-web:${NEW}). Set REGISTRY=<host> --push to publish." )

Deploy: point docker-compose / the platform-app Terraform at the ${NEW} images.
EOF
