# Issue execution metadata

Issues default to Codex. Use one level-two `Worker` section to select an engine:

```markdown
## Worker
engine: ccc
model: sonnet
effort: low
```

Only `engine`, `model`, and `effort` are accepted. Engine names are `codex` and
`ccc` (case insensitive). Missing engine means `codex`. Missing Codex model and
effort use project settings. Missing CCC model is recorded as CLI-selected (the
current adapter supplies `sonnet`); missing CCC effort means `medium`. Codex project defaults never supply CCC settings.

Codex retains its existing model-ID contract (1–128 letters, digits, dots,
underscores or hyphens, starting with a letter or digit), including configured
custom IDs. Codex efforts are `low`, `medium`, `high`, and `xhigh`. CCC models are
`sonnet`, `opus`, and `haiku`; CCC efforts are `low`, `medium`, and `high`.
Effort values are case insensitive. Unsupported values are errors.

Existing Issues can continue using:

```markdown
## Codex
model: gpt-6.1-sol
effort: low
```

If both sections exist, Worker values take precedence, Codex supplies missing
values, and overlapping values must agree. Conflicts are rejected regardless of
section order. Nonempty Codex settings alongside `engine: ccc` are incompatible.
Duplicate sections, duplicate keys, empty values and unknown keys are errors.
End metadata with another level-one or level-two heading before writing prose.
Fenced examples and indented code outside metadata do not select an engine.

Server queues retain selection in the existing work-reference JSON and require
the selected agent provider when assigning work. Older references default to
Codex. Worker assignment checks use that selection and reject an Issue whose
engine changed after queueing. The Issue read API exposes parsed selection for
administration clients alongside eligibility errors.

The default Worker composition registers Codex and the optional CCC adapter.
Managed Workers advertise CCC only after a successful readiness observation;
installing Claude alone does not establish readiness. Unavailable selections
never fall back to Codex. See the [operator runbook](dual-engine-operations.md).

Metadata selects execution settings only. It cannot authorize credentials,
permissions, paths, provisioning, or executable commands. Existing lease,
capacity, validation, Git and recovery ownership rules continue to apply.

## Implementation-engine boundary

`ImplementationEngineSelector` accepts explicitly injected `IImplementationEngine`
instances; Codex remains the built-in default through its existing executor.
The Worker and `ExecutionRunner` accept the same selector. Registration is a code
composition seam, not dynamic plugin loading or proof of execution readiness.
Global Codex startup preflight remains unchanged.

Engines receive `ImplementationRequest` with the assigned isolated worktree,
Issue, frozen model/effort and attempt context, and return `ImplementationResult`.
Implementation, validation repair and integration repair use the same selected
engine. Engines may edit task files only. Worker-owned validation, Git state
verification, branches, commits, rebase, integration, push, GitHub reporting and
lease checks remain outside the engine. Implementations must reuse bounded
process execution, deadlines, cancellation and credential filtering, and preserve
the existing task, authentication and infrastructure failure classifications.
The Codex adapter delegates directly without changing prompts or CLI arguments.

History freezes engine identity when an attempt is created. Older SQLite rows
migrate to `codex`; retries and recovery retain that identity even if Issue
metadata changes. An unavailable preserved engine blocks execution rather than
switching engines. Legacy Codex-session reconstruction applies only to Codex.
