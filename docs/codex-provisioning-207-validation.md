# Codex CLI provisioning acceptance for Issue 207

Implementation changes cover the shared typed provider, fixed Codex privilege helper, Worker/Server installer policies and rollback, uninstall cleanup, and bounded failure classification. Deterministic tests exercise the helper with real filesystem preparation and isolated environment/argument recording, both generated sudo policies, installer upgrade/rollback, uninstall cleanup, policy rejection, root/non-root routing, and installation/authentication separation.

**Clean-host acceptance is operator-owned after integration.** Repository implementation and deterministic validation can complete without access to a separate Ubuntu 24.04 VM or installed managed Worker/Server pair. Local tests do not establish that the packaged service-account workflow succeeds on that host. After integration, the operator records the following campaign without manual npm, ownership, cache, sudoers or shell changes; failures become focused follow-up work:

1. Install the packaged Worker normally. Record package version, OS and stock npm version. Confirm the Worker runs as `codex-worker`, the installed helper is root-owned 0755, and its sudo policy is root-owned 0440 and passes visudo.
2. Enable Worker-local provisioning and allowlist the three Codex actions. Run `sudo codex-worker provision install codex-cli --allow-elevation`. Verify `sudo -u codex-worker -H sh -lc 'command -v codex && codex --version'` and capability installation/authentication states.
3. Uninstall Codex through the supported CLI. Queue typed installation from Server with `allowElevation=true`; verify terminal success/report delivery and Worker capability re-detection while the service remains unprivileged. No manual privilege or package changes are permitted between these steps.
4. Queue/update and uninstall through the same managed path. Verify capability and executable states after each action. Check that unrelated privileged commands remain denied by `sudo -n` for the service account.
5. Reinstall and upgrade Worker packaging, then uninstall/purge it. Confirm privilege policy/helper/cache lifecycle, retained external tool/login behavior, and safe retention of shared state if Server is also installed.

Installation does not require interactive Codex login. Record authentication-required as a valid result when the service account has no existing login.

## Local validation performed

- Full .NET solution build: zero warnings/errors. NuGet restore initially failed inside the sandbox with NU1301 (network permission denied); the approved run outside the sandbox restored and built successfully.
- Full .NET solution tests: 1,424 passed (1,187 Worker/Server tests and 237 toolbox tests), with no failures or skips, in the approved run outside the sandbox.
- Focused provisioning tests: 165 passed. The sandbox denied the test runner's socket; the approved run outside the sandbox passed.
- Installer/uninstaller tests, Worker upgrade/rollback tests, and helper contracts passed, including shared helper retention while the other component remains installed.
- The official npm 9.2.0 package was downloaded into a temporary directory and run without installing it on the host. Its configuration loader accepted both the candidate probe flags and the helper's separate configuration paths. The helper contract script also checked the locally available npm configuration loader.
- Dashboard tests: 6 passed. Release packaging regression tests passed.
- Shell syntax and diff consistency checks passed.

These are Codex development checks. The Worker's configured validation gate runs separately. No clean-host, package installation, service-account sudo authorization, or Server report-delivery acceptance is claimed by these results.
