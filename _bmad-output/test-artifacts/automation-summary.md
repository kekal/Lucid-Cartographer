---
stepsCompleted: ['step-01-preflight-and-context', 'automate-osrm-docs-link-click']
lastStep: 'automate-osrm-docs-link-click'
lastSaved: '2026-06-15'
inputDocuments:
  - _bmad-output/project-context.md
  - _bmad/tea/config.yaml
  - LucidCartographer/Components/Shared/Trip/TripStopList.razor
  - LucidCartographer/Components/Shared/Trip/TripViewModel.cs
  - LucidCartographer/Services/UiStrings.cs
  - LucidCartographer.Tests/Integration/IntegrationTestBase.cs
  - LucidCartographer.Tests/Components/Trip/TripMockEstimateNoteRenderTests.cs
---

# Automation Summary — "How to enable OSRM" link click

## Request
Add a test that checks clicking the **"How to enable OSRM"** link in the Trip View
Mock-estimate note.

## Target analysis
- Link defined in `UiStrings` (`TripMockEstimateOsrmLink` → href `TripMockEstimateOsrmHref` = `docs/osrm.md`),
  rendered in `TripStopList.razor` as `<a href="docs/osrm.md" target="_blank" rel="noopener">`.
  No `@onclick` — "clicking" is browser navigation to a served doc in a new tab.
- Existing coverage (`TripMockEstimateNoteRenderTests`, bUnit) already asserts the link's
  presence / href / target / rel / text. **Gap:** nothing verified that clicking actually
  reaches a *served* doc.

## Defect found (and fixed)
Clicking the link **404'd** in the running app:
- `<base href="/">` → href resolves to `/docs/osrm.md`.
- The app serves static files from `wwwroot` only; there is no `wwwroot/docs/`, and the
  real `docs/osrm.md` lives at the repo root (never copied).
- Even a `wwwroot` copy wouldn't serve: `.md` is an unknown content type to
  `UseStaticFiles`, and the Docker image strips `*.md` (`.dockerignore`).

**Fix:** serve `/docs/osrm.md` via a minimal-API endpoint (`Endpoints/DocsEndpoints.cs`,
matching the repo's `Endpoints/*Endpoints.cs` convention) backed by an **embedded resource**
(`Endpoints/Docs/osrm.md`, included in the app `.csproj`; `.dockerignore` negation lets the
source reach the build context). Wired into both `Program.cs` and the hand-composed
integration host (`IntegrationTestBase`) — the host composes the pipeline by hand, so the
endpoint must be mapped in both or the link 404s under test.

## Test added
`LucidCartographer.Tests/Integration/OsrmDocsLinkIntegrationTests.cs`
`ClickingOsrmLink_OpensServedOperatorGuide_NotA404` (Playwright, `[Collection("Integration")]`):
1. Seeds a Drive/Mock-Estimated trip (so `TripViewModel.RecommendsOsrm` → the note + link render).
2. Enters Trip View; locates the link by its localized text.
3. Clicks it, captures the `target=_blank` popup + navigation response.
4. Asserts the new tab lands on `/docs/osrm.md`, the response is **200**, and the body
   contains the guide heading ("Enabling OSRM measured travel times") — proving the served
   doc, not the SPA 404 fallback.

## Verification
- Red→green proven: with the endpoint disabled the test fails on the served-doc status
  (`Expected 200, Actual 404`); with the fix it passes.
- New test + existing `TripMockEstimateNoteRender` bUnit tests: **5/5 pass**.
- Trip integration filter + new test (host change regression gate): **21/21 pass**.
- Build clean (`TreatWarningsAsErrors`, 0 warnings).

## Files changed
- `LucidCartographer/Endpoints/DocsEndpoints.cs` (new)
- `LucidCartographer/Endpoints/Docs/osrm.md` (new — shipped copy of the operator guide)
- `LucidCartographer/LucidCartographer.csproj` (EmbeddedResource)
- `LucidCartographer/.dockerignore` (`!Endpoints/Docs/osrm.md`)
- `LucidCartographer/Program.cs` (`MapDocsEndpoints`)
- `LucidCartographer.Tests/Integration/IntegrationTestBase.cs` (mirror `MapDocsEndpoints` + using)
- `LucidCartographer.Tests/Integration/OsrmDocsLinkIntegrationTests.cs` (new test)

## Follow-up (not done)
- `docs/osrm.md` (repo root, maintainer docs) and `Endpoints/Docs/osrm.md` (shipped copy)
  are duplicated by necessity (Docker context + `*.md` strip). Keep them in sync when the
  guide changes; a build-time copy step could enforce this later if drift becomes a risk.
