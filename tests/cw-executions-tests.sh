#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
mkdir "$test_root/bin"
export CW_EXECUTION_TEST_ROOT="$test_root"
export PATH="$test_root/bin:$PATH"
# History commands need only the API, even with no configured checkout.
export CW_REPO_DIR="$test_root/missing-checkout"
export CW_WORKER_API_URL=http://127.0.0.1:5080
cat > "$test_root/bin/curl" <<'MOCK'
#!/usr/bin/env bash
printf '%s\n' "${@: -1}" > "$CW_EXECUTION_TEST_ROOT/url"
[[ ! -f $CW_EXECUTION_TEST_ROOT/unavailable ]] || exit 7
cat "$CW_EXECUTION_TEST_ROOT/response"
printf '\n%s' "$(cat "$CW_EXECUTION_TEST_ROOT/status")"
MOCK
chmod +x "$test_root/bin/curl"
printf '200' > "$test_root/status"
python3 - "$test_root" <<'PY'
import json, sys
root = sys.argv[1]
first = dict(executionId="11111111-1111-1111-1111-111111111111", project="Example", repository="owner/repo", issueNumber=42, issueTitle="Example issue", state="Failed", result="failed", startedAtUtc="2026-01-01T00:00:00Z", completedAtUtc="2026-01-01T00:01:00Z", durationMilliseconds=60000, attemptNumber=1, resumed=False, repairCount=1, repairs=[dict(attempt=1,maximumAttempts=2,passedAfterRepair=False)], validationOutcome="failed", featureBranch="feature/42", completedBranch="completed/42", commitSha="abc123", integrationBranch="main", recoveryState="recoverable", recoveryBaseCommit="base123", recoveryStatus="Workspace retained", recoveryExpiresAtUtc="2026-01-08T00:01:00Z", reportingFailure="report unavailable", originalIssueBody="unsafe issue body", implementationSummary="raw Codex output", environment={"TOKEN":"secret-value"})
second = dict(first, executionId="22222222-2222-2222-2222-222222222222", attemptNumber=2, retryOfExecutionId=first["executionId"], resumed=True, state="Validating", result=None, completedAtUtc=None, durationMilliseconds=None)
third = dict(second, executionId="33333333-3333-3333-3333-333333333333", attemptNumber=3, retryOfExecutionId=second["executionId"], resumed=False, state="Completed", result="succeeded", completedAtUtc="2026-01-01T00:03:00Z", validationOutcome="passed")
for name, value in (("list", [third,second,first]), ("exact", first), ("issue", [first,second,third])):
    with open(f"{root}/{name}.json", "w") as file: json.dump(value,file)
PY
cp "$test_root/list.json" "$test_root/response"
"$repo_root/cw" executions list > "$test_root/list.out"
grep -Fq 'Attempt 3' "$test_root/list.out"
grep -Fq 'Completed / succeeded' "$test_root/list.out"
grep -Fq 'Validating' "$test_root/list.out"
grep -Fq 'in progress' "$test_root/list.out"
grep -Fq '#42 Example issue' "$test_root/list.out"
grep -Fxq "$CW_WORKER_API_URL/api/executions" "$test_root/url"
"$repo_root/cw" e > "$test_root/alias.out"
cmp "$test_root/list.out" "$test_root/alias.out"
cp "$test_root/exact.json" "$test_root/response"
"$repo_root/cw" executions show 11111111-1111-1111-1111-111111111111 > "$test_root/exact.out"
grep -Fxq "$CW_WORKER_API_URL/api/executions/11111111-1111-1111-1111-111111111111" "$test_root/url"
for expected in 'recoverable' 'Workspace retained' '2026-01-08' 'feature/42' 'completed/42' 'abc123' 'integration  main' 'repair 1/2' 'report unavailable' '60.0s'; do
  grep -Fq "$expected" "$test_root/exact.out"
done
! grep -Eq 'unsafe|raw Codex|secret-value|TOKEN' "$test_root/exact.out"
cp "$test_root/issue.json" "$test_root/response"
"$repo_root/cw" e show --issue 42 > "$test_root/issue.out"
grep -Fxq "$CW_WORKER_API_URL/api/executions/issue/42" "$test_root/url"
[[ $(grep -c 'Example · owner/repo · #42' "$test_root/issue.out") == 1 ]]
grep -Fq 'resumed from 11111111-1111-1111-1111-111111111111' "$test_root/issue.out"
grep -Fq 'retry of 22222222-2222-2222-2222-222222222222' "$test_root/issue.out"
[[ $(grep -c 'Attempt ' "$test_root/issue.out") == 3 ]]
printf '[]' > "$test_root/response"
"$repo_root/cw" e list | grep -Fq 'No executions found'
for malformed in '{' '{}' '[{}]' 'null' '[{"executionId":123}]'; do
  printf '%s' "$malformed" > "$test_root/response"
  if "$repo_root/cw" e list > "$test_root/error.out" 2>&1; then exit 1; fi
  grep -Fq 'invalid execution metadata' "$test_root/error.out"
done
for status in 404 500; do
  printf '%s' "$status" > "$test_root/status"
  if "$repo_root/cw" e show --issue 42 > "$test_root/error.out" 2>&1; then exit 1; fi
  if [[ $status == 404 ]]; then grep -Fq 'not found' "$test_root/error.out"; else grep -Fq 'HTTP 500' "$test_root/error.out"; fi
done
touch "$test_root/unavailable"
if "$repo_root/cw" e list > "$test_root/error.out" 2>&1; then exit 1; fi
grep -Fq 'Management API unavailable' "$test_root/error.out"
for args in 'show invalid' 'show --issue 0' 'show --issue -1' 'show --issue 2147483648' 'list extra' 'other'; do
  if "$repo_root/cw" e $args > "$test_root/error.out" 2>&1; then exit 1; fi
  grep -Fq 'usage:' "$test_root/error.out"
done
echo 'cw execution tests passed'
