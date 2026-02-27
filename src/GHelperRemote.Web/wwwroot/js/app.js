/* ============================================================
   G-Helper Remote - Frontend Application
   ============================================================ */

(function () {
    'use strict';

    // ── Constants ──────────────────────────────────────────────
    const SIGNALR_HUB_URL = '/hubs/sensors';
    const API_BASE = '/api';
    const RECONNECT_DELAYS = [0, 1000, 2000, 5000, 10000, 30000];
    const DEBOUNCE_CHARGE_MS = 500;
    const TOAST_DURATION_MS = 3000;

    // Fan curve SVG geometry
    const FAN = {
        SVG_W: 400,
        SVG_H: 300,
        PADDING: { top: 20, right: 20, bottom: 35, left: 45 },
        TEMP_MIN: 20,
        TEMP_MAX: 100,
        SPEED_MIN: 0,
        SPEED_MAX: 100,
        POINT_COUNT: 8,
        POINT_RADIUS: 6,
    };

    FAN.PLOT_W = FAN.SVG_W - FAN.PADDING.left - FAN.PADDING.right;
    FAN.PLOT_H = FAN.SVG_H - FAN.PADDING.top - FAN.PADDING.bottom;

    // ── State ──────────────────────────────────────────────────
    const state = {
        connected: false,
        perfMode: null,
        gpuMode: null,
        fanTab: 'cpu',             // 'cpu' | 'gpu'
        fanProfileId: 0,           // 0=Balanced, 1=Turbo, 2=Silent
        fanCurves: { cpu: null, gpu: null },
        draggingPoint: null,
        chargeLimitTimer: null,
    };

    // ── Helpers ────────────────────────────────────────────────

    function $(sel, ctx) { return (ctx || document).querySelector(sel); }
    function $$(sel, ctx) { return Array.from((ctx || document).querySelectorAll(sel)); }

    /** API fetch wrapper with toast on error */
    async function api(method, url, body, options) {
        options = options || {};
        const showErrorToast = options.showErrorToast !== false;

        const opts = {
            method,
            headers: { 'Content-Type': 'application/json' },
        };
        if (body !== undefined) {
            opts.body = JSON.stringify(body);
        }
        try {
            const res = await fetch(`${API_BASE}${url}`, opts);
            if (!res.ok) {
                const ct = res.headers.get('content-type') || '';
                var message = `HTTP ${res.status}`;
                var code = null;

                if (ct.includes('application/json')) {
                    const payload = await res.json().catch(() => null);
                    if (payload) {
                        if (payload.error) message = payload.error;
                        if (payload.code) code = payload.code;
                    }
                } else {
                    const text = await res.text().catch(() => '');
                    if (text) {
                        try {
                            const payload = JSON.parse(text);
                            message = payload.error || text;
                            code = payload.code || null;
                        } catch (_) {
                            message = text;
                        }
                    }
                }

                var error = new Error(message);
                error.code = code;
                error.status = res.status;
                throw error;
            }
            const ct = res.headers.get('content-type') || '';
            if (ct.includes('application/json')) {
                return await res.json();
            }
            return null;
        } catch (err) {
            if (showErrorToast) {
                showToast(err.message || 'Request failed', 'error');
            }
            throw err;
        }
    }

    // ── Toast System ──────────────────────────────────────────

    function showToast(message, type) {
        type = type || 'info';
        const container = $('#toastContainer');
        const toast = document.createElement('div');
        toast.className = `toast ${type}`;
        toast.innerHTML = `<span class="toast-bar"></span><span>${escapeHtml(message)}</span>`;
        container.appendChild(toast);

        setTimeout(function () {
            toast.classList.add('removing');
            toast.addEventListener('animationend', function () {
                toast.remove();
            });
        }, TOAST_DURATION_MS);
    }

    function escapeHtml(str) {
        const d = document.createElement('div');
        d.textContent = str;
        return d.innerHTML;
    }

    // ── Loading Overlay ────────────────────────────────────────

    function showLoading(id) {
        const el = $('#' + id);
        if (el) el.classList.remove('hidden');
    }

    function hideLoading(id) {
        const el = $('#' + id);
        if (el) el.classList.add('hidden');
    }

    // ── Connection Status ─────────────────────────────────────

    function setConnectionStatus(status) {
        const dot = $('#statusDot');
        const text = $('#statusText');
        dot.className = 'status-dot';
        if (status === 'connected') {
            dot.classList.add('connected');
            text.textContent = 'Connected';
            state.connected = true;
        } else if (status === 'reconnecting') {
            dot.classList.add('reconnecting');
            text.textContent = 'Reconnecting...';
            state.connected = false;
        } else {
            text.textContent = 'Disconnected';
            state.connected = false;
        }
    }

    // ── SignalR ───────────────────────────────────────────────

    function startSignalR() {
        const connection = new signalR.HubConnectionBuilder()
            .withUrl(SIGNALR_HUB_URL)
            .withAutomaticReconnect(RECONNECT_DELAYS)
            .configureLogging(signalR.LogLevel.Warning)
            .build();

        connection.on('SensorData', function (data) {
            updateSensorData(data);
        });

        connection.onreconnecting(function () {
            setConnectionStatus('reconnecting');
        });

        connection.onreconnected(function () {
            setConnectionStatus('connected');
            showToast('Reconnected', 'success');
        });

        connection.onclose(function () {
            setConnectionStatus('disconnected');
            // Manual reconnect after all automatic attempts fail
            setTimeout(function () {
                startConnection(connection);
            }, 5000);
        });

        startConnection(connection);
    }

    async function startConnection(connection) {
        try {
            await connection.start();
            setConnectionStatus('connected');
        } catch (err) {
            setConnectionStatus('disconnected');
            setTimeout(function () {
                startConnection(connection);
            }, 5000);
        }
    }

    // ── Sensor Data Update ────────────────────────────────────

    function updateSensorData(data) {
        if (data.cpuTemperature !== undefined) {
            const el = $('#cpuTemp');
            el.textContent = Math.round(data.cpuTemperature) + '\u00B0C';
            el.className = 'status-value ' + tempClass(data.cpuTemperature);
        }
        if (data.gpuTemperature !== undefined) {
            const el = $('#gpuTemp');
            el.textContent = Math.round(data.gpuTemperature) + '\u00B0C';
            el.className = 'status-value ' + tempClass(data.gpuTemperature);
        }
        if (data.cpuFanRpm !== undefined) {
            $('#cpuFan').textContent = data.cpuFanRpm + ' RPM';
        }
        if (data.gpuFanRpm !== undefined) {
            $('#gpuFan').textContent = data.gpuFanRpm + ' RPM';
        }
        if (data.battery && data.battery.chargePercent !== undefined) {
            $('#batteryLevel').textContent = data.battery.chargePercent + '%';
        }
    }

    function tempClass(temp) {
        if (temp < 60) return 'temp-cool';
        if (temp <= 80) return 'temp-warm';
        return 'temp-hot';
    }

    // ── Card Collapse ─────────────────────────────────────────

    function initCollapsible() {
        $$('.card-header[data-collapse]').forEach(function (header) {
            header.addEventListener('click', function () {
                const bodyId = header.getAttribute('data-collapse');
                const body = $('#' + bodyId);
                if (!body) return;
                const isCollapsed = body.classList.contains('collapsed');
                body.classList.toggle('collapsed');
                header.classList.toggle('collapsed', !isCollapsed);
            });
        });
    }

    function expandCard(bodyId) {
        const body = $('#' + bodyId);
        const header = $('.card-header[data-collapse="' + bodyId + '"]');
        if (body) body.classList.remove('collapsed');
        if (header) header.classList.remove('collapsed');
    }

    // ── Performance Mode ──────────────────────────────────────

    async function loadPerfMode() {
        try {
            const data = await api('GET', '/mode');
            setPerfModeUI(data.mode !== undefined ? data.mode : data);
        } catch (_) { /* toast already shown */ }
    }

    function setPerfModeUI(mode) {
        state.perfMode = mode;
        $$('#perfModeButtons .pill-btn').forEach(function (btn) {
            btn.classList.toggle('active', parseInt(btn.dataset.mode) === mode);
        });
    }

    function initPerfMode() {
        $$('#perfModeButtons .pill-btn').forEach(function (btn) {
            btn.addEventListener('click', async function () {
                const mode = parseInt(btn.dataset.mode);
                if (mode === state.perfMode) return;
                setBtnsDisabled('#perfModeButtons', true);
                showLoading('perfLoading');
                try {
                    var result = await api('PUT', '/mode', { mode: mode }, { showErrorToast: false });
                    setPerfModeUI(mode);
                    if (result && result.warning) {
                        showToast(result.warning, 'info');
                    } else {
                        showToast('Performance mode changed', 'success');
                    }
                } catch (err) {
                    showModeChangeError(err);
                }
                hideLoading('perfLoading');
                setBtnsDisabled('#perfModeButtons', false);
            });
        });
    }

    // ── GPU Mode ──────────────────────────────────────────────

    async function loadGpuMode() {
        try {
            const data = await api('GET', '/gpu');
            setGpuModeUI(data.mode !== undefined ? data.mode : data);
        } catch (_) { /* toast already shown */ }
    }

    function setGpuModeUI(mode) {
        state.gpuMode = mode;
        $$('#gpuModeButtons .pill-btn').forEach(function (btn) {
            btn.classList.toggle('active', parseInt(btn.dataset.mode) === mode);
        });
        const warning = $('#gpuWarning');
        if (mode === 2) {
            warning.classList.remove('hidden');
        } else {
            warning.classList.add('hidden');
        }
    }

    function initGpuMode() {
        $$('#gpuModeButtons .pill-btn').forEach(function (btn) {
            btn.addEventListener('click', async function () {
                const mode = parseInt(btn.dataset.mode);
                if (mode === state.gpuMode) return;
                setBtnsDisabled('#gpuModeButtons', true);
                showLoading('gpuLoading');
                try {
                    var result = await api('PUT', '/gpu', { mode: mode }, { showErrorToast: false });
                    setGpuModeUI(mode);
                    if (result && result.warning) {
                        showToast(result.warning, 'info');
                    } else {
                        showToast('GPU mode changed', 'success');
                    }
                } catch (err) {
                    showModeChangeError(err);
                }
                hideLoading('gpuLoading');
                setBtnsDisabled('#gpuModeButtons', false);
            });
        });
    }

    function showModeChangeError(err) {
        if (err && err.code === 'ghelper_exe_not_found') {
            expandCard('ghelperBody');
            var input = $('#ghelperPathInput');
            if (input) input.focus();
            showToast('Set your GHelper.exe path in G-Helper Executable, or tap Auto Find.', 'error');
            return;
        }

        showToast((err && err.message) || 'Failed to apply mode', 'error');
    }

    // ── G-Helper Executable Path ─────────────────────────────

    function setGHelperPathUI(data) {
        if (!data) return;

        var input = $('#ghelperPathInput');
        var status = $('#ghelperPathStatus');
        if (!input || !status) return;

        input.value = data.configuredPath || data.resolvedPath || '';

        if (data.isResolved) {
            status.textContent = 'Resolved: ' + data.resolvedPath;
            status.style.color = 'var(--green)';
        } else {
            status.textContent = 'Path not resolved. Save the full GHelper.exe path or use Auto Find.';
            status.style.color = 'var(--yellow)';
        }
    }

    async function loadGHelperPath() {
        try {
            var data = await api('GET', '/ghelper/executable');
            setGHelperPathUI(data);
        } catch (_) { /* toast shown */ }
    }

    function initGHelperPath() {
        var saveBtn = $('#ghelperPathSaveBtn');
        var autoBtn = $('#ghelperPathAutoBtn');
        var input = $('#ghelperPathInput');

        if (!saveBtn || !autoBtn || !input) return;

        saveBtn.addEventListener('click', async function () {
            var path = input.value.trim();
            if (!path) {
                showToast('Enter the full path to GHelper.exe', 'error');
                input.focus();
                return;
            }

            showLoading('ghelperLoading');
            saveBtn.disabled = true;
            autoBtn.disabled = true;
            try {
                var data = await api('PUT', '/ghelper/executable', { path: path });
                setGHelperPathUI(data);
                if (data && data.persisted === false) {
                    showToast((data.warning || 'Path applied, but could not persist to appsettings.json.'), 'info');
                } else {
                    showToast('G-Helper executable path saved', 'success');
                }
            } catch (_) { /* toast shown */ }
            hideLoading('ghelperLoading');
            saveBtn.disabled = false;
            autoBtn.disabled = false;
        });

        autoBtn.addEventListener('click', async function () {
            showLoading('ghelperLoading');
            saveBtn.disabled = true;
            autoBtn.disabled = true;
            try {
                var data = await api('POST', '/ghelper/executable/auto-detect', undefined, { showErrorToast: false });
                setGHelperPathUI(data);
                if (data && data.persisted === false) {
                    showToast((data.warning || 'Path auto-detected, but could not persist to appsettings.json.'), 'info');
                } else {
                    showToast('Auto-detected GHelper.exe path', 'success');
                }
            } catch (err) {
                showToast((err && err.message) || 'Could not auto-detect GHelper.exe', 'error');
            }
            hideLoading('ghelperLoading');
            saveBtn.disabled = false;
            autoBtn.disabled = false;
        });
    }

    // ── Shared Button Helpers ─────────────────────────────────

    function setBtnsDisabled(containerSel, disabled) {
        $$(containerSel + ' .pill-btn').forEach(function (btn) {
            btn.disabled = disabled;
        });
    }

    // ── Fan Curve Editor ──────────────────────────────────────

    function defaultCurvePoints() {
        // 8 points, evenly spaced temps from TEMP_MIN to TEMP_MAX
        var pts = [];
        for (var i = 0; i < FAN.POINT_COUNT; i++) {
            var temp = FAN.TEMP_MIN + (i / (FAN.POINT_COUNT - 1)) * (FAN.TEMP_MAX - FAN.TEMP_MIN);
            var speed = Math.min(100, Math.round((i / (FAN.POINT_COUNT - 1)) * 100));
            pts.push({ temp: Math.round(temp), speed: speed });
        }
        return pts;
    }

    function tempToX(temp) {
        var ratio = (temp - FAN.TEMP_MIN) / (FAN.TEMP_MAX - FAN.TEMP_MIN);
        return FAN.PADDING.left + ratio * FAN.PLOT_W;
    }

    function speedToY(speed) {
        var ratio = (speed - FAN.SPEED_MIN) / (FAN.SPEED_MAX - FAN.SPEED_MIN);
        return FAN.PADDING.top + FAN.PLOT_H - ratio * FAN.PLOT_H;
    }

    function xToTemp(x) {
        var ratio = (x - FAN.PADDING.left) / FAN.PLOT_W;
        return Math.round(FAN.TEMP_MIN + ratio * (FAN.TEMP_MAX - FAN.TEMP_MIN));
    }

    function yToSpeed(y) {
        var ratio = (FAN.PADDING.top + FAN.PLOT_H - y) / FAN.PLOT_H;
        return Math.round(FAN.SPEED_MIN + ratio * (FAN.SPEED_MAX - FAN.SPEED_MIN));
    }

    function clamp(val, min, max) {
        return Math.max(min, Math.min(max, val));
    }

    function buildFanCurveSvg() {
        var svg = $('#fanCurveSvg');
        var ns = 'http://www.w3.org/2000/svg';

        // Clear
        svg.innerHTML = '';

        // Background fill for plot area
        var bg = document.createElementNS(ns, 'rect');
        bg.setAttribute('x', FAN.PADDING.left);
        bg.setAttribute('y', FAN.PADDING.top);
        bg.setAttribute('width', FAN.PLOT_W);
        bg.setAttribute('height', FAN.PLOT_H);
        bg.setAttribute('fill', '#08081a');
        svg.appendChild(bg);

        // Grid lines - temperature (vertical)
        for (var t = FAN.TEMP_MIN; t <= FAN.TEMP_MAX; t += 10) {
            var x = tempToX(t);
            var line = document.createElementNS(ns, 'line');
            line.setAttribute('x1', x);
            line.setAttribute('y1', FAN.PADDING.top);
            line.setAttribute('x2', x);
            line.setAttribute('y2', FAN.PADDING.top + FAN.PLOT_H);
            line.setAttribute('class', t % 20 === 0 ? 'grid-line-major' : 'grid-line');
            svg.appendChild(line);

            // Label
            if (t % 20 === 0) {
                var label = document.createElementNS(ns, 'text');
                label.setAttribute('x', x);
                label.setAttribute('y', FAN.PADDING.top + FAN.PLOT_H + 14);
                label.setAttribute('text-anchor', 'middle');
                label.setAttribute('class', 'axis-label');
                label.textContent = t + '\u00B0';
                svg.appendChild(label);
            }
        }

        // Grid lines - speed (horizontal)
        for (var s = FAN.SPEED_MIN; s <= FAN.SPEED_MAX; s += 10) {
            var y = speedToY(s);
            var hline = document.createElementNS(ns, 'line');
            hline.setAttribute('x1', FAN.PADDING.left);
            hline.setAttribute('y1', y);
            hline.setAttribute('x2', FAN.PADDING.left + FAN.PLOT_W);
            hline.setAttribute('y2', y);
            hline.setAttribute('class', s % 20 === 0 ? 'grid-line-major' : 'grid-line');
            svg.appendChild(hline);

            // Label
            if (s % 20 === 0) {
                var slabel = document.createElementNS(ns, 'text');
                slabel.setAttribute('x', FAN.PADDING.left - 8);
                slabel.setAttribute('y', y + 3);
                slabel.setAttribute('text-anchor', 'end');
                slabel.setAttribute('class', 'axis-label');
                slabel.textContent = s + '%';
                svg.appendChild(slabel);
            }
        }

        // Axis titles
        var xTitle = document.createElementNS(ns, 'text');
        xTitle.setAttribute('x', FAN.PADDING.left + FAN.PLOT_W / 2);
        xTitle.setAttribute('y', FAN.SVG_H - 2);
        xTitle.setAttribute('text-anchor', 'middle');
        xTitle.setAttribute('class', 'axis-title');
        xTitle.textContent = 'Temperature (\u00B0C)';
        svg.appendChild(xTitle);

        var yTitle = document.createElementNS(ns, 'text');
        yTitle.setAttribute('x', 12);
        yTitle.setAttribute('y', FAN.PADDING.top + FAN.PLOT_H / 2);
        yTitle.setAttribute('text-anchor', 'middle');
        yTitle.setAttribute('class', 'axis-title');
        yTitle.setAttribute('transform',
            'rotate(-90, 12, ' + (FAN.PADDING.top + FAN.PLOT_H / 2) + ')');
        yTitle.textContent = 'Fan Speed (%)';
        svg.appendChild(yTitle);

        // Filled area under curve
        var area = document.createElementNS(ns, 'path');
        area.setAttribute('class', 'curve-area');
        area.setAttribute('id', 'fanCurveArea');
        svg.appendChild(area);

        // Curve line
        var curveLine = document.createElementNS(ns, 'polyline');
        curveLine.setAttribute('class', 'curve-line');
        curveLine.setAttribute('id', 'fanCurveLine');
        svg.appendChild(curveLine);

        // Point group (rendered on top)
        var pointGroup = document.createElementNS(ns, 'g');
        pointGroup.setAttribute('id', 'fanCurvePoints');
        svg.appendChild(pointGroup);

        renderFanCurvePoints();
        attachFanCurveDragHandlers(svg);
    }

    function renderFanCurvePoints() {
        var ns = 'http://www.w3.org/2000/svg';
        var pts = state.fanCurves[state.fanTab] || defaultCurvePoints();
        state.fanCurves[state.fanTab] = pts;

        // Update curve line
        var linePoints = pts.map(function (p) {
            return tempToX(p.temp) + ',' + speedToY(p.speed);
        }).join(' ');
        var curveLine = $('#fanCurveLine');
        if (curveLine) curveLine.setAttribute('points', linePoints);

        // Update filled area
        var area = $('#fanCurveArea');
        if (area) {
            var d = 'M ' + tempToX(pts[0].temp) + ',' + speedToY(0);
            pts.forEach(function (p) {
                d += ' L ' + tempToX(p.temp) + ',' + speedToY(p.speed);
            });
            d += ' L ' + tempToX(pts[pts.length - 1].temp) + ',' + speedToY(0) + ' Z';
            area.setAttribute('d', d);
        }

        // Render draggable points
        var pointGroup = $('#fanCurvePoints');
        if (!pointGroup) return;
        pointGroup.innerHTML = '';

        pts.forEach(function (p, i) {
            var circle = document.createElementNS(ns, 'circle');
            circle.setAttribute('cx', tempToX(p.temp));
            circle.setAttribute('cy', speedToY(p.speed));
            circle.setAttribute('r', FAN.POINT_RADIUS);
            circle.setAttribute('class', 'curve-point');
            circle.setAttribute('data-index', i);
            pointGroup.appendChild(circle);
        });
    }

    function attachFanCurveDragHandlers(svg) {
        function getSvgCoords(evt) {
            var pt = svg.createSVGPoint();
            var source = evt.touches ? evt.touches[0] : evt;
            pt.x = source.clientX;
            pt.y = source.clientY;
            var ctm = svg.getScreenCTM().inverse();
            var svgPt = pt.matrixTransform(ctm);
            return { x: svgPt.x, y: svgPt.y };
        }

        function onStart(evt) {
            var target = evt.target;
            if (!target.classList.contains('curve-point')) return;
            evt.preventDefault();
            var idx = parseInt(target.getAttribute('data-index'));
            state.draggingPoint = idx;
            target.classList.add('dragging');
        }

        function onMove(evt) {
            if (state.draggingPoint === null) return;
            evt.preventDefault();

            var coords = getSvgCoords(evt);
            var idx = state.draggingPoint;
            var pts = state.fanCurves[state.fanTab];

            var newTemp = clamp(xToTemp(coords.x), FAN.TEMP_MIN, FAN.TEMP_MAX);
            var newSpeed = clamp(yToSpeed(coords.y), FAN.SPEED_MIN, FAN.SPEED_MAX);

            // Enforce monotonic temperature constraint
            var prevTemp = idx > 0 ? pts[idx - 1].temp + 1 : FAN.TEMP_MIN;
            var nextTemp = idx < pts.length - 1 ? pts[idx + 1].temp - 1 : FAN.TEMP_MAX;
            newTemp = clamp(newTemp, prevTemp, nextTemp);

            // Enforce monotonic speed constraint
            var prevSpeed = idx > 0 ? pts[idx - 1].speed : FAN.SPEED_MIN;
            var nextSpeed = idx < pts.length - 1 ? pts[idx + 1].speed : FAN.SPEED_MAX;
            newSpeed = clamp(newSpeed, prevSpeed, nextSpeed);

            pts[idx] = { temp: newTemp, speed: newSpeed };
            renderFanCurvePoints();

            // Re-add dragging class to new element
            var newCircle = $('[data-index="' + idx + '"]', $('#fanCurvePoints'));
            if (newCircle) newCircle.classList.add('dragging');

            // Update info text
            $('#fanCurveHover').textContent = newTemp + '\u00B0C \u2192 ' + newSpeed + '%';
        }

        function onEnd() {
            if (state.draggingPoint === null) return;
            var el = $('.curve-point.dragging', svg);
            if (el) el.classList.remove('dragging');
            state.draggingPoint = null;
            $('#fanCurveHover').textContent = 'Drag points to adjust';
        }

        // Mouse events
        svg.addEventListener('mousedown', onStart);
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onEnd);

        // Touch events
        svg.addEventListener('touchstart', onStart, { passive: false });
        document.addEventListener('touchmove', onMove, { passive: false });
        document.addEventListener('touchend', onEnd);
    }

    function initFanCurve() {
        // Tab switching
        $$('#fanTabs .tab-btn').forEach(function (btn) {
            btn.addEventListener('click', function () {
                $$('#fanTabs .tab-btn').forEach(function (b) { b.classList.remove('active'); });
                btn.classList.add('active');
                state.fanTab = btn.dataset.fan;
                renderFanCurvePoints();
            });
        });

        // Profile selector
        $('#fanProfileSelect').addEventListener('change', function () {
            state.fanProfileId = parseInt(this.value);
            loadFanCurves();
        });

        // Save button
        $('#fanSaveBtn').addEventListener('click', saveFanCurve);

        // Reset button
        $('#fanResetBtn').addEventListener('click', function () {
            state.fanCurves[state.fanTab] = defaultCurvePoints();
            renderFanCurvePoints();
            showToast('Curve reset to default', 'info');
        });

        buildFanCurveSvg();
    }

    async function loadFanCurves() {
        try {
            var data = await api('GET', '/fans/' + state.fanProfileId);
            if (data) {
                if (data.cpu && data.cpu.temps) {
                    state.fanCurves.cpu = data.cpu.temps.map(function (t, i) {
                        return { temp: t, speed: data.cpu.speeds[i] };
                    });
                }
                if (data.gpu && data.gpu.temps) {
                    state.fanCurves.gpu = data.gpu.temps.map(function (t, i) {
                        return { temp: t, speed: data.gpu.speeds[i] };
                    });
                }
                renderFanCurvePoints();
            }
        } catch (_) { /* toast shown */ }
    }

    async function saveFanCurve() {
        showLoading('fanLoading');
        $('#fanSaveBtn').disabled = true;
        try {
            var cpuPts = state.fanCurves.cpu || defaultCurvePoints();
            var gpuPts = state.fanCurves.gpu || defaultCurvePoints();
            var payload = {
                cpu: {
                    temps: cpuPts.map(function (p) { return p.temp; }),
                    speeds: cpuPts.map(function (p) { return p.speed; })
                },
                gpu: {
                    temps: gpuPts.map(function (p) { return p.temp; }),
                    speeds: gpuPts.map(function (p) { return p.speed; })
                }
            };
            await api('PUT', '/fans/' + state.fanProfileId, payload);
            showToast('Fan curve saved', 'success');
        } catch (_) { /* toast shown */ }
        hideLoading('fanLoading');
        $('#fanSaveBtn').disabled = false;
    }

    // ── Display Section ───────────────────────────────────────

    async function loadDisplay() {
        try {
            var data = await api('GET', '/display');
            if (data) {
                if (data.minRefreshRate !== undefined) {
                    $('#refreshRateMin').value = data.minRefreshRate;
                }
                if (data.maxRefreshRate !== undefined) {
                    $('#refreshRateMax').value = data.maxRefreshRate;
                }
                if (data.screenAuto !== undefined) {
                    $('#screenAutoToggle').checked = data.screenAuto;
                }
                if (data.overdrive !== undefined) {
                    $('#overdriveToggle').checked = data.overdrive;
                }
            }
        } catch (_) { /* toast shown */ }
    }

    function initDisplay() {
        $('#displaySaveBtn').addEventListener('click', async function () {
            showLoading('displayLoading');
            this.disabled = true;
            try {
                await api('PUT', '/display', {
                    minRefreshRate: parseInt($('#refreshRateMin').value) || 60,
                    maxRefreshRate: parseInt($('#refreshRateMax').value) || 144,
                    screenAuto: $('#screenAutoToggle').checked,
                    overdrive: $('#overdriveToggle').checked,
                });
                showToast('Display settings applied', 'success');
            } catch (_) { /* toast shown */ }
            hideLoading('displayLoading');
            this.disabled = false;
        });
    }

    // ── Keyboard Section ──────────────────────────────────────

    async function loadKeyboard() {
        try {
            var data = await api('GET', '/keyboard');
            if (data) {
                if (data.brightness !== undefined) {
                    $('#kbBrightness').value = data.brightness;
                    $('#kbBrightnessValue').textContent = data.brightness;
                }
                if (data.mode !== undefined) {
                    $('#kbMode').value = data.mode;
                }
            }
        } catch (_) { /* toast shown */ }
    }

    function initKeyboard() {
        // Brightness slider live value
        $('#kbBrightness').addEventListener('input', function () {
            $('#kbBrightnessValue').textContent = this.value;
        });

        $('#keyboardSaveBtn').addEventListener('click', async function () {
            showLoading('keyboardLoading');
            this.disabled = true;
            try {
                await api('PUT', '/keyboard', {
                    brightness: parseInt($('#kbBrightness').value),
                    mode: parseInt($('#kbMode').value),
                });
                showToast('Keyboard settings applied', 'success');
            } catch (_) { /* toast shown */ }
            hideLoading('keyboardLoading');
            this.disabled = false;
        });
    }

    // ── Battery Section ───────────────────────────────────────

    async function loadBattery() {
        try {
            var data = await api('GET', '/battery');
            if (data) {
                if (data.designCapacity !== undefined) {
                    $('#designCapacity').textContent = formatMwh(data.designCapacity);
                }
                if (data.fullChargeCapacity !== undefined) {
                    $('#currentCapacity').textContent = formatMwh(data.fullChargeCapacity);
                }
                if (data.healthPercent !== undefined) {
                    var el = $('#batteryHealthPct');
                    el.textContent = data.healthPercent + '%';
                    el.style.color = data.healthPercent >= 80 ? 'var(--green)' :
                        data.healthPercent >= 50 ? 'var(--yellow)' : 'var(--red)';
                }
                if (data.cycleCount !== undefined) {
                    $('#cycleCount').textContent = data.cycleCount;
                }
                if (data.chargeLimit !== undefined) {
                    $('#chargeLimit').value = data.chargeLimit;
                    $('#chargeLimitValue').textContent = data.chargeLimit + '%';
                }
            }
        } catch (_) { /* toast shown */ }
    }

    function formatMwh(val) {
        if (val >= 1000) {
            return (val / 1000).toFixed(1) + ' Wh';
        }
        return val + ' mWh';
    }

    function initBattery() {
        var slider = $('#chargeLimit');
        slider.addEventListener('input', function () {
            $('#chargeLimitValue').textContent = this.value + '%';
        });

        slider.addEventListener('change', function () {
            var value = parseInt(this.value);
            // Debounce the API call
            clearTimeout(state.chargeLimitTimer);
            state.chargeLimitTimer = setTimeout(async function () {
                showLoading('batteryLoading');
                try {
                    await api('PUT', '/battery', { chargeLimit: value });
                    showToast('Charge limit set to ' + value + '%', 'success');
                } catch (_) { /* toast shown */ }
                hideLoading('batteryLoading');
            }, DEBOUNCE_CHARGE_MS);
        });
    }

    // ── Initialization ────────────────────────────────────────

    function init() {
        initCollapsible();
        initPerfMode();
        initGpuMode();
        initGHelperPath();
        initFanCurve();
        initDisplay();
        initKeyboard();
        initBattery();

        // Start SignalR
        startSignalR();

        // Load all sections independently
        loadPerfMode();
        loadGpuMode();
        loadGHelperPath();
        loadFanCurves();
        loadDisplay();
        loadKeyboard();
        loadBattery();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

})();
