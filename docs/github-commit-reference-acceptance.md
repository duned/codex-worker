# Native GitHub commit references: investigation and acceptance

## Status

The native-event acceptance criterion remains **unverified**. Production
integration is unchanged because the evidence does not establish a cause or a
safe ordering fix. This completes the Issue's expressly permitted offline
investigation fallback; it does not claim that native references are fixed.

The live campaign is already authorized against **`duned/codex-worker-test`
only**. No further human authorization is required. This checkout has local
Git test fixtures and Worker-owned Git/GitHub operations, but no scoped live
comparison harness. Codex must not run Git/`gh` lifecycle writes, obtain the
Worker's withheld credentials, or substitute connector writes for Worker-owned
pushes. Such writes would also fail to reproduce the Worker's push credential
identity/type and transport. Consequently no live mutation was attempted here.
The exact remaining step is to run the comparison below in the authenticated
Worker/test-owned environment, record native events for fresh implementation
SHAs, and select a production change only if the direct-flow fix is reproduced.

## Historical evidence retained from the previous attempt

On 2026-10-05 the previous attempt reported authenticated read-only verification
of these timelines, commits and refs. The resumed investigation uses that
retained evidence and the Issue context; it has not repeated those authenticated
reads. Counts and ref tips below describe that observation, not current state.

- [Issue #8 timeline](https://api.github.com/repos/duned/codex-worker-test/issues/8/timeline?per_page=100)
  returned eight events, including native `referenced` event `31940279262` at
  `2026-09-27T17:27:35Z` for implementation SHA
  `27ce3a6b7875c31db3bc7d884cd1ae463b64e663`.
- [Issue #10 timeline](https://api.github.com/repos/duned/codex-worker-test/issues/10/timeline?per_page=100)
  returned fourteen events and no `referenced` event. Its completion report
  identifies attempt 2, execution `7d1ba25f-c934-4bfe-97a1-7ea1f56b143f`.
  Each response contained fewer than the requested 100 events; no second page
  was needed. These are observations at that time, not an indexing guarantee.
- [Implementation #8](https://api.github.com/repos/duned/codex-worker-test/commits/27ce3a6b7875c31db3bc7d884cd1ae463b64e663)
  has subject `Implement #8: Add random number endpoint` and one parent. It is
  the second parent of merge commit
  `4457a96f3d58493f1939db29d573201deb3702ae`, whose first parent is the
  implementation's parent. Its retained completed ref still points to the
  implementation SHA.
- [Implementation #10](https://api.github.com/repos/duned/codex-worker-test/commits/6bb93e6a79f8db245f8320cdd0e25df8078884d6)
  has subject `Implement #10: 10 · Add ping endpoint` and sole parent
  `4457a96f3d58493f1939db29d573201deb3702ae`. Both remote `main` and
  `completed/10-add-ping-endpoint-10-retry-2` point to this implementation SHA.
  Thus direct reachability, the Issue number in the message and completed-ref
  retention are verified despite the missing native event.
- The author/committer identities differ: #8 uses the original Ubuntu identity;
  #10 uses the managed node's Codex Worker identity. Authentication identity/type,
  repository settings and provider indexing state at push time remain unknown.
  Retry lineage is also different. None of these variables has been isolated.

The historical source immediately before `32005e47de60782f2ecd94d2aebf1883286344bc`
(`Implement #28: Extra: Use fast-forward integration for completed feature branches`)
used `merge --no-ff --no-edit`, pushed the base, then pushed the completed ref.
That change replaced the merge with fast-forward/rebase integration while keeping
the same push order. This verifies the source sequence, but the exact deployed
binary and push transactions for #8 are not available from the timeline alone.
Reversing ref publication is therefore a hypothesis, not an established fix.

## Offline coverage

`GitRepository.CommitAndIntegrateAsync` currently refreshes/reconciles the base,
rebases and validates when necessary, fast-forwards the local base, pushes the
base, then publishes the completed branch when enabled. Both refs point to the
implementation commit; there is no merge commit. The commit subject remains
`Implement #N: ...`. Without completed-branch retention there is only a base
push. With auto-merge disabled, integration does not push either ref.

`DirectIntegrationPushSequenceIsIndependentOfRetryAndRecovery` observes separate
receive transactions on a temporary bare remote. It covers first attempts,
fresh retries and a new repository instance recovering preserved implementation
after post-rebase validation failure, both with and without completed-branch
retention. All use the same base-first sequence and retain the Issue reference
in the subject. With retention, both refs receive the same single-parent commit;
without retention, only the base is pushed and no completed ref is created.

`HistoricalMergeSequencePublishesImplementationWithBaseBeforeCompletedRef`
reproduces the historical `merge --no-ff --no-edit` and two-push sequence in a
disposable local fixture, using a Worker-produced implementation commit. It
verifies that the first push publishes a two-parent merge containing the
implementation and the second creates a completed ref at the implementation.
It also verifies that disabling automatic integration initially publishes no
refs. The comparison confirms that both sequences first make the implementation
reachable through the base; topology and the base tip differ, push order does
not. This is a reconstruction from repository history, not execution of the old
deployed binary or proof of GitHub's indexing behavior.

Retry/restart lineage does not affect these local ref transactions. Its effect
on native GitHub events remains unisolated and requires the live repetitions.

GitHub's [timeline API](https://docs.github.com/en/rest/issues/timeline)
provides the native event observation boundary. Its
[event type documentation](https://docs.github.com/en/rest/using-the-rest-api/issue-event-types#referenced)
describes `referenced` events. Neither documents a required Git push/ref ordering.
Rendering `#N` as a link in a commit message does not prove an Issue timeline
event exists. A comment or a `cross-referenced` event is not the acceptance target.

Local validation results are reported with the task outcome. Prior attempt
results do not certify this diff or the Worker's authoritative validation gate.

## Scoped live comparison

Run this already-authorized campaign through the authenticated Worker/test-owned
boundary against `duned/codex-worker-test`, with Issues enabled. Never target
`duned/codex-worker` or another repository. Use fresh Issues and fresh commits
for each cell: indexing an already-published SHA can contaminate subsequent
experiments. Keep the same
push credential identity/type, commit author/committer, repository visibility,
base/default branch relationship and repository settings throughout. Record these
as metadata without recording tokens, credential-helper output or raw environment.
Do not use the production repository or replay writes against the reported Issues.

Re-read the reported Issues' paginated timelines and exact commits at campaign
time. Record full implementation SHAs and matching `referenced` events;
inspect parent counts and available execution history for actual ref publication
order. The Issue body alone does not specify all Git/push operations of the old
merge path. Do not infer retry causality from two executions.

Use a test-owned experimental checkout for the comparison cells. This is
an acceptance experiment, not a new Worker integration/recovery workflow.
Keep credentials inside that trusted host; pass no secret payloads to Codex.
Record the IDs of all disposable Issues and branch refs created by the campaign
so cleanup is confined to those resources.

| Cell | Publication sequence | Purpose |
| --- | --- | --- |
| A | Current Worker: fast-forward base push, then create completed ref | Reproduce current behavior |
| B | Publish implementation to a fresh completed ref, then fast-forward/push base | Isolate reversal of the two pushes |
| C | Publish a fresh experimental ref at starting base, then advance that ref to implementation, then fast-forward/push base | Distinguish branch creation from an existing-ref update |
| D | Push a base with a two-parent merge containing implementation, then create completed ref | Compare merge topology while retaining A's publication order |
| E | Publish implementation ref first, then push a base with a two-parent merge | Separate topology from ordering; use the verified historical sequence if different |
| F | Current Worker with completed-branch retention disabled | Determine whether base-only publication is sufficient |

For every cell, use `Implement #N: ...` and one implementation commit. For D/E,
observe the implementation SHA separately from the merge SHA; a reference from
the merge alone is insufficient. Snapshot the timeline before publication, after
each push and after a bounded indexing observation window (for example, poll
every 5 seconds for at most 2 minutes). Follow pagination on every observation.
Persist only event IDs/types, `commit_id`, safe commit URLs and timestamps.
If indexing is delayed beyond the window, record an inconclusive result; do not
assume absence is permanent or automatically repeat pushes.

The success predicate is an event with `event == "referenced"` and
`commit_id == IMPLEMENTATION_SHA` on the intended Issue. Also verify the remote
configured base contains that SHA, the implementation subject still references
the Issue, and the direct cells contain no new merge commit. Record remote ref
SHAs and each push's order/result. Never force-push or delete evidence to repeat
the campaign.

After identifying a reproducible sequence, repeat A and the successful direct
cell with first attempts, fresh retries, and restarted integration recovery.
Use the supported Worker history/recovery operations for Worker cells; do not
edit durable ownership metadata or fabricate lineage. Include a case requiring
rebase before publication, and test both managed and standalone ownership with
equivalent Git settings. Fresh Issues/SHAs prevent an earlier attempt's timeline
event from being mistaken for the retry's event.

After retaining the redacted results, clean up only the recorded disposable
resources through the test-owned Git/GitHub boundary: close campaign Issues and
delete campaign refs after confirming they belong to this campaign and no Worker
execution or recovery still owns them. Preserve default/configured base branches,
pre-existing Issues/refs and active or uncertain recovery resources. Report
failed cleanup explicitly; do not force it or discard unrecorded evidence.

Also repeat the topology/ordering cells with each historical author/committer
identity while keeping push authentication fixed. Separately compare the actual
historical push authentication types if available. Do not change identity,
authentication and ref ordering together and attribute the result to ordering.

## Gate for a production change

Retain the redacted comparison results before selecting an ordering change.
If all cells produce references, investigate the original execution's identity,
permissions and GitHub indexing rather than introducing a speculative extra push.
If no cell does, escalate the documented reproduction to GitHub support.

If a direct cell reliably fixes discovery, extend `GitRepository` at its existing
integration boundary. Add deterministic tests for that proven ref transaction
order and for authority loss/cancellation between pushes, failed/uncertain
publication, base advancement, completed-ref collisions and recovery after the
first push. Preserve the configured retention policy, verified ownership and
non-force push/recovery semantics. A ref published before base integration must
not be treated as evidence that integration completed. Do not add remote refs
when retention is disabled without an explicit lifecycle/cleanup design.

Run the existing integration, recovery and GitHub lifecycle suites, then repeat
the scoped native-event predicate on actual normal, retry and restarted Worker
executions. Until these results exist, neither a root cause nor the Issue's native
reference acceptance criterion is established.
