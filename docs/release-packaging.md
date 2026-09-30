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

To publish a release, run the script from a clean checkout for the release tag, review the generated archives and `checksums.txt`, and attach all three files as assets to the matching GitHub Release. The checksum file contains SHA-256 hashes of both archives and can be checked with `sha256sum --check checksums.txt` after downloading the assets.
