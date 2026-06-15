---
title: Trip Planning for Collections (LucidCartographer)
status: final
created: 2026-06-11
updated: 2026-06-11
---

# PRD: Trip Planning for Collections
*Working title — confirm.*

## 0. Document Purpose

This PRD is for the LucidCartographer maintainer (acting PM) and the downstream BMad workflows it feeds — `bmad-ux`, `bmad-create-architecture`, and `bmad-create-epics-and-stories`. It specifies a **launch-grade** capability that turns an existing POI **Collection** into a viewable, ordered **Trip** without introducing a new top-level entity. Vocabulary is anchored in the Glossary (§3); features are grouped with globally-numbered FRs nested under them; assumptions are tagged inline `[ASSUMPTION]` and indexed in §9. Technical mechanism, rejected alternatives, and the routing-engine comparison live in the companion **`addendum.md`** — this PRD stays at the capability level and does not duplicate them.

## 1. Vision

LucidCartographer already lets people gather places into Collections on a map. The missing step is *intent*: a collection of pins says "here are places I care about," but a traveller needs "here is the order I'll visit them, how long each hop takes, how long I'll linger, and what the loop looks like on the map." Trip Planning closes that gap — **as a lens over the collection you already have, not a new thing to learn.**

Flip a single **Trip View** toggle and the collection comes alive: stops gain a visiting order, connecting lines trace the route across the map, each leg shows real travel time, each stop shows dwell time, and a running timeline tells you when you'll arrive where. Flip it off and you're back to a plain collection. A designated **Start** and **Finish** make every trip an honest roundtrip. And because the app already speaks **MCP**, an AI agent can do the tedious part — ordering the stops into a sensible loop and respecting soft constraints like opening hours — then hand control back to you.

It matters because it converts LucidCartographer from a *place keeper* into a *trip thinker* with minimal new surface area, staying true to the self-hosted, no-lock-in ethos: a built-in estimate works out of the box, and real road times come from a routing provider you enable — never a mandatory metered third-party API.

## 2. Target User

### 2.1 Jobs To Be Done
- **Functional:** "Take the places I've already saved and see them in a sensible visiting order, on the map, as a loop I can actually drive/walk/cycle."
- **Functional:** "Know how long the hops between places take, and how long I'll spend at each, so I can tell if the day fits."
- **Functional:** "Not have to manually figure out the best order — let the tool (or an AI) propose it, then let me tweak."
- **Contextual:** "Do all of this on my own self-hosted instance, on desktop at home and on my phone on the road, without sending my data to a metered API."
- **Emotional:** "Trust that the loop is complete — it starts and ends where I want, nothing stranded."

### 2.2 Non-Users (v1)
- Logistics/fleet operators needing time-window VRP, vehicle capacities, or multi-vehicle dispatch — this is single-traveller itinerary planning.
- Turn-by-turn live navigation users — this plans and visualizes; it does not narrate driving.
- Collaborative/real-time multi-editor trip parties — out of scope (see §5).

### 2.3 Key User Journeys

- **UJ-1. Yurik turns a weekend's saved spots into a loop.**
  - **Persona + context:** Yurik, the maintainer, has a "Lisbon weekend" collection of ~12 POIs already enriched. He wants to see them as a drivable day.
  - **Entry state:** Authenticated, viewing the collection on desktop, Trip View **off**.
  - **Path:** Toggles **Trip View on** → the map draws numbered stops and connecting lines; a side panel lists stops in order with per-leg drive times and a running timeline. He sets his hotel as **Start**, leaves **Finish** blank (roundtrip). He drags two stops to reorder, watching times update.
  - **Climax:** The timeline shows the loop fits in a day (returns to the hotel by 18:10); the map shows a clean closed loop.
  - **Resolution:** Trip persists; reopening the collection later restores the order, start/finish, and times. **Edge case:** one POI has no coordinates → it's listed as "not placeable" and excluded from the route with a clear flag, not silently dropped.

- **UJ-2. Mara lets the AI order a messy collection, then tweaks.**
  - **Persona + context:** Mara dumped 25 attractions into a collection with no thought to order.
  - **Entry state:** Authenticated, Trip View on, stops in import order (a zig-zag mess on the map).
  - **Path:** Picks **Drive** mode, sets a start, taps **TSP-Sort**. The system computes the travel-time matrix and reorders the stops into an efficient loop; the map redraws. She then asks her connected AI agent (via MCP) to "put the museums in the morning and the rooftop bar last" — the agent assigns a new Stop Order honoring those constraints; she can re-run TSP-Sort or hand-tweak from there.
  - **Climax:** A 25-stop tangle becomes a coherent loop in seconds, honoring both distance and her preferences.
  - **Resolution:** She nudges two stops by hand; her manual order sticks. **Edge case:** the routing engine is down → the system falls back to straight-line estimates, labels the times as approximate, and still orders the loop.

- **UJ-3. Priya checks travel feasibility on her phone, on the road.**
  - **Persona + context:** Priya, mid-trip, wonders if she can squeeze in two more stops before her evening flight.
  - **Entry state:** Authenticated on mobile, Trip View on, Trip in **Any/Air** mode (v1 is single-mode per Trip — see §6.2).
  - **Path:** Adds the two POIs; they appear as unordered new stops. She taps TSP-Sort; the timeline recomputes. She sets the airport POI as **Finish** and enters a **Manual** Travel Time for the flight leg (her known flight duration); the short ground hops stay at **Estimated** Fidelity.
  - **Climax:** The timeline shows airport arrival with 40 minutes of slack — feasible. The number she trusts most (the flight leg) is Manual; she reads the short Estimated hops as the rough figures they're badged to be.
  - **Resolution:** She heads out. **Edge case:** adding the stops pushes airport arrival past flight time → the timeline flags the overrun so she drops one stop. *(v1 note: she cannot mix a Drive mode for the ground hops with Air in the same Trip — per-Leg mode override is deferred, §6.2.)*

## 3. Glossary

*Downstream workflows and readers must use these terms exactly. FRs, UJs, and SMs use them verbatim; no synonyms anywhere else in the PRD.*

- **POI** — A point of interest: an existing LucidCartographer place with an optional latitude/longitude. Unchanged by this PRD.
- **Collection** — An existing ordered-or-unordered grouping of POIs. This PRD adds trip semantics *to* the Collection; it is the single owning entity.
- **Trip** — A Collection viewed through trip semantics (ordered Stops, Start/Finish, travel/dwell times, timeline). Not a separate entity — same Collection, additional fields and a view. One Collection ⇔ one Trip.
- **Trip View** — The UI mode that reveals trip semantics (order numbers, connecting Legs, times, timeline). Toggling it **off** shows the plain Collection; toggling it **on** reveals the Trip. A display toggle, not a separate object.
- **Stop** — A POI in its role as an ordered member of a Trip. Carries a **Stop Order** and an optional **Dwell Time**. Within a Trip, Stop ↔ POI is 1:1 — the Collection–POI membership key prevents a POI appearing twice — so a Stop is uniquely identified by its POI. Every Stop is a POI; not every POI in a Collection need be routable (a Stop without coordinates is **Unplaceable**).
- **Stop Order** — The integer sequence (1..N) defining visiting order of Stops within a Trip: contiguous, gap-free, one Stop per value.
- **Start Stop** — The Stop at Stop Order 1. Designating a Start *pins* a Stop to Order 1.
- **Finish Stop** — For an **open-path** Trip, the Stop at Stop Order N, pinned there. A **Roundtrip** has no separate Finish Stop.
- **Roundtrip** — A Trip with no distinct Finish Stop: after the last Stop (Order N) the route returns to the Start (Order 1), closing the loop via a return **Leg** — not by giving any Stop two Stop Order values. The default trip shape.
- **Leg** — A directed hop between two consecutive Stops. Carries a computed **Travel Time** and distance, and optional route geometry.
- **Travel Mode** — One of **Any/Air**, **Drive**, **Walk**, **Cycle**, selected per Trip. Determines how Legs are computed.
- **Travel Time** — Duration of a Leg under the Trip's Travel Mode, supplied by the **Travel-Time Provider** and carrying a **Fidelity**.
- **Travel-Time Provider** — The pluggable component that supplies `(duration, distance, Fidelity)` for an ordered `(FromStop, ToStop, Travel Mode)`. Which provider is active is a per-deployment configuration choice — the FRs depend on this contract, not on any specific engine. Candidate providers: a haversine **Mock** (shipping default), **OSRM** (self-hosted road routing), a **Google-Maps scrape**, and **Manual** entry. (Provider catalogue: `addendum.md`.)
- **Fidelity** — The trustworthiness label every Travel Time carries: **Measured** (real road routing), **Estimated** (straight-line × assumed speed), **Placeholder** (Any/Air with no manual time), or **Manual** (user-entered). The UI badges Travel Times by Fidelity so a number is never mistaken for more than it is.
- **Dwell Time** — User-set duration spent at a Stop. An overnight is just a large Dwell Time on a hotel Stop (there is no day/night concept — see §5).
- **Itinerary Timeline** — The accumulated schedule: optional start time + Σ(Travel Time + Dwell Time) along the Stop Order, yielding an arrival time per Stop.
- **Routing Engine** — A **Measured**-fidelity Travel-Time Provider that returns road durations and geometry for Drive/Walk/Cycle (OSRM is the reference implementation). One provider option, not a hard dependency.
- **Haversine Fallback** — The **Estimated**-fidelity provider: straight-line distance × an assumed mode speed. Used for **Any/Air** Legs and whenever a Measured provider is unavailable or out of coverage. Also the shipping-default **Mock** provider.
- **TSP-Sort** — The on-demand action (FR-15) that reorders Stops to minimize total Travel Time, pinning Start and Finish.
- **Distance Matrix** — The all-pairs (N×N) Travel-Time table among a Trip's placeable Stops, computed on demand as the input to TSP-Sort. Distinct from the consecutive **Legs** shown to the user (the N or N−1 hops along the Stop Order); both draw from the same per-`(FromStop, ToStop, Travel Mode, Provider)` cache.

## 4. Features

### 4.1 Trip View Toggle & Stop Ordering

**Description:** A persistent **Trip View** toggle on the Collection page. Off → today's plain Collection. On → the Collection renders as a **Trip**: Stops show **Stop Order** numbers, a stop list/panel appears, and the map gains connecting Legs (§4.2) and times (§4.3). Stops can be reordered by drag, and any POI lacking coordinates is shown as **Unplaceable** and excluded from routing without being removed from the Collection. Realizes UJ-1.

**Functional Requirements:**

#### FR-1: Toggle Trip View on a Collection
A user can toggle Trip View on/off for any Collection. Realizes UJ-1.

**Consequences (testable):**
- With Trip View off, no Trip affordances are visible — no Stop Order numbers, no Legs, no timeline; the page presents the same POI set and controls as the pre-feature Collection view. (The toggle control itself, and a one-time Stop Order seed written on first toggle-on per FR-2, are the only additions.)
- With Trip View on, Stops render with Stop Order badges and the trip panel is shown.
- The toggle state is restored when the Collection is reopened. `[ASSUMPTION: toggle state is persisted per Collection, not per user/session.]`
- Toggling does not modify, reorder, or delete any POI membership.

#### FR-2: Establish and persist Stop Order
The system assigns and persists a Stop Order (1..N) for the Stops of a Trip. Realizes UJ-1, UJ-2.

**Consequences (testable):**
- A Collection that has never had a Stop Order receives a deterministic seed order on first Trip-View open. `[ASSUMPTION: seed order is by existing POI added-date ascending.]`
- Stop Order is contiguous (no gaps) and unique within a Trip.
- Stop Order persists across reloads and survives the Collection being edited elsewhere (adding a POI appends it as the new last Stop; removing a POI re-compacts the order).

#### FR-3: Manual reorder by drag
A user can drag a Stop to a new position; the Stop Order and all dependent views update. Realizes UJ-1, UJ-2.

**Consequences (testable):**
- Dropping a Stop renumbers the affected range and immediately updates Legs (§4.2), Travel Times (§4.3), and the Itinerary Timeline (§4.4).
- A manual reorder overrides any prior TSP-Sort result and is persisted.
- A pinned Start (Order 1) and Finish (Order N) keep their slots: drag reorders the **interior** Stops only, and dropping a Stop at the first/last position does **not** transfer the Start/Finish role to it — that role changes only via FR-14.

#### FR-4: Flag Unplaceable Stops
The system identifies Stops without usable coordinates and excludes them from routing while keeping them in the Trip. Realizes UJ-1 (edge case).

**Consequences (testable):**
- A Stop with null latitude or longitude is labelled **Unplaceable** in the stop list and is not drawn on the map or included in any Leg or the Distance Matrix.
- Unplaceable Stops do not break Stop Order numbering of the remaining Stops, and the timeline computes over placeable Stops only.

#### FR-17: Discoverable Trip View toggle
The Trip View toggle is a visible control in the Collection view's **filtered-results region**, not buried in a menu. Realizes UJ-1.

**Consequences (testable):**
- The toggle is rendered in the filtered-results region of the Collection view (the same region that shows the current POI result set).
- It is present/enabled on Collections with ≥2 placeable POIs; on Collections with fewer it may be hidden or disabled. `[ASSUMPTION: ≥2 placeable POIs is the visibility threshold.]`
- Both desktop and mobile render paths expose the toggle.

**Feature-specific NFRs:**
- Desktop and mobile render paths (`Viewport.IsMobile` → `Mobile*Screen`) must both implement Trip View per project convention.

### 4.2 Route Visualization on the Map

**Description:** With Trip View on, the Leaflet map draws **Legs** connecting consecutive Stops in Stop Order, with numbered markers. Phase 1 draws straight connectors; Phase 2 draws real road geometry for Drive/Walk/Cycle from the Routing Engine. Any/Air Legs are always straight (great-circle) lines by design. Realizes UJ-1, UJ-2.

**Functional Requirements:**

#### FR-5: Draw ordered connecting Legs
The map draws a line for each Leg between consecutive Stops in Stop Order, including the closing Leg of a Roundtrip. Realizes UJ-1.

**Consequences (testable):**
- N placeable Stops in a Roundtrip produce N Legs (closing the loop to Start); a Start≠Finish Trip produces N−1 Legs.
- Reordering Stops (FR-3) or TSP-Sort (FR-15) redraws Legs to match the new order without a full page reload.
- Markers display their Stop Order number.

#### FR-6: Road-shaped geometry when available
For Drive/Walk/Cycle Legs, when the active provider returns road geometry (Measured), the map draws it; otherwise it draws a straight connector. Realizes UJ-2.

**Consequences (testable):**
- When the provider returns geometry for a Leg, the drawn line follows roads; when it does not (no Measured provider, provider down, out-of-coverage, or Any/Air mode), a straight connector is drawn instead.
- Straight (non-Measured) connectors are visually distinguishable from road geometry, consistent with the Leg's Fidelity badge. `[ASSUMPTION: distinction is via line style, e.g. dashed for non-Measured Legs.]`

#### FR-7: Map and list stay in sync
Selecting a Stop in the list highlights it on the map and vice versa. Realizes UJ-1.

**Consequences (testable):**
- Selecting a list Stop pans the map so that Stop's marker is within the viewport and visually emphasized (distinct from unselected markers); the selection clears when another is chosen.
- Clicking a marker scrolls its list row into view and emphasizes it.
- This reuses the existing marker-click interop without regressing current popup/tooltip behavior.

### 4.3 Travel Time & Distance Computation

**Description:** For a Trip's Stops, the system obtains per-Leg **Travel Time** and distance under the selected **Travel Mode** from the configured **Travel-Time Provider**, and badges each value with its **Fidelity**. The app depends on the provider *contract* — `(duration, distance, Fidelity)` for a `(FromStop, ToStop, Travel Mode)` — not on any specific engine: the shipping default is a haversine **Mock** (Estimated fidelity), and a deployment may swap in a Measured provider (OSRM), a scrape, or rely on Manual entries. Results are cached and recomputed when inputs change. Computation runs as a background job (mirroring the enrichment pattern). Realizes UJ-1, UJ-2, UJ-3.

**Functional Requirements:**

#### FR-8: Select Travel Mode per Trip
A user can set the Travel Mode for a Trip to Any/Air, Drive, Walk, or Cycle. Realizes UJ-2, UJ-3.

**Consequences (testable):**
- Changing Travel Mode invalidates cached Travel Times computed under the prior mode and triggers recomputation.
- Any/Air mode does not call a Measured provider; absent a Manual entry its Travel Time carries **Placeholder** Fidelity. `[ASSUMPTION: Any/Air placeholder = a single configurable assumed speed — see §8.]`
- For an **Any/Air Leg**, a user can enter a **manual Travel Time** (e.g. a known flight duration); that value carries **Manual** Fidelity and overrides the placeholder for that Leg. Because the system cannot know flight schedules, an Any/Air Travel Time with no Manual entry is shown with its Placeholder badge — never presented as a real door-to-door time. *(This is the first slice of per-Leg override; full per-Leg mode override remains deferred — see §8.)*

#### FR-9: Obtain per-Leg Travel Time from the provider
The system obtains Travel Time and distance for each Leg from the configured Travel-Time Provider under the Trip's Travel Mode, each value carrying its Fidelity. Realizes UJ-1.

**Consequences (testable):**
- For Drive/Walk/Cycle, when a Measured provider is configured, Travel Time comes from that provider's matrix/route for the profile and is badged **Measured**.
- Each Leg shows its Travel Time, distance, and Fidelity badge in the stop list and/or on the map.
- A Trip's total Travel Time equals the sum of its Legs' Travel Times.

#### FR-10: Graceful degradation with honest Fidelity
When the active provider cannot serve a Leg — unreachable, **or reachable but returning no route / out-of-coverage** — or the mode is Any/Air, the system falls back to the Estimated (haversine) provider rather than failing. Realizes UJ-2 (edge case), UJ-3.

**Consequences (testable):**
- With the Measured provider unreachable, every affected Leg still shows a Travel Time badged **Estimated**; no Leg is left blank and the feature does not error out.
- When the provider is reachable but returns **no route / out-of-coverage** for a Drive/Walk/Cycle Leg (e.g. an endpoint outside the loaded coverage area), that Leg degrades to **Estimated** Fidelity — it does not error or render blank.
- When a Measured provider becomes available again, a recompute can upgrade Estimated values to **Measured**.

#### FR-11: Cache and invalidate computed results
Per-pair Travel-Time results are cached by `(FromStop, ToStop, Travel Mode, Provider)` and reused until an input changes; both the displayed Legs and the on-demand Distance Matrix read this cache. Realizes UJ-1.

**Consequences (testable):**
- A Stop Order change that introduces no new `(FromStop, ToStop, Mode)` pair (all consecutive pairs already cached) triggers **no** recomputation — only the displayed Legs change.
- A cached entry is invalidated when any of its inputs change: either Stop's coordinates, the Travel Mode, the active Provider, or the Any/Air assumed-speed setting.
- **Estimated→Measured upgrade:** when a Measured Provider becomes available (or is newly configured), entries previously served at Estimated Fidelity are eligible for recompute; the upgrade happens on the next computation trigger or an explicit "recompute travel times" action — it is not silent or instantaneous. `[ASSUMPTION: an explicit recompute action plus a provider-available signal both trigger upgrade.]`
- Computation runs off the request thread; the UI shows a pending/“computing” state and resolves without a manual refresh. `[ASSUMPTION: implemented as a background job analogous to PoiEnrichmentBackgroundService.]`

**Feature-specific NFRs:**
- With a self-hosted provider (Mock or OSRM), POI coordinates stay within the deployment. Any provider that calls out (a scrape, or a future hosted API) must surface that data egress to the operator before it is enabled (see §11).

### 4.4 Dwell Time & Itinerary Timeline

**Description:** Each Stop can carry a **Dwell Time**. The **Itinerary Timeline** accumulates an optional start time plus alternating Travel Time and Dwell Time along the Stop Order to give an arrival time per Stop and a return/finish time. There is no day concept: an overnight is a hotel Stop with a long Dwell Time. Realizes UJ-1, UJ-3.

**Functional Requirements:**

#### FR-12: Set Dwell Time per Stop
A user can set a Dwell Time on any Stop. Realizes UJ-1, UJ-3.

**Consequences (testable):**
- Dwell Time is stored per Stop within the Trip (the same POI may have different Dwell Time in different Trips). `[ASSUMPTION: Dwell Time lives on the Collection–POI membership, not on the POI.]`
- A Stop with no Dwell Time set contributes zero to the timeline.
- An overnight is expressible purely as a large Dwell Time (e.g. 600 minutes on a hotel Stop) with no special "day" handling.

#### FR-13: Compute the Itinerary Timeline
The system computes arrival time at each Stop and the Trip's finish time from the Stop Order, Travel Times, and Dwell Times. Realizes UJ-1, UJ-3.

**Consequences (testable):**
- **Computation:** the timeline walks placeable Stops in Stop Order. `arrival(1) = TripStart` (or offset 0); `departure(k) = arrival(k) + Dwell(k)`; `arrival(k+1) = departure(k) + TravelTime(Leg k→k+1)`. For a Roundtrip, a final return arrival back at the Start uses the closing Leg's Travel Time; the Start's own Dwell is counted once, at the beginning.
- Given an optional Trip start time, each Stop shows a computed clock arrival time; with no start time set, the timeline shows cumulative elapsed offsets instead. `[ASSUMPTION: explicit trip start time is optional; default is relative offsets — see §8.]`
- **Unplaceable Stops** (FR-4) are skipped by the Leg walk; if such a Stop carries a Dwell Time it still contributes its Dwell to the running total at its ordered position, but adds no Travel Time. `[ASSUMPTION: Unplaceable dwell counts; flag if this should instead be excluded.]`
- A Leg whose Travel Time is **Placeholder** Fidelity (un-entered Any/Air) propagates that uncertainty: downstream arrival times from that Leg onward are badged as resting on a Placeholder. `[ASSUMPTION: timeline still computes through Placeholders rather than showing "—" — see §8 OQ4.]`
- A Trip whose total exceeds its **time budget** — an optional per-Trip field — is flagged (overrun shown distinctly). With no budget set, no overrun flag is shown. `[ASSUMPTION: time-budget is an optional per-Trip field and a soft flag, not a hard constraint.]`

### 4.5 Start / Finish & Roundtrip

**Description:** A Trip designates a **Start Stop**; the **Finish Stop** defaults to the Start (Roundtrip) or can be set to a different Stop. Start/Finish are pinned by TSP-Sort and anchor the map loop and timeline. Realizes UJ-1, UJ-3.

**Functional Requirements:**

#### FR-14: Designate Start and Finish
A user can set any Stop as Start and, optionally, any other Stop as Finish. Realizes UJ-1, UJ-3.

**Consequences (testable):**
- Setting a Start Stop pins it to Stop Order 1; the map loop and timeline anchor on it.
- Leaving Finish unset makes the Trip a Roundtrip (closing Leg returns from Order N to the Start); setting a distinct Finish makes it an open path ending there, with the Finish Stop pinned to Order N.
- Start (Order 1) and Finish (Order N) are pinned; TSP-Sort (FR-15) and drag (FR-3) reorder only the interior Stops between them. No Stop ever holds two Stop Order values.

### 4.6 Ordering: Manual, TSP-Sort, and MCP

**Description:** Stops can be ordered three independent ways, all writing the same **Stop Order**: (1) **manual drag** (FR-3); (2) an explicit **"Sort in Traveling Salesman order"** button that reorders Stops to minimize total Travel Time over the Distance Matrix, pinning Start and Finish (nearest-neighbor + 2-opt; see `addendum.md`); and (3) an external agent over **MCP** that assigns Stop Order numbers directly. None of these run automatically — each is invoked deliberately — and any resulting order remains freely editable by manual drag. Realizes UJ-2.

**Functional Requirements:**

#### FR-15: "Sort in Traveling Salesman order" button
A user can trigger an explicit **"Sort in Traveling Salesman order"** action that reorders Stops to minimize total Travel Time, keeping Start and Finish pinned. Realizes UJ-2.

**Consequences (testable):**
- The action is **on-demand only** — the system never reorders Stops without the user pressing the button.
- It never moves the Start Stop from first or the Finish Stop from last; with no Start (or no Finish) designated, it optimizes without that pin.
- The resulting Stop Order has total Travel Time ≤ the pre-sort order for the same Stops and Mode (it does not make the loop worse).
- It completes interactively for trips up to at least 30 Stops. `[ASSUMPTION: N≤30 target per research; larger N still completes but without an interactivity guarantee.]`
- The result is overridable by a subsequent manual drag (FR-3).

#### FR-16: Assign Stop Order via MCP
An external agent can read and **assign Stop Order numbers** for the POIs in a Collection through the MCP server, and may also set Start/Finish and Dwell Time. Realizes UJ-2.

**Consequences (testable):**
- The MCP surface exposes, at minimum: read ordered Stops + computed Legs for a Collection; **assign Stop Order numbers** to the Collection's POIs; set Start/Finish; set Dwell Time. `[ASSUMPTION: these extend the existing authenticated /mcp tool set; exact tool names TBD in architecture.]`
- An order assigned via MCP persists identically to a manual drag and is reflected in map, times, and timeline; it remains editable by manual drag afterward.
- MCP trip operations honor the existing `/mcp` auth guard (loopback/LAN bypass or API key); no new unauthenticated surface is added.

**Notes:** The app stores order numbers mechanically; *how* an agent decides them (e.g. honoring opening hours or "museums in the morning") is the agent's concern, not a system feature. The TSP button (FR-15) remains the deterministic, offline way to get a good loop without any agent.

## 5. Non-Goals (Explicit)
- **Not a navigation app.** No turn-by-turn voice/live guidance. `[NON-GOAL for MVP]`
- **Not multi-day with a calendar.** No day/date entity, no per-day buckets — overnights are Dwell Time. `[NON-GOAL for MVP]`
- **Not a fleet/VRP optimizer.** No vehicle capacities, time-window hard constraints, or multiple vehicles.
- **Not collaborative/real-time.** No simultaneous multi-user trip editing or sharing workflow in v1.
- **Not dependent on a mandatory paid mapping API.** The **default** experience is fully self-hosted (Mock or OSRM). Any out-calling provider — a hosted API (e.g. Google Routes BYO-key) or a Google-Maps scrape — is strictly opt-in and surfaces its data egress before it can be enabled (see §11); none is the default and none is required.

## 6. MVP Scope

### 6.1 In Scope
- Trip View toggle over existing Collections, surfaced in the filtered-results region (FR-1, FR-17).
- Persistent Stop Order with seed, manual drag-reorder, Unplaceable handling (FR-2–FR-4).
- Map Legs: straight connectors **and** road geometry when a Measured provider supplies it; list/map sync (FR-5–FR-7).
- Travel Mode selection (Any/Air, Drive, Walk, Cycle); per-Leg Travel Time from the pluggable Travel-Time Provider, each value Fidelity-badged; cached background computation (FR-8–FR-11).
- A pluggable **Travel-Time Provider** with a haversine **Mock as the shipping default** (so v1 ships with no routing infra); richer providers (OSRM, scrape, Manual) are swappable per deployment.
- Dwell Time per Stop and the Itinerary Timeline with soft time-budget flag (FR-12–FR-13).
- Start/Finish designation and Roundtrip default (FR-14).
- Three ordering paths, all on-demand: manual drag (FR-3), a "Sort in Traveling Salesman order" button (NN + 2-opt, FR-15), and MCP order-number assignment (FR-16).

### 6.2 Out of Scope for MVP
- **Which** Measured provider ships as the recommended non-mock default (OSRM vs a scrape vs a hosted BYO-key API) — **deferred**. v1's contract works with any of them; only the Mock default is committed. `[NOTE FOR PM: this is the open "real travel time" decision — the Mock keeps v1 unblocked while it's pending.]`
- Per-Leg Travel-Mode override (mixed-mode trips, e.g. fly then drive) — **deferred;** v1 is single mode per Trip plus the Any/Air Manual slice. `[NOTE FOR PM: directly relevant to the Any/Air + ground combination in UJ-3; revisit early.]`
- Opening-hours-aware *hard* scheduling (time-window optimization / VROOM) — v1 only *warns*, does not solve.
- Multi-day calendars, sharing/export of the itinerary as a document, and live traffic.
- Provider data-refresh automation (e.g. OSM extract updates) — manual refresh acceptable for v1 (see §12).

## 7. Success Metrics

**Primary**
- **SM-1 — Trip adoption:** % of active Collections that have had Trip View enabled at least once within 30 days of release. Target: ≥ 40%. Validates FR-1.
- **SM-2 — Assisted-ordering usage & retention:** % of Trips where TSP-Sort or MCP ordering (FR-15/FR-16) was used **and** the resulting order was kept (not fully manually overridden) after one session. Target: ≥ 50% of ordered Trips. Validates FR-15, FR-16.
- **SM-3 — Time accuracy trust:** **when a Measured provider is configured,** share of Drive/Walk/Cycle Legs returned at **Measured** Fidelity (not degraded to Estimated) under normal operation. Target: ≥ 95%. Validates FR-9, FR-10 (proxy for "the times users see are real road times"). *(Not applicable while the Mock provider is the only one configured — accuracy trust is then carried by honest Fidelity badging, not by this target.)*

**Secondary**
- **SM-4 — Feature completeness per Trip:** median count of trip attributes set per Trip (Start, Mode, ≥1 Dwell Time). Target: ≥ 2. Validates FR-12, FR-14.
- **SM-5 — Compute responsiveness:** p95 time from triggering TSP-Sort to redrawn route for N≤30 Stops. Target: ≤ 3 s with a warm matrix. Validates FR-11, FR-15.

**Counter-metrics (do not optimize)**
- **SM-C1 — Don't trade away the plain Collection.** Plain-collection (Trip-View-off) usage stays a healthy share of Collection sessions; if it collapses, we've made Collections *feel like* they must become Trips. Counterbalances SM-1 (don't force trips on everyone).
- **SM-C2 — Don't chase false precision.** Frequency of Travel-Time recomputation per Trip edit — should stay low (cache working). A spike means we're hammering the provider on every keystroke. Counterbalances SM-3/SM-5.

## 8. Open Questions
1. **Default Measured provider** — which provider backs Measured Fidelity beyond the Mock: self-hosted OSRM, a Google-Maps scrape (ToS-gray — see `addendum.md` §C), a hosted BYO-key API, or "Manual only"? The provider *contract* (FR-9/§4.3) is settled; the default impl is not. (§4.3, §12)
2. **Per-Leg Travel Mode override** — single mode per Trip (v1) vs allowing, e.g., an Air leg followed by Drive legs. **UJ-3 (flight + ground) is the forcing function**; v1 ships only the Any/Air *Manual Travel Time* slice (FR-8), full per-Leg mode override deferred. (FR-8)
3. **No POI-level default dwell** — Dwell Time lives on the Collection–POI membership, so there is no per-POI default to seed it from; a POI in two Trips starts with no dwell in each. Acceptable, or do we want an optional POI-level default to seed from? (FR-12)
4. **Air placeholder honesty** — is a badged Placeholder enough for unentered Air Legs, or should the timeline simply show "—" until the user enters a Travel Time? (FR-8, FR-13)
5. **Measured-provider deployment shape (if OSRM is chosen)** — one instance with multiple profiles vs multiple instances; which OSM extract (region vs global) ships and the image/RAM footprint. (Architecture)
6. **Explicit trip start time** — should the timeline default to a wall-clock start (anchoring arrival times) or to relative offsets? (FR-13)
7. **Any/Air speed model** — single assumed speed vs distance-tiered (short hops vs long flights). (FR-8)
8. **Trip View persistence granularity** — per Collection (assumed) vs per user/session. (FR-1)

*Owners & revisit (none block the next workflows — each has a working default in the doc): OQ1, OQ5 → `bmad-create-architecture`; OQ2 → revisit immediately post-v1 (UJ-3 forcing function); OQ3, OQ4, OQ6, OQ8 → `bmad-ux`; OQ7 → `bmad-create-architecture`/config.*

## 9. Assumptions Index
*None blocks the build. The **load-bearing** ones (Dwell-on-membership; the FR-11 cache/upgrade behavior; the FR-13 timeline edge rules) deserve an explicit nod before story creation; the rest are **downstream-owned** defaults that `bmad-ux` / `bmad-create-architecture` will settle.*

- §4.1 / FR-1 — Trip View toggle state is persisted per Collection (not per user/session).
- §4.1 / FR-2 — Seed Stop Order is existing POI added-date ascending.
- §4.2 / FR-6 — Non-Measured Legs are distinguished by line style (e.g. dashed), consistent with the Fidelity badge.
- §4.3 / FR-8 — Any/Air uses a single configurable assumed speed as a **badged placeholder only**; users may enter a manual per-Leg Travel Time; full per-Leg mode override deferred.
- §4.3 / FR-11 — Travel-time computation is a background job analogous to `PoiEnrichmentBackgroundService`.
- §4.3 / FR-11 — Estimated→Measured upgrade fires on an explicit recompute action plus a provider-available signal (not silently).
- §4.4 / FR-12 — Dwell Time is stored on the Collection–POI membership, not on the POI.
- §4.4 / FR-13 — Explicit trip start time is optional (default relative offsets); time-budget is an optional per-Trip field and a soft flag.
- §4.4 / FR-13 — Unplaceable Stops' Dwell still counts toward the timeline; the timeline computes through Placeholder legs rather than blanking (OQ4).
- §4.6 / FR-15 — Interactive TSP-Sort targets N≤30 Stops.
- §4.6 / FR-16 — Trip MCP tools extend the existing authenticated `/mcp` set; names TBD.
- §4.1 / FR-17 — Trip View toggle lives in the filtered-results region; visibility threshold is ≥2 placeable POIs.

---

## 10. Cross-Cutting NFRs
- **Performance:** Distance Matrix + TSP-Sort for N≤30 complete within the SM-5 budget (p95 ≤ 3 s warm). Map redraw on reorder is incremental, not full-page.
- **Reliability / graceful degradation:** A provider outage or out-of-coverage result must never break Trip View — the Estimated (haversine) fallback keeps every Leg populated and Fidelity-badged (FR-10).
- **Responsiveness of UI:** ViewModel-driven state per project layering (Component → ViewModel → Service → Data); long compute off the circuit thread via background job + `StateChanged` notification.
- **Accessibility:** order badges, Legs, and timeline carry `aria-label`s; computing states use `aria-live`; both desktop and `Mobile*Screen` paths implemented (project conventions).
- **Internationalization of UI text:** all new strings go through `UiStrings` (no hardcoded UI text — project rule).
- **Observability:** travel-time computations and provider failures are logged, distinguishing Measured vs Estimated/Placeholder/Manual Legs (feeds SM-3).

## 11. Constraints & Guardrails
- **Cost:** The default (Mock) and the self-hosted OSRM provider incur no per-request mapping cost. A hosted provider (e.g. Google Routes BYO-key) would be the only metered option and is opt-in, not the default.
- **Privacy / data residency:** With a self-hosted provider (Mock or OSRM), Stop coordinates stay within the deployment boundary. Any provider that calls out — a scrape, or a hosted API — sends Stop coordinates to a third party and **must surface that egress to the operator before it is enabled**; it is never the silent default.
- **Licensing:** If an OSM-based provider (e.g. OSRM) is used, its data carries **ODbL** → the UI must show OSM attribution on the map; the engine code (OSRM, BSD-2-Clause) is permissive for bundling. A scrape provider carries third-party ToS exposure (see `addendum.md` §C). (Provider details: `addendum.md` §B.)

## 12. Integration, Dependencies & Deployment
- **Travel-Time Provider abstraction:** the app codes against the provider contract (FR-9). v1 ships the **Mock** (haversine) provider needing no extra infra. A Measured provider is optional and chosen per deployment.
- **Optional OSRM provider:** if OSRM is the chosen Measured provider, it runs as a docker-compose sidecar with a preprocessed OSM extract and a profile per ground Travel Mode (car/bike/foot); region-scoped extract to bound image/RAM. It is **not a launch dependency**. (Trade-offs: `addendum.md` §G.)
- **Schema migration:** Stop Order and Dwell Time on the Collection–POI membership; a cached **Leg/RouteSegment** store (with Fidelity + Source); trip fields (Travel Mode, Start/Finish) on the Collection. EF Core migration following existing conventions.
- **Map interop:** extend `leafletInterop.js` / `LeafletMap.razor` / `LeafletMapService.cs` with polyline rendering (straight + road geometry).
- **Background job:** a travel-time computation service mirroring `PoiEnrichmentBackgroundService` (poll/trigger, per-worker DbContext, SQLite write serialization).
- **MCP:** new trip tools on the existing authenticated `/mcp` server (FR-16).
- **Coverage boundary:** a Measured provider's route fidelity is bounded by its coverage (for OSRM, the loaded OSM extract). A Leg outside coverage degrades to **Estimated** Fidelity (FR-10) — it must not error. Choosing a narrow coverage area trades coverage for footprint; this is surfaced, not silent.
- **Operations:** OSM data staleness handled by manual refresh in v1; automated refresh deferred (§6.2).

---
*Mechanism, rejected alternatives (incl. Google-scraping rationale), the full routing-engine comparison, the NN+2-opt algorithm, and data-model specifics are preserved in **`addendum.md`** for the architecture and UX workflows.*
