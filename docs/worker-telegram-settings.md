# Worker Telegram notifications

Telegram notifications belong to one Worker process and cover all of that Worker's
projects and lifecycle notifications. The only supported settings are enabled,
bot token and chat ID. Delivery formatting, the ten-second HTTP timeout and
completion idempotency remain product behavior, not configurable settings.

The administration dashboard's Worker editor can explicitly manage these settings
for that Worker. This is not a Server-wide notification destination. Until an
operator chooses **Manage Telegram for this Worker**, local `telegram.enabled`
YAML and the Worker's `TELEGRAM_BOT_TOKEN` / `TELEGRAM_CHAT_ID` environment remain
authoritative. Saving unrelated Worker settings preserves that source.

An explicit override replaces all three local settings. Blank token input keeps
the existing managed token; it does not import or fall back to an environment
token. The deliberate clear checkbox removes the managed token when saved.
Disable notifications before clearing. Disabled notifications require neither a
token nor a chat ID. Chat IDs accept numeric IDs (including negative group IDs)
and `@channel` usernames. New bot tokens must have the Telegram bot-ID/token
shape; saving does not contact Telegram or send a test message.

The Server registry stores only enabled, chat ID and a credential reference. Bot
tokens use the existing AES-GCM credential store and require
`CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY`. Read APIs and the dashboard expose only
a configured indicator and the reference, never the token. Replacement creates
an immutable encrypted credential assigned to that Worker; after a successful
revision-checked save the previous credential is revoked. Rejected saves revoke
staged credentials; uncertain commit outcomes preserve them for reconciliation.

Runtime delivery uses the existing authorized credential endpoint and a separate
per-Worker delivery authorization, provisioned automatically during enrollment or
association. No manual token copying or systemd environment edits are required.
The Worker generates and atomically saves the secret in its protected identity
state (`.delivery`) before confirming persistence over its authenticated managed
connection; the Server stores only a verifier. The Worker API token remains separate.

For an existing Worker, open **Worker detail → Current control state → Provision
or repair delivery authorization**. Keep the Worker online with active Worker API
authentication. Repair runs on authenticated heartbeats even while executions are
active. The dashboard shows **pending** until the Worker saves and confirms
the authorization, then **active**. After five minutes without confirmation it shows
**failed**: restore connectivity/service-account access to identity state and request
repair again. Repeated pending requests reuse the operation; expired retries create
a fresh operation and reject stale confirmations. Revocation stops automatic
reprovisioning until an administrator requests repair. Missing, revoked or invalid
authorization leaves Telegram synchronization failed and retains the running
notification configuration. Once active, wait for configuration synchronization,
then use **Send test message** to verify Telegram delivery. Operators can also replace
the bot token or disable Telegram. Existing environment delivery tokens remain a
fallback for installations without a saved delivery authorization. The credential
delivery endpoint is Worker-only; management sessions cannot use it to retrieve saved tokens.

Changes travel in managed snapshot contract 4 (only Workers with an override).
Contracts 2 and 3 remain unchanged for untouched installations. The snapshot hash
includes Telegram settings and credential identity, so replacements and disables
are observable by existing synchronization diagnostics. An older Worker rejects
contract 4; upgrade it before configuring an override.

Workers apply settings on authenticated synchronization when no tasks are active;
no process restart or test message is required. The editor displays applied only
when current diagnostics confirm synchronization and the Worker is not stale.
Saved settings are desired state until acknowledged by a heartbeat. Standalone
Workers retain their local configuration; Server settings cannot authorize
standalone work or configure a disconnected local process.

Tokens are retained only in runtime memory, not the snapshot cache. On a restart
during a Server outage, a cached explicit override cannot fall back to environment
credentials: managed notification delivery waits for authenticated retrieval.
A validated cached snapshot with no override preserves local notification
behavior. Cached projects still do not authorize scheduling during an outage.

## Verify delivery from the dashboard

Open **Workers → Worker → Edit → Telegram notifications**. Enable managed
Telegram, enter the destination (for example `-100123456789` or `@my_channel`),
and supply a replacement bot token if needed. Save. The displayed values are the
Server's **desired** settings. **Pending** means that the Worker has not yet
acknowledged this snapshot; wait until it is idle and synchronization completes.
**Applied** means its latest heartbeat acknowledges the current snapshot, including
successful credential retrieval and runtime configuration. It does not by itself
prove that Telegram accepts the token or that the bot can post to the destination.
**Stale** means the Worker is offline or has an old heartbeat; **failed**
synchronization requires checking credential delivery authorization and diagnostics.
The saved token is never displayed.

Once Telegram is enabled and the current configuration is acknowledged by an
online Worker, select **Send test message**. Unsaved edits must be saved first.
The action queues one bounded request through the authenticated Worker control
path. The Worker checks the requested snapshot against its applied snapshot and
the current Server snapshot, then sends with its active notifier. It never asks
the management client to retrieve a token. Saving alone never queues a test.

The result starts as **Pending**; select **Refresh test result** to observe
completion. **Telegram accepted** means the Bot API returned HTTP success and
`ok: true` from that Worker. A rejection means checking the bot token, exact chat
ID, bot membership, and channel posting permissions. Network failure, a changed
configuration, timeout, or an interrupted Worker cannot establish delivery;
refresh Worker diagnostics before explicitly requesting another test. Requests
expire after 60 seconds and dispatched tests are never automatically replayed,
including after a Server or Worker restart. A timeout can leave delivery uncertain.
The dashboard retains the latest observation per Worker; it describes the
snapshot used by that test, not a later configuration update.
