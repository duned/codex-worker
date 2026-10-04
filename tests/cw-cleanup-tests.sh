#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
mkdir "$test_root/bin"
export CW_CLEANUP_TEST_ROOT="$test_root"
export PATH="$test_root/bin:$PATH"
export CW_WORKER_API_URL=http://127.0.0.1:5080
cat > "$test_root/bin/curl" <<'MOCK'
#!/usr/bin/env bash
printf '%s\n' "${@: -1}" > "$CW_CLEANUP_TEST_ROOT/url"
while (($#)); do
  if [[ $1 == --data ]]; then printf '%s' "$2" > "$CW_CLEANUP_TEST_ROOT/body"; fi
  shift
done
cat "$CW_CLEANUP_TEST_ROOT/response"
[[ ! -f $CW_CLEANUP_TEST_ROOT/unavailable ]] || exit 22
MOCK
chmod +x "$test_root/bin/curl"
printf '%s' '[{"inspection":{"executionId":"11111111-1111-1111-1111-111111111111","decision":"safe","reasonCode":"integrated","message":"Safe retained work."},"outcome":"dry-run"}]' > "$test_root/response"
"$repo_root/cw" execution cleanup 11111111-1111-1111-1111-111111111111 > "$test_root/out"
grep -Fq 'dry-run (integrated)' "$test_root/out"
grep -Fxq "$CW_WORKER_API_URL/api/executions/cleanup" "$test_root/url"
python3 - "$test_root/body" <<'PY'
import json,sys
body=json.load(open(sys.argv[1]))
assert body == dict(executionId="11111111-1111-1111-1111-111111111111",limit=20,apply=False)
PY
"$repo_root/cw" execution cleanup --issue 42 --limit 2 --apply --json > "$test_root/out"
python3 - "$test_root/body" "$test_root/out" <<'PY'
import json,sys
assert json.load(open(sys.argv[1])) == dict(issueNumber=42,limit=2,apply=True)
assert json.load(open(sys.argv[2]))[0]["outcome"] == "dry-run"
PY
"$repo_root/cw" execution cleanup --stale --limit 100 > "$test_root/out"
python3 - "$test_root/body" <<'PY'
import json,sys
assert json.load(open(sys.argv[1])) == dict(stale=True,limit=100,apply=False)
PY
for args in '' 'invalid' '--issue 0' '--limit 101 --stale' '--limit 0 --stale' '--issue 42 --stale' '--stale --unknown' '--limit 1'; do
  if "$repo_root/cw" execution cleanup $args > "$test_root/out" 2>&1; then exit 1; fi
done
printf '%s' '[{"inspection":{"executionId":"11111111-1111-1111-1111-111111111111","decision":"keep","reasonCode":"authoritative-recovery","message":"Preserve current recovery."},"outcome":"refused"}]' > "$test_root/response"
if "$repo_root/cw" execution cleanup --issue 42 --apply > "$test_root/out"; then exit 1; fi
grep -Fq 'Preserve current recovery' "$test_root/out"
printf '%s' '{"error":"Wait for a completed drain."}' > "$test_root/response"
touch "$test_root/unavailable"
if "$repo_root/cw" execution cleanup --stale --apply > "$test_root/out" 2>&1; then exit 1; fi
grep -Fq 'Wait for a completed drain' "$test_root/out"
echo 'cw cleanup tests passed'
