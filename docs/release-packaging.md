# Linux release packaging

The Linux x64 packaging script creates self-contained .NET 10 archives for Ubuntu 24.04. Build on a machine with the .NET 10 SDK and the Linux x64 publishing workload/runtime packs available:

```sh
packaging/release-linux-x64.sh [--set-version VERSION] [output-directory]
```

The output directory defaults to a unique directory under the system temporary directory, outside the checkout. Without `--set-version`, the script gets the version from the shared MSBuild `Version` property in `Directory.Build.props`. It publishes each application for `linux-x64` with `--self-contained true`, and archives the complete publish output with a `VERSION` file. Resulting names include the application version and platform:

```text
codex-server-0.13.0-linux-x64.tar.gz
codex-worker-0.13.0-linux-x64.tar.gz
wet-0.13.0-linux-x64.tar.gz
checksums.txt
```

Use `packaging/release-linux-x64.sh --version` to print the effective MSBuild version without publishing. An explicit version can be embedded in the packages with `--set-version 1.2.3`; it does not edit the checkout. The archives contain the apphost, .NET runtime, native dependencies and application dependencies required by each application. The Server and Worker archives also include their embedded dashboard resources. They do not contain operator configuration or credentials. On Ubuntu 24.04 x64, extract the desired archive and run `./CodexServer`, `./CodexWorker` or `./wet`; configure Server and Worker through their normal environment and configuration mechanisms. WET is a standalone CLI and does not install or start Worker or Server services.

Default packaging output is intentionally retained after success at the printed temporary path; remove it when no longer needed. On failure, default temporary output is removed. An explicit output directory is operator-owned and may retain partial files after failure; inspect or remove them before retrying. Choosing an explicit directory inside the source checkout can make it dirty, so use an external path.

## Publishing a release

Run the release command from a clean checkout with the intended source commit checked out:

```sh
packaging/release.sh 0.15.0
```

The command requires Git, GitHub CLI (`gh`), `rg`, the .NET 10 SDK with Linux x64 runtime packs, `tar`, and `sha256sum`. Authenticate `gh` first. The command checks tracked and untracked checkout state, verifies that neither the local nor remote `v0.15.0` tag exists, and checks all GitHub Releases (including drafts) before building. GitHub operations explicitly target `origin`, even if `GH_REPO` or the GitHub CLI default repository points elsewhere. It builds the Server, Worker and WET archives with the requested version, then creates a draft GitHub Release targeting the checked out commit with all three archives and `checksums.txt`. It publishes the draft after all assets upload successfully. The GitHub Release operation creates the corresponding `v0.15.0` tag at the exact checked out commit; that commit must already be available on GitHub. The command does not commit a version bump: the requested version overrides the shared MSBuild version for the published assemblies and archive `VERSION` files. Temporary build output is removed on both success and failure, leaving the checkout clean.

If GitHub reports an upload failure after creating a draft or tag, inspect it with `gh release view v0.15.0` before retrying. If final publication returns an error, its outcome may be uncertain: inspect the release first. A complete unpublished draft can be reviewed and published with `gh release edit v0.15.0 --draft=false`. Existing tags and releases are never overwritten. Partial drafts/tags are intentionally retained for inspection; complete them manually or explicitly delete the incomplete release and its tag before retrying. The command never automatically deletes remote state. Set `TMPDIR` outside the checkout; a temporary directory inside it is rejected. The checksum file contains SHA-256 hashes of all three archives and can be checked with `sha256sum --check checksums.txt` after downloading all assets.

## Installing published releases

Operators installing Server or Worker should start with the [A–B–C Server + Worker installation](../README.md#install-server--worker). Those service installs need Ubuntu 24.04 x86_64 and systemd, but no source checkout or .NET installation.

To install WET on Ubuntu 24.04 x86_64 from a published release, download and extract its archive, then place the standalone executable on `PATH` (replace `OWNER/REPOSITORY` and `VERSION`):

```sh
version=0.20.0
curl -fL "https://github.com/OWNER/REPOSITORY/releases/download/v${version}/wet-${version}-linux-x64.tar.gz" -o wet.tar.gz
mkdir wet-release
tar -xzf wet.tar.gz -C wet-release
install -Dm755 wet-release/wet "$HOME/.local/bin/wet"
"$HOME/.local/bin/wet" --help
```

The archive is self-contained, so this install does not need a .NET runtime, repository checkout, Worker or Server installation, or system service. Add `$HOME/.local/bin` to `PATH` if it is not already there. To verify only the downloaded WET archive, also download `checksums.txt` and run `sha256sum --ignore-missing --check checksums.txt` from the directory containing both files.

For a specific release, use its matching `vVERSION` installer source and pass the same `--version VERSION`. For latest, the quick path explicitly uses `main` with no version option. Keep `checksums.txt` alongside the Server and Worker release archives; installers verify the selected service archive before extraction. The same file also includes the standalone WET archive.

See the [Linux installation reference](linux-installation.md) for generated management tokens, unattended Worker enrollment, local publish-directory installs, paths, updates, removal, and troubleshooting.

## Local release tests

Run `bash tests/release-tests.sh` to exercise packaging and release decisions with stubbed GitHub, Git, and .NET commands, without publishing a real release.

## Product version policy

The shared MSBuild `Version` property in `Directory.Build.props` is the authoritative current product version: `0.15.0`. Worker and Server product metadata, banners and version endpoints derive from that property through generated assembly metadata.

The current product version changes only through the explicit release/version process. Starting the next numbered roadmap or GitHub Issue series does not bump it; neither Issue numbering nor branch names determine a product version. While 16.x work is in progress before the v0.16 release, the intended current version remains `0.15.0`. Do not independently bump versions as part of unrelated feature Issues.

Explicitly prepare the next source version in `Directory.Build.props` as part of its release. `packaging/release.sh VERSION` and `packaging/release-linux-x64.sh --set-version VERSION` continue to override the shared MSBuild version for the requested build, including published assemblies, archive names and archive `VERSION` files, without editing the source property.

### Managed configuration contract upgrades

The v0.26.1 managed snapshot contract was version 1. Version 2, introduced
in v0.26.2, adds nullable Server-owned `maxParallelTasks` and includes it in
snapshot hash material (including the null marker). The current Server and
Worker use the same version 2 hash calculation. A running v0.26.1 Worker
rejects a fresh version 2 response before hash validation: upgrade the Worker
as well as the Server. Version skew does not authorize cached scheduling.
A matching version 2 pair still rejects malformed project definitions and
hash mismatches; these require correcting the authoritative Server state.

Worker startup retrieves authenticated authoritative configuration before
requiring the cache to validate. A recognized version 1 cache is never applied
as version 2 or used for execution. If the Server is unavailable or supplies
an invalid contract, the Worker retains durable state, remains non-execution-ready,
and retries synchronization through its normal control loop. Successful refresh
validates and atomically writes the cache before replacing applied configuration.

The Linux `install-worker.sh` update path retains the service account, existing
YAML, enrollment identity, credentials and data directories. Normal upgrades
therefore repair the old cache automatically when the authenticated Server is
reachable; reinstalling or deleting enrollment is unnecessary.

For emergency cache recovery, stop `codex-worker`, back up only
`<identityFile>.configuration.json` to a secure location outside the active cache
path, then restart the service to fetch fresh configuration. Preserve ownership
and restrictive permissions on the backup. Never delete the identity, tokens,
credentials, execution history, checkouts or recovery worktrees. Confirm that
configuration synchronization and execution readiness recover before expecting
new assignments; a running service alone is not proof of readiness.
