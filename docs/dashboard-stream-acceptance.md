# Server dashboard stream lifecycle

The dashboard owns one authenticated fetch stream per tab. Connect (including
Enter in the token field) replaces the connection using the entered token;
Connect with an empty field disconnects. Replacement aborts the previous fetch,
cancels its reader or retry timer, and waits for cleanup before issuing another
stream request. Obsolete responses cannot publish Worker events or live status.
Failures and clean stream endings retry after three seconds, with one request
or retry wait at a time. Page teardown stops the stream; restoring a page from
the back-forward cache reconnects its current in-memory token. Different tabs
remain independent. Tokens stay in page memory and Authorization headers.

## Deterministic reproduction and regression coverage

`tests/dashboard/server-stream-lifecycle.test.cjs` executes the actual dashboard
Connect handler and Enter listener in the existing Node/VM test style. Deferred
fetches, reads, cancellation completion and manually advanced retry timers
exercise replacement, disconnect/reconnect, stale fetch/read completion,
request failure, retry cancellation, simulated Server restart, EOF, page
teardown/cache restoration, and independent tabs. No external Server or timer
sleep is required.

Before the change, the same harness against the original dashboard reproduced
two active requests in one tab through these sequences:

1. Connect, receive a response and leave `reader.read()` pending, Connect with an
   empty field, then Connect with a token again. The empty connection action
   changes the shared token but does not cancel the pending reader.
2. Connect, fail the request to enter its retry delay, Connect with an empty
   field, reconnect with a token, then advance the old retry timer. The earlier
   loop observes the new shared token and opens a competing request.

These reproduce concrete single-tab accumulation paths. They do not establish
the operator's exact sequence or explain the precise count of seven requests
observed at shutdown. A Server outage alone should not multiply a single loop,
but previously surviving loops could each reconnect after an outage.

Run the local dashboard checks with:

```sh
node --test --test-isolation=none tests/dashboard/*.test.cjs
```

## Browser acceptance checklist

Use a deployed Server and one browser tab. Keep management credentials out of
screenshots, exported Network traces, logs, and notes. Inspect the Network panel
filtered to `/api/v1/events/stream`; count **active/pending requests**, rather than
all historical completed/cancelled entries. Cancellation can take a short time
to propagate to the Server, so compare settled requests after each action.

- Connect with the management token and confirm one active stream and live Worker
  updates. Re-enter the token and repeatedly use Connect and Enter. Confirm
  earlier requests are cancelled and only one active stream remains.
- Replace the token with another valid management token and confirm one stream.
  Try an invalid token, observe bounded sequential retries, then replace it with
  the valid token. Confirm recovery without additional concurrent streams.
- Connect with the empty token field to disconnect. Confirm zero active streams.
  Rapidly disconnect/reconnect and confirm one active stream and current updates.
- Navigate normally through dashboard panels. Confirm one active stream. Navigate
  away and back (including browser back/forward cache), then reload and reconnect.
  Confirm the old page's stream ends and each connected page has one stream.
  Reload must require re-entering the token; it must not restore credentials from
  persistent browser storage.
- Temporarily stop/restart the Server while connected. During the outage observe
  sequential retries; after restart confirm one active stream, connected live
  status, and fresh Worker updates without another Connect action.
- Optionally open a second tab and connect there. Each tab should independently
  own one stream; closing either tab must leave the other's stream working.

The deterministic tests simulate restart and cancellation; they do not replace
this browser/HTTPS/live deployment acceptance. This task did not run that campaign.
