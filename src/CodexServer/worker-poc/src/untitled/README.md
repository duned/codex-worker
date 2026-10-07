# Untitled UI source provenance

Public source copied from https://github.com/untitleduico/react at
`4702dc0ea8d140c3491a85670c7b4fab47b722da` (MIT). Paths below this directory
retain the upstream repository layout. The complete license is at
`../../UNTITLED-UI-LICENSE` and is included in the embedded production bundle.
Dependencies are pinned in the existing package manifest and npm lockfile.

Visible upstream components used by the application:

- `SidebarNavigationSimple`, `MobileNavigationHeader`, `NavList`, `NavItemBase`:
  complete desktop sidebar and React Aria modal mobile navigation.
- `Button`, `Input`: controls, navigation back, session presentation.
- `Badge`: independent textual status observations throughout the view.
- `TableCard.Root`, `TableCard.Header`: summary, execution, capability, control
  and login surfaces; `Table` and its Header/Head/Body/Row/Cell: recent outcomes.
- `FeaturedIcon` and the public `@untitledui/icons`: shell and content icons.

Their referenced helpers and base components are retained as upstream source;
unused source has been omitted. `styles/theme.css` provides the upstream tokens.
`styles/globals.css` uses Tailwind 4, React Aria variants and animation utilities.
PostCSS compiles it in the existing esbuild/MSBuild asset pipeline.

Deliberate adaptations:

- The demo Untitled UI logo module exports the product's Codex Server identity.
- Sidebar demo search and account UI are omitted; the feature slot contains the
  existing administration session's sign-out callback. No fake search or user.
- Navigation has an accessible landmark and mobile dialog name.
- Unused prose typography import/plugin and stylesheet are omitted. The selected
  components do not use prose styles. Font stacks use local system fallbacks;
  no external font or other runtime assets are fetched.

`../poc.css` contains layout/composition rules only. Component styling remains in
these upstream modules, using the upstream tokens and utility classes.

- Mobile navigation closes its React Aria state after selecting a navigation link.
  Product dialog patterns in `shared/Dialogs.tsx` compose the retained public
  Button source and tokens with upstream React Aria modal primitives, using the
  same overlay/surface composition as the approved shell. They are product
  composition rather than a claimed copy of a paid Untitled UI modal component.
