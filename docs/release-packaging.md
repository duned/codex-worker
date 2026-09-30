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

## Installing published releases

Operators should start with the [A–B–C Server + Worker installation](../README.md#install-server--worker). Release installs need Ubuntu 24.04 x86_64 and systemd, but no source checkout or .NET installation.

For a specific release, use its matching `vVERSION` installer source and pass the same `--version VERSION`. For latest, the quick path explicitly uses `main` with no version option. Keep `checksums.txt` alongside both release archives; installers verify the selected archive before extraction.

See the [Linux installation reference](linux-installation.md) for generated management tokens, unattended Worker enrollment, local publish-directory installs, paths, updates, removal, and troubleshooting.

## Local release tests

Run `bash tests/release-tests.sh` to exercise packaging and release decisions with stubbed GitHub, Git, and .NET commands, without publishing a real release.
