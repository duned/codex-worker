# Server dashboard streams and shared loading

Each tab owns one authenticated fetch stream and one session polling loop.
Signing in exchanges the transient management token for an administration cookie
session; the input is cleared. Reload and back-forward cache restoration check
that session without asking for a token while it remains valid. No token is kept
in browser storage. Session expiry, HTTP/stream 401 or 403, logout and page teardown
cancel readers, requests and retry timers. Replacement streams wait for previous
reader cleanup; obsolete responses cannot publish state or reconnect.

Transport failures and EOF retry sequentially after three seconds. Invalid JSON,
rendering errors and oversized event buffers stop the stream with a fixed,
bounded Worker event processing diagnostic and require deliberate refresh or
reconnection after correcting the data/deployment. They do not trigger transport
retries. SSE continues to use the configured HTTP JSON naming and converters.

Ordinary equivalent GETs share only their pending promise, keyed by the complete
path including filters. The registry holds at most 64 pending reads, requests
have a 15-second deadline, and completed data is not cached. Route changes cancel
obsolete shared reads and reload current observations. Session transitions clear
the registry. Successful mutations invalidate pending observations before their
callers refresh affected views. One polling owner refreshes shared observations
and active Worker/provisioning/queue views; rendering does not start polling.
Read failures identify the method/resource, HTTP status or connection/data cause,
stale state and a safe next action, without displaying error response bodies,
query values, authentication challenges or tokens. Existing advanced operation
and execution correlation IDs remain available.

## Deterministic regression checks

The tests execute the delivered dashboard bundle and actual login, route, polling,
provisioning action and stream logic using DOM/HTTP/clock seams. Deferred fetches,
JSON decoding, reader cancellation and manually invoked timers cover duplicate
Home reads, project/Worker detail navigation, Server GitHub setup and provisioning
progress, filter/session isolation, registry bounds, mutation invalidation,
stale responses (including HTTP snapshots overtaken by stream updates), session
expiry/logout, invalid events, cancellation, EOF,
simulated Server restart and back-forward restoration. No timer sleeps or real
provider credentials are required. The existing Server event tests compare SSE
and HTTP JSON and feed the resulting events into the dashboard renderer.

```sh
node --test --test-isolation=none tests/dashboard/*.test.cjs
dotnet test CodexWorker.sln
```

## Deployed HTTPS acceptance

Use an explicitly designated test deployment at its configured HTTPS
administration origin, with a trusted certificate and the normal identity and
authorization controls. Do not alter operator VMs for this checklist. Keep
credentials out of screenshots, exported Network traces, logs and notes.

1. Record the installed artifact version and source commit from release metadata
   or the deployment manifest. Compare `/api/version` and the dashboard version
   with the selected artifact; confirm that commit contains the dashboard change.
   A closed Issue or reported merge is not evidence that it is deployed. A version
   alone cannot distinguish artifacts built from different commits.
2. Sign in using the normal administration form. Filter Network to
   `/api/v1/events/stream` and count active/pending requests, not historical
   completed entries. After cancellation settles, expect exactly one stream.
   Leave the tab idle while a test Worker reports updates; confirm fresh live
   status, capacity and Home activity without manual refresh.
3. Rapidly switch Home, project detail, Worker detail, Executions and Settings /
   Server GitHub setup. Exercise execution filters, browser back/forward and a
   navigation away/back that uses the back-forward cache. Expect one stream and
   current resource data, with cancelled old reads unable to overwrite it. Check
   equivalent pending GETs occur once; different filters and node command scopes
   remain separate resources. Confirm provisioning progress updates while active
   and after leaving and returning, without creating additional command mutations.
4. Reload. An unexpired administration cookie should restore the session and one
   stream without token entry. An expired/rejected cookie should show sign-in and
   zero active streams, without repeated authentication requests. Verify expiry
   while idle also tears down polling and the stream. Sign in again, then log out;
   expect zero active streams and no later response restoring the signed-in UI.
5. On the designated test deployment, stop/restart the Server. During interruption,
   expect bounded connection diagnostics and sequential retries. If the session is
   still valid, expect one recovered stream and fresh updates after restart; if
   rejected, expect sign-in recovery and zero retrying streams. Do not re-enter a
   token merely to recover a transport interruption.
6. Optionally open a second tab. Each authenticated tab owns one independent stream;
   closing or signing out a tab tears down its owner. Other tabs must transition
   to session recovery when the Server rejects their revoked session.

## Live validation record

This task did **not** run deployed artifact/commit verification, trusted-HTTPS
browser acceptance, idle real-Worker updates, live navigation/reload/cache checks,
live session expiry/logout, provider authentication/provisioning, multi-tab checks,
or a live Server stop/restart campaign. Automated simulations do not establish
live deployment acceptance. No operator VM or real provider credential was used.
