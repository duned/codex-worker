# Dashboard Issue title audit

Issue titles are display metadata. `WorkReference.Title` is optional and the
repository-scoped `github_issue_reads` snapshot supplies the latest locally known
title, including after eligibility invalidation. A saved execution title remains
the fallback when no cached title is available. No title establishes current
GitHub state, eligibility, labels, enqueue authority or completion.

## Surfaces and contracts

| Surface | Backing response and presentation |
| --- | --- |
| Home current/recent executions | Execution list; shared `IssueTitle` (completed rows use `ExecutionTable`). |
| Home recent project activity | Execution list; shared `IssueTitle`, with a separate execution details link. |
| Project current execution and history | Execution list; shared `IssueTitle`. |
| Worker detail current execution and history | Execution list filtered by Worker; shared `IssueTitle`. Worker inventory cards carry capacity and identity, not Issue identities. |
| Execution list | Paged execution list; shared `IssueTitle`. |
| Execution detail and timeline header | Single execution response; the same cached-title projection as lists, then shared `IssueTitle`. Timeline events describe execution stages rather than independent Issue references. |
| Issue detail | Existing full-Issue response contains its title; if unavailable, the heading can use the title from the existing status/history execution responses. History rows identify attempts within this Issue page. |
| Project Issue browser, editor and action previews | Existing full-Issue reads and mutation preview/results already carry titles. Discovery candidates carry titled work references. |
| Issue relationships and graph | Existing relationship nodes carry titles; graph nodes permit unavailable titles. |
| Standalone Worker dashboard | Local execution runtime/history already carries the saved `IssueTitle`; it cannot read the Server cache. |

Both execution list APIs and the single execution API use
`DecorateIssueTitlesAsync`. It reads cached titles in one batch keyed by canonical
repository (case-insensitive GitHub owner/name) plus positive Issue number. It
never calls GitHub or queries the database once per row. Full-Issue reads remain
the authoritative cache population/refresh boundary; no new read or refresh path
is introduced for title rendering.

## Intentional identity-only references

Breadcrumbs and navigation labels such as `#27` identify the enclosing Issue;
the page heading supplies its available title. Relationship graph edges and
mutation batch result items identify endpoints by number; titled nodes/full-Issue
records supply their display text. Maintenance operation requests, recovery IDs,
and diagnostics identify operations/executions rather than adding a second Issue
summary. Number-only discovery pagination is an internal input to existing full
reads, not a dashboard Issue display contract. These identifiers are not enriched
through additional GitHub requests.

Missing or blank titles remain normal: the shared Issue renderer preserves its
localized unavailable-title text and internal Issue link. A missing full-Issue
read still leaves Issue detail unavailable for actions; a cached display title
does not make it actionable.

Focused coverage includes list/detail projection parity, invalidated snapshots,
saved-title fallback, missing cache entries, overlapping Issue numbers in distinct
repositories, repository casing aliases and unchanged GitHub request counts.
Home rendering tests cover current work and recent project activity with supplied
and missing titles; existing shared renderer and Worker tests cover other consumers
and reject title-triggered Issue reads.
