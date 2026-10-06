# Guided project onboarding validation

Automated checks use stubbed Server GitHub command results, local registry databases,
and the dashboard VM seams; no provider credentials or operator nodes are required.
The Server discovers one page of up to 50 repositories per request using its actual
GitHub CLI context. Repository discovery is bounded to 1,000 pages. Repository read
and exact base-branch checks are separate from Issue write, Git push, and Worker
provider authentication. Failed or unavailable checks cannot approve a review.

## Manual HTTPS checks

Use an existing test Server HTTPS deployment with a trusted certificate and an
administration session. Do not disable certificate checks or modify operator VMs.

1. With Server GitHub disconnected, open Projects → Create project. Load repositories
   and confirm the actionable connection error and link to Settings → Server GitHub.
   Complete the existing guided connection there, then retry repository discovery.
2. Select an accessible repository. Confirm its name, description and actual default
   branch populate the essentials. Use next-page loading where available. Use the
   deliberate manual fallback for a repository absent from discovery.
3. Try an unreadable repository and a nonexistent base branch. Verify that neither
   enables saving. Try an authentication requirement with invalid scope, an invalid
   version, and duplicate requirements; confirm the Server validation identifies the
   offending field or requirement.
4. Verify and review a valid definition, then change a field. Saving must require a
   fresh review. Confirm the review shows lifecycle and automatic-discovery policy,
   requirements and labels, and keeps Worker authentication and push access separate.
   Automatic discovery starts off for new projects. Enable it deliberately and review
   the polling interval; page size and cycle deadline are available under advanced
   tuning. The Server remains authoritative for defaults and bounds.
5. Save with no registered Workers. Confirm the result offers Add / associate Worker
   and explains remaining preparation. Confirm no Issue labels or queue entries change.
   New definitions retain the established enabled lifecycle default, explicitly shown
   during review; automatic discovery remains unset. This form does not activate a
   Worker or establish execution readiness.
6. Edit a project with configured requirements, labels and automatic discovery. Confirm
   unchanged advanced policy is retained. Race with a second definition edit and
   confirm stale revisions preserve the draft and offer loading the current definition.
7. Drop the save response after it reaches the Server. Choose Check saved definition.
   Confirm the existing identity is recovered without another create request. If the
   save did not apply, re-review before retrying. If reconciliation is unavailable,
   confirm no mutation repeats. Reloading the browser loses the in-memory draft; refresh
   Projects to recover persisted definitions before creating another project. Registry
   uniqueness continues to prevent duplicate names, identities and repositories.

## Manual automatic-discovery acceptance: codex-worker-test

Use an authorized administration session and the existing disposable/test project. Do
not change any live project as part of automated validation.

1. Open Projects → `codex-worker-test` and inspect Automatic Issue discovery, central
   project lifecycle, the polling interval, and configured ready/blocked labels. A
   missing or legacy policy is shown as disabled. Dashboard inspection itself must
   not enqueue work.
2. Confirm the Server service account can read the repository in Settings → Server
   GitHub connection, and confirm the project's configured ready label matches the
   intended test Issues. Inspect blocked-by dependencies as well; setting discovery
   does not alter GitHub labels or bypass eligibility.
3. Edit the test project, enable discovery, review the opt-in policy and interval,
   then save. Wait at least one configured interval for the normal Server cycle. One
   bounded page is read per due cycle, so later pages may require more intervals.
4. Inspect project executions and Issue eligibility. Distinguish an eligible Issue
   that has not yet been discovered (the dashboard has no last-cycle count), an Issue
   rejected by ready-label/dependency rules, and a queued request waiting for an
   eligible authorized Worker with capacity/readiness. Automatic discovery does not
   retry terminal requests. Execution still follows normal Worker admission and
   assignment.
5. If no eligible requests appear, check Server repository read access/authentication,
   the ready label and open blockers, project lifecycle, and the queue. Explicit
   enqueue and queued eligibility refresh remain available in Issue administration;
   they do not bypass admission. Disabling discovery pauses future discovery cycles
   without canceling existing queued requests or active leases.

Live provider and manual HTTPS checks are not part of the deterministic test suite.
