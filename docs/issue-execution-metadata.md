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
effort use project settings; missing CCC model delegates to its CLI and missing
CCC effort means `medium`. Codex project defaults never supply CCC settings.

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

This checkout currently advertises and executes only Codex. Standalone CCC
Issues remain ready with an unavailable-engine diagnostic; managed CCC requests
wait for a Worker advertising the CCC provider. A defensive execution check
prevents CCC selections from running through the Codex executor. Installing the
Claude CLI alone does not establish CCC execution readiness.

Metadata selects execution settings only. It cannot authorize credentials,
permissions, paths, provisioning, or executable commands. Existing lease,
capacity, validation, Git and recovery ownership rules continue to apply.
