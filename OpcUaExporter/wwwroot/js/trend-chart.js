// Live multi-series trend chart rendered on a <canvas>, driven from Blazor
// via JS interop (OpcUaService.TrendUpdate -> Index.razor -> here).
(function () {
    var PALETTE = ['#f0a030', '#60a8f0', '#3ecf8e', '#f06060', '#c060f0', '#f0e060', '#60f0d0', '#f08060'];
    var DEFAULT_WINDOW_MS = 60 * 1000;
    var MAX_POINTS_PER_SERIES = 6000;

    var state = {
        canvas: null,
        ctx: null,
        wrap: null,
        order: [],          // nodeIds, in display/color order
        series: {},          // nodeId -> { points: [{t,v}], label, axis }
        windowMs: DEFAULT_WINDOW_MS,
        resizeObserver: null,
        rafScheduled: false,
        tickInterval: null
    };

    function colorFor(nodeId) {
        var idx = state.order.indexOf(nodeId);
        if (idx < 0) idx = 0;
        return PALETTE[idx % PALETTE.length];
    }

    function resizeCanvas() {
        if (!state.canvas || !state.wrap) return;
        var dpr = window.devicePixelRatio || 1;
        var rect = state.wrap.getBoundingClientRect();
        var w = Math.max(1, Math.round(rect.width));
        var h = Math.max(1, Math.round(rect.height));
        var targetW = Math.round(w * dpr);
        var targetH = Math.round(h * dpr);
        if (state.canvas.width !== targetW || state.canvas.height !== targetH) {
            state.canvas.width = targetW;
            state.canvas.height = targetH;
            state.canvas.style.width = w + 'px';
            state.canvas.style.height = h + 'px';
        }
        scheduleDraw();
    }

    function scheduleDraw() {
        if (state.rafScheduled) return;
        state.rafScheduled = true;
        requestAnimationFrame(function () {
            state.rafScheduled = false;
            draw();
        });
    }

    function formatTime(ms) {
        var d = new Date(ms);
        function pad(n) { return (n < 10 ? '0' : '') + n; }
        return pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
    }

    function axisOf(s) { return s.axis === 'right' ? 'right' : 'left'; }

    // Grid/label colors follow the current light/dark theme (set via CSS custom
    // properties on <html>) rather than being hardcoded, so the chart stays
    // legible when the user switches theme.
    function themeColors() {
        var cs = getComputedStyle(document.documentElement);
        var grid = cs.getPropertyValue('--chart-grid').trim();
        var label = cs.getPropertyValue('--chart-label').trim();
        return {
            grid: grid || 'rgba(255,255,255,0.06)',
            label: label || '#ffffff'
        };
    }

    function draw() {
        var ctx = state.ctx;
        if (!ctx || !state.canvas) return;

        var dpr = window.devicePixelRatio || 1;
        var w = state.canvas.width;
        var h = state.canvas.height;
        var colors = themeColors();

        ctx.save();
        ctx.clearRect(0, 0, w, h);

        // Use real wall-clock time as the right edge of the window so the chart keeps
        // scrolling forward even when a tag hasn't produced a new value recently — the
        // last known value is then held flat out to "now" (see effectivePoints below).
        // The .NET host and the WebView2 JS runtime share the same OS clock, so there's
        // no meaningful drift to guard against here.
        var now = Date.now();
        var tMin = now - state.windowMs;
        var tMax = now;

        var hasRightAxis = state.order.some(function (id) {
            var s = state.series[id];
            return s && axisOf(s) === 'right';
        });

        var padL = 56 * dpr, padR = (hasRightAxis ? 56 : 10) * dpr, padT = 10 * dpr, padB = 26 * dpr;
        var plotW = Math.max(1, w - padL - padR);
        var plotH = Math.max(1, h - padT - padB);

        // Builds the points actually drawn for a series. OPC UA servers commonly only
        // report a notification when a value *changes*, so two consecutive real updates
        // can be far apart in time. Connecting them directly would draw a diagonal ramp,
        // wrongly implying the value drifted gradually between the two readings, when in
        // reality it was held constant and then stepped. To render this correctly (and so
        // a live tag visibly ticks forward even without new data), this resamples onto a
        // one-point-per-second heartbeat grid, holding the last known value at each tick,
        // while still keeping every real update at its exact timestamp so genuine changes
        // show up precisely. The result is anchored at tMin (using the most recent value
        // known as of the window start) and held flat out to tMax (now).
        var HEARTBEAT_MS = 1000;
        function effectivePoints(s) {
            if (s.points.length === 0) return [];

            var before = null;
            var real = [];
            for (var i = 0; i < s.points.length; i++) {
                var p = s.points[i];
                if (p.t <= tMin) before = p;
                else if (p.t <= tMax) real.push(p);
            }
            if (!before && real.length === 0) return [];

            var pts = [];
            var lastT = tMin;
            var lastV = before ? before.v : real[0].v;
            pts.push({ t: tMin, v: lastV });

            real.forEach(function (p) {
                var t = lastT + HEARTBEAT_MS;
                while (t < p.t) {
                    pts.push({ t: t, v: lastV });
                    t += HEARTBEAT_MS;
                }
                pts.push({ t: p.t, v: p.v });
                lastT = p.t;
                lastV = p.v;
            });

            var t = lastT + HEARTBEAT_MS;
            while (t < tMax) {
                pts.push({ t: t, v: lastV });
                t += HEARTBEAT_MS;
            }
            if (pts[pts.length - 1].t < tMax) pts.push({ t: tMax, v: lastV });

            return pts;
        }

        // Rounds a raw span to a "nice" value: 1, 2, 5 or 10 times a power of ten
        // (the classic Heckbert nice-numbers algorithm used by most charting
        // libraries for axis ticks).
        function niceNum(range, round) {
            if (!(range > 0) || !isFinite(range)) return 1;
            var exponent = Math.floor(Math.log10(range));
            var fraction = range / Math.pow(10, exponent);
            var niceFraction;
            if (round) {
                if (fraction < 1.5) niceFraction = 1;
                else if (fraction < 3) niceFraction = 2;
                else if (fraction < 7) niceFraction = 5;
                else niceFraction = 10;
            } else {
                if (fraction <= 1) niceFraction = 1;
                else if (fraction <= 2) niceFraction = 2;
                else if (fraction <= 5) niceFraction = 5;
                else niceFraction = 10;
            }
            return niceFraction * Math.pow(10, exponent);
        }

        // Snaps a data range to nice round boundaries and a round step size, so
        // labels land on values like 0/5/10 instead of whatever the current
        // min/max happen to be.
        function niceScale(min, max, targetTicks) {
            if (min === max) {
                min -= 1;
                max += 1;
            }
            var step = niceNum(niceNum(max - min, false) / Math.max(1, targetTicks - 1), true);
            var niceMin = Math.floor(min / step) * step;
            var niceMax = Math.ceil(max / step) * step;
            var ticks = [];
            for (var v = niceMin; v <= niceMax + step / 2; v += step) {
                ticks.push(Math.round(v / step) * step);
            }
            return { min: niceMin, max: niceMax, step: step, ticks: ticks };
        }

        function formatTick(v, step) {
            var decimals = step >= 1 ? 0 : Math.min(6, Math.max(0, Math.ceil(-Math.log10(step))));
            return v.toFixed(decimals);
        }

        // More vertical space means more room to fit labels without them crowding
        // into each other, so the tick count scales with the plot's pixel height
        // instead of always targeting a fixed number of labels.
        var targetTicks = Math.max(3, Math.min(20, Math.round(plotH / (42 * dpr)) + 1));

        function computeRange(axis) {
            var vMin = Infinity, vMax = -Infinity, any = false;
            state.order.forEach(function (id) {
                var s = state.series[id];
                if (!s || axisOf(s) !== axis) return;
                effectivePoints(s).forEach(function (p) {
                    any = true;
                    if (p.v < vMin) vMin = p.v;
                    if (p.v > vMax) vMax = p.v;
                });
            });
            if (!any) return null;
            return niceScale(vMin, vMax, targetTicks);
        }

        var leftRange = computeRange('left');
        var rightRange = hasRightAxis ? computeRange('right') : null;
        var anyPoints = !!leftRange || !!rightRange;
        var gridRange = leftRange || rightRange;

        // grid — drawn at the primary axis's nice tick values so gridlines line
        // up with round numbers instead of an arbitrary even split of the plot.
        ctx.strokeStyle = colors.grid;
        ctx.lineWidth = 1;
        if (gridRange) {
            gridRange.ticks.forEach(function (t) {
                var gy = padT + plotH - ((t - gridRange.min) / (gridRange.max - gridRange.min)) * plotH;
                ctx.beginPath();
                ctx.moveTo(padL, gy);
                ctx.lineTo(padL + plotW, gy);
                ctx.stroke();
            });
        } else {
            var yGridSteps = targetTicks;
            for (var i = 0; i <= yGridSteps; i++) {
                var gy0 = padT + (plotH * i / yGridSteps);
                ctx.beginPath();
                ctx.moveTo(padL, gy0);
                ctx.lineTo(padL + plotW, gy0);
                ctx.stroke();
            }
        }

        if (!anyPoints) {
            ctx.fillStyle = colors.label;
            ctx.font = (12 * dpr) + 'px sans-serif';
            ctx.fillText('Waiting for live data…', padL + 8 * dpr, padT + 20 * dpr);
            ctx.restore();
            return;
        }

        function xFor(t) { return padL + ((t - tMin) / (tMax - tMin)) * plotW; }
        function yForRange(range) {
            return function (v) { return padT + plotH - ((v - range.min) / (range.max - range.min)) * plotH; };
        }
        var yForLeft = leftRange ? yForRange(leftRange) : null;
        var yForRight = rightRange ? yForRange(rightRange) : null;

        // y-axis labels — placed at each axis's own nice tick values so the
        // numbers shown are always round, even if left/right ticks don't align.
        ctx.font = 'bold ' + (13 * dpr) + 'px monospace';
        ctx.textBaseline = 'middle';
        if (leftRange) {
            ctx.fillStyle = colors.label;
            ctx.textAlign = 'left';
            leftRange.ticks.forEach(function (t) {
                ctx.fillText(formatTick(t, leftRange.step), 4 * dpr, yForLeft(t));
            });
        }
        if (rightRange) {
            ctx.fillStyle = colors.label;
            ctx.textAlign = 'right';
            rightRange.ticks.forEach(function (t) {
                ctx.fillText(formatTick(t, rightRange.step), w - 4 * dpr, yForRight(t));
            });
        }

        // x-axis timestamp ticks
        var tickCount = 7;
        ctx.fillStyle = colors.label;
        ctx.font = 'bold ' + (12 * dpr) + 'px monospace';
        ctx.textBaseline = 'top';
        for (var ti = 0; ti < tickCount; ti++) {
            var frac = ti / (tickCount - 1);
            var tx = padL + plotW * frac;
            ctx.strokeStyle = colors.grid;
            ctx.beginPath();
            ctx.moveTo(tx, padT);
            ctx.lineTo(tx, padT + plotH);
            ctx.stroke();

            ctx.textAlign = frac <= 0.001 ? 'left' : (frac >= 0.999 ? 'right' : 'center');
            ctx.fillText(formatTime(tMin + (tMax - tMin) * frac), tx, padT + plotH + 4 * dpr);
        }
        ctx.textAlign = 'left';

        state.order.forEach(function (id) {
            var s = state.series[id];
            if (!s) return;
            var yFor = axisOf(s) === 'right' ? yForRight : yForLeft;
            if (!yFor) return;

            var pts = effectivePoints(s);
            if (pts.length === 0) return;

            ctx.strokeStyle = colorFor(id);
            ctx.lineWidth = 1.75 * dpr;
            ctx.beginPath();
            pts.forEach(function (p, idx) {
                var x = xFor(p.t), y = yFor(p.v);
                if (idx === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
            });
            ctx.stroke();

            // Marks every per-second (and real-update) point, not just the latest one,
            // so the chart visibly reads as a live series ticking forward each second
            // rather than a single line that only moves when a marker appears at the end.
            ctx.fillStyle = colorFor(id);
            pts.forEach(function (p) {
                ctx.beginPath();
                ctx.arc(xFor(p.t), yFor(p.v), 1.5 * dpr, 0, Math.PI * 2);
                ctx.fill();
            });

            var last = pts[pts.length - 1];
            ctx.fillStyle = colorFor(id);
            ctx.beginPath();
            ctx.arc(xFor(last.t), yFor(last.v), 2.5 * dpr, 0, Math.PI * 2);
            ctx.fill();
        });

        ctx.restore();
    }

    window.trendChart = {
        init: function (canvasEl) {
            if (!canvasEl) return;
            state.canvas = canvasEl;
            state.ctx = canvasEl.getContext('2d');
            state.wrap = canvasEl.parentElement;
            state.order = [];
            state.series = {};

            if (state.resizeObserver) {
                state.resizeObserver.disconnect();
            }
            if (window.ResizeObserver && state.wrap) {
                state.resizeObserver = new ResizeObserver(resizeCanvas);
                state.resizeObserver.observe(state.wrap);
            }

            // Redraw once a second even when no new point arrives, so the chart keeps
            // scrolling forward in real time and holds each series at its last known
            // value instead of freezing at the moment of the last update.
            if (state.tickInterval) {
                clearInterval(state.tickInterval);
            }
            state.tickInterval = setInterval(scheduleDraw, 1000);

            resizeCanvas();
        },

        // list: [{ nodeId, label, axis }] – the full current set of trended tags.
        // Reconciles against existing state, keeping point history for tags
        // that are still present so re-trending doesn't wipe the chart.
        setSeries: function (list) {
            var next = {};
            var nextOrder = [];
            (list || []).forEach(function (item) {
                nextOrder.push(item.nodeId);
                next[item.nodeId] = state.series[item.nodeId] || { points: [], label: item.label, axis: item.axis };
                next[item.nodeId].label = item.label;
                next[item.nodeId].axis = item.axis === 'right' ? 'right' : 'left';
            });
            state.order = nextOrder;
            state.series = next;
            scheduleDraw();
        },

        addPoint: function (nodeId, timestampMs, value) {
            // A point can arrive (e.g. the initial seed value for a newly-trended tag)
            // before Blazor's next render has called setSeries to register it — create
            // a placeholder series rather than dropping the point; setSeries will fill
            // in the real label/axis (and adopt these points) once it runs.
            var s = state.series[nodeId];
            if (!s) {
                s = state.series[nodeId] = { points: [], label: nodeId, axis: 'left' };
                if (state.order.indexOf(nodeId) < 0) state.order.push(nodeId);
            }

            s.points.push({ t: timestampMs, v: value });

            var cutoff = timestampMs - state.windowMs;
            while (s.points.length > 0 && s.points[0].t < cutoff) {
                s.points.shift();
            }
            if (s.points.length > MAX_POINTS_PER_SERIES) {
                s.points.splice(0, s.points.length - MAX_POINTS_PER_SERIES);
            }

            scheduleDraw();
        },

        removeSeries: function (nodeId) {
            delete state.series[nodeId];
            state.order = state.order.filter(function (id) { return id !== nodeId; });
            scheduleDraw();
        },

        clear: function () {
            state.series = {};
            state.order = [];
            scheduleDraw();
        },

        // Sets the visible time window (in milliseconds) shown on the chart.
        setWindow: function (ms) {
            if (typeof ms !== 'number' || !(ms > 0)) return;
            state.windowMs = ms;
            scheduleDraw();
        },

        resize: resizeCanvas,

        // Re-reads theme colors and redraws immediately (called when the
        // light/dark theme is toggled) so a visible chart doesn't wait for
        // the next data point to pick up the new palette.
        themeChanged: scheduleDraw
    };
})();
