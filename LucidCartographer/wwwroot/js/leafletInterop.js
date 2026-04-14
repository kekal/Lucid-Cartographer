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
        },

        destroyMap: function () {
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
        }
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
