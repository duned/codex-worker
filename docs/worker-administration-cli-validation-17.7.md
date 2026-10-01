# 17.7 Worker administration CLI integration validation

**Overall: PASS**  
**Date:** 2026-10-01  
**Environment:** Ubuntu 24.04, .NET SDK 10.0.112

The Release `linux-x64` Worker artifact (version `0.15.0`) was published from this checkout and run directly from a temporary `/tmp` workspace. The temporary config, projects directory, identity, and CLI-related home/cache directories were all inside that workspace. Capability discovery issued read-only probes against installed host tools; no host files, dependencies, services, or provisioning state were modified.

| Result | Validation | Diagnostics |
| --- | --- | --- |
| PASS | Publish Worker artifact | NuGet.org was unreachable; restore used the existing local package cache and placed packages in the temporary workspace. No machine dependencies were installed or updated. |
| PASS | CLI help | Root, `status`, `config`, `capabilities`, and `provision` help returned exit code 0. |
| PASS | Status | Text and `--json` commands returned exit code 0. JSON parsed successfully. |
| PASS | Config administration | `config show` and `config validate` passed in text and JSON modes. The isolated managed-mode configuration validated with no diagnostics. All JSON parsed successfully. |
| PASS | Capability inventory | `capabilities list` and `capabilities refresh` passed in text and JSON modes. All JSON parsed successfully. |
| PASS | Provisioning status | Text and `--json` commands returned exit code 0. JSON parsed successfully. |
| PASS | Provisioning denial path | `provision install git --allow-elevation --json` returned exit code 2 with `diagnostic: Denied`; the temporary config had provisioning disabled, so no installer was invoked. |
| PASS | Protected Worker service unchanged | Before and after: `LoadState=loaded`, `ActiveState=active`, `SubState=running`, `MainPID=452730`, `Result=success`, `NRestarts=0`. Both snapshots were identical. |
| SKIPPED | Direct host-mutating provisioning | Successful install, upgrade, uninstall, authentication, and credential operations were not run, as required by the safety guard. |

The temporary artifact, config, identity, and command outputs were removed after validation. The installed Worker and its service were not modified.
