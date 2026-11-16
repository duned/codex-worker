# Legacy dashboard (backup)

This is the previous Server dashboard, kept only as a reference while the new
dashboard in `src/CodexServer/dashboard` replaces it. It is not built, embedded,
tested or deployed: `CodexServer.csproj`, `tools/validate.py` and
`tools/dashboard-deploy.py` only use `src/CodexServer/dashboard`.

`repo-tests/dashboard` holds the repository-level scripts that exercised this
dashboard; they still reference the old `worker-poc` paths and are not run.

Delete this directory once the new dashboard covers everything you still need.
