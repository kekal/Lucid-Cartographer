// MED-09: Wrapped in IIFE to avoid polluting global scope with mutable state.
// The only global is window.leafletInterop (required for Blazor JS interop).
(function () {
    "use strict";

    var state = {
        map: null,
        layerGroups: {},
        markers: {},
        dotnetRef: null,
        labelsVisible: false,
        // When true, marker.bindPopup is skipped and the tooltip click does not
        // openPopup either. The mobile layout shows a POI detail panel below
        // the map that replaces what the popup would say, so the popup is
        // pure visual noise on the phone. MapPage pushes this flag via
        // leafletInterop.setMobileMode after each viewport flip.
        mobileMode: false,
        // GPS "you are here" star (mobile only). userMarker is the Leaflet
        // marker; locating guards against starting more than one geolocation
        // watch; recenterOnNextFix tells the locationfound handler to pan the
        // map to the device on the next fix (set by the locate FAB).
        userMarker: null,
        locating: false,
        recenterOnNextFix: false,
        // Localized message shown (as a transient map toast) when a geolocation
        // attempt fails. Passed in from MapPage via locateUser so the text stays
        // in UiStrings rather than being hardcoded here.
        locateErrorMessage: null,
        // True when the latest locate request came from a user tap on the FAB.
        // The passive auto-locate on map load fails silently (no toast) because
        // it has no user gesture and the failure isn't actionable; only an
        // explicit tap surfaces the toast.
        locateUserInitiated: false
    };

    // Lightweight transient toast anchored to the map container. Used to give
    // the user visible feedback when geolocation fails — without it a denied /
    // unavailable fix leaves the locate FAB looking dead. Auto-dismisses; a new
    // toast replaces the previous one so repeated taps don't stack.
    function showMapToast(message) {
        if (!message || !state.map) return;
        var container = state.map.getContainer();
        if (!container) return;
        var existing = container.querySelector('.map-toast');
        if (existing) existing.remove();
        var toast = document.createElement('div');
        toast.className = 'map-toast';
        toast.setAttribute('role', 'status');
        toast.textContent = message;
        container.appendChild(toast);
        // Force reflow so the fade-in transition runs, then schedule removal.
        // eslint-disable-next-line no-unused-expressions
        toast.offsetWidth;
        toast.classList.add('map-toast-show');
        setTimeout(function () {
            toast.classList.remove('map-toast-show');
            setTimeout(function () { if (toast.parentNode) toast.remove(); }, 300);
        }, 4000);
    }

    function onUserLocationFound(e) {
        var ll = e.latlng;
        if (state.userMarker) {
            state.userMarker.setLatLng(ll);
        } else {
            var icon = L.divIcon({
                className: 'user-location-marker',
                html: '<div class="user-loc-pulse"></div><div class="user-loc-star">&#9733;</div>',
                iconSize: [28, 28],
                iconAnchor: [14, 14]
            });
            // interactive:false so the star never steals clicks from POI
            // markers underneath; zIndexOffset keeps it above the POI dots.
            state.userMarker = L.marker(ll, {
                icon: icon,
                interactive: false,
                keyboard: false,
                zIndexOffset: 2000
            }).addTo(state.map);
        }
        if (state.recenterOnNextFix) {
            state.recenterOnNextFix = false;
            state.map.setView(ll, Math.max(state.map.getZoom(), 14));
        }
    }

    function onUserLocationError(e) {
        state.recenterOnNextFix = false;
        // Reset the in-progress guard and tear down the (now-dead) watch so a
        // subsequent locate FAB tap can retry from scratch. Without this, the
        // first failed attempt (e.g. the passive auto-locate on map load, which
        // some mobile browsers reject because it lacks a user gesture) would
        // leave `locating` stuck true — every later FAB tap then hits the
        // `if (state.locating) return;` guard and silently does nothing, so the
        // button looks broken even though the device could grant a fix on a
        // user-initiated retry.
        state.locating = false;
        if (state.map) {
            try { state.map.stopLocate(); } catch (_) { }
        }
        // Common causes: permission denied, GPS off, or an insecure context
        // (plain http over a LAN IP — browsers only expose geolocation on
        // https/localhost). Surface a visible toast so the FAB isn't a silent
        // dead end — but only for a user-initiated tap; the passive auto-locate
        // on load fails silently so we don't nag on every mobile page open.
        if (state.locateUserInitiated) {
            showMapToast(state.locateErrorMessage);
        }
        if (window.console) {
            window.console.warn('Geolocation unavailable:', e && e.code, e && e.message);
        }
    }

    function escapeHtml(text) {
        if (!text) return '';
        var div = document.createElement('div');
        div.appendChild(document.createTextNode(text));
        return div.innerHTML;
    }

    // Bind a permanent tooltip showing the POI name to the right of the marker
    // dot. The label is interactive: clicking it does the same as clicking the
    // dot (opens the popup + selects the POI). It sits to the right of the dot
    // (offset clears the 24px circle) so it never overlaps — and thus never
    // steals — clicks meant for the marker itself.
    function bindLabel(marker) {
        marker.bindTooltip(escapeHtml(marker._poiName || ''), {
            permanent: true,
            direction: 'right',
            offset: [16, 0],
            className: 'poi-label',
            interactive: true
        });

        var tooltip = marker.getTooltip();
        if (tooltip) {
            tooltip.on('click', function () {
                if (!state.mobileMode) {
                    marker.openPopup();
                }
                if (state.dotnetRef && marker._poiId != null) {
                    state.dotnetRef.invokeMethodAsync('OnMarkerClickedJs', marker._poiId);
                }
            });
        }
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
            // Reset on (re)init so the JS state matches the freshly-constructed,
            // transient MapPageViewModel (whose ShowPoiLabels defaults to false).
            state.labelsVisible = false;
            // The previous map (if any) was removed above, taking its geolocation
            // watch and user marker with it. Reset so locateUser starts cleanly
            // against the new map (e.g. after a desktop<->mobile viewport flip).
            state.userMarker = null;
            state.locating = false;
            state.recenterOnNextFix = false;

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
                try { state.map.stopLocate(); } catch (_) { }
                state.map.remove();
                state.map = null;
            }
            if (state.dotnetRef) {
                try { state.dotnetRef.dispose(); } catch (_) { }
                state.dotnetRef = null;
            }
            state.layerGroups = {};
            state.markers = {};
            state.userMarker = null;
            state.locating = false;
            state.recenterOnNextFix = false;
            state._boundsHandler = null;
        },

        addCollectionMarkers: function (collectionId, pois, color) {
            if (state.layerGroups[collectionId]) {
                state.map.removeLayer(state.layerGroups[collectionId]);
            }

            var group = L.layerGroup();

            pois.forEach(function (poi) {
                var icon = L.divIcon({
                    className: 'custom-marker',
                    html: '<div style="width:24px;height:24px;border-radius:50%;background:' + color + ';border:2px solid white;box-shadow:0 1px 4px rgba(0,0,0,0.3);"></div>',
                    iconSize: [24, 24],
                    iconAnchor: [12, 12]
                });

                var marker = L.marker([poi.latitude, poi.longitude], { icon: icon });

                if (!state.mobileMode) {
                    marker.bindPopup(
                        '<div style="font-family:Inter,sans-serif;min-width:180px;">' +
                        '<strong style="font-family:Manrope,sans-serif;font-size:14px;">' + escapeHtml(poi.name) + '</strong>' +
                        (poi.address ? '<br><span style="color:#414754;font-size:12px;">' + escapeHtml(poi.address) + '</span>' : '') +
                        '<br><a href="' + (poi.googleMapsUrl || 'https://www.google.com/maps/search/?api=1&query=' + encodeURIComponent(poi.name)) +
                        '" target="_blank" rel="noopener" style="color:#005bbf;font-size:12px;text-decoration:none;">Open in Google Maps &#8599;</a>' +
                        '</div>'
                    );
                }

                marker.on('click', function () {
                    if (state.dotnetRef) {
                        state.dotnetRef.invokeMethodAsync('OnMarkerClickedJs', poi.id);
                    }
                });

                // Remember the name + id so the labels toggle can (re)bind
                // tooltips — and wire their click to selection — for markers
                // added at any time (enrichment refresh, re-show).
                marker._poiName = poi.name;
                marker._poiId = poi.id;
                if (state.labelsVisible) {
                    bindLabel(marker);
                }

                marker.addTo(group);
                state.markers[poi.id] = marker;
            });

            group.addTo(state.map);
            state.layerGroups[collectionId] = group;
        },

        // Toggle permanent name labels next to every marker currently on the map.
        // New markers added later read state.labelsVisible in addCollectionMarkers,
        // so the setting persists across collection re-shows within the session.
        setLabelsVisible: function (visible) {
            state.labelsVisible = !!visible;
            for (var id in state.markers) {
                if (!Object.prototype.hasOwnProperty.call(state.markers, id)) continue;
                var marker = state.markers[id];
                if (state.labelsVisible) {
                    if (!marker.getTooltip()) {
                        bindLabel(marker);
                    }
                } else if (marker.getTooltip()) {
                    marker.unbindTooltip();
                }
            }
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
        },

        // Pushed by MapPage when the viewport flips between desktop and
        // mobile. On mobile, the POI detail panel below the map replaces what
        // Leaflet's built-in popup would say, so we skip bindPopup entirely
        // and close any popup that may still be open from a prior desktop
        // session. Existing markers added in the other mode keep their state
        // until the next addCollectionMarkers re-bind — that's fine: a closed
        // popup never appears, and an unbound popup is just a no-op when
        // highlightMarker tries to open it.
        setMobileMode: function (on) {
            state.mobileMode = !!on;
            if (state.mobileMode && state.map) {
                state.map.closePopup();
            }
        },

        // Request the device's location and drop a star marker on it (mobile).
        // Leaflet's map.locate() calls navigator.geolocation under the hood —
        // the FIRST call triggers the browser's permission prompt. watch:true
        // keeps the star following the user as they move. setView:false: we
        // never auto-pan on a passive fix (that would yank the map away from
        // the POIs the user is looking at). Passing recenter=true (the locate
        // FAB) pans to the device on the next fix instead.
        //
        // Note: geolocation is only exposed in a secure context — https or
        // localhost. Over plain http to a LAN IP the browser fires
        // locationerror; we then show a toast (errorMessage) and reset the
        // guard so the next FAB tap retries. The passive auto-locate on load
        // has no user gesture, which some mobile browsers reject — the FAB tap
        // (a real gesture) is the reliable trigger, so it must always be able
        // to re-issue the request rather than being blocked by a stale guard.
        locateUser: function (recenter, errorMessage) {
            if (!state.map) return;

            // Remember the localized failure text (from UiStrings) so the error
            // handler can show it as a toast. Updated on every call so it stays
            // current across viewport flips / re-wires.
            if (typeof errorMessage === 'string') {
                state.locateErrorMessage = errorMessage;
            }
            // recenter is only true for the locate FAB tap; the passive
            // auto-locate on load passes false. Drives whether a failure shows
            // the toast (see onUserLocationError).
            state.locateUserInitiated = !!recenter;

            if (recenter) {
                if (state.userMarker) {
                    // Already have a fix — jump there immediately for snappy
                    // feedback; the ongoing watch keeps it fresh.
                    state.map.setView(state.userMarker.getLatLng(), Math.max(state.map.getZoom(), 14));
                } else {
                    state.recenterOnNextFix = true;
                }
            }

            // Only one watch at a time. A second map.locate({watch:true}) would
            // start a second navigator.geolocation.watchPosition and leak it.
            if (state.locating) return;
            state.locating = true;
            state.map.on('locationfound', onUserLocationFound);
            state.map.on('locationerror', onUserLocationError);
            state.map.locate({
                watch: true,
                enableHighAccuracy: true,
                setView: false,
                maximumAge: 10000,
                timeout: 20000
            });
        },

        // Scroll the mobile POI list so the row for `poiId` is in view. Used
        // by MapPage when the user closes the detail panel: the list is back
        // in the DOM and we want it positioned on the POI they were just
        // viewing. The mobile list is non-virtualized (.flex column), so a
        // plain querySelector + scrollIntoView is enough — no need to share
        // the desktop's index/itemSize logic with the virtualized PoiTable.
        scrollMobileRowIntoView: function (poiId) {
            var el = document.querySelector('.m-app .row[data-poi-id="' + poiId + '"]');
            if (el && el.scrollIntoView) {
                // block:'center' centres the row in the panel, which is the
                // most ergonomic landing for a phone — the user can see the
                // POIs above and below for context. behavior:'auto' (instant)
                // is correct here: the user just dismissed the detail and is
                // visually re-orienting, a smooth animation feels laggy.
                el.scrollIntoView({ block: 'center', behavior: 'auto' });
            }
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
    // Scrolls a virtualized list container so the row at `index` (with fixed
    // `itemSize` px) becomes visible. Used by PoiTable to follow map-driven
    // selection — Virtualize doesn't render the row until it's in view, so
    // scrollIntoView on the row element won't work.
    // Scrolls a Virtualize-backed list so the row with data-poi-id=poiId
    // sits flush under the sticky thead. Two-pass:
    //   1. Rough jump to index*itemSize so Virtualize materialises the row.
    //   2. After paint, look up the actual <tr> and snap to its real offsetTop
    //      minus the sticky header height — robust against varying row heights.
    window.LucidCartographer.scrollListToPoi = function (container, poiId, index, itemSize) {
        if (!container || index < 0) return;
        var thead = container.querySelector('thead');
        var headerH = thead ? thead.offsetHeight : 0;
        container.scrollTop = index * itemSize;
        var snap = function () {
            var row = container.querySelector('tr[data-poi-id="' + poiId + '"]');
            if (!row) return;
            container.scrollTop = row.offsetTop - headerH;
        };
        if (typeof requestAnimationFrame === 'function') {
            requestAnimationFrame(function () { requestAnimationFrame(snap); });
        } else {
            setTimeout(snap, 16);
        }
    };

    window.LucidCartographer.downloadFile = function (filename, contentType, base64Data) {
        var link = document.createElement('a');
        link.download = filename;
        link.href = 'data:' + contentType + ';base64,' + base64Data;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
    };

    // Locate FAB handler — wired client-side rather than via Blazor @onclick on
    // purpose. The geolocation permission prompt only appears when the request
    // is made under "transient activation" (synchronously inside the user's tap
    // handler). Routing the tap through Blazor Server (@onclick -> SignalR ->
    // server -> JS interop callback) loses that activation by the time
    // navigator.geolocation runs, so the browser silently denies WITHOUT ever
    // prompting. A delegated click listener on document calls locateUser
    // straight from the gesture, preserving activation so the prompt shows. One
    // listener, attached once — survives Blazor re-renders (the FAB only exists
    // in the mobile layout; closest() is a no-op elsewhere).
    document.addEventListener('click', function (e) {
        var btn = e.target && e.target.closest ? e.target.closest('#locate-fab') : null;
        if (!btn) return;
        var msg = btn.getAttribute('data-loc-error') || null;
        try { window.leafletInterop.locateUser(true, msg); } catch (_) { }
    });
})();
