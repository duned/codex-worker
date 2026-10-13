#!/usr/bin/env bash
set -euo pipefail

KEEP=10
MODE="${1:-}"

if [[ "$MODE" != "" && "$MODE" != "--apply" ]]; then
  echo "Uso: $0 [--apply]"
  exit 2
fi

if ! command -v gh >/dev/null || ! command -v jq >/dev/null; then
  echo "Necesitas tener instalados gh y jq."
  exit 1
fi

releases=$(gh release list \
  --limit 100 \
  --exclude-drafts \
  --json tagName,publishedAt,isPrerelease \
  | jq -r '
      map(select(.publishedAt != null))
      | sort_by(.publishedAt)
      | reverse
      | .['"$KEEP"':]
      | reverse
      | .[]
      | [.publishedAt, .tagName, (.isPrerelease | tostring)]
      | @tsv
    ')

if [[ -z "$releases" ]]; then
  echo "No hay releases antiguas que borrar."
  exit 0
fi

echo "Releases que se eliminarían (se conservan las $KEEP más recientes):"
while IFS=$'\t' read -r date tag prerelease; do
  printf '  %s  %s  prerelease=%s\n' "$date" "$tag" "$prerelease"
done <<< "$releases"

if [[ "$MODE" != "--apply" ]]; then
  echo
  echo "Simulación: no se ha borrado nada. Revisa la lista y ejecuta '$0 --apply' para confirmar."
  exit 0
fi

echo
while IFS=$'\t' read -r date tag prerelease; do
  echo "Eliminando release y tag: $tag ($date)"
  gh release delete "$tag" --cleanup-tag --yes
done <<< "$releases"
