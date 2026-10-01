# Linux release artifact size investigation

## Decision

Keep the existing self-contained `linux-x64` release mode. The current release
script produced 48.93 MB and 49.18 MB compressed archives for Server and Worker
on Ubuntu 24.04 x64 with .NET SDK 10.0.112. Both contain about 112 MB of files
before compression. These reproduce the Issue's approximate 46 MB figures to
within the expected difference between decimal MB, rounded estimates and this
SDK build.

Framework-dependent publishing is much smaller, but requires .NET 10 and the
ASP.NET Core 10 shared framework on the machine. It breaks the clean-machine
deployment promise. Trimming and Native AOT are not currently viable safe
options: publish fails on many unsuppressed reflection/dynamic-code diagnostics
from application JSON/YAML paths. No smaller candidate has passed the required
functional checks, so release scripts and artifact behavior remain unchanged.

## Measurements

Measured on Ubuntu 24.04 x64, .NET SDK 10.0.112 / runtime 10.0.12, with
`packaging/release-linux-x64.sh --set-version 0.15.0`. Publish sizes are
directory bytes (`du -sb`) before `VERSION` is added. Archive sizes use the
script's `tar -czf` settings and include `VERSION`; uncompressed archive bytes
are the sum of regular-file sizes from the archive listing. Framework-dependent
archives were made with the same tar settings and the same `VERSION` entry.
Values are decimal MB (1 MB = 1,000,000 bytes).

Exact publish-directory / compressed archive bytes were: Worker current and
self-contained `112,628,856 / 49,179,649`, Worker framework-dependent
`3,083,704 / 1,335,748`, Server current and self-contained
`112,023,324 / 48,932,403`, and Server framework-dependent
`2,478,172 / 1,092,663`.

| Target | Publish mode | Publish directory | Uncompressed archive | Compressed artifact | External runtime | Compatible | Notes |
|---|---|---:|---:|---:|---|---|---|
| Worker | Current | 112.63 MB | 112.63 MB | 49.18 MB | No | Executable/help smoke; task flow not checked | 343 archive entries, including `VERSION`; 320 DLLs, 15 `.so`, 2 PDBs |
| Worker | Framework-dependent | 3.08 MB | 3.08 MB | 1.34 MB | Yes | Not functionally run; needs .NET 10 and ASP.NET Core 10, so not clean-machine compatible | 7 DLLs, SQLite native library, apphost and config files |
| Worker | Self-contained | 112.63 MB | 112.63 MB | 49.18 MB | No | Same as Current | Same publish properties and runtime contents as Current |
| Worker | Trimmed (`partial`) | No output | — | — | No | No; publish fails | `IL2026` on reflection-based System.Text.Json and YamlDotNet paths; warnings are errors |
| Worker | Native AOT | No output | — | — | No | No; publish fails | `IL2026` and `IL3050` on JSON and YAML dynamic/reflection paths; warnings are errors |
| Server | Current | 112.02 MB | 112.02 MB | 48.93 MB | No | Yes; API smoke checked | 343 archive entries, including `VERSION`; 319 DLLs, 15 `.so`, 2 PDBs |
| Server | Framework-dependent | 2.48 MB | 2.48 MB | 1.09 MB | Yes | Not functionally run; needs .NET 10 and ASP.NET Core 10, so not clean-machine compatible | 6 DLLs, SQLite native library, apphost and config files |
| Server | Self-contained | 112.02 MB | 112.02 MB | 48.93 MB | No | Same as Current | Same publish properties and runtime contents as Current |
| Server | Trimmed (`partial`) | No output | — | — | No | No; publish fails | `IL2026` on reflection-based System.Text.Json and ASP.NET Core JSON response paths; warnings are errors |
| Server | Native AOT | No output | — | — | No | No; publish fails | `IL2026` and `IL3050` on JSON serialization and ASP.NET Core JSON response paths; warnings are errors |

The two products have nearly equal sizes because most of each self-contained
publish is the same .NET 10 / ASP.NET Core runtime. The largest files include
`System.Private.CoreLib.dll` (15.58 MB), `System.Private.Xml.dll` (7.90 MB),
`libcoreclr.so` (7.11 MB), `libclrjit.so` (4.75 MB), and
`System.Linq.Expressions.dll` (3.72 MB). The Worker-specific application
assembly is larger and it includes YamlDotNet, while the Server uses the Web
SDK; together with their other distinct files, this leaves Worker only 0.61 MB
larger in the measured uncompressed publish.

The two product assemblies plus shared `CodexProvisioning.dll` are under 0.8 MB
for Worker and under 0.6 MB for Server. In comparison, the self-contained
outputs include 15 native shared objects each, including the roughly 1.47 MB
SQLite `libe_sqlite3.so`. The framework-dependent outputs still contain this
SQLite native library. Each self-contained output also contains 2 PDB files:
164 KB for Worker and 104 KB for Server, under 0.2% of publish size. Removing
them would make symbol-based crash diagnosis harder for a negligible archive
reduction, so the release script is unchanged.

## Compatibility findings

- Both applications use Microsoft.Data.Sqlite and its native SQLite provider.
  They also use System.Text.Json for persisted records and HTTP payloads.
- Worker configuration and project YAML use YamlDotNet reflection serializers;
  the Worker also uses HTTP JSON APIs and process execution. Trim/AOT publish
  reported YamlDotNet `IL3050` and multiple HTTP/System.Text.Json `IL2026`
  diagnostics.
- Server is an ASP.NET Core Web SDK application with minimal API endpoints,
  configuration binding, DI and runtime JSON serialization (including
  `Results.Json`). Its trim/AOT publish reported `IL2026` and `IL3050` in
  registry JSON persistence and ASP.NET Core JSON response code.
- `Directory.Build.props` treats warnings as errors. No trim/AOT diagnostics
  were suppressed. Addressing them would require explicit JSON source
  generation and a YamlDotNet static serialization strategy, followed by broad
  runtime contract testing; successful publish alone would not establish safe
  behavior.
- No additional nonstandard Linux shared-library dependency was identified:
  SQLite's native library and .NET native runtime files are included in the
  archive. Validation ran on Ubuntu 24.04 but not on a pristine VM without a
  .NET installation.

## Functional validation and release tooling

The actual release script completed for version `0.15.0`; both generated
checksums verified. Extracted Worker `--help` and Server `--version` commands
ran. The self-contained Server started with a temporary SQLite data directory
and returned healthy from `/health` within the first 200 ms polling interval.
No repeatable startup-time benchmark was made. Worker GitHub preflight and task
execution were not run because they require authorized GitHub/Codex credentials
and services; no framework-dependent, trimmed or AOT candidate passed
functional validation. The Server smoke ran with loopback permission in this
environment.

The archive paths, executable-at-root layout, `VERSION` entry, version override,
checksum generation and existing installer expectations are unchanged. This
investigation did not alter release configuration or packaging scripts.

## Reproducing the benchmark

Run the current mode through the release script on the same target:

```sh
packaging/release-linux-x64.sh --set-version 0.15.0 /tmp/codex-release
```

For a framework-dependent comparison, restore the project for `linux-x64`, then
publish with `--runtime linux-x64 --self-contained false`. A trimming attempt
used `--self-contained true -p:PublishTrimmed=true -p:TrimMode=partial`; Native
AOT used `--self-contained true -p:PublishAot=true`. Capture the complete
publish diagnostics: the current projects fail these latter publishes under
warnings-as-errors. For successful outputs, use `du -sb` and the release
script's `tar -czf` options; add the same `VERSION` contents before comparing
archive sizes. Use an Ubuntu 24.04 x64 machine without an installed .NET runtime
to validate the self-contained deployment contract.
