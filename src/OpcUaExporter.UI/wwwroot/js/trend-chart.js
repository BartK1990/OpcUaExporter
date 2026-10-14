// Live multi-series trend chart rendered on a <canvas>, driven from Blazor
// via JS interop (OpcUaService.TrendUpdate -> Index.razor -> here).
(function () {
    var PALETTE = ['#f0a030', '#60a8f0', '#3ecf8e', '#f06060', '#c060f0', '#f0e060', '#60f0d0', '#f08060'];
    var DEFAULT_WINDOW_MS = 60 * 1000;
    var MAX_POINTS_PER_SERIES = 6000;
    // The chart is designed to tick forward once per second (see effectivePoints below).
    // Some OPC UA servers report data-change notifications faster than the requested
    // sampling interval regardless of what the client asks for, which would otherwise
    // make the chart visibly add more than one point per second per series.
    var HEARTBEAT_MS = 1000;

    var state = {
        canvas: null,
        ctx: null,
        wrap: null,
        order: [],          // nodeIds, in display/color order
        series: {},          // nodeId -> { points: [{t,v}], label, axis }
        windowMs: DEFAULT_WINDOW_MS,
        resizeObserver: null,
        rafScheduled: false,
        tickInterval: null,
        hover: null          // { x, y } in canvas pixels while the mouse is over the chart
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

    // Mirrors the chart's right-edge time into the header clock. Looked up on each draw
    // because Blazor re-creates the header whenever the chart panel is toggled.
    function updateClock(ms) {
        var panel = state.canvas && state.canvas.closest('.chart-panel');
        var clock = panel && panel.querySelector('.chart-clock');
        if (!clock) return;
        var text = formatTime(ms);
        if (clock.textContent !== text) clock.textContent = text;
    }

    // Compact value text for the hover tooltip: integers as-is, otherwise up to 4 decimals.
    function formatValue(v) {
        if (v === null || v === undefined || !isFinite(v)) return '-';
        if (Number.isInteger(v)) return String(v);
        return String(parseFloat(v.toFixed(4)));
    }

    function onMouseMove(e) {
        var dpr = window.devicePixelRatio || 1;
        state.hover = { x: e.offsetX * dpr, y: e.offsetY * dpr };
        scheduleDraw();
    }

    function onMouseLeave() {
        state.hover = null;
        scheduleDraw();
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
        var cs = getComputedStyle(document.documentElement);
        var tooltipBg = cs.getPropertyValue('--bg-1').trim() || '#13161b';
        var tooltipBorder = cs.getPropertyValue('--border-hi').trim() || '#3d4758';

        ctx.save();
        ctx.clearRect(0, 0, w, h);

        // Use real wall-clock time as the right edge of the window so the chart keeps
        // scrolling forward even when a tag hasn't produced a new value recently — the
        // last known value is then held out to the edge (see effectivePoints below).
        // The .NET host and the WebView2 JS runtime share the same OS clock, so there's
        // no meaningful drift to guard against here.
        //
        // The edge is snapped *down* to a whole HEARTBEAT_MS boundary rather than being
        // the raw Date.now(). Redraws don't land at an exact 1 s cadence, so an unsnapped
        // edge would slide the window by an arbitrary number of milliseconds each frame,
        // and with it the resampling grid in effectivePoints — moving every plotted point
        // slightly relative to the underlying samples and making segments visibly flip
        // between flat and sloped from one repaint to the next. Snapping makes the window
        // (and therefore the grid) advance in exact one-second steps, so a point once
        // plotted keeps its position until it scrolls off the left edge.
        var tMax = Math.floor(Date.now() / HEARTBEAT_MS) * HEARTBEAT_MS;
        var tMin = tMax - state.windowMs;
        updateClock(tMax);

        var hasRightAxis = state.order.some(function (id) {
            var s = state.series[id];
            return s && axisOf(s) === 'right';
        });

        var padL = 56 * dpr, padR = (hasRightAxis ? 56 : 10) * dpr, padT = 10 * dpr, padB = 26 * dpr;
        var plotW = Math.max(1, w - padL - padR);
        var plotH = Math.max(1, h - padT - padB);

        // Builds the points actually drawn for a series, resampled onto a strict
        // one-point-per-second grid so every series always has exactly one point
        // per second and consecutive points are simply connected with a straight
        // line (no mix of held-flat "steps" and diagonal "ramps" between points).
        // Each grid tick takes the most recent real value known as of that tick.
        //
        // The grid ticks are absolute (whole multiples of HEARTBEAT_MS since the
        // epoch, which is what tMin/tMax are snapped to above), never relative to
        // the moment of the redraw. That's what keeps a point stationary once it
        // has been plotted: the same grid tick always resolves to the same sample,
        // so the shape of the line only ever grows on the right and is clipped on
        // the left, instead of every segment being re-sampled at a slightly
        // different offset each frame.
        //
        // The grid starts at the window start (tMin) only if the series already
        // has history from before the window — otherwise it starts at the first
        // grid tick at or after the series' first real point, so nothing is drawn
        // before the moment the tag was actually added to the chart.
        function effectivePoints(s) {
            var all = s.points;
            if (all.length === 0) return [];

            // Value carried into the first drawn tick: the newest sample at or
            // before the window start, when the series has that much history.
            var idx = 0;
            var lastV = null;
            var hasHistory = false;
            while (idx < all.length && all[idx].t <= tMin) {
                lastV = all[idx].v;
                hasHistory = true;
                idx++;
            }

            var startT = hasHistory
                ? tMin
                : Math.ceil(all[idx].t / HEARTBEAT_MS) * HEARTBEAT_MS;
            if (startT > tMax) return [];

            var pts = [];
            for (var t = startT; t <= tMax; t += HEARTBEAT_MS) {
                // Samples newer than tMax are deliberately left pending: they get
                // picked up by the grid tick that actually covers them, so a value
                // never lands at one x position on one frame and a different one
                // on the next.
                while (idx < all.length && all[idx].t <= t) {
                    lastV = all[idx].v;
                    idx++;
                }
                pts.push({ t: t, v: lastV });
            }

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

        var drawn = [];   // [{ id, label, color, pts, yFor }] for the hover readout
        state.order.forEach(function (id) {
            var s = state.series[id];
            if (!s) return;
            var yFor = axisOf(s) === 'right' ? yForRight : yForLeft;
            if (!yFor) return;

            var pts = effectivePoints(s);
            if (pts.length === 0) return;
            drawn.push({ id: id, label: s.label, color: colorFor(id), pts: pts, yFor: yFor });

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

        drawHover(drawn);

        ctx.restore();

        // Vertical line at the hovered second, a marker on each series, and a tooltip
        // listing the timestamp and every series' value at that second. The hovered
        // time snaps to the same one-second grid the series are drawn on, so the values
        // shown are exactly the points under the line.
        function drawHover(series) {
            var hv = state.hover;
            if (!hv || hv.x < padL || hv.x > padL + plotW || hv.y < padT || hv.y > padT + plotH) return;

            var tRaw = tMin + ((hv.x - padL) / plotW) * (tMax - tMin);
            var tHover = Math.round(tRaw / HEARTBEAT_MS) * HEARTBEAT_MS;
            if (tHover < tMin) tHover = tMin;
            if (tHover > tMax) tHover = tMax;
            var x = xFor(tHover);

            ctx.strokeStyle = colors.label;
            ctx.globalAlpha = 0.55;
            ctx.lineWidth = 1 * dpr;
            ctx.setLineDash([4 * dpr, 3 * dpr]);
            ctx.beginPath();
            ctx.moveTo(x, padT);
            ctx.lineTo(x, padT + plotH);
            ctx.stroke();
            ctx.setLineDash([]);
            ctx.globalAlpha = 1;

            var rows = [];
            series.forEach(function (sr) {
                var i = Math.round((tHover - sr.pts[0].t) / HEARTBEAT_MS);
                if (i < 0 || i >= sr.pts.length) return;
                var p = sr.pts[i];
                if (p.v === null || p.v === undefined) return;
                ctx.fillStyle = sr.color;
                ctx.beginPath();
                ctx.arc(x, sr.yFor(p.v), 3.5 * dpr, 0, Math.PI * 2);
                ctx.fill();
                rows.push({ color: sr.color, text: sr.label + ': ' + formatValue(p.v) });
            });

            var fontPx = 12 * dpr;
            var lineH = 16 * dpr;
            var pad = 6 * dpr;
            var swatch = 8 * dpr;
            var title = formatTime(tHover);
            ctx.font = 'bold ' + fontPx + 'px monospace';
            var boxW = ctx.measureText(title).width;
            ctx.font = fontPx + 'px monospace';
            rows.forEach(function (r) {
                boxW = Math.max(boxW, swatch + 6 * dpr + ctx.measureText(r.text).width);
            });
            boxW += pad * 2;
            var boxH = pad * 2 + lineH * (rows.length + 1);

            var bx = x + 10 * dpr;
            if (bx + boxW > w - 2 * dpr) bx = x - 10 * dpr - boxW;
            if (bx < 2 * dpr) bx = 2 * dpr;
            var by = Math.min(Math.max(padT, hv.y - boxH / 2), Math.max(padT, padT + plotH - boxH));

            ctx.globalAlpha = 0.94;
            ctx.fillStyle = tooltipBg;
            ctx.fillRect(bx, by, boxW, boxH);
            ctx.globalAlpha = 1;
            ctx.strokeStyle = tooltipBorder;
            ctx.lineWidth = 1 * dpr;
            ctx.strokeRect(bx, by, boxW, boxH);

            ctx.textAlign = 'left';
            ctx.textBaseline = 'middle';
            ctx.fillStyle = colors.label;
            ctx.font = 'bold ' + fontPx + 'px monospace';
            ctx.fillText(title, bx + pad, by + pad + lineH / 2);
            ctx.font = fontPx + 'px monospace';
            rows.forEach(function (r, i) {
                var ry = by + pad + lineH * (i + 1) + lineH / 2;
                ctx.fillStyle = r.color;
                ctx.fillRect(bx + pad, ry - swatch / 2, swatch, swatch);
                ctx.fillStyle = colors.label;
                ctx.fillText(r.text, bx + pad + swatch + 6 * dpr, ry);
            });
        }
    }

    window.trendChart = {
        init: function (canvasEl) {
            if (!canvasEl) return;
            if (state.canvas && state.canvas !== canvasEl) {
                state.canvas.removeEventListener('mousemove', onMouseMove);
                state.canvas.removeEventListener('mouseleave', onMouseLeave);
            }
            state.canvas = canvasEl;
            state.ctx = canvasEl.getContext('2d');
            state.hover = null;
            // addEventListener ignores a listener already registered, so re-init on the same canvas is safe.
            canvasEl.addEventListener('mousemove', onMouseMove);
            canvasEl.addEventListener('mouseleave', onMouseLeave);
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

            // Single once-a-second redraw driver for live data: this keeps the chart
            // scrolling forward and holding each series at its last known value even
            // when no new point arrives, and — since addPoint itself never schedules a
            // draw — it's also what caps the whole chart to exactly one repaint per
            // second no matter how many tags are trended or how often they update.
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
                s = state.series[nodeId] = { points: [], label: nodeId, axis: 'left', lastCommitT: null };
                if (state.order.indexOf(nodeId) < 0) state.order.push(nodeId);
            }

            // Some servers push data-change notifications faster than once a second no
            // matter what sampling interval was requested. Drop those extra updates
            // outright (rather than folding them into the pending point) so the chart's
            // displayed value only ever changes once per second, not just its committed
            // history — otherwise the last point would still visibly jitter in between.
            if (s.lastCommitT !== null && (timestampMs - s.lastCommitT) < HEARTBEAT_MS) {
                return;
            }
            s.points.push({ t: timestampMs, v: value });
            s.lastCommitT = timestampMs;

            // Keep a little more than one window of history: draw() needs the newest
            // sample at or before the window start to anchor the left edge, and its
            // window edge can sit slightly further back than this sample's timestamp.
            var cutoff = timestampMs - state.windowMs - 2 * HEARTBEAT_MS;
            while (s.points.length > 0 && s.points[0].t < cutoff) {
                s.points.shift();
            }
            if (s.points.length > MAX_POINTS_PER_SERIES) {
                s.points.splice(0, s.points.length - MAX_POINTS_PER_SERIES);
            }

            // Deliberately no scheduleDraw() here. With several tags trended at once,
            // each one's addPoint call lands at a slightly different moment within the
            // same second, and redrawing on every call would make the whole chart visibly
            // repaint more than once per second. The once-a-second state.tickInterval
            // (see init) is the single driver for live-data redraws, so newly committed
            // points are picked up on its next tick instead of immediately.
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
