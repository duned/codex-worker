# Linux release packaging

The Linux x64 packaging script creates self-contained .NET 10 archives for Ubuntu 24.04. Build on a machine with the .NET 10 SDK and the Linux x64 publishing workload/runtime packs available:

```sh
packaging/release-linux-x64.sh [--set-version VERSION] [output-directory]
```

The output directory defaults to a unique directory under the system temporary directory, outside the checkout. Without `--set-version`, the script gets the version from the shared MSBuild `Version` property in `Directory.Build.props`. It publishes each application for `linux-x64` with `--self-contained true`, and archives the complete publish output with a `VERSION` file. Resulting names include the application version and platform:

```text
codex-server-0.13.0-linux-x64.tar.gz
codex-worker-0.13.0-linux-x64.tar.gz
checksums.txt
```

Use `packaging/release-linux-x64.sh --version` to print the effective MSBuild version without publishing. An explicit version can be embedded in the packages with `--set-version 1.2.3`; it does not edit the checkout. The archives contain the apphost, .NET runtime, native dependencies, application dependencies, and embedded dashboard resources required by each application. They do not contain operator configuration or credentials. On Ubuntu 24.04 x64, extract the desired archive and run `./CodexServer` or `./CodexWorker`; configure each application through its normal environment and configuration mechanisms.

Default packaging output is intentionally retained after success at the printed temporary path; remove it when no longer needed. On failure, default temporary output is removed. An explicit output directory is operator-owned and may retain partial files after failure; inspect or remove them before retrying. Choosing an explicit directory inside the source checkout can make it dirty, so use an external path.

## Publishing a release

Run the release command from a clean checkout with the intended source commit checked out:

```sh
packaging/release.sh 0.15.0
```

The command requires Git, GitHub CLI (`gh`), `rg`, the .NET 10 SDK with Linux x64 runtime packs, `tar`, and `sha256sum`. Authenticate `gh` first. The command checks tracked and untracked checkout state, verifies that neither the local nor remote `v0.15.0` tag exists, and checks all GitHub Releases (including drafts) before building. GitHub operations explicitly target `origin`, even if `GH_REPO` or the GitHub CLI default repository points elsewhere. It builds the Server and Worker archives with the requested version, then creates a draft GitHub Release targeting the checked out commit with both archives and `checksums.txt`. It publishes the draft after all assets upload successfully. The GitHub Release operation creates the corresponding `v0.15.0` tag at the exact checked out commit; that commit must already be available on GitHub. The command does not commit a version bump: the requested version overrides the shared MSBuild version for both published assemblies and archive `VERSION` files. Temporary build output is removed on both success and failure, leaving the checkout clean.

If GitHub reports an upload failure after creating a draft or tag, inspect it with `gh release view v0.15.0` before retrying. If final publication returns an error, its outcome may be uncertain: inspect the release first. A complete unpublished draft can be reviewed and published with `gh release edit v0.15.0 --draft=false`. Existing tags and releases are never overwritten. Partial drafts/tags are intentionally retained for inspection; complete them manually or explicitly delete the incomplete release and its tag before retrying. The command never automatically deletes remote state. Set `TMPDIR` outside the checkout; a temporary directory inside it is rejected. The checksum file contains SHA-256 hashes of both archives and can be checked with `sha256sum --check checksums.txt` after downloading the assets.

The Server can be installed directly on Ubuntu 24.04 x64 from GitHub Release assets, without a checkout or .NET installation:

```sh
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/main/packaging/linux/install-server.sh | sudo bash
```

Pass `--version VERSION` after `bash -s --` to install a specific release. The installer resolves the latest release when no version is specified, downloads the matching `codex-server-VERSION-linux-x64.tar.gz` and `checksums.txt`, and verifies the archive before extracting it. Keep `checksums.txt` alongside both archives on every release. The installer is served from the repository's `main` branch; releases themselves are selected from the versioned GitHub Release assets.

On first install, the installer generates a 256-bit management token in `/etc/codex-server/server.env` if that setting is not already present. The file is owned by `root:codex-server` with mode `0640`, and installer reruns preserve the configured token. Retrieve it explicitly when needed with `sudo sed -n 's/^[[:space:]]*CODEX_SERVER_MANAGEMENT_TOKEN[[:space:]]*=[[:space:]]*//p' /etc/codex-server/server.env`; the installer does not print the credential. The service starts with this token available, so no manual token configuration or restart is needed on a fresh install.

## Installing a Worker

On a clean Ubuntu 24.04 x86_64 host, the installer downloads the self-contained archive and `checksums.txt` from the selected GitHub Release, verifies the archive SHA-256, and installs the Worker binary and systemd unit. Root, `curl`, `tar`, `sha256sum`, and a working systemd installation are required; a .NET SDK or runtime is not.

```sh
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/main/packaging/linux/install-worker.sh | sudo bash
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/main/packaging/linux/install-worker.sh | sudo bash -s -- --version 0.13.0
```

These pipe commands are supported on a clean host. The installer also works when downloaded and invoked directly with `sudo bash install-worker.sh`; in either mode it exits unsuccessfully with an actionable diagnostic if a required installation step fails.

With no option the installer resolves the latest GitHub Release. `--version VERSION` pins an installation; an optional leading `v` is accepted. It preserves `/etc/codex-worker/worker.yml`, `/etc/codex-worker/worker.env`, and `/var/lib/codex-worker` across reruns. It installs binaries under `/opt/codex-worker`, configuration under `/etc/codex-worker`, persistent data and projects under `/var/lib/codex-worker`, and the systemd unit at `/etc/systemd/system/codex-worker.service`. Binaries and the unit are root-owned. The service runs as the unprivileged `codex-worker` account, configuration is root-owned and group-readable, and persistent state is private to the service account. Logs are available through journald; `/run/codex-worker` is created by systemd for runtime files.

After installation, set `server.url`, add project configuration as appropriate, and put registration or provider secrets in `worker.env` with mode `0640`. Registration with Codex Server is a separate bootstrap step. Start the service when that configuration is ready:

```sh
sudo systemctl enable --now codex-worker
sudo systemctl status codex-worker
sudo journalctl -u codex-worker
```

For manual removal:

```sh
sudo systemctl disable --now codex-worker
sudo rm /etc/systemd/system/codex-worker.service
sudo systemctl daemon-reload
sudo rm -rf /opt/codex-worker /etc/codex-worker
```

Treat `/var/lib/codex-worker` as persistent identity and execution state: back it up or explicitly remove it based on retention requirements. Failed upgrades keep the previous binaries under `/opt/codex-worker.previous.*`; inspect and remove those directories manually when no longer needed. If installation fails, check the diagnostic, release asset availability, OS and architecture, and outbound HTTPS. Service startup problems are reported by `systemctl status` and `journalctl`.

## Local release tests

Run `bash tests/release-tests.sh` to exercise packaging and release decisions with stubbed GitHub, Git, and .NET commands, without publishing a real release.
