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

Live provider and manual HTTPS checks are not part of the deterministic test suite.
