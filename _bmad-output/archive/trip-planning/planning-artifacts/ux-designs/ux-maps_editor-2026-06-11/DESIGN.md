---
name: LucidCartographer
description: Self-hosted map workbench for organizing geographic POIs into collections — and now into ordered trips. Calm, dense, trustworthy. Material-3-derived palette, dual desktop/mobile surfaces, dark mode first-class.
status: final
updated: 2026-06-11
colors:
  primary: '#005bbf'
  on-primary: '#ffffff'
  primary-container: '#1a73e8'
  secondary: '#006e2c'
  secondary-container: '#86f898'
  tertiary: '#b81d17'
  warn: '#9a4a08'
  surface: '#f7f9ff'
  surface-container-low: '#f1f4fa'
  surface-container: '#ebeef4'
  surface-container-high: '#e5e8ee'
  surface-container-highest: '#dfe3e8'
  on-surface: '#181c20'
  on-surface-variant: '#414754'
  on-surface-muted: '#5e6470'
  outline: '#727785'
  outline-variant: '#c1c6d6'
  surface-dark: '#0f1115'
  surface-elev-dark: '#161a20'
  surface-elev-2-dark: '#1c2128'
  surface-elev-3-dark: '#242a33'
  on-surface-dark: '#e8eaef'
  on-surface-variant-dark: '#b4b9c4'
  on-surface-muted-dark: '#98a0ad'
  primary-dark: '#6aa1e8'
  secondary-dark: '#6edc96'
  tertiary-dark: '#ff786e'
  warn-dark: '#f7c87a'
typography:
  headline:
    family: 'Manrope'
    weights: '400 / 700 / 800'
    note: 'Page titles, section headings, nav links, card titles'
  body:
    family: 'Inter'
    weights: '400 / 500 / 600'
    note: 'Default UI text, secondary text, labels'
  mono:
    family: 'system monospace'
    note: 'Coordinates, leg distances, code-like values'
  icon:
    family: 'Material Symbols Outlined'
    note: 'FILL 0, wght 400, GRAD 0, opsz 24'
  fallback:
    note: "system-ui, -apple-system, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif — when the Google Fonts CDN is blocked"
rounded:
  sm: 8px
  md: 14px
  lg: 20px
  xl: 28px
spacing:
  '1': 4px
  '2': 8px
  '3': 12px
  '4': 16px
  '5': 24px
  '6': 32px
components:
  header-height: 64px
  desktop-breakpoint: 768px
  table-row: 44px
---

# LucidCartographer — Design Spine

> Visual identity for a self-hosted Blazor-Server map workbench. This spine **formalizes the already-implemented design system** (`LucidCartographer/tailwind.config.js`, `wwwroot/css/base.css`, `wwwroot/css/mobile.css`) and extends it to the new **Trip View** feature. Paired with `EXPERIENCE.md`. The spine wins on conflict with any mock, screen, or stylesheet — where code and spine disagree, reconcile to the spine or amend the spine deliberately.

## Brand & Style

LucidCartographer is a workbench, not a consumer app. It is a tool a single self-hoster runs on their own box to wrangle hundreds of saved places — and now to turn a saved collection into a thought-through trip. The personality is **calm, dense, and trustworthy**: the map and the data are the subject; the chrome stays out of the way.

Three commitments shape every visual decision:

1. **The data is the hero.** Surfaces are quiet near-white (light) or deep slate (dark) so that map pins, route lines, and POI rows carry the color. UI chrome never competes with cartography.
2. **Density without clutter.** This is a power tool handling long lists. Tight, legible rows; virtualized tables; a clear type hierarchy that lets the eye scan. Generous where it aids comprehension (panels, modals), tight where volume demands it (tables, stop lists).
3. **Honest signaling.** The product never dresses up a guess as a fact. Travel-time **Fidelity badges**, dashed estimate lines, and explicit empty markers are first-class visual citizens. Trust is a design material here, not an afterthought.

The aesthetic lineage is **Material 3** — a tonal surface ramp, a single confident primary, semantic secondary/tertiary — adapted into a denser, more utilitarian register. Dark mode is first-class, not a bolt-on.

## Colors

A Material-3-derived semantic palette. Light is the default working surface; **dark mode is fully supported and first-class** (most planning happens at night). Tokens are referenced by name throughout `EXPERIENCE.md` as `{colors.token}`.

**Brand & action**
- **Primary — Deep Blue (`#005bbf` light / `#6aa1e8` dark).** The single confident accent: primary actions, active nav, selection, the Trip View toggle when on. `primary-container #1a73e8` for lighter primary fills.
- **Secondary — Forest Green (`#006e2c` light / `#6edc96` dark).** Success and "enriched/confirmed" states (e.g. a resolved POI, an import completed). `secondary-container #86f898` for soft fills.
- **Tertiary — Red (`#b81d17` light / `#ff786e` dark).** Errors, destructive confirmation, "needs attention" (e.g. a POI that failed to resolve and needs a manual URL).
- **Warn — Amber (`#9a4a08` light / `#f7c87a` dark).** Pending/in-progress and soft cautions (enrichment computing, time-budget overrun). Distinct from tertiary error — caution, not failure.

**Surfaces (light)** — a tonal ramp, lightest to most-raised:
`surface #f7f9ff` → `surface-container-low #f1f4fa` → `surface-container #ebeef4` → `surface-container-high #e5e8ee` → `surface-container-highest #dfe3e8`.

**Surfaces (dark)** — `surface #0f1115` → `elev #161a20` → `elev-2 #1c2128` → `elev-3 #242a33`.

**Text**
- `on-surface #181c20` (light) / `#e8eaef` (dark) — primary text.
- `on-surface-variant #414754` (light) / `#b4b9c4` (dark) — secondary text.
- `on-surface-muted #5e6470` (light) / `#98a0ad` (dark) — tertiary/least-emphasis text. **Note:** `#5e6470` is the AA-tuned replacement for the old `#727785`, which failed 4.5:1 on light containers; use the muted token, not raw `outline`, for muted text.

**Lines** — `outline #727785` (standard border), `outline-variant #c1c6d6` (hairline divider).

Avoid: introducing new accent hues for decoration; using `tertiary` (error red) for anything that is not an error or destructive action; using raw `outline` as a text color on light surfaces (fails contrast — use `on-surface-muted`).

## Typography

Two families, sharply divided by role — this division is the type system:

- **Manrope (headline)** — weights 400/700/800. Page titles (`text-2xl` / 1.5rem, weight 800, `tracking-tight`), section headings (`text-lg` weight 700), nav links, card titles. Labels and table headers: `text-xs` / 0.75rem, weight 600, **uppercase, `tracking-wider`**.
- **Inter (body)** — weights 400/500/600. Body text (`text-sm` 0.875rem / `text-base` 1rem, weight 400), secondary text in `on-surface-variant`, small labels (`text-xs` weight 500/600).
- **System monospace** — coordinates, per-leg distances, any code-like value. Pairs with `text-xs` / `text-sm`.
- **Material Symbols Outlined** — icons, `FILL 0, wght 400, GRAD 0, opsz 24`. Filled variant (`FILL 1`) for active/selected states (e.g. active mobile tab, filled rating star).

Line height: 1.5 default; 1.4 in dense mobile modal body; 1.3 for tiny map labels (10px). Display sizes are not used — the largest text is the page title. The fallback stack (`system-ui …`) must render legibly if Google Fonts is blocked; never rely on a webfont for legibility-critical layout.

## Layout & Spacing

Scale: **4 / 8 / 12 / 16 / 24 / 32 px** (`spacing.1`–`spacing.6`). Largest gaps between major regions (map vs panel); smallest between tightly-coupled elements (a stop badge and its label).

**Two distinct surfaces, one breakpoint at 768px:**
- **Desktop (≥768px):** sticky top header (`header-height 64px`), then a two-region working area — typically map on one side, list/detail panel on the other. Fixed-width side panels (e.g. `w-80` / 320px config rail on Operations); content fills the rest with `overflow-auto`. Card grids `grid-cols-1 → md:grid-cols-2/3`.
- **Mobile (<768px):** no top header; a fixed **bottom tab bar**. Screens are full-bleed, single-column. The map page splits map (~46% top) over a bottom panel. **Safe-area insets** (`env(safe-area-inset-*)`) are honored on header padding and the tab bar — content never hides under a notch or home indicator.

Desktop and mobile are **distinct render paths** (`Viewport.IsMobile` → `Mobile*Screen`), not a single fluid layout. A change to a page's UI must be made on both paths.

## Elevation & Depth

A **three-tier shadow system** carries elevation, used sparingly and reserved for genuinely floating surfaces:
- **`shadow-1`** — resting raised elements (cards): `0 1px 2px rgba(16,24,40,.06), 0 1px 3px rgba(16,24,40,.10)`.
- **`shadow-2`** — interactive/hover and small popovers: `0 4px 12px rgba(16,24,40,.08), 0 2px 4px rgba(16,24,40,.04)`.
- **`shadow-3`** — modals, FAB stacks, the highest layer: `0 12px 32px rgba(16,24,40,.18), …` (light) / heavier black-based shadow in dark mode.

Primary hierarchy comes from the **surface tonal ramp**, not shadow — a raised panel is distinguished first by sitting on `surface-container*`, then by a shadow if it truly floats. The desktop header uses `bg-white/80 backdrop-blur-md` with a subtle `shadow-sm`. Modals sit on a `bg-black/40` scrim at the top z-layer.

## Shapes

Rounded scale: **`sm 8 / md 14 / lg 20 / xl 28` px.**
- `sm 8px` — inputs, table rows, small chips, pills.
- `md 14px` — buttons, smaller cards, dropdowns.
- `lg 20px` — cards, panels, modal sheets.
- `xl 28px` — large mobile sheets, hero surfaces.

Collection color dots and rating stars are the only true circles. Map markers are pin-shaped (numbered for trip stops). Imagery (POI hero images) follows its container's corner radius exactly. No fully-pill buttons by default; pills are reserved for status chips and category tags.

## Components

Visual specifications. Behavioral rules live in `EXPERIENCE.md.Component Patterns`.

**Existing (formalized)**
- **Button** — primary: `bg-primary text-on-primary`, `rounded-md`, `text-sm` weight 600, `shadow-sm` → `shadow` on hover. Secondary/ghost: transparent or `surface-container` fill, `on-surface` text, weight 500. Destructive: `tertiary`. Disabled: reduced opacity + `cursor-not-allowed`, label retained.
- **POI table row** — `44px` row (`table-row`), sticky header on `surface`, virtualized. Columns: select checkbox · location (name + address + enrichment-state icon) · coordinates (mono) · collection chips · added date · row actions. Hover `surface-container-low`; selected `surface-container`. Enrichment-state icon: `location_on` green (enriched) · `hourglass` amber (pending) · `error` red (needs manual URL).
- **Collection sidebar row** — color dot · name · count · visibility toggle. `role="button"`, `aria-pressed`. No single-selection — all visible collections union onto the map/table.
- **POI detail pane / mobile detail** — desktop right sidebar; mobile full-screen sheet with hero (image or colored scrim, striped fallback). Inline-editable name, address, collection chips (`bg-primary/10 text-primary`), category pill (`surface-container-high`), Google rating (filled stars), quick actions.
- **Chip / pill** — `rounded-sm`, `text-xs`. Collection chip `bg-primary/10 text-primary`; category pill `surface-container-high`; status pill tinted by semantic color (`warn` for "Enriching…", etc.).
- **Cards (sources/operations)** — `rounded-lg p-5/6`, `shadow-1`, `surface` or `surface-container-low`.
- **File-upload panel** — dashed drop zone (`border-2 border-dashed outline-variant` → `border-primary bg-primary/5` on hover), color-picker circles (selected: `ring-2 ring-primary scale-110`).
- **Modal** — desktop: centered card (`rounded-xl shadow-3`, `w-80`) on `bg-black/40`. Mobile: full-screen slide-in sheet, `role="dialog" aria-modal`.
- **FAB stack** — bottom-right on map: locate · fit-bounds · toggle-labels. Circular, `shadow-3`.
- **Leaflet map** — fills its region; numbered pin markers; OSM attribution shown when an OSM-based tile/route provider is used (ODbL).

**New — Trip View (extends the system)**
- **Trip View toggle** — a switch living in the collection's filtered-results region (not a menu). Off = plain collection. On = `primary`-accented active state. Visible/enabled only at ≥2 placeable POIs.
- **Stop-order badge** — small numbered circle pinned to each stop, both in the list and on the map marker. `primary` fill, `on-primary` numeral, `rounded` full. Start uses a distinct glyph/ring; Finish likewise. `text-xs` weight 700.
- **Stop list row** — drag handle · order badge · POI name · dwell-time field · running timeline value. Reorderable; visually echoes the POI table row but trip-scoped.
- **Route leg (map)** — connector between consecutive stops. **Line solidity tracks _geometric_ fidelity, not time-trust:** only **Measured** (the system has real road geometry) renders **solid, full-weight, `primary`**. Every other state — **Estimated, Manual, Placeholder, and Air** — renders **dashed AND muted** (lighter `primary` / reduced-opacity stroke), because the system lacks real geometry for them. A Manual flight time is trusted via its *badge*, never via a solid line that would imply road data the system doesn't have. Air is a dashed great-circle line. The closing roundtrip leg uses the same language.
- **Fidelity badge** — a small pill on each leg's time, one of: **Measured** (`secondary`/confirmed tone) · **Estimated** (neutral `on-surface-muted`) · **Manual** (`primary` — user-entered, trusted) · **Placeholder** (least emphasis; internal-only — never the user-facing leg-time slot; an unmeasured/unentered leg shows **"—"** instead). `text-xs`, never larger than the time it qualifies.
- **Itinerary timeline** — per-stop, shows **both**: relative cumulative offset (e.g. `+2h15m`, always) and wall-clock arrival (e.g. `14:10`, only when a trip start time is set), with finish/return time at the end. **Aggregate honesty rule:** a running total (and the final arrival) **inherits the lowest fidelity among the legs it sums** — if any contributing leg is Estimated, the total is shown qualified (e.g. `~18:10 · Estimated`), never as a clean confident time. A total is only as trustworthy as its softest leg. A soft **time-budget overrun** flag uses `warn`, not `tertiary`; the budget itself is an optional per-trip field.
- **Travel-mode selector** — per-trip choice: Any/Air · Drive · Walk · Cycle. Segmented control, `primary` active segment.

## Do's and Don'ts

| Do | Don't |
|---|---|
| Let map pins, route lines, and data rows carry the color; keep chrome quiet | Introduce decorative accent hues that compete with cartography |
| Use the surface tonal ramp for hierarchy first, shadow only for floating layers | Stack shadows to fake depth on resting surfaces |
| Reserve `tertiary` red strictly for errors and destructive actions | Use error-red for warnings, pending states, or emphasis |
| Use `warn` amber for pending/caution (enrichment, overrun) | Conflate caution (amber) with failure (red) |
| Render **any unmeasured/unentered leg time as an em-dash "—"** (any travel mode) | Show a "Placeholder" badge in the user-facing leg time, or fabricate a number (decision OQ4: em-dash, quieter) |
| Render **only Measured legs solid**; Estimated/Manual/Placeholder/Air all dashed + muted | Render a Manual or Estimated leg as a solid line implying road geometry the system lacks |
| Show relative offset always, wall-clock when a start time exists | Imply a precise clock time when no start time was given |
| Qualify a running total/arrival with the **lowest fidelity it sums** (`~18:10 · Estimated`) | Print a clean confident total over a mix of Measured + Estimated + Manual legs |
| Use `on-surface-muted #5e6470` for muted text (AA-tuned) | Use raw `outline #727785` as a text color on light surfaces (fails contrast) |
| Update both desktop and `Mobile*Screen` paths for any UI change | Assume one fluid layout covers both surfaces |
| Honor safe-area insets on mobile header and tab bar | Let content hide under a notch or home indicator |
