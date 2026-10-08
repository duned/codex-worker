# Home and Worker visual alignment

Reviewed the design guide and checked-in visual references:
`images/server-dashboard-final/home-1280.png` and
`images/worker-detail-poc/worker-detail-1280.png`.
The Home reference predates the guide's Workers/Projects composition; the current
guide defines content order, while the captures inform shell, surfaces and hierarchy.

Home now uses unframed sections, compact Worker cards, and separated Project rows.
Worker detail retains the status summary, current work and secondary control rail,
with reporting explanations and action reasons in disclosures. Shared status chips
use the Untitled UI large size. Shared timestamps use browser-local time, as the
Server sidebar did, with explicit `dd/MM/yyyy, HH:mm:ss` formatting. Existing dark
preference, persistent toggle, external-link and deletion icons remain in use.

## Reproduce visual evidence

After installing dashboard dependencies and Playwright, build the dashboard and run:

```sh
npm run check --prefix src/CodexServer/worker-poc
node tests/dashboard/home-worker-visual-review.cjs
```

The capture script uses deterministic existing contract fixtures and writes eight
full-page images to `docs/images/home-worker-alignment`: both routes at 1280 and
375 pixels in dark and light themes. It checks overflow, theme selection, runtime
errors, date labels and Project section structure without making API mutations.
Review those images alongside the reference images before visual acceptance.

No new screenshots were produced in this execution: frontend dependencies were
missing, offline restore lacked cached packages, online restore failed DNS for
registry.npmjs.org, and Playwright was unavailable. Visual acceptance remains
unverified; screenshots must not be inferred from the implementation or fixtures.
