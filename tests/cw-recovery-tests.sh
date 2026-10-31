#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
mkdir "$test_root/bin"
export CW_RECOVERY_TEST_ROOT="$test_root"
export PATH="$test_root/bin:$PATH"
export CW_REPO_DIR="$test_root/missing-checkout"
export CW_WORKER_API_URL=http://127.0.0.1:5080
cat > "$test_root/bin/curl" <<'MOCK'
#!/usr/bin/env bash
printf '%s\n' "$@" > "$CW_RECOVERY_TEST_ROOT/args"
cat "$CW_RECOVERY_TEST_ROOT/response"
MOCK
chmod +x "$test_root/bin/curl"
cat > "$test_root/response" <<'JSON'
{"items":[{"executionId":"11111111-1111-1111-1111-111111111111","issueNumber":42,"reasonCode":"process-inspection-unavailable","explanation":"Process state could not be inspected.","checkStatus":"blocked","checkedAtUtc":"2026-10-31T00:00:00Z"}],"hasMore":false,"total":1}
JSON
"$repo_root/cw" recovery blocked --issue 42 --reason process-inspection-unavailable > "$test_root/human"
grep -Fq 'Execution 11111111-1111-1111-1111-111111111111 · Issue #42 · process-inspection-unavailable' "$test_root/human"
tail -n 1 "$test_root/args" | grep -Fxq 'http://127.0.0.1:5080/api/recovery/blocked'
grep -Fxq 'issueNumber=42' "$test_root/args"
grep -Fxq 'reasonCode=process-inspection-unavailable' "$test_root/args"
"$repo_root/cw" recovery blocked --json > "$test_root/json"
python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); assert d["items"][0]["issueNumber"] == 42' "$test_root/json"
printf '{"items":[],"hasMore":false,"total":0}\n' > "$test_root/response"
"$repo_root/cw" recovery blocked | grep -Fq 'No retained blocked recovery diagnostics found.'
for args in 'blocked --issue 0' 'blocked --reason "bad reason"' 'unknown'; do
  if "$repo_root/cw" recovery $args > "$test_root/error" 2>&1; then exit 1; fi
done
echo 'cw recovery tests passed'
