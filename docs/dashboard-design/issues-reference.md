# Issues workflow visual reference

The [Issues workflow and list image](issues-list-reference.svg) is embedded in the dashboard design guide. This note preserves behavior and API field mappings; sample values are illustrative.

## API and state mapping

- Issue state and eligibility come from `ManagedGitHubIssue.State`, `IsEligible` and `EligibilityReasons`; the UI does not recalculate eligibility.
- Queue presence comes from bounded execution projections keyed by project and work reference. Missing records are “Not shown” when the query is incomplete or unavailable.
- Issue details include labels and blocked-by relationships. Discovery policy is configuration only; do not invent cycle timestamps or candidate totals.
