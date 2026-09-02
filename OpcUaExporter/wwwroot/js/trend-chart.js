// Live multi-series trend chart rendered on a <canvas>, driven from Blazor
// via JS interop (OpcUaService.TrendUpdate -> Index.razor -> here).
(function () {
    var PALETTE = ['#f0a030', '#60a8f0', '#3ecf8e', '#f06060', '#c060f0', '#f0e060', '#60f0d0', '#f08060'];
    var WINDOW_MS = 3 * 60 * 1000;
    var MAX_POINTS_PER_SERIES = 1200;

    var state = {
        canvas: null,
        ctx: null,
        wrap: null,
        order: [],          // nodeIds, in display/color order
        series: {},          // nodeId -> { points: [{t,v}], label }
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
        var tMin = now - WINDOW_MS;
        var tMax = now;

        var padL = 46 * dpr, padR = 10 * dpr, padT = 10 * dpr, padB = 10 * dpr;
        var plotW = Math.max(1, w - padL - padR);
        var plotH = Math.max(1, h - padT - padB);

        var vMin = Infinity, vMax = -Infinity, anyPoints = false;

        state.order.forEach(function (id) {
            var s = state.series[id];
            if (!s) return;
            s.points.forEach(function (p) {
                if (p.t < tMin) return;
                anyPoints = true;
                if (p.v < vMin) vMin = p.v;
                if (p.v > vMax) vMax = p.v;
            });
        });

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

        if (vMin === vMax) {
            vMin -= 1;
            vMax += 1;
        } else {
            var pad = (vMax - vMin) * 0.08;
            vMin -= pad;
            vMax += pad;
        }

        function xFor(t) { return padL + ((t - tMin) / (tMax - tMin)) * plotW; }
        function yFor(v) { return padT + plotH - ((v - vMin) / (vMax - vMin)) * plotH; }

        // y-axis labels
        ctx.fillStyle = 'rgba(255,255,255,0.45)';
        ctx.font = (10 * dpr) + 'px monospace';
        ctx.textBaseline = 'middle';
        ctx.fillText(vMax.toFixed(2), 4 * dpr, padT);
        ctx.fillText(vMin.toFixed(2), 4 * dpr, padT + plotH);

        state.order.forEach(function (id) {
            var s = state.series[id];
            if (!s || s.points.length === 0) return;

            var visible = s.points.filter(function (p) { return p.t >= tMin; });
            if (visible.length === 0) return;

            ctx.strokeStyle = colorFor(id);
            ctx.lineWidth = 1.75 * dpr;
            ctx.beginPath();
            visible.forEach(function (p, idx) {
                var x = xFor(p.t), y = yFor(p.v);
                if (idx === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
            });
            ctx.stroke();

            var last = visible[visible.length - 1];
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

        // list: [{ nodeId, label }] – the full current set of trended tags.
        // Reconciles against existing state, keeping point history for tags
        // that are still present so re-trending doesn't wipe the chart.
        setSeries: function (list) {
            var next = {};
            var nextOrder = [];
            (list || []).forEach(function (item) {
                nextOrder.push(item.nodeId);
                next[item.nodeId] = state.series[item.nodeId] || { points: [], label: item.label };
                next[item.nodeId].label = item.label;
            });
            state.order = nextOrder;
            state.series = next;
            scheduleDraw();
        },

        addPoint: function (nodeId, timestampMs, value) {
            var s = state.series[nodeId];
            if (!s) return;

            s.points.push({ t: timestampMs, v: value });

            var cutoff = timestampMs - WINDOW_MS;
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

        resize: resizeCanvas
    };
})();
