# Documentation inventory

## Combined review (28 / Issue 367)

Reviewed the integrated documentation consolidation (Issue 368) and test reduction (Issue 369). The operator entry point is [operations](operations.md), supported by Linux installation, HTTPS deployment, self-update, Server and Worker local administration, and execution maintenance. It covers standalone YAML/checkouts and managed enrollment/central projects, service-account prerequisites, updates/backups/drain, outbound communication, snapshot validation, lease fencing and connectivity-loss recovery. Specialized recovery, security, capability and optional-feature guides remain linked from that entry point. Engineering instructions, release/package artifacts, examples, campaign guides and approved/draft visual references remain available.

The final `docs` inventory contains **31 Markdown files**, including the two audit records and contributor/design references; **23 historical reports were removed**. The table below enumerates the retained assets and removal rationale. README provides a short operator navigation set. A local link check of README, AGENTS and all 31 documentation Markdown files found no missing file targets. A source/reference scan found no remaining references to deleted reports outside this intentional inventory. External active-Issue references cannot be verified locally; visual assets and campaign references were preserved for that reason.

The [test audit](test-suite-audit-28.2.md) records **2,088 → 2,085 tests** and **357.607 → 337.949 seconds** (5.50% faster) in its successful before/after sessions. The case-count reduction is modest (0.14%); the timing is a single observed pair, not proof of a repeatable whole-suite speedup. Review of the test diff confirms only Git worktree tests changed: copied repositories/remotes remain independently mutable, sibling-preservation assertions survive consolidation, and fresh/retry/recovery plus completed-ref presence/absence remain covered. Core lifecycle, managed readiness/leases, recovery, security, installation and configuration suites were retained. No production behavior, version or validation configuration changed.

Combined local validation on 2026-10-12 attempted `dotnet build CodexWorker.sln -m:1` and `dotnet test CodexWorker.sln --no-restore -m:1 --logger trx --results-directory /tmp/issue-367-tests` once. NuGet access was denied by the sandbox (`NU1301`, `api.nuget.org:443`), preventing build/test execution and a new runtime measurement. The figures above are the child's recorded measurements, not results from this checkout. The Worker's separately configured authoritative validation remains required before integration; no passing combined gate is claimed here.

## Per-file decisions

Inventory for the operator consolidation: tracked Markdown and documentation assets, including contributor material. Installation scripts, service units and configuration examples were also reviewed as the implementation source of truth. Historical reports are removed only where current guides retain the operational contract; executable campaign guides, engineering/release instructions and visual assets remain. External active-Issue asset references cannot be enumerated locally, so assets are preserved. New entry points are README, operations and development. The retained inventory itself records removal decisions; removed paths below are intentional historical identifiers, not links.

| File | Decision | Reason |
| --- | --- | --- |
| `AGENTS.md` | Retain | Active engineering instructions. |
| `CHANGELOG.md` | Retain | Release history and workflow. |
| `README.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/codex-interruption-recovery.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/codex-provisioning-207-validation.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/codex-quota-observations.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/dashboard-design-guide.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/dashboard-design/executions-reference.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/dashboard-design/projects-reference.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/dashboard-design/settings-server-preparation-reference.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/dashboard-design/workers-list-reference.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/dashboard-stream-acceptance.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/e2e-test-plan-v1.md` | Retain | Executable campaign reference. |
| `docs/execution-maintenance.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/execution-record-administration.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/github-commit-reference-acceptance.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/home-worker-visual-alignment.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/images/server-dashboard-final/executions-1280.png` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/images/server-dashboard-final/home-1280.png` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/images/server-dashboard-final/login-dark-1280.png` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/images/server-dashboard-final/login-light-375.png` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/images/server-dashboard-final/preparation-light-1280.png` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/images/server-dashboard-final/project-form-375.png` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/images/server-dashboard-final/settings-form-375.png` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/images/server-dashboard-final/worker-375.png` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/images/worker-detail-poc/worker-detail-1280.png` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/images/worker-detail-poc/worker-detail-375.png` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/linux-installation.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/managed-completion-reconciliation.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/managed-interrupted-retry.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/managed-project-lifecycle-integration.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/managed-worker-e2e.md` | Retain | Executable campaign reference. |
| `docs/mockups/README.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/mockups/enqueue-issue-dialog-approved.svg` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/mockups/execution-detail.svg` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/mockups/execution-list.svg` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/mockups/home-summary.svg` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/mockups/project-detail.svg` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/mockups/project-editor-dark-reference.svg` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/mockups/project-list.svg` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/mockups/settings.svg` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/mockups/sidebar-administration-dark-reference.svg` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/mockups/worker-detail-approved.jpg` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/mockups/worker-detail-approved.svg` | Retain | Current owner-approved Worker detail visual target for the React + Untitled UI dashboard. |
| `docs/mockups/worker-detail-controls-refinement.svg` | Retain | Owner-approved Worker controls, restart-status and direct status-refresh reference. |
| `docs/mockups/worker-detail-lower-refinement.svg` | Retain | Owner-approved Worker readiness and technical-details layout reference. |
| `docs/mockups/worker-detail-dark.svg` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/mockups/worker-editor-dark-reference.svg` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/mockups/workers-list-dark-reference.svg` | Retain | Preserve visual/reference assets, including owner-review assets whose external Issue references cannot be verified offline. |
| `docs/node-capabilities.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/project-onboarding-validation.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/release-packaging.md` | Retain | Release history and workflow. |
| `docs/remote-https-acceptance.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/remote-https-deployment.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/secret-protection.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/secure-transport-integration-review.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/self-update.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/server-control-plane-audit-18.1.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/server-dashboard-cutover.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/server-dashboard-final-review.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/server-dashboard-integration-acceptance.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/server-dashboard-navigation.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/server-dashboard-react.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/server-executions-react.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/server-local-administration-18.10.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/server-local-administration.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/server-projects-react.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/server-settings-react.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/server-worker-detail-poc.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/v0.15-release-readiness.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/work-execution-toolbox.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/worker-administration-cli-validation-17.7.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/worker-administration-integration-review-19.10.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/worker-administration-runtime-audit-19.1.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/worker-dashboard-updates.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/worker-local-administration.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/worker-local-e2e-19.9.md` | Remove | Completed feature migration, audit or one-off acceptance evidence; operational guidance retained in current runbooks. |
| `docs/worker-onboarding.md` | Retain | Current operator, contributor or dashboard design reference. |
| `docs/worker-telegram-settings.md` | Retain | Current operator, contributor or dashboard design reference. |
| `src/CodexServer/worker-poc/src/untitled/README.md` | Retain | Current operator, contributor or dashboard design reference. |

Configuration and packaging inputs reviewed: `config/worker.example.yml`, `config/project.example.yml`, `config/managed-project.example.json`, `packaging/linux/worker.managed.example.yml`, all Linux install/update/uninstall scripts and both systemd units, plus `packaging/release.sh` and `packaging/release-linux-x64.sh`. These remain canonical workflow artifacts and are unchanged. The three new guides/inventory are retained as operator entry point, contributor workflow and cleanup rationale respectively.
