// Adapted from Untitled UI's MIT-licensed Badge (see ../UNTITLED-UI-LICENSE).
// Retain the pill-color/md variant and gray/success/warning/error palette only;
// scoped CSS replaces Tailwind utilities so the existing shell is unaffected.
export const Badge = ({ color = 'gray', children }) => (
  <span className={`poc-badge poc-badge-${color}`}>{children}</span>
);
