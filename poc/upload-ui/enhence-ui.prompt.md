# Prompt: Restyle FlowX.Upload Angular UI — Professional Finance App, Modern Minimalist

You are acting as a senior UX designer and Angular developer. Your task is
to restyle an existing, fully-functional Angular v14 feature module —
**visual design only**. Do not change component logic, selectors, inputs,
outputs, routing, or the HTTP contract. Treat this as a design system
retrofit onto working code.

## App context

This UI belongs to a **store-management application**. The specific feature
is a **bulk inventory upload workflow**: a branch manager uploads a
multi-sheet Excel file to create, update, or delete products offered at a
branch. The flow is asynchronous and stateful:

1. User uploads a file (optionally as a "dry run" — a simulation that
   changes nothing).
2. The system parses and validates it in the background. If rows are
   invalid, the user fixes them inline and resubmits — no re-upload needed.
3. Once valid, the system proposes a **plan** (a list of proposed
   create/update/delete changes). The user reviews it and selects which
   rows to actually execute.
4. The system executes the confirmed plan and reports **per-row results**
   — some rows can succeed while others fail; that's a normal outcome, not
   an error state.

**Who uses this:** operations/inventory staff at a financial or retail
services company, during working hours, often reviewing consequential
bulk changes (hundreds of rows, real financial/inventory impact) before
approving them. This is closer to a **banking back-office or reconciliation
tool** than a consumer app — the design should read as trustworthy,
precise, and calm, not playful or decorative. Users need to scan dense
tabular data quickly and feel confident before clicking "confirm" on a
batch of changes they can't easily undo.

## Current technical state — do not change

- **Angular v14**, NgModule-based feature module (not standalone
  components).
- **Bootstrap 5**, imported globally via SCSS (`@import "~bootstrap/scss/bootstrap";`
  in `styles.scss`) — assume you can also import individual Bootstrap Sass
  partials and override Bootstrap's `$variables` before that import, not
  just apply utility classes on top.
- All components use `ChangeDetectionStrategy.OnPush` and typed Reactive
  Forms. Preserve this.
- File tree (only `.html` and `.scss` files should change; `.ts` files
  should not need edits unless a template needs a new `@Input`/computed
  getter purely to support a visual state — flag any such case explicitly
  rather than silently adding one):

```
upload/
  components/
    upload-start/
      upload-start.component.html   — file picker + dry-run toggle form
      upload-start.component.scss
    upload-status/
      upload-status.component.html  — container: dry-run banner, error alert, ngSwitch over child views
      upload-status.component.scss
    validation-errors/
      validation-errors.component.html  — editable table of invalid rows
      validation-errors.component.scss
    plan-review/
      plan-review.component.html    — selectable table of proposed changes, Create/Update/Delete badges
      plan-review.component.scss
    processing-indicator/
      processing-indicator.component.html  — spinner + label, shown during async work
      processing-indicator.component.scss
    upload-results/
      upload-results.component.html  — results table, per-row success/fail badges, retry button on failure
      upload-results.component.scss
```

## Design direction: modern, minimalist, professional finance app

**Visual language:** restrained, high-contrast-but-not-loud, generous
whitespace, thin 1px borders instead of heavy drop shadows, flat surfaces
over skeuomorphic depth. Think Stripe Dashboard, Mercury, Brex, or a modern
banking back-office tool — not a consumer SaaS marketing site, not
Bootstrap's out-of-the-box look (avoid the default rounded-everything,
saturated blue/green/red "Bootstrap blue" feel entirely).

### Color system

Override Bootstrap's Sass variables in a new shared partial
(`src/styles/_finance-theme.scss`, imported before Bootstrap's own
`_variables.scss` is consumed) rather than fighting Bootstrap with
`!important` utility overrides. Target palette:

- **Background:** near-white (`#FAFAFB` or `#F8F9FB`), not pure white, for
  the page canvas; pure white (`#FFFFFF`) for cards/tables to create subtle
  layering without shadows.
- **Primary text:** near-black charcoal, not pure black (`#111827` /
  `#1A1F2B`).
- **Secondary/muted text:** cool gray (`#6B7280`).
- **Borders/dividers:** very light cool gray (`#E5E7EB`), 1px, used
  generously instead of shadows to separate sections.
- **Brand/primary accent:** a single deep, desaturated navy or ink-blue
  (e.g. `#14213D` or `#1B2559`) — used sparingly, for primary buttons,
  active states, and the header/logo area only. This is the one place the
  UI is allowed to feel "branded."
- **Semantic colors — desaturate all of Bootstrap's defaults**, they read
  as too bright/consumer-facing for this context:
  - Success (Create / row succeeded): muted forest green (`#1E7F4F`), not
    Bootstrap's default `#198754`.
  - Info (Update): muted steel blue (`#3B5B92`), not Bootstrap's bright
    `#0DCAF0`.
  - Danger (Delete / row failed / validation error): muted brick red
    (`#B3261E`), not Bootstrap's bright `#DC3545`.
  - Warning (retry / dry-run notice): muted amber (`#B45309`), not
    Bootstrap's bright yellow.
  - Apply these as **background tints at ~8–12% opacity with the full-tone
    color as border/text**, not solid saturated badge fills — e.g. a
    "Create" badge should be pale green background with dark green text
    and a hairline green border, not a solid bright-green pill.

### Typography

- System font stack led by **Inter** (`Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif`)
  if available via existing project fonts; otherwise Bootstrap's default
  system stack is acceptable — do not add a new font-loading dependency
  without flagging it.
- **Numeric and identifier data (row numbers, sheet indices, plan item
  IDs, any currency/price values visible in descriptions) should use a
  monospace or tabular-figure font** (`"SFMono-Regular", ui-monospace, Menlo, monospace`)
  so columns of numbers align visually — a hallmark of finance-tool
  polish that plain Bootstrap tables lack.
- Restrained size scale: page titles `1.25rem`/`600` weight, card headers
  `0.9375rem`/`600`, body/table text `0.875rem`/`400`, helper/muted text
  `0.8125rem`/`400`. Avoid Bootstrap's default `h1`-`h6` jumps — this UI
  has at most two heading levels in view at once.
- Slightly tightened letter-spacing (`-0.01em`) on headings only.

### Layout & spacing

- Generous, consistent spacing using Bootstrap's spacing scale
  (`$spacer` multiples) — err toward more whitespace between sections than
  Bootstrap's defaults, especially around card headers/footers and table
  cell padding.
- Cards: flat white surface, 1px `#E5E7EB` border, **no `box-shadow`** (or
  at most a barely-visible `0 1px 2px rgba(0,0,0,0.04)`), border-radius
  reduced from Bootstrap's default (`0.375rem`) to a tighter, more
  "financial software" `0.25rem` — override `$border-radius`.
- Tables: no heavy zebra-striping; use a very subtle row hover state
  (`#F8F9FB`) instead. Header row: uppercase, `0.75rem`, `600` weight,
  muted gray text, bottom border only (no background fill) — this is the
  single highest-impact change for making the plan-review and results
  tables read as "financial software" rather than "generic Bootstrap CRUD
  table."
- Buttons: primary action = solid navy/ink fill, no rounded-pill shape,
  modest `0.25rem` radius; secondary/ghost actions ("Select all",
  "Deselect all") = outline or text-only, never a second solid color
  competing with the primary action. Exactly one solid-filled primary
  button visible per screen at a time.

### Component-specific requirements

- **`upload-start`**: the dry-run checkbox should be visually distinct as
  a deliberate, slightly elevated toggle (not a plain default checkbox) —
  this is a meaningful mode switch, not an incidental option. File input
  should look custom-styled (a bordered drop-zone-style control), not the
  raw native `<input type="file">` chrome.
- **`upload-status`**: the dry-run banner (`.alert-info`) should be
  restyled as a quiet, persistent top strip (muted amber or navy tint, not
  Bootstrap's default bright blue alert) — it needs to stay noticeable for
  the entire session without feeling like a transient toast.
- **`processing-indicator`**: replace or restyle Bootstrap's default
  spinner border color to the muted navy accent; keep it small and calm,
  not a large attention-grabbing loader — this is a background task, not a
  blocking wait.
- **`validation-errors`**: each editable input cell should have a subtle
  focus state using the accent color (not Bootstrap's default bright blue
  focus ring). Error messages in muted brick red, not bright Bootstrap
  danger red.
- **`plan-review`**: Create/Update/Delete badges use the desaturated
  tinted-background style described above. The "selected count" summary
  in the card header should be visually prominent (this is the number the
  user is about to commit to) — consider a small pill/counter treatment
  distinct from plain text.
- **`upload-results`**: success/fail badges use the same tinted style.
  Failed rows' detail text should be legible but not alarming — muted red
  text, not a red background flood on the whole row. The retry button
  (shown only on session failure) should use the muted amber/warning
  treatment, visually distinct from the primary navy action elsewhere.

### Accessibility (non-negotiable)

- Maintain **WCAG AA contrast** (4.5:1 for body text, 3:1 for large text)
  for every color pairing you introduce — verify the desaturated palette
  above against both the white card background and the near-white page
  background.
- Preserve all existing `formControlName`, `aria-*`, and semantic HTML
  (`<table>`, `<thead>`, `role="status"` on the spinner, etc.) exactly as
  present in the current markup — you're restyling, not restructuring
  semantics.
- Do not rely on color alone to convey status — keep the existing text
  labels ("Succeeded"/"Failed", the badge text itself) alongside color.

## Constraints — do not do these

- Do not add new npm dependencies (icon libraries, font-loading services,
  CSS frameworks) without explicitly flagging the addition and why
  Bootstrap alone can't achieve it.
- Do not change any `@Input()`/`@Output()` signatures, component
  selectors, or the `[ngSwitch]`/`*ngIf` structural logic in
  `upload-status.component.html`.
- Do not remove or rename existing CSS/SCSS class hooks if they're
  referenced from TypeScript (none currently are, but verify before
  renaming a class used only for a `[ngClass]` binding).
- Do not change table column content or component structure/order —
  visual restyling only.

## Deliverables

1. A new shared `src/styles/_finance-theme.scss` partial with Bootstrap
   `$variable` overrides (colors, `$border-radius`, `$font-family-base`,
   spacing if changed), plus the one-line instruction for where to import
   it relative to Bootstrap's own partials in `styles.scss`.
2. Updated `.scss` file for each of the six components listed above,
   using the shared theme variables rather than hardcoded hex values
   where possible.
3. Any `.html` template changes needed purely to hang new classes/wrapper
   elements for the styling above (e.g. a drop-zone wrapper around the
   file input) — keep these minimal and note each one.
4. A short summary at the end listing: every file touched, and one line
   per component on what changed visually and why it serves the
   "professional finance app" goal.