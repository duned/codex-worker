# Separate-host HTTPS deployment (23.5)

This recipe supports Ubuntu 24.04 x64 with Nginx and Certbot on the **same machine
as Server**, and one or more separate managed Worker machines. Workers initiate
HTTPS requests; assignments, provisioning plans and typed commands arrive in
responses. No inbound Worker port or remote Worker administration is required.
Use a disposable deployment for acceptance; these instructions do not authorize
changes to an existing operator VM.

This is a reviewed deployment recipe, not evidence of a live separate-host pass.
Run the remote acceptance campaign through [23.6's runbook](remote-https-acceptance.md) when hosts are
available. Record artifact versions/checksums, OS/package versions, topology,
redacted effective policy and each observation. The
[managed Worker campaign](managed-worker-e2e.md) and
[secret sentinel checks](secret-protection.md#remote-campaign-checks) supply
application/lifecycle checks; neither substitutes for that remote campaign.

## Topology and prerequisites

Reserve a DNS name, illustrated as `codex-server.example.com` throughout. Replace
it in both checked-in Nginx examples and commands. Publish A (and AAAA only if
IPv6 is actually routed/firewalled) records to the Server. Use a dedicated Nginx
instance without a CDN, load balancer, other virtual hosts, inherited real-IP
rules, WAF body capture or APM request tracing. Maintain a supported Ubuntu package
security-update policy.

Administration uses an **existing operator-managed VPN** terminating on the
Server, with routed client source addresses. Replace the example `10.77.0.0/24`
with its actual client subnet. The proxy checks the TCP peer address; never allow
all private address space, a NAT address shared with untrusted clients, or a
client-supplied forwarding header. Provision and test the VPN before enabling
firewall policy. Its transport/firewall setup depends on the chosen VPN and is
an operator prerequisite. VPN browsers resolve the same DNS name and connect to
the Server through the VPN route. SSH is allowed only through the VPN. Have
console recovery available before changing firewall/SSH access.

Server HTTP is exclusively `127.0.0.1:5090`. Workers retain
`http://127.0.0.1:5080` for their local API. Never forward either port. If the proxy
is moved to another machine, this recipe no longer applies: use an HTTPS backend
with a valid hostname/chain, verified upstream TLS (`proxy_ssl_verify on`, trusted
CA and explicit SNI), and a firewall allowing only that proxy. Plain HTTP between
machines, even across a private network, is not supported.

## Install Server and protect its configuration

Follow [Linux installation](linux-installation.md#server-installation-and-operations)
using a pinned published release and matching installer. Keep its dedicated
`codex-server` account, `/opt/codex-server` releases, `/var/lib/codex-server` state,
`/etc/codex-server/server.env` and existing systemd unit. Do not run Server as root.

```sh
sudo codex-server config set ListenUrl http://127.0.0.1:5090
sudo codex-server config validate
sudo systemctl restart codex-server
sudo ss -lntp
curl --fail http://127.0.0.1:5090/livez
```

In the root-owned, `root:codex-server` mode-0640 environment file, keep
`ASPNETCORE_ENVIRONMENT=Production`. Set `AllowedHosts=codex-server.example.com;127.0.0.1`
and `Logging__LogLevel__Microsoft.AspNetCore=Warning` to avoid framework
Information request URL/query logging. Configure the management token from a
protected deployment secret source as `CODEX_SERVER_MANAGEMENT_TOKEN`; retain the
installer-generated encryption key for managed secrets. Do not print the file,
use secrets as CLI arguments, use shell tracing, or put Worker tokens in it.
Restart and validate after edits. Configuration validation does not prove runtime
bind address or management-token presence: check both separately.

Protect `/etc/codex-server` and state as the installer does; only the service
account and authorized root operators should access secrets/state. Do not grant
Nginx's `www-data` account access to Server configuration/database. Restrict
sudo/proxy administration to designated operators; no Worker account may edit
proxy files, units, hooks or certificates.

## Certificate bootstrap and proxy activation

On the dedicated Server, install Ubuntu packages and prepare the ACME webroot:

```sh
sudo apt-get update
sudo apt-get install nginx certbot ca-certificates
sudo install -d -o root -g root -m 0755 /var/lib/letsencrypt
sudo rm -f /etc/nginx/sites-enabled/default
```

Copy [acme-bootstrap.conf](../packaging/linux/nginx/acme-bootstrap.conf) from the
same reviewed checkout/artifact into `/etc/nginx/sites-available/codex-server`,
replace the DNS placeholder, and install it root-owned mode 0644. Enable only
this site with a symlink in `sites-enabled`. This temporary site publishes only
HTTP-01 challenges; it does not proxy or redirect application traffic.

```sh
sudo ln -s /etc/nginx/sites-available/codex-server /etc/nginx/sites-enabled/codex-server
sudo nginx -t
sudo systemctl enable --now nginx
sudo systemctl reload nginx
```

Apply the Server host/cloud firewall before certificate issuance. Example UFW
policy for a routed VPN interface `wg0` (replace interface/subnet as appropriate):

```sh
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw allow in on wg0 from 10.77.0.0/24 to any port 22 proto tcp
sudo ufw deny 5090/tcp
sudo ufw deny 5080/tcp
sudo ufw enable
sudo ufw status verbose
```

Also allow the VPN's own transport per its installation guide. Remove any prior
broad SSH/backend rules; UFW denies do not override an earlier matching allow.
Apply equivalent cloud rules, including IPv6, and keep UFW IPv6 enabled when
publishing AAAA. HTTP port 80 remains open only for certificate challenges.

Issue a certificate with your DNS name and an operator contact address:

```sh
sudo certbot certonly --webroot -w /var/lib/letsencrypt \
  --cert-name codex-server.example.com -d codex-server.example.com \
  --email operator@example.com --agree-tos --non-interactive
```

Replace the temporary site with
[codex-server.conf](../packaging/linux/nginx/codex-server.conf), substituting DNS
and VPN subnet. Keep root ownership and 0644 mode. Certificate paths must match
the Certbot certificate name. Install
[reload-codex-proxy.sh](../packaging/linux/nginx/reload-codex-proxy.sh) as
`/etc/letsencrypt/renewal-hooks/deploy/reload-codex-proxy.sh`, root-owned mode 0750.

```sh
sudo nginx -t
sudo systemctl reload nginx
sudo systemctl enable --now certbot.timer
sudo systemctl list-timers certbot.timer
sudo certbot renew --dry-run --run-deploy-hooks
```

Nginx's privileged master reads keys; its workers run as Ubuntu's `www-data`.
Keep `/etc/letsencrypt` private directories root-only (0700), private key targets
root-owned 0600, and hooks/configuration non-writable by service accounts.
Do not make keys readable by `www-data` or `codex-server`. Check actual symlink
targets with `sudo namei -l /etc/letsencrypt/live/codex-server.example.com/privkey.pem`.
Recheck ownership after renewal. Never copy keys into the checkout or Worker.

The example permits TLS 1.2 with ECDHE/AES-GCM and TLS 1.3, and rejects unknown
SNI/Host. Serve `fullchain.pem`, including intermediates; the certificate SAN
must match `server.url`. Workers use platform trust and hostname checks with no
bypass. For an operator private CA, install its authenticated public root on
every Worker (and administrator client) as
`/usr/local/share/ca-certificates/codex-control-plane.crt`, then run
`sudo update-ca-certificates` and restart Worker. Never install a private key or
trust an unverified certificate obtained from the failing endpoint. Private CA
issuance/renewal replaces Certbot and must have its own controlled lifecycle.

References: [Ubuntu certificate setup](https://ubuntu.com/server/docs/how-to/security/obtain-tls-certificates/),
[Nginx TLS directives](https://nginx.org/en/docs/http/ngx_http_ssl_module.html),
[proxy directives](https://nginx.org/en/docs/http/ngx_http_proxy_module.html), and
[request limits](https://nginx.org/en/docs/http/ngx_http_limit_req_module.html).

## Routes, abuse limits and non-storage policy

The checked-in method/path map exposes only enrollment, Worker registration
updates, heartbeat, configuration retrieval, assignment requests, provisioning
plan/typed command request/report, execution report/lease renewal, and independent
credential delivery. Application authentication remains mandatory on those
routes. Everything else requires the VPN source subnet, including the dashboard,
health/status, management APIs, credential metadata/mutations, scheduling policy,
revocation and event streaming. New routes default to VPN-only; review the map
when upgrading. The dashboard shell is VPN-restricted; its management API calls
also require a finite administration session or the application management bearer token.
`GET`, `POST` and `DELETE /api/v1/administration/session` remain VPN-only; never
add them to the public Worker route map. A Worker token grants no management
access, even over VPN. Do not expose `/api/v1/workers/` as a blanket public prefix.

No forwarded-header middleware is enabled. The proxy removes Forwarded,
X-Forwarded-For/Host/Proto and X-Real-IP and fixes the upstream Host. Do not enable
ASP.NET automatic forwarded headers or Nginx real_ip in this topology. If future
code requires forwarding, explicitly trust only the actual loopback proxy and
replace incoming values rather than appending arbitrary client headers.

### Dashboard administration sessions

Set `Server:AdministrationOrigin` to the exact browser origin, for example
`https://codex-server.example.com` (no trailing slash), through Server configuration
or `Server__AdministrationOrigin` in the service environment. The installed CLI supports:

```sh
sudo codex-server config show
sudo codex-server config set AdministrationOrigin https://codex-server.example.com
sudo codex-server config validate
sudo systemctl restart codex-server
```

`config show` exposes the origin and safe guidance, with paths/credentials redacted;
`config validate` permits an unset origin for bearer-only deployments but explains
that browser login is disabled. The Server also warns at startup. It must match the
fixed upstream Host including any non-default port. The origin is operator
configuration, never inferred from forwarded headers or the HTTP upstream scheme.
An unset origin disables browser session login; bearer API/CLI clients still work.
Keep the loopback listener, proxy TLS and VPN route restrictions in place.

#### Upgrade recovery and bounded rejection diagnostics

An upgrade preserves the existing management token and environment; it cannot
infer the trusted browser origin. Before opening the upgraded dashboard, inspect
`sudo codex-server config show` and configure the exact origin if it is absent or
inconsistent. Do not change the loopback ListenUrl to the external HTTPS URL.
Keep existing valid origin settings. An invalid configured origin is a startup
configuration error; correct only that setting locally using a private editor if
`config set` cannot load the invalid configuration, then validate and restart.
Do not print or share the private EnvironmentFile. The installer prints these
setup steps; it does not overwrite an operator's proxy configuration.

For the reported separate-VM deployment, the supported values are:

- Browser URL and `AdministrationOrigin`: `https://checha.duckdns.org`.
- Server `ListenUrl`: `http://127.0.0.1:5090` (or the existing loopback port,
  with the same port in `proxy_pass`).
- TLS virtual host: `checha.duckdns.org`; fixed `proxy_set_header Host checha.duckdns.org;`.
  No forwarded-header trust; retain the existing VPN management allowlist.

On the Server, using the upgraded artifact:

```sh
sudo codex-server --version
sudo codex-server config show
sudo codex-server config set AdministrationOrigin https://checha.duckdns.org
sudo codex-server config validate
sudo systemctl restart codex-server
```

Review only the relevant proxy server name, upstream Host/port and VPN policy
locally. If an edit is needed, run `sudo nginx -t` then
`sudo systemctl reload nginx`; a Server origin change requires a Server restart.
Do not replace the operator's proxy configuration with the example wholesale.
Neither step changes Worker identity, history, or the management token.

A 403 alone does not identify the rejecting layer. From the administrator's VPN
client, a bounded **credential-free** probe checks the actual TLS/proxy route:

```sh
curl --silent --show-error --max-time 10 --output /dev/null --dump-header - \
  --request POST --header 'Origin: https://checha.duckdns.org' \
  https://checha.duckdns.org/api/v1/administration/session
```

Use this command only without Authorization or cookies; never add real credentials,
`-v`, `-k` or a cookie jar. Inspect only HTTP status, `X-Codex-Request-Id` and
`X-Codex-Administration-Error`. The Server's fixed diagnostic codes are
`administration-origin-missing`, `administration-host-mismatch`,
`administration-origin-mismatch` (403), `administration-token-invalid` (401), and
`administration-session-invalid` (401 on restoration). No submitted value is echoed.
An expected 401/token-invalid from this **token-free** probe means the Server
accepted the origin/Host; it says nothing about the preserved token's validity.
A 403 without a Server diagnostic may be proxy/network rejection or a different
artifact/routing/header policy; it is not proof of a bad token. Verify VPN source
membership, route and artifact before retrying. Do not weaken the network policy.
If needed, repeat the same token-free POST locally to the loopback listener with
`Host: checha.duckdns.org` and the same Origin to isolate the application from the
proxy. Do not expose the loopback port remotely. Compare only status and fixed
codes, not raw browser requests, logs or environment files.

The dashboard checks session configuration on reload before token entry and
shows distinct configuration guidance, invalid-token/session help, or an unknown
access rejection pointing to VPN/proxy checks. It displays only allowlisted
messages, never arbitrary proxy error bodies. There are no automatic login retries.
Origin/Host rejection does not justify rotating a valid management token.

Retrieve the existing token only in a private local Server terminal when needed:

```sh
sudo sed -n 's/^[[:space:]]*CODEX_SERVER_MANAGEMENT_TOKEN[[:space:]]*=[[:space:]]*//p' /etc/codex-server/server.env
```

Paste it into the dashboard password field. Do not send its output to support,
attach the environment file, export a HAR, or share cookies/authentication headers.
The token is never returned through a login-help API.

Deployed acceptance checklist for `https://checha.duckdns.org`:

1. Verify the installed/running artifact version, redacted configuration and
   explicit origin, loopback upstream port/Host and unchanged VPN restrictions.
   Restart Server after configuration edits; validate/reload proxy after its edits.
2. From the allowed VPN client, run the token-free probe and identify the layer
   by fixed diagnostic/status. Outside the allowed network, management routes
   must remain rejected without reaching the backend.
3. Sign in with the preserved token; confirm a Secure/HttpOnly/SameSite=Strict
   host-only cookie without recording its value. No token appears in URLs or
   browser persistent storage.
4. Reload: the session restores without token entry. Observe exactly one active
   `/api/v1/events/stream` connection and continuing dashboard updates. Do not
   export request headers or responses.
5. Log out: the stream closes and administration hides. Reload stays signed out;
   a mutation without valid CSRF must be rejected.

The fix is validated with local deterministic application/dashboard regressions
and an isolated proxy test when tools are available. The operator VMs, effective
configuration, real-token sign-in, deployed SSE and logout have **not** been
inspected or exercised during implementation. Missing origin is a reproduced
upgrade condition, not a proven cause of this deployment's original 403.

Sign in at the dashboard's Administration sign in panel with the management token.
Only the login request carries this token; it is cleared from the input immediately
and never written to browser storage, URLs or cookies. The Server issues an opaque
`__Host-CodexAdministration` cookie with Secure, HttpOnly, SameSite=Strict, Path=/,
no Domain and an explicit eight-hour expiration. Sessions have no sliding renewal,
are bounded to 256 per Server, and remain only in Server memory (outside backups).
Reload retrieves the session's expiration and a separate CSRF token. Cookie-authenticated
mutations require both the exact configured Origin and this token in `X-Codex-CSRF`.
No cross-origin credential/CORS access is enabled. An explicit Authorization header
never falls back to a cookie for management APIs; Worker APIs still require Worker
bearer credentials, and Worker credentials cannot establish sessions.

Logout invalidates the Server session and cancels its streams. The page stops
pending requests, hides administration and clears transient credentials. If logout
cannot reach the Server, it offers an explicit retry; the cookie may remain valid
until logout succeeds or expiration. Expiration, observed management-token rotation
(including removing the token), and Server restart invalidate sessions. Rotation
is checked on each session request and at most every five seconds on an idle SSE
stream; expired sessions cancel connected streams at their deadline. Do not rely
on hot-changing a service environment: restart after changing the service token,
which immediately drops all sessions. Rejections return to sign in without automatic
authentication retries; transport failures remain connection failures.

For direct local administration only, explicitly set the origin to the exact
loopback HTTP `ListenUrl`, for example `http://127.0.0.1:5090`. This uses a separate
HttpOnly, SameSite=Strict `CodexLocalAdministration` cookie without Secure. This
configuration is accepted only for the matching direct loopback HTTP listener;
it does not downgrade the HTTPS cookie or accept forwarded-header claims. Use
bearer CLI access if browser sessions are not configured. Never select HTTP origin
for a production TLS proxy.

Manual HTTPS verification on a disposable deployment (no provider credentials):

1. From VPN, configure the HTTPS origin, sign in, inspect the cookie flags and
   no-store headers in browser tools, reload and confirm projects/Workers restore.
   Verify the management token appears only in the login request and not in storage.
2. Sign out while a stream and API request are active. Confirm teardown, an expired
   Set-Cookie and 401 on reuse of the old session cookie; reload remains signed out.
3. Restart with a changed management token and confirm old tabs return to sign in.
   Confirm expiry after eight hours, with no automatic login retry. Interrupt the
   proxy temporarily to verify connection failures remain distinct from rejection.
4. Submit a cookie mutation with missing/wrong CSRF or foreign/missing Origin;
   confirm rejection and no mutation. Confirm supported bearer requests still work.
5. Outside VPN, verify all three session methods and SSE are denied by the proxy,
   even with valid credentials. Verify spoofed forwarding headers change neither
   cookie flags nor origin authorization. Do not capture tokens in logs/screenshots.

These live HTTPS/browser steps are operator validation; deterministic tests do not
establish their deployment result.

All proxy responses carry non-storage headers, including rejections. Cache
lookup/write/store are off, conditional validators are removed, response/request
proxy buffering is off, and no upstream retry can blindly replay a mutation.
Purge any old proxy/CDN caches during migration. Access logging is off; per-site
error logging is discarded because standard Nginx errors can include URLs. There
is no header/body capture. Use service lifecycle logs, Server request IDs and
bounded product diagnostics; do not enable Nginx debug logging to troubleshoot
authenticated requests. Audit global logging/APM and cloud tooling too.

Requests are bounded to 4 MiB; header timeout is 10 seconds, body/send idle
timeout 30 seconds, backend connect 5 seconds and backend read idle timeout
180 seconds (allows bounded management GitHub discovery). These are idle I/O
limits, not execution durations; Workers do not hold connections during tasks.
Per-source limits are 100 concurrent requests and 20 requests/second with a
100-request burst; enrollment additionally allows 6/minute with burst 5.
Rejections return 429. Budget shared NATs: defaults target up to 20 Workers per
source with normal 20-second heartbeat intervals, regular polling, and capacity
up to 8. Stagger restarts/enrollments; measure lease-renewal/report bursts under
actual load. Increase bounded per-source limits if needed before adding larger
fleets, keeping enrollment separately constrained. Limits cannot prevent a
distributed attack; use infrastructure connection protection without body capture.

## Enroll separate Workers and verify polling

On each Worker, allow outbound DNS, trusted HTTPS to Server, time synchronization
and the providers/package feeds needed by its configured execution tools. Deny
inbound public traffic; SSH may be VPN-only. No 5080 public rule is needed.

Use the [supported Worker installer](linux-installation.md#worker-installation-and-operations)
with `--server https://codex-server.example.com --register --start --capacity 2`
and the protected `--token-file /run/secrets/worker-bootstrap-token` source.
Prepare that root-owned 0600 file through your protected secret-management
transport from stdout of `sudo codex-server worker-token create` on Server.
Do not paste the token into commands, shell variables/history, chat or captured
terminal transcripts. Disable tracing/session recording for issuance and secret
transfer; use a pipe/protected file, never an argument. Remove the transient
bootstrap file after acknowledged enrollment; retain owner-only identity,
`.token`, `.server` and `.pending` recovery files as specified by registration.
Use installer Git identity options for unattended setup; keep the packaged
managed YAML and Worker service-account/state conventions.

```sh
sudo -u codex-worker /opt/codex-worker/CodexWorker config validate --config /etc/codex-worker/worker.yml
sudo -u codex-worker /opt/codex-worker/CodexWorker status --config /etc/codex-worker/worker.yml
sudo ss -lntp
curl --fail http://127.0.0.1:5080/api/status
```

Check local API output privately; do not archive operational payloads. On Server,
use `sudo codex-server worker list` and `worker show WORKER_ID` to verify active
API authentication and advancing last-seen heartbeats across several intervals.
Inspect current configuration-sync observations on the running Worker, not merely
its cached CLI status. Missing execution tools/login may legitimately report
not-ready while polling succeeds. Follow the managed campaign to provision tools,
pass actual Codex preflight, verify catalog revision and a disposable assignment
(including lease renewal/reporting). Commands remain typed and Worker-initiated.

## Rotation, revocation, upgrades and recovery

Drain with `sudo codex-server worker drain WORKER_ID`, wait for zero active
assignments, then stop that Worker's service. For **genuine rotation**, create
`sudo codex-server worker-token authorize WORKER_ID rotate` into the protected
source, and rerun the supported Worker installer with the same identity/config,
`--register --operation rotate --server https://codex-server.example.com
--token-file /run/secrets/worker-bootstrap-token --start`. This generates new
material; ordinary enrollment retry is not rotation. Verify the old API credential
is rejected using a disposable protected test client, then verify polling and
`worker enable WORKER_ID`.

Test API and delivery revocation independently: `worker revoke-token WORKER_ID`
blocks API renewal/reporting without revoking delivery; `worker
revoke-delivery-token WORKER_ID` blocks delivery without blocking API polling.
Use separate synthetic delivery authorization and assigned sentinel credentials
on a disposable deployment; delivery setup uses the VPN-only authenticated
`PUT /api/v1/workers/WORKER_ID/credential-access` contract with a protected body,
not shell arguments. See [secret protection](secret-protection.md) and
[credential administration](server-local-administration.md).
Provider revocation must occur at the provider too; Server cannot erase delivered
copies. Revocation during execution can expire leases into recovery.

For lost/revoked API material use a Worker-bound `recover` authorization and
`--operation recover`. For Server migration use destination-bound `associate`
and `--operation associate`, then revoke the old Server association separately.
Preserve pending material after uncertain responses; retry with the same
identity/endpoint/operation. Never delete identity, database or worktrees to
repair registration. [Enrollment recovery](worker-local-administration.md#enrollment-rotation-and-server-migration)
describes the acknowledgement and uncertainty rules.

Upgrade with the existing pinned installers; they preserve configuration/state.
Drain before Worker upgrades. Back up Server with its supported backup command
and protect configuration/encryption-key sources separately. Reconcile proxy
route changes and validate Nginx before reload. Moving from co-located HTTP to
HTTPS on the same Server retains its registry and stable Worker identities;
issue a Worker-bound `associate` authorization from the retained registry and run
`register --operation associate` through the new HTTPS endpoint. Any endpoint
change requires `associate`, even when the registry is unchanged; `rotate` is for
credential changes at the existing endpoint. Verify polling before retiring old
access. A different Server requires associate, not copying its database or
sending old credentials. Remove legacy shared registration environment settings.
Restore requires fresh API/delivery authorization and re-provisioned provider
secrets; backups do not contain usable authentication.

For renewal failure, inspect `systemctl status certbot.timer`, Certbot service
journal, DNS/CAA, port 80 and webroot reachability. Alert before expiry (for example
21 days) and compare the on-disk certificate with the certificate actually served:

```sh
sudo openssl x509 -in /etc/letsencrypt/live/codex-server.example.com/fullchain.pem -noout -dates -serial
openssl s_client -connect codex-server.example.com:443 -servername codex-server.example.com \
  -verify_hostname codex-server.example.com -verify_return_error </dev/null
```

Renewal writes certificates then the hook validates and gracefully reloads Nginx.
If validation/reload fails, the running proxy may still serve the old certificate;
fix configuration, run `nginx -t`, reload, and verify the served serial/expiry.
Never fall back to HTTP or disable certificate validation. A bad/expired chain
stops trusted Worker communication; cached snapshots cannot authorize new work.
Drain/reconcile expired or uncertain executions using established recovery rules.

## Acceptance checks and evidence

From a Worker and a separate non-VPN client, without credentials in arguments:

```sh
curl --fail --show-error https://codex-server.example.com/api/v1/workers/example/configuration -o /dev/null
curl --silent --show-error -D - https://codex-server.example.com/api/v1/projects -o /dev/null
curl --silent --show-error -D - https://codex-server.example.com/ -o /dev/null
nc -vz -w 3 SERVER_PUBLIC_IP 5090
nc -vz -w 3 WORKER_PUBLIC_IP 5080
```

Expect the first request to fail with application 401 and non-storage headers;
the next two must return proxy 403 outside VPN, even with valid management
authentication supplied by a protected client config. Backend/Worker port probes
must fail. Repeat for IPv6 if published. On VPN, management API without management
authentication returns 401; with it succeeds. A forged X-Forwarded-For naming a
VPN client must still get 403 from outside VPN. Wrong SNI/hostname must fail TLS;
wrong Host with valid SNI must return 421. Port 80 must return 404 outside ACME,
including Worker routes. `ss -lntp` on both nodes must show loopback-only backend
and Worker API, never wildcard/private/public listeners for those ports.

Use protected curl config/header/body files (0600) for authenticated synthetic
checks, without `-v`, trace, body printing or shell tracing. Observe success/error
headers privately: Cache-Control no-store, Pragma no-cache, Expires 0; no
ETag/Last-Modified, cache HIT, 304 or stale secret. Repeat delivery with conditional
headers after replace/reassign/revoke. Audit effective Nginx configuration with
`sudo nginx -T` privately and inspect all applicable log/cache/APM stores for
synthetic sentinels as in 23.4. Never use production secrets as test sentinels.
Check that oversized requests get 413 and enrollment bursts get 429 while normal
multi-Worker heartbeats/lease renewals remain healthy.

On a **disposable deployment only**, run `certbot renew --dry-run
--run-deploy-hooks` and verify successful hook/reload plus continued trusted
polling. In an isolated proxy configuration, substitute an untrusted/self-signed,
wrong-SAN or expired test certificate. Confirm curl and actual Worker enrollment/
polling reject it without creating an enrollment or obtaining assignments.
Restore the valid certificate, validate/reload, then verify polling recovery.
Test renewal failure by breaking the disposable HTTP-01 webroot route; dry-run
must fail and the old valid endpoint remain available. Restore and dry-run again.
Do not exhaust public CA issuance limits or replace production certificates.

Local `python3 tests/remote-https-proxy-tests.py` validates the example in an
isolated Nginx process with a temporary certificate/backend when Nginx is
available. It does not install services, change firewall rules, use real secrets,
or establish public trust, renewal or separate-host acceptance. Record skipped
checks honestly; run the live checks above under 23.6 rather than treating local
syntax/application tests as deployment validation.
