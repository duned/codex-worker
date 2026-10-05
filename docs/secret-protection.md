# Secret delivery and operational output

The Server encrypts managed provider secrets with AES-GCM. Public credential metadata contains no payload. Keep `CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY` in the protected deployment secret source, outside the registry and backup archives; restore never supplies it. Key rotation and replacing the secret provider require separate work.

Worker API authentication, Worker delivery authorization, assigned provider secrets, and node-local login state are separate permissions. Delivery requires the independent `X-Worker-Credential-Token` for the exact Worker and a Ready credential assigned to that Worker. Management or API authentication cannot substitute for delivery authorization. Reassignment removes the previous Worker's delivery access; replacement changes future deliveries; revocation stops future delivery. Revoking delivery does not invalidate a provider token or erase a copy already delivered.

Bootstrap uses `Authorization` and `X-Codex-Worker-Token`; normal Worker requests use `Authorization`; delivery uses `X-Worker-Credential-Token`. All are secrets. Explicit registration/rotation uses `--token-stdin` from a protected source. `--token` and `--token=...` are rejected before configuration reads, stdin, network access, or identity changes. Migrate scripts to stdin; do not place a literal token in a shell command, enable tracing, or capture token input in logs. If an old command exposed a token, revoke/replace the authorization rather than reusing it. The installer retains its protected stdin and `--token-file` input handling.

The Worker retains owner-only `.pending` enrollment material through uncertain responses and publishes `.token` only after acknowledgement and authentication. Treat both as secrets: do not attach them to support reports, copy them to the Server, or include them in general node backups. Preserve pending state for verified recovery; failure is not permission to discard it. Codex/GitHub login caches and SSH private keys stay on their owning node and never enter the Server registry.

## HTTP and trusted TLS proxy

The supported separate-host setup is the [Ubuntu Nginx HTTPS recipe](remote-https-deployment.md),
with checked-in proxy examples, VPN management restrictions and renewal/recovery checks.

The application sets `Cache-Control: no-store`, `Pragma: no-cache`, and `Expires: 0` for every `/api` response, including binding/authentication failures and provisioning/login challenges. API responses have no ETag/Last-Modified validators, and conditional request headers do not select a cached representation. This covers credential delivery and bootstrap alongside operational API views. Existing caches must be purged when deploying this policy.

TLS termination is a trusted component able to see decrypted bodies and authentication headers. HTTPS and application tests cannot prove that proxy operators, debug tooling, or infrastructure logs protect those values. The deployment policy coordinated with series 23.5 must exclude the entire `/api` path subtree from proxy/CDN caching, including errors, and must preserve the application's non-storage headers. Disable cache lookup and cache writes, stale serving, conditional/304 handling, and response-body capture there. Do not rely on caches automatically recognizing custom authorization headers.

Proxy, ingress, WAF, APM and application request tracing must never record request/response bodies, `Authorization`, `X-Codex-Worker-Token`, or `X-Worker-Credential-Token`. Use an allowlist of bounded operational fields such as method, route template, status, duration, and generated request ID. Do not log environment dumps, URL credentials, secret-bearing query strings, or raw provider output. Keep proxy upstream traffic on the same node's loopback listener as described in [Linux installation](linux-installation.md).

## Operational boundaries and audit

Shared redaction handles authentication headers, credential key/value fields and quoted secret fields before diagnostics are bounded; Worker diagnostics additionally redact known secret values. Redaction is a fallback for structured diagnostics, not authorization to log arbitrary bodies or provider output. Polling failure notifications omit raw exception messages. Registration errors return bounded contract guidance and request correlation, without echoing submitted identity fields or authentication. Provisioning authentication returns typed outcomes without provider stdout/stderr; device-login challenges are deliberately visible only to authorized operators while active. Do not capture those challenges in logs or support bundles.

Credential list/show, CLI JSON/human results and dashboard metadata use the secret-free metadata contract. Explicit token issuance and active device-login instructions are intentional secret outputs: use a protected operator session and never archive its terminal output. Backup export scrubs encrypted payloads, credential assignments, delivery authorization, bootstrap authorizations, active device challenges and API-token hashes. It excludes environment/configuration secrets and node login/private keys. See [Server backup and credentials](server-local-administration.md) for restore requirements.

For a compromised node, drain scheduling if controlled completion remains safe; disable scheduling immediately if it does not. Revoke that Worker's API token **and** delivery token using the existing Server administration operations. Revoke assigned credentials in the Server and revoke/rotate their tokens at the provider, including node-local Codex/GitHub/SSH authorization as appropriate. API revocation stops renewal/reporting; retain uncertain execution/worktree state for the established lease-expiry and reconciliation process. Isolate and rebuild the node, remove compromised local copies/login state, then explicitly recover enrollment with new API/delivery credentials and newly issued assigned provider secrets. Verify readiness and access before enabling scheduling. A Server-only revoke or node logout is not a complete provider-side compromise response.

## Remote campaign checks

Use synthetic recognizable sentinels, never production credentials. Through the deployed TLS endpoint:

- Exercise bootstrap/rotation success, malformed/unauthorized requests, delivery success, wrong-token/unassigned/reassigned/replaced/revoked delivery, and independent API/delivery revocation. Confirm non-storage headers on successes and errors.
- Repeat delivery with `If-None-Match` and `If-Modified-Since`; confirm no 304, stale secret, or cache HIT, including after reassignment/revocation. Inspect the actual proxy cache configuration and purge any prior entries.
- Exercise provisioning success/failure and login expiry. Inspect application, proxy, ingress, WAF/APM, service journal and CLI captures for synthetic secrets, authentication headers, bodies and raw provider output; inspect exported backup contents and metadata too.
- Verify secret-source/encryption-key exclusion, owner-only active/pending enrollment files and protected installer token-file handling on the node.

Local HTTP, CLI and backup tests validate application contracts only. External proxy cache/logging policy and node deployment permissions require the remote campaign; an app-only pass must not be reported as infrastructure validation.
