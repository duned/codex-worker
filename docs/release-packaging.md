# Linux release packaging

The Linux x64 release script creates self-contained .NET 10 archives for Ubuntu 24.04. Build on a machine with the .NET 10 SDK and the Linux x64 publishing workload/runtime packs available:

```sh
packaging/release-linux-x64.sh [output-directory]
```

The output directory defaults to `artifacts/`. The script gets the version from the shared MSBuild `Version` property in `Directory.Build.props`, publishes each application for `linux-x64` with `--self-contained true`, and archives the complete publish output with a `VERSION` file. Resulting names include the application version and platform:

```text
codex-server-0.13.0-linux-x64.tar.gz
codex-worker-0.13.0-linux-x64.tar.gz
checksums.txt
```

Use `packaging/release-linux-x64.sh --version` to print the effective release version without publishing. The archives contain the apphost, .NET runtime, native dependencies, application dependencies, and embedded dashboard resources required by each application. They do not contain operator configuration or credentials. On Ubuntu 24.04, extract the desired archive and run `./CodexServer` or `./CodexWorker`; configure each application through its normal environment and configuration mechanisms.

The Server can be installed directly on Ubuntu 24.04 x64 from GitHub Release assets, without a checkout or .NET installation:

```sh
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/main/packaging/linux/install-server.sh | sudo bash
```

Pass `--version VERSION` after `bash -s --` to install a specific release. The installer resolves the latest release when no version is specified, downloads the matching `codex-server-VERSION-linux-x64.tar.gz` and `checksums.txt`, and verifies the archive before extracting it. Keep `checksums.txt` alongside both archives on every release. The installer is served from the repository's `main` branch; releases themselves are selected from the versioned GitHub Release assets.

On first install, the installer generates a 256-bit management token in `/etc/codex-server/server.env` if that setting is not already present. The file is owned by `root:codex-server` with mode `0640`, and installer reruns preserve the configured token. Retrieve it explicitly when needed with `sudo sed -n 's/^[[:space:]]*CODEX_SERVER_MANAGEMENT_TOKEN[[:space:]]*=[[:space:]]*//p' /etc/codex-server/server.env`; the installer does not print the credential. The service starts with this token available, so no manual token configuration or restart is needed on a fresh install.

To publish a release, run the script from a clean checkout for the release tag, review the generated archives and `checksums.txt`, and attach all three files as assets to the matching GitHub Release. The checksum file contains SHA-256 hashes of both archives and can be checked with `sha256sum --check checksums.txt` after downloading the assets.

## Installing a Worker

On a clean Ubuntu 24.04 x86_64 host, the installer downloads the self-contained archive and `checksums.txt` from the selected GitHub Release, verifies the archive SHA-256, and installs the Worker binary and systemd unit. Root, `curl`, `tar`, `sha256sum`, and a working systemd installation are required; a .NET SDK or runtime is not.

```sh
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/main/packaging/linux/install-worker.sh | sudo bash
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/main/packaging/linux/install-worker.sh | sudo bash -s -- --version 0.13.0
```

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
