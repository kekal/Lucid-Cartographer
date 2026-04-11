window.leafletInterop = {
    map: null,
    layerGroups: {},
    markers: {},
    dotnetRef: null,

    initMap: function (elementId, dotnetRef) {
        if (this.map) {
            this.map.remove();
        }
        this.map = L.map(elementId, {
            zoomControl: false
        }).setView([50.0, 20.0], 5);

        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            attribution: '&copy; OpenStreetMap contributors',
            maxZoom: 19
        }).addTo(this.map);

        L.control.zoom({ position: 'topright' }).addTo(this.map);

        this.dotnetRef = dotnetRef;
        this.layerGroups = {};
        this.markers = {};
    },

    addCollectionMarkers: function (collectionId, pois, color) {
        // Remove existing layer group if any
        if (this.layerGroups[collectionId]) {
            this.map.removeLayer(this.layerGroups[collectionId]);
        }

        var group = L.layerGroup();
        var self = this;

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
                '<strong style="font-family:Manrope,sans-serif;font-size:14px;">' + self.escapeHtml(poi.name) + '</strong>' +
                (poi.address ? '<br><span style="color:#414754;font-size:12px;">' + self.escapeHtml(poi.address) + '</span>' : '') +
                '<br><a href="' + (poi.googleMapsUrl || 'https://www.google.com/maps/search/?api=1&query=' + poi.latitude + ',' + poi.longitude) +
                '" target="_blank" rel="noopener" style="color:#005bbf;font-size:12px;text-decoration:none;">Open in Google Maps &#8599;</a>' +
                '</div>'
            );

            marker.on('click', function () {
                if (self.dotnetRef) {
                    self.dotnetRef.invokeMethodAsync('OnMarkerClickedJs', poi.id);
                }
            });

            marker.addTo(group);
            self.markers[poi.id] = marker;
        });

        group.addTo(this.map);
        this.layerGroups[collectionId] = group;
    },

    removeCollectionMarkers: function (collectionId) {
        if (this.layerGroups[collectionId]) {
            this.map.removeLayer(this.layerGroups[collectionId]);
            delete this.layerGroups[collectionId];
        }
    },

    focusOnPoi: function (lat, lon, zoom) {
        if (this.map) {
            this.map.flyTo([lat, lon], zoom || 16, { duration: 0.8 });
        }
    },

    fitBounds: function () {
        if (!this.map) return;
        var allLayers = [];
        for (var key in this.layerGroups) {
            this.layerGroups[key].eachLayer(function (layer) {
                allLayers.push(layer);
            });
        }
        if (allLayers.length > 0) {
            var group = L.featureGroup(allLayers);
            this.map.fitBounds(group.getBounds().pad(0.1));
        }
    },

    invalidateSize: function () {
        if (this.map) {
            this.map.invalidateSize();
        }
    },

    highlightMarker: function (poiId) {
        var marker = this.markers[poiId];
        if (marker) {
            marker.openPopup();
        }
    },

    escapeHtml: function (text) {
        if (!text) return '';
        var div = document.createElement('div');
        div.appendChild(document.createTextNode(text));
        return div.innerHTML;
    }
};
