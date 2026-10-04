#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$repo_root"

usage() {
  echo "Usage: $0 VERSION"
}

if [[ "${1:-}" == "--help" ]]; then
  usage
  exit 0
fi

if (($# != 1)); then
  usage >&2
  exit 2
fi

version="$1"
if [[ ! "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
  echo "Version must be a stable semantic version in MAJOR.MINOR.PATCH form (for example 1.2.3)." >&2
  exit 2
fi

tag="v$version"
for command in git gh dotnet tar sha256sum rg; do
  if ! command -v "$command" >/dev/null 2>&1; then
    echo "Required command '$command' was not found in PATH." >&2
    exit 1
  fi
done

if ! commit="$(git -C "$repo_root" rev-parse --verify HEAD)"; then
  echo "Release must run from a Git checkout with at least one commit." >&2
  exit 1
fi
checkout_status="$(git -C "$repo_root" status --porcelain --untracked-files=all)" || {
  echo "Could not inspect checkout state; no release was created." >&2
  exit 1
}
if [[ -n "$checkout_status" ]]; then
  echo "Release requires a clean checkout. Commit or remove all tracked and untracked changes first." >&2
  exit 1
fi

local_tag_status=0
git -C "$repo_root" show-ref --verify --quiet "refs/tags/$tag" || local_tag_status=$?
if ((local_tag_status == 0)); then
  echo "Tag '$tag' already exists locally; refusing to overwrite it." >&2
  exit 1
elif ((local_tag_status != 1)); then
  echo "Could not inspect local tags; no release was created." >&2
  exit 1
fi
if ! origin_url="$(git -C "$repo_root" remote get-url origin)"; then
  echo "Git remote 'origin' is required to check and publish the release tag." >&2
  exit 1
fi
set +e
git -C "$repo_root" ls-remote --exit-code --tags origin "refs/tags/$tag" >/dev/null 2>&1
remote_tag_status=$?
set -e
if ((remote_tag_status == 0)); then
  echo "Tag '$tag' already exists on origin; refusing to overwrite it." >&2
  exit 1
elif ((remote_tag_status != 2)); then
  echo "Could not check the '$tag' tag on origin. Check Git access and try again." >&2
  exit 1
fi

if ! gh auth status >/dev/null 2>&1; then
  echo "GitHub CLI is not authenticated. Run 'gh auth login' and retry." >&2
  exit 1
fi
# Normalize the common SSH remote form to a GitHub URL for gh's repository selector.
if [[ "$origin_url" =~ ^git@([^:]+):(.+)$ ]]; then
  origin_url="https://${BASH_REMATCH[1]}/${BASH_REMATCH[2]}"
elif [[ "$origin_url" =~ ^ssh://git@([^/]+)/(.+)$ ]]; then
  origin_url="https://${BASH_REMATCH[1]}/${BASH_REMATCH[2]}"
fi
origin_url="${origin_url%.git}"
# Pin all release operations to origin, independent of GH_REPO or gh's default remote.
export GH_REPO="$origin_url"
release_tags="$(gh api --paginate 'repos/{owner}/{repo}/releases' --jq '.[].tag_name')" || {
  echo "Could not list GitHub Releases. Check GitHub CLI access and try again." >&2
  exit 1
}
if printf '%s\n' "$release_tags" | rg --fixed-strings --line-regexp --quiet "$tag"; then
  echo "GitHub Release '$tag' already exists; refusing to overwrite it." >&2
  exit 1
fi
if ! gh api "repos/{owner}/{repo}/commits/$commit" --silent; then
  echo "Commit '$commit' is not accessible on GitHub origin. Push the intended source commit and verify GitHub access before retrying." >&2
  exit 1
fi

output_dir="$(mktemp -d "${TMPDIR:-/tmp}/codex-worker-release-$version.XXXXXX")"
trap 'rm -rf -- "$output_dir"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
output_dir="$(cd "$output_dir" && pwd -P)"
case "$output_dir/" in
  "$repo_root/"*)
    echo "Temporary output must be outside the checkout. Set TMPDIR to an external directory." >&2
    exit 1
    ;;
esac

if ! "$repo_root/packaging/release-linux-x64.sh" --set-version "$version" "$output_dir"; then
  echo "Packaging failed; temporary artifacts were removed and no release was created." >&2
  exit 1
fi
if ! current_commit="$(git -C "$repo_root" rev-parse --verify HEAD)" ||
   ! checkout_status="$(git -C "$repo_root" status --porcelain --untracked-files=all)"; then
  echo "Could not verify checkout state after packaging; no release was created." >&2
  exit 1
fi
if [[ "$current_commit" != "$commit" || -n "$checkout_status" ]]; then
  echo "Checkout changed during packaging; no release was created. Inspect the checkout before retrying." >&2
  exit 1
fi

assets=(
  "$output_dir/codex-server-$version-linux-x64.tar.gz"
  "$output_dir/codex-worker-$version-linux-x64.tar.gz"
  "$output_dir/wet-$version-linux-x64.tar.gz"
  "$output_dir/checksums.txt"
)
for asset in "${assets[@]}"; do
  if [[ ! -s "$asset" ]]; then
    echo "Expected release asset '$asset' was not generated; no GitHub Release was created." >&2
    exit 1
  fi
done

if ! gh release create "$tag" "${assets[@]}" --target "$commit" --title "$tag" \
  --notes "Codex Worker $version release." --draft; then
  echo "GitHub Release creation failed. Check 'gh release view $tag' for a draft or partial release before retrying." >&2
  exit 1
fi

if ! gh release edit "$tag" --draft=false; then
  echo "Publication could not be confirmed for '$tag'. Inspect its state with 'gh release view $tag' and, if it is still a complete draft, publish with 'gh release edit $tag --draft=false' when ready." >&2
  exit 1
fi

echo "Published GitHub Release $tag from $commit."
