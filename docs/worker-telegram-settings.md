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

Runtime delivery uses the existing authorized credential endpoint and the
Worker's `CODEX_WORKER_CREDENTIAL_DELIVERY_TOKEN`. Missing, revoked or invalid
delivery credentials leave synchronization pending/failed and retain the running
notification configuration. Operators can replace the token, restore credential
delivery authorization, or disable Telegram. The credential delivery endpoint is
Worker-only; management sessions cannot use it to retrieve saved tokens.

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
