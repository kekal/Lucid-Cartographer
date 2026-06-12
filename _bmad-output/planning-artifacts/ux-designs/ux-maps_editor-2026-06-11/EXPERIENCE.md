---
name: LucidCartographer
status: final
sources:
  - {planning_artifacts}/prds/prd-maps_editor-2026-06-11/prd.md
  - {planning_artifacts}/prds/prd-maps_editor-2026-06-11/addendum.md
  - '{project-root}/_bmad-output/project-context.md'
updated: 2026-06-11
---

# LucidCartographer — Experience Spine

> How the product *works*: information architecture, behavior, states, interactions, accessibility, and journeys. Visual identity lives in `DESIGN.md`; tokens are cross-referenced as `{colors.x}`, `{components.x}`, etc. This spine **formalizes the implemented experience** and extends it to the new **Trip View** feature. Spine wins on conflict with any screen or mock.

## Foundation

**Form factor:** Self-hosted, single-user **web app** with two first-class surfaces — **desktop** (planning at home) and **mobile** (on the road). These are distinct render paths (`Viewport.IsMobile` → `Mobile*Screen`), split at the `{components.desktop-breakpoint}` (768px) breakpoint, not a single fluid layout. Every experience decision must land on both surfaces.

**UI system:** No third-party component library. The app is **Blazor Server** (`@rendermode InteractiveServer`) with a bespoke **Tailwind v3** design system (the `surface-*` / `on-surface-*` / `primary` token palette in `DESIGN.md`). Map rendering is **Leaflet** via JS interop. State follows a strict **Component → ViewModel → Service → Data** layering; components are thin markup/binding bridges and the ViewModel's `StateChanged` drives re-render. `DESIGN.md` is the visual reference; this spine specifies behavior.

**Posture:** A trustworthy power tool for a technical self-hoster. Privacy-first (coordinates stay in the deployment unless an opt-in out-calling provider is enabled and surfaced first). Honesty over polish — the product would rather show a humble "—" or an "Estimated" badge than a confident lie.

## Information Architecture

| Surface | Reached from | Purpose |
|---|---|---|
| **Map** (`/`) | App open · Map tab/nav | The home workbench: map + collections + POI list/detail. **Trip View lives here**, toggled within the filtered-results region. |
| **Data Sources** (`/datasources`) | Sources tab/nav | Import POIs: GPX/KML, Google Takeout, shared Google list, URL scrape. |
| **Operations** (`/operations`) | Operations tab/nav | Set ops across collections (subtract/intersect/union/symdiff), dedupe with tolerance. |
| **More** (`/more`) | Mobile "More" tab | Mobile-only: theme toggle, account, sign-out. (Desktop surfaces these in the header.) |
| **POI detail** | Row/marker tap | View/edit one POI: name, address, category, rating, enrichment, collection membership. |
| **Login** (`/login`) | Unauthenticated | Username/password (PBKDF2). Full-bleed, no chrome. |
| **Error** (`/Error`) | Unhandled error | Request ID + message. |

**Navigation:** desktop = sticky top header (`{components.header-height}` 64px) with nav links + global search + enrichment-status island + session/logout. Mobile = fixed **bottom tab bar** (Map · Sources · Operations · More), filled icon = active. Modals stack **one level deep**, never two.

**Collections model:** collections are toggled visible/hidden in the sidebar; **all visible collections union** onto the map and table — there is no single "current" collection selection. Trip View operates on the filtered result set of a collection.

→ Trip View is an **additive lens over an existing collection**, never a separate entity or a required step. The IA does not gain a "Trips" section; a collection simply gains a trip-shaped view.

## Voice and Tone

Microcopy rules. Brand voice/aesthetic lives in `DESIGN.md.Brand & Style`. All strings route through `UiStrings` (no hardcoded text) and are i18n-ready.

| Do | Don't |
|---|---|
| "Not placeable — no coordinates. Kept in the collection, excluded from the route." | Silently drop a POI that can't be routed |
| "Estimated" · "Measured" · "Manual" on a time | "≈14 min" with no provenance |
| "Back to hotel ~18:10 · Estimated (+8h05m)." | "Trip optimized!" |
| "Couldn't reach the routing engine — showing straight-line estimates." | "Routing error" |
| "X parsed • Y new • Z duplicates linked" | "Import done" |
| Plain, complete, factual sentences. | Exclamation marks, hype, false precision. |

The product never claims more certainty than it has. A number it didn't measure says so; a place it couldn't resolve says so.

## Component Patterns

Behavioral rules. Visual specs live in `DESIGN.md.Components`.

| Component | Use | Behavioral rules |
|---|---|---|
| **POI table** (`{components.table-row}` 44px, virtualized) | Map page list | Row click selects POI → detail. Clicks on checkbox/actions don't select (stopPropagation). Batch select → Move/Copy/Delete (disabled at 0 selected). |
| **Collection sidebar** | Map page | Each row toggles that collection's visibility (`aria-pressed`). No single-select; visible set unions onto map+table. Enter/Space activate. |
| **POI detail** | Desktop pane / mobile sheet | Inline-edit name (Enter saves, Esc cancels, Shift+Enter on mobile). Quick actions: Open in Maps, Focus on map, enrich, rename. Mobile sheet: Esc / Back button / browser-Back all close. |
| **File-upload panel** | Data Sources | Drag-drop or pick file; name + color the new collection; live feedback (parsed/new/duplicate counts). |
| **Trip View toggle** | Map filtered-results region | Switches the visible collection between plain and trip view. **Enabled only at ≥2 placeable POIs**; hidden/disabled below. State **persists per-collection** (decision OQ8) — reopening the collection restores trip on/off and stop order. |
| **Stop list** | Trip View on | Drag handle reorders stops; reorder triggers incremental map redraw + timeline recompute (no full reload). Each row: order badge, name, dwell field, timeline value. |
| **Travel-mode selector** | Trip View on | Per-trip: Any/Air · Drive · Walk · Cycle. Changing mode re-requests leg times (background). v1: one mode per trip — no per-leg ground/air mix. |
| **Ordering actions** | Trip View on | Three **on-demand, never automatic** paths: (1) manual drag; (2) **TSP-Sort** button — computes an efficient loop (p95 ≤ 3s for N≤30); (3) **MCP/AI** assignment by a connected agent (may also set Start/Finish/dwell). Any result stays freely drag-editable; manual edits stick. |
| **Start / Finish controls** | Trip View on | Designate a stop as Start (pinned order 1) and optionally Finish (pinned order N). Roundtrip (closed loop back to Start) is the default. |
| **Itinerary timeline** | Trip View on | Accumulation rules (FR-13): the **Start's dwell counts once, at the beginning**; each subsequent stop = prior arrival + leg travel + that stop's dwell; on a **roundtrip**, the closing leg produces a **distinct return-to-Start arrival** shown as the finish readout. Per stop: relative offset (always) + wall-clock (when start time set). Totals follow the aggregate honesty rule. |
| **Fidelity badge** | Each leg time | Reflects provenance: Measured / Estimated / Manual / Placeholder. **Any** leg (any travel mode) with no measured or manual time shows its time as **"—"** (decision OQ4 generalized), never a Placeholder badge in the user-facing slot. On the map, only Measured legs are solid; all others are dashed+muted (`DESIGN.md` — line solidity = geometric fidelity, not time-trust). |
| **Manual time entry** | Per leg | User can type a leg time (e.g. flight duration) → badge becomes **Manual** (trusted), recomputes timeline. The leg's map line stays dashed+muted (no road geometry); trust is carried by the badge, not the line. |
| **Recompute travel times** | Trip View on | Explicit user-initiated "Recompute travel times" action re-requests leg times from the active provider (background). When the provider returns real geometry, an Estimated leg **upgrades to Measured** (FR-10/FR-11) — its line goes solid, its badge updates, the timeline recomputes. The upgrade is never silent-on-a-stale-screen: it lands via `StateChanged`. |

## State Patterns

| State | Surface | Treatment |
|---|---|---|
| **Loading** | Any list/map | Centered spinner (`{colors.primary}` border) + `aria-live="polite"` label. |
| **Empty — no collections** | Sidebar | "No collections yet — Import data to get started." Link to Data Sources. |
| **Empty — no POIs** | Table | `location_off` icon + "No POI to display." |
| **Import success** | Data Sources | `{colors.secondary}` tint, `aria-live="polite"`: "X parsed • Y new • Z duplicates linked." |
| **Import / load error** | Data Sources / content | `{colors.tertiary}` tint, `aria-live="assertive"`, `error` icon + message. ErrorBoundary fallback offers "Try again." |
| **Enrichment pending** | Header island + POI row | Spinner + "Fetching details for new POIs. X / Y fetched." Per-POI `hourglass` (amber). Hides when queue drains. |
| **Enrichment failed** | POI row + fallback dialog | `error` (red) icon → "needs manual URL"; EnrichFallbackDialog accepts a pasted Google Maps URL. |
| **Trip View unavailable** | Filtered-results region | Below 2 placeable POIs, the toggle is hidden/disabled — never an error, just absent. |
| **Unplaceable POI** | Stop list | Flagged "Not placeable" — kept in the collection, **excluded from routing**, never silently dropped. |
| **Leg computing** | Map + timeline | Background compute; leg shows a pending state via `aria-live`; timeline marks affected values as provisional until `StateChanged` lands the result. Map redraw is incremental. |
| **Routing provider down** | Trip View | Graceful degradation to straight-line estimates; legs render dashed+muted, badges read **Estimated**, copy says estimates are approximate. The loop still orders. Recovery: a later **Recompute** upgrades Estimated→Measured (see Component Patterns). |
| **Mixed-fidelity total** | Timeline | A running total/arrival that sums legs of differing fidelity is qualified to the **lowest** among them (`~18:10 · Estimated`), never shown as a clean confident time (`DESIGN.md` aggregate honesty rule; protects SM-C2). |
| **Unplaceable stop in timeline** | Timeline | An unplaceable stop contributes **no travel time** but its **dwell time still accrues** into the running timeline (FR-13) — it occupies time, just not a routed leg. Never silently zeroed. |
| **Time-budget overrun** | Timeline | Optional soft `{colors.warn}` flag (amber, not red) when arrival exceeds the trip's **optional budget field**; informational, non-blocking. No flag when no budget is set. |
| **Focus** | Modals | Focus moves into the modal on open (back button / first field); Esc closes; focus is trapped while open. |

## Interaction Primitives

- **Tap/click to select; drag to reorder.** Stop reordering is direct drag; no separate "edit mode."
- **List ↔ map two-way sync.** Selecting a stop in the list pans/highlights its marker; clicking a marker scrolls its list row into view. This binding is a core trip interaction, not a nicety.
- **Toggles are explicit and reversible.** Trip View, collection visibility, and map labels are switches with clear on/off; turning Trip View off restores the plain collection intact (no data loss).
- **Ordering is always user-initiated.** TSP-Sort and MCP assignment run only when asked; the system never reorders stops on its own.
- **Mobile modal = full-screen slide-in**, integrated with browser history (Back closes it).
- **FAB map controls** (locate / fit-bounds / toggle-labels) float bottom-right, thumb-reachable on mobile.
- **Banned:** auto-reordering stops, silently dropping unplaceable POIs, showing an unmeasured time as if measured, sending coordinates to a third party without first surfacing it to the operator.

## Accessibility Floor

Behavioral accessibility (visual contrast targets live in `DESIGN.md`).

- **Live regions:** loading/computing/enrichment states use `aria-live="polite"`; errors use `aria-live="assertive"`. Trip leg/timeline recomputation announces via live region.
- **Labels:** stop-order badges, route legs, and timeline values carry descriptive `aria-label`s (a number on a pin is meaningless to a screen reader without one). Toggle controls expose `aria-pressed`; the Trip View toggle announces its on/off state.
- **Keyboard:** every interactive control is reachable and operable by keyboard. Collection rows: Enter/Space. Modals: Esc to close, focus trapped, focus returns on close. Inline name edit: Enter save / Esc cancel. **Stop reordering must have a keyboard-accessible path** (not drag-only) — `[ASSUMPTION]` keyboard reorder mechanism (e.g. move-up/move-down controls or arrow-key reordering) to be specified; flag for build.
- **Semantics:** landmark roles (`header`/`nav`/`main`), `role="dialog" aria-modal` on modals, `<table>` with `<th scope="col">`, `role="search"` on search.
- **Both surfaces:** every accessibility affordance must exist on **both** desktop and `Mobile*Screen` paths.
- **Targets:** mobile touch targets ≥ ~44px; honor safe-area insets so controls aren't clipped.

## Inspiration & Anti-patterns

What Trip View is — and pointedly is not:

| It is | It is not |
|---|---|
| A **lens** that orders an existing collection | A separate "Trips" entity or a wizard you must complete |
| An **honest** estimator that badges every number's provenance | A turn-by-turn navigator or a precise ETA engine |
| A **single-traveller** planner, desktop + phone | A collaborative/party planner or a fleet/logistics tool |
| **Self-hosted**, working out of the box with a mock provider | A product that silently calls metered third-party APIs |

**Counter-metrics (do not regress):** (SM-C1) collections must never feel like they *must* become trips — Trip View is optional and additive. (SM-C2) never chase false precision — a humble estimate beats a confident wrong number.

## Responsive & Platform

- **Desktop (≥768px):** map + side panel side-by-side; Trip View renders the stop list/timeline in the panel beside the map. Top header carries nav + search + enrichment status.
- **Mobile (<768px):** map (~46% top) over a bottom panel; Trip View's stop list and timeline occupy the bottom panel / a sheet. Bottom tab bar for navigation. Both paths must implement the Trip View toggle and the full ordering/timeline behavior — mobile is the explicit on-the-road scenario (UJ-3), not a degraded view.
- Map attribution (OSM/ODbL) must be visible on both surfaces when an OSM-based provider is active.

## Key Flows

### Flow 1 — Yurik turns a weekend's saved spots into a loop (desktop)
Yurik, the maintainer, has ~12 enriched POIs in his "Lisbon weekend" collection open on his desktop at home.
1. In the collection's filtered-results region he flips **Trip View on**. The pins gain numbered **stop badges**, connecting **legs** draw between them, and a side panel appears with per-leg drive times and a running timeline.
2. He sets his hotel as **Start** (pins to order 1) and leaves Finish blank — **roundtrip** by default.
3. He **drags two stops** to reorder; the map redraws incrementally and the timeline recomputes live. Most legs show a **Measured** badge (solid lines); one hop with no road data reads **Estimated** (dashed + muted).
4. **Climax:** the timeline shows the loop fits the day — relative offsets accumulate to `+8h05m` and, because he set a 10:00 start, the wall-clock readout says **back to hotel by ~18:10**, qualified **Estimated** because that one soft leg feeds the total. Honest, and still clearly within the day.
5. He closes the collection and reopens it later — **Trip View and his stop order persist** (per-collection).
- *Edge:* one POI has no coordinates → listed as **"Not placeable,"** excluded from the route with a clear flag, never silently dropped.

### Flow 2 — Mara lets the AI order a messy collection, then tweaks (desktop)
Mara has dumped 25 attractions in import order — a zig-zag mess — into one collection.
1. She turns on Trip View, picks **Drive**, sets a start hotel.
2. She taps **TSP-Sort**; within ~3s the stops reorder into an efficient loop and the map redraws.
3. She asks her connected **MCP AI agent**: "put the museums in the morning and the rooftop bar last." The agent assigns a new **Stop Order** honoring the constraints.
4. **Climax:** she hand-nudges two stops; the **manual order sticks** — no system reshuffle undoes her edit. The trip is hers, the tools only assisted.
- *Edge:* the routing engine is down → times fall back to **straight-line estimates**, legs render dashed+muted with **Estimated** badges, copy notes they're approximate, and the loop still orders.

### Flow 3 — Priya checks travel feasibility on her phone, on the road (mobile)
Priya is mid-trip on her phone, her trip in **Any/Air** mode.
1. She adds two POIs; they appear as **unordered new stops** at the end.
2. She taps **TSP-Sort**; the timeline recomputes. Ground hops show **Estimated** badges; the Air/Any legs with no manual time show their time as **"—"** (honest blank, decision OQ4).
3. She sets the airport POI as **Finish** and types a **Manual** travel time for the flight leg (e.g. 2h20m) → that leg's badge becomes **Manual** and is trusted; short ground hops stay **Estimated**.
4. **Climax:** the timeline shows airport arrival with **40 min of slack** — feasible. She trusts the Manual flight figure and reads the Estimated hops as rough, exactly as the badges tell her to.
- *Edge:* adding one more stop pushes arrival **past flight time** → the timeline raises a soft **`{colors.warn}` overrun flag**, so she drops a stop. *(v1 limit: she can't mix Drive ground hops with Air in one trip — per-leg mode override is deferred.)*

---

## Open items for build
- **[ASSUMPTION] — build-blocking for a11y:** Keyboard-accessible stop reordering mechanism not yet specified (drag is the primary path). Decide: move-up/down buttons vs arrow-key reorder. Must satisfy the Accessibility Floor before Trip View ships.
- **Phase note:** legs are straight-line in Phase 1; real road geometry (Drive/Walk/Cycle) arrives in Phase 2. Dashed+muted styling already distinguishes non-Measured, so the Phase 1→2 transition is visually continuous.
- **Provider egress (firm rule, placement TBD):** enabling any out-calling routing provider **must** surface an explicit consent/notice to the operator **before the first out-call** — this guard is non-negotiable (privacy posture / Banned-behavior). Only the *placement and exact copy* of the consent are deferred to build; the requirement itself is not.
- **Time budget:** the optional per-trip budget is a settable field; the `warn` overrun flag fires only when a budget is set.
