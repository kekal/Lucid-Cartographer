// MED-09: Wrapped in IIFE to avoid polluting global scope with mutable state.
// The only global is window.leafletInterop (required for Blazor JS interop).
(function () {
    "use strict";

    var state = {
        map: null,
        layerGroups: {},
        markers: {},
        dotnetRef: null
    };

    function escapeHtml(text) {
        if (!text) return '';
        var div = document.createElement('div');
        div.appendChild(document.createTextNode(text));
        return div.innerHTML;
    }

    window.leafletInterop = {
        initMap: function (elementId, dotnetRef) {
            // Dispose previous dotnetRef to prevent .NET reference leak on re-init
            if (state.dotnetRef) {
                try { state.dotnetRef.dispose(); } catch (_) { }
            }
            if (state.map) {
                state.map.remove();
            }
            if (state.resizeObserver) {
                try { state.resizeObserver.disconnect(); } catch (_) { }
                state.resizeObserver = null;
            }

            var container = document.getElementById(elementId);

            state.map = L.map(elementId, {
                zoomControl: false
            }).setView([50.0, 20.0], 5);

            L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
                attribution: '&copy; OpenStreetMap contributors',
                maxZoom: 19
            }).addTo(state.map);

            L.control.zoom({ position: 'topright' }).addTo(state.map);

            state.dotnetRef = dotnetRef;
            state.layerGroups = {};
            state.markers = {};

            // On SPA navigation (e.g. from /datasources back to /), Leaflet may
            // initialise before the flex layout has finalised the container's
            // dimensions — it then measures 0x0 and the tile grid never paints
            // until a hard refresh. Two defences:
            //   1. Kick invalidateSize on the next animation frame + a short
            //      timeout, so first paint picks up the real container size.
            //   2. Attach a ResizeObserver so any later size change (sidebar
            //      toggle, window resize, pane reopen) keeps the map in sync.
            var invalidate = function () {
                if (state.map) {
                    state.map.invalidateSize();
                }
            };
            if (typeof requestAnimationFrame === 'function') {
                requestAnimationFrame(invalidate);
            }
            setTimeout(invalidate, 100);
            setTimeout(invalidate, 400);

            if (container && typeof ResizeObserver === 'function') {
                state.resizeObserver = new ResizeObserver(function () {
                    invalidate();
                });
                state.resizeObserver.observe(container);
            }
        },

        destroyMap: function () {
            if (state.resizeObserver) {
                try { state.resizeObserver.disconnect(); } catch (_) { }
                state.resizeObserver = null;
            }
            if (state.map) {
                state.map.remove();
                state.map = null;
            }
            if (state.dotnetRef) {
                try { state.dotnetRef.dispose(); } catch (_) { }
                state.dotnetRef = null;
            }
            state.layerGroups = {};
            state.markers = {};
        },

        addCollectionMarkers: function (collectionId, pois, color) {
            if (state.layerGroups[collectionId]) {
                state.map.removeLayer(state.layerGroups[collectionId]);
            }

            var group = L.layerGroup();

            pois.forEach(function (poi) {
                var icon = L.divIcon({
                    className: 'custom-marker',
                    html: '<div style="width:12px;height:12px;border-radius:50%;background:' + color + ';border:2px solid white;box-shadow:0 1px 4px rgba(0,0,0,0.3);"></div>',
                    iconSize: [12, 12],
                    iconAnchor: [6, 6]
                });

                var marker = L.marker([poi.latitude, poi.longitude], { icon: icon });

                marker.bindPopup(
                    '<div style="font-family:Inter,sans-serif;min-width:180px;">' +
                    '<strong style="font-family:Manrope,sans-serif;font-size:14px;">' + escapeHtml(poi.name) + '</strong>' +
                    (poi.address ? '<br><span style="color:#414754;font-size:12px;">' + escapeHtml(poi.address) + '</span>' : '') +
                    '<br><a href="' + (poi.googleMapsUrl || 'https://www.google.com/maps/search/?api=1&query=' + poi.latitude + ',' + poi.longitude) +
                    '" target="_blank" rel="noopener" style="color:#005bbf;font-size:12px;text-decoration:none;">Open in Google Maps &#8599;</a>' +
                    '</div>'
                );

                marker.on('click', function () {
                    if (state.dotnetRef) {
                        state.dotnetRef.invokeMethodAsync('OnMarkerClickedJs', poi.id);
                    }
                });

                marker.addTo(group);
                state.markers[poi.id] = marker;
            });

            group.addTo(state.map);
            state.layerGroups[collectionId] = group;
        },

        removeCollectionMarkers: function (collectionId) {
            if (state.layerGroups[collectionId]) {
                state.map.removeLayer(state.layerGroups[collectionId]);
                delete state.layerGroups[collectionId];
            }
        },

        focusOnPoi: function (lat, lon, zoom) {
            if (state.map) {
                state.map.flyTo([lat, lon], zoom || 16, { duration: 0.8 });
            }
        },

        fitBounds: function () {
            if (!state.map) return;
            var allLayers = [];
            for (var key in state.layerGroups) {
                state.layerGroups[key].eachLayer(function (layer) {
                    allLayers.push(layer);
                });
            }
            if (allLayers.length > 0) {
                var group = L.featureGroup(allLayers);
                state.map.fitBounds(group.getBounds().pad(0.1));
            }
        },

        invalidateSize: function () {
            if (state.map) {
                state.map.invalidateSize();
            }
        },

        highlightMarker: function (poiId) {
            var marker = state.markers[poiId];
            if (marker) {
                marker.openPopup();
            }
        },

        getBounds: function () {
            if (!state.map) return null;
            var b = state.map.getBounds();
            return {
                south: b.getSouth(),
                west: b.getWest(),
                north: b.getNorth(),
                east: b.getEast()
            };
        },

        enableBoundsTracking: function () {
            if (!state.map || state._boundsHandler) return;
            state._boundsHandler = function () {
                if (!state.dotnetRef) return;
                var b = state.map.getBounds();
                state.dotnetRef.invokeMethodAsync('OnBoundsChangedJs', {
                    south: b.getSouth(),
                    west: b.getWest(),
                    north: b.getNorth(),
                    east: b.getEast()
                });
            };
            state.map.on('moveend', state._boundsHandler);
            // Fire immediately so the initial viewport is known
            state._boundsHandler();
        }
    };

    // Resizable splitter between map and POI table
    window.leafletInterop.initSplitter = function (handleEl, tableEl, dotnetRef) {
        if (!handleEl || !tableEl) return;
        var startY, startH;
        function onPointerMove(e) {
            var delta = startY - e.clientY;
            var newH = Math.max(80, Math.min(window.innerHeight * 0.7, startH + delta));
            tableEl.style.height = newH + 'px';
            if (state.map) state.map.invalidateSize();
        }
        function onPointerUp(e) {
            document.removeEventListener('pointermove', onPointerMove);
            document.removeEventListener('pointerup', onPointerUp);
            handleEl.releasePointerCapture(e.pointerId);
            var h = parseInt(tableEl.style.height, 10) || 256;
            if (dotnetRef) dotnetRef.invokeMethodAsync('OnSplitterResizedJs', h);
        }
        handleEl.addEventListener('pointerdown', function (e) {
            e.preventDefault();
            startY = e.clientY;
            startH = tableEl.offsetHeight;
            handleEl.setPointerCapture(e.pointerId);
            document.addEventListener('pointermove', onPointerMove);
            document.addEventListener('pointerup', onPointerUp);
        });
    };

    // MED-08: downloadFile moved from inline script in App.razor to this module.
    // Callable via JS interop as window.LucidCartographer.downloadFile.
    window.LucidCartographer = window.LucidCartographer || {};
    window.LucidCartographer.downloadFile = function (filename, contentType, base64Data) {
        var link = document.createElement('a');
        link.download = filename;
        link.href = 'data:' + contentType + ';base64,' + base64Data;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
    };
})();
