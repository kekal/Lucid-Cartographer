#!/bin/bash
# Test runner — executes tests in isolated groups with live progress.
# Each group runs as a separate dotnet test process.
# Usage: bash run-tests.sh [results-file]

RESULTS="${1:-test-results.txt}"
cd "$(dirname "$0")"

echo "=== TEST RUN $(date) ===" > "$RESULTS"
echo "" >> "$RESULTS"

TOTAL_PASS=0
TOTAL_FAIL=0
N=0

run() {
    local name="$1"
    local filter="$2"
    N=$((N + 1))

    echo "[$N] RUN $name" >> "$RESULTS"

    local raw
    raw=$(dotnet test --no-build --verbosity minimal --filter "$filter" -- RunConfiguration.CollectSourceInformation=false 2>&1)

    local p f
    p=$(echo "$raw" | sed -n 's/.*Passed:[[:space:]]*\([0-9]*\).*/\1/p' | tail -1)
    f=$(echo "$raw" | sed -n 's/.*Failed:[[:space:]]*\([0-9]*\).*/\1/p' | tail -1)
    p=${p:-0}; f=${f:-0}

    TOTAL_PASS=$((TOTAL_PASS + p))
    TOTAL_FAIL=$((TOTAL_FAIL + f))

    if [ "$f" -gt 0 ]; then
        echo "[$N] ❌ $name: $p pass, $f fail" >> "$RESULTS"
        echo "$raw" | grep "\[FAIL\]" >> "$RESULTS"
    else
        echo "[$N] ✅ $name: $p pass" >> "$RESULTS"
    fi
}

# Build once
echo "Building..." >> "$RESULTS"
dotnet build --verbosity quiet 2>&1 | tail -1 >> "$RESULTS"
echo "" >> "$RESULTS"

# === UNIT TESTS (~3s total) ===
run "Unit: GeoUtils+PoiMatcher+PoiService" \
    "FullyQualifiedName~GeoUtilsTests|FullyQualifiedName~PoiMatcherTests|FullyQualifiedName~PoiServiceTests"

run "Unit: Importers+Orchestrator" \
    "FullyQualifiedName~GpxImporterTests|FullyQualifiedName~KmlImporterTests|FullyQualifiedName~GeoJsonImporterTests|FullyQualifiedName~CsvImporterTests|FullyQualifiedName~ImportOrchestratorTests"

run "Unit: Exporters+SetOps" \
    "FullyQualifiedName~KmlExporterTests|FullyQualifiedName~GpxExporterTests|FullyQualifiedName~SetOperationServiceTests"

# === BUNIT TESTS (~2s total) ===
run "bUnit: All components" \
    "FullyQualifiedName~Components."

# === INTEGRATION TESTS (each ~1-4min) ===
run "Integration: Navigation+Layout" \
    "FullyQualifiedName~NavigationTests|FullyQualifiedName~LayoutAndSettingsTests"

run "Integration: DataSources+FileImport" \
    "FullyQualifiedName~DataSourcesIntegrationTests|FullyQualifiedName~FileImportTests"

run "Integration: Scraper" \
    "FullyQualifiedName~ScraperIntegrationTests"

run "Integration: Map+Sidebar+Table+DetailPane" \
    "FullyQualifiedName~Integration.MapIntegrationTests|FullyQualifiedName~Integration.MapSidebarTests|FullyQualifiedName~Integration.PoiTableTests|FullyQualifiedName~Integration.PoiDetailPaneTests"

run "Integration: Operations+Extended" \
    "FullyQualifiedName~OperationsIntegrationTests|FullyQualifiedName~OperationsExtendedTests"

run "Integration: Export+Commit" \
    "FullyQualifiedName~ExportIntegrationTests|FullyQualifiedName~CommitToLayerTests"

run "Integration: Search+CrossPage+Edge" \
    "FullyQualifiedName~SearchIntegrationTests|FullyQualifiedName~CrossPageFlowTests|FullyQualifiedName~EdgeCaseTests"

# === FINAL ===
echo "" >> "$RESULTS"
echo "=== TOTAL: $TOTAL_PASS pass, $TOTAL_FAIL fail ===" >> "$RESULTS"
[ "$TOTAL_FAIL" -eq 0 ] && echo "✅ ALL PASSED" >> "$RESULTS" || echo "❌ $TOTAL_FAIL FAILED" >> "$RESULTS"
echo "=== DONE $(date) ===" >> "$RESULTS"
