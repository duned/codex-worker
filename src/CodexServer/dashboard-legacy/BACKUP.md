# Legacy dashboard (backup)

This is the previous Server dashboard, kept frozen while the new dashboard in
`src/CodexServer/dashboard` replaces it. `CodexServer.csproj` builds it (Vite only,
no lint/tests) and embeds it so the Server serves it under `/old/...` with assets
under `/dashboard-assets/legacy/`. `tools/dashboard-deploy.py` and the development
dashboard override only handle `src/CodexServer/dashboard`.

`repo-tests/dashboard` holds the repository-level scripts that exercised this
dashboard; they still reference the old `worker-poc` paths and are not run.

Delete this directory once the new dashboard covers everything you still need.
