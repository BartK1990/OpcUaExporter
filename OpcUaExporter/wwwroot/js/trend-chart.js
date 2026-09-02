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
        rafScheduled: false
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

    function draw() {
        var ctx = state.ctx;
        if (!ctx || !state.canvas) return;

        var dpr = window.devicePixelRatio || 1;
        var w = state.canvas.width;
        var h = state.canvas.height;

        ctx.save();
        ctx.clearRect(0, 0, w, h);

        // Anchor the visible window to the newest data point actually received rather than
        // an independently-computed wall-clock "now" — keeps the chart correct even if the
        // .NET host clock and the WebView2 JS clock ever drift apart.
        var latestT = -Infinity;
        state.order.forEach(function (id) {
            var s = state.series[id];
            if (!s) return;
            s.points.forEach(function (p) {
                if (p.t > latestT) latestT = p.t;
            });
        });
        var now = latestT === -Infinity ? Date.now() : latestT;
        var tMin = now - state.windowMs;
        var tMax = now;

        var hasRightAxis = state.order.some(function (id) {
            var s = state.series[id];
            return s && axisOf(s) === 'right';
        });

        var padL = 46 * dpr, padR = (hasRightAxis ? 46 : 10) * dpr, padT = 10 * dpr, padB = 20 * dpr;
        var plotW = Math.max(1, w - padL - padR);
        var plotH = Math.max(1, h - padT - padB);

        // Builds the points actually drawn for a series: anchored at tMin using the most
        // recent value known as of the window start (so a tag that hasn't updated in a
        // while still shows its last value instead of nothing), and held flat out to tMax
        // (now) so the line always reflects the tag's current value, not just update times.
        function effectivePoints(s) {
            if (s.points.length === 0) return [];

            var before = null;
            var visible = [];
            for (var i = 0; i < s.points.length; i++) {
                var p = s.points[i];
                if (p.t <= tMin) before = p;
                else if (p.t <= tMax) visible.push(p);
            }

            var pts = [];
            if (before) pts.push({ t: tMin, v: before.v });
            Array.prototype.push.apply(pts, visible);
            if (pts.length === 0) return [];

            var last = pts[pts.length - 1];
            if (last.t < tMax) pts.push({ t: tMax, v: last.v });
            return pts;
        }

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
            if (vMin === vMax) {
                vMin -= 1;
                vMax += 1;
            } else {
                var pad = (vMax - vMin) * 0.08;
                vMin -= pad;
                vMax += pad;
            }
            return { min: vMin, max: vMax };
        }

        var leftRange = computeRange('left');
        var rightRange = hasRightAxis ? computeRange('right') : null;
        var anyPoints = !!leftRange || !!rightRange;

        // grid
        ctx.strokeStyle = 'rgba(255,255,255,0.06)';
        ctx.lineWidth = 1;
        for (var i = 0; i <= 4; i++) {
            var gy = padT + (plotH * i / 4);
            ctx.beginPath();
            ctx.moveTo(padL, gy);
            ctx.lineTo(padL + plotW, gy);
            ctx.stroke();
        }

        if (!anyPoints) {
            ctx.fillStyle = 'rgba(255,255,255,0.35)';
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

        // y-axis labels
        ctx.font = (10 * dpr) + 'px monospace';
        ctx.textBaseline = 'middle';
        if (leftRange) {
            ctx.fillStyle = 'rgba(255,255,255,0.45)';
            ctx.textAlign = 'left';
            ctx.fillText(leftRange.max.toFixed(2), 4 * dpr, padT);
            ctx.fillText(leftRange.min.toFixed(2), 4 * dpr, padT + plotH);
        }
        if (rightRange) {
            ctx.fillStyle = 'rgba(255,255,255,0.45)';
            ctx.textAlign = 'right';
            ctx.fillText(rightRange.max.toFixed(2), w - 4 * dpr, padT);
            ctx.fillText(rightRange.min.toFixed(2), w - 4 * dpr, padT + plotH);
        }

        // x-axis timestamp ticks
        var tickCount = 5;
        ctx.fillStyle = 'rgba(255,255,255,0.45)';
        ctx.font = (10 * dpr) + 'px monospace';
        ctx.textBaseline = 'top';
        for (var ti = 0; ti < tickCount; ti++) {
            var frac = ti / (tickCount - 1);
            var tx = padL + plotW * frac;
            ctx.strokeStyle = 'rgba(255,255,255,0.06)';
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
            var s = state.series[nodeId];
            if (!s) return;

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

        resize: resizeCanvas
    };
})();
