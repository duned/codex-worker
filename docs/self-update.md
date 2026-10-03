# Updating installed hosts

Run the update command from a separate operator session, outside the Server or
Worker service being upgraded:

```bash
sudo codex-server update
sudo codex-worker update
```

Both commands check published GitHub releases for `duned/codex-worker`, exclude
drafts, prereleases, unsupported tags and releases missing the component's Linux
x64 archive or `checksums.txt`, and compare semantic versions. They never downgrade
or reinstall the current version. A newer release prompts `[Y/n]`; Enter accepts,
`n` declines, and end of input declines. Redirected input requires `--yes` to apply
an update. Discovery is bounded to 1,000 releases and 60 seconds.

Use `update --check` for monitoring, or `update --yes` for automation. `--json`
emits one camel-case JSON object with `contractVersion: 1`, `component`,
`currentVersion`, `latestVersion`, `updateAvailable`, `updateAttempted`, `status`,
`finalVersion` and `error`. JSON never prompts: without `--yes` it checks only.
`--check` always prevents installation, including when combined with `--yes`.
Statuses are `current`, `available`, `declined`, `updated`, `failed`, `canceled`
and `invalidArguments`. Exit codes are 0 for completed checks, declined updates
and successful upgrades, 1 for operational failure, 2 for invalid arguments and
130 for cancellation/timeouts. A failed attempted installation reports an unknown
final version; inspect service state before retrying.

Installation requires root and the packaged Linux x64 installation (Ubuntu 24.04).
The command downloads the selected release tag's complete `install-server.sh` or
`install-worker.sh` before executing it with `--version`. Server installation uses
`--non-interactive` and does not request provisioning bootstrap. Worker updates do
not request registration or change configuration; the installer preserves the
previous service active state. The existing installers own checksums, configuration,
persistent data, permissions, binary activation, rollback and service restart.
The command verifies the installed executable's `--version` after installer success.
Installer output is captured rather than mixed into JSON or exposing service secrets.
Installation is bounded to 15 minutes; cancellation terminates child processes and
requires inspecting service state before retrying. For installer failure details,
check `systemctl status codex-server`/`codex-worker`, `journalctl -u` for that service,
and the documented manual installer workflow.

The Server operator helper routes updates directly as root, without loading the
service's EnvironmentFile or running inside the service being restarted. The
Worker installer adds `/usr/local/bin/codex-worker` pointing to its installed
executable. An existing Server helper from an older release must first be upgraded
through the manual installer, or invoke
`sudo /opt/codex-server/current/CodexServer update` when the installed executable
already supports the command. Existing Workers can use
`sudo /opt/codex-worker/CodexWorker update` until the installer creates the alias.
