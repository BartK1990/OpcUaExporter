// Drag-to-resize for <table> columns via a handle on the right edge of each
// resizable <th>; double-clicking a handle fits the column to its content.
// Resizes just that column's <col> element (table-layout:fixed), leaving the
// rest of the table alone. Uses event delegation so it keeps working across
// Blazor re-renders without any interop calls.
//
// Keeping the dragged edge under the pointer means keeping the table's total
// width under control: with table-layout:fixed the browser shares any width the
// columns don't claim back out over them, so a column set to N px renders wider
// than N and the edge drifts away from the cursor. Two things prevent that here
// - every column is pinned to a pixel width when the drag starts, and the spare
// width goes to a trailing auto-width column (<col class="col-filler">) if the
// table has one, or to the table's own width if it doesn't.
(function () {
    var MIN_WIDTH = 32;
    // Autofit ceiling: a single very long Node ID would otherwise push the
    // column past the width of the panel and leave the user dragging it back.
    var MAX_AUTOFIT_WIDTH = 600;
    var drag = null;

    function isFiller(col) {
        return col.classList.contains('col-filler');
    }

    // <col> boxes report the rendered column width in Chromium; fall back to the
    // header cell so a browser that doesn't lay <col> out can't collapse a column.
    function measure(col, headerRow, index) {
        var width = col.getBoundingClientRect().width;
        if (!width && headerRow && headerRow.children[index]) {
            width = headerRow.children[index].getBoundingClientRect().width;
        }
        return width;
    }

    // The table plumbing behind a resize handle, or null if the handle isn't in
    // a table this script can resize.
    function target(e) {
        var handle = e.target.closest ? e.target.closest('.col-resize-handle') : null;
        if (!handle) return null;

        var th = handle.closest('th');
        var table = th && th.closest('table');
        var colgroup = table && table.querySelector('colgroup');
        if (!th || !table || !colgroup) return null;

        var cols = Array.prototype.slice.call(colgroup.children);
        var index = Array.prototype.indexOf.call(th.parentElement.children, th);
        var col = cols[index];
        if (!col || isFiller(col)) return null;

        return { handle: handle, th: th, table: table, cols: cols, col: col, index: index };
    }

    function endDrag() {
        if (!drag) return;
        drag.handle.classList.remove('dragging');
        document.body.classList.remove('col-resizing');
        drag = null;
    }

    document.addEventListener('mousedown', function (e) {
        if (e.button !== 0) return;

        var t = target(e);
        if (!t) return;
        var handle = t.handle, th = t.th, table = t.table, cols = t.cols, col = t.col, index = t.index;

        // Pin the current layout: a column still on an auto/percentage width
        // would be re-laid-out as the drag changes the totals, moving the edge
        // by something other than the pointer delta. Measure every column
        // before writing any of them - a write reflows the table, so measuring
        // and writing in one pass would read widths that have already shifted.
        var headerRow = th.parentElement;
        var widths = cols.map(function (c, i) { return measure(c, headerRow, i); });
        var startWidth = widths[index];
        if (!startWidth) return;

        var total = 0;
        var hasFiller = false;
        cols.forEach(function (c, i) {
            total += widths[i];
            if (isFiller(c)) {
                hasFiller = true;
                return;
            }
            if (widths[i]) c.style.width = widths[i] + 'px';
        });

        drag = {
            col: col,
            table: table,
            handle: handle,
            startX: e.clientX,
            startWidth: startWidth,
            // No filler column to take up the slack, so the table itself has to
            // grow and shrink with the drag or the spare width is shared out
            // over the columns again.
            startTableWidth: hasFiller ? 0 : total
        };

        handle.classList.add('dragging');
        document.body.classList.add('col-resizing');
        e.preventDefault();
    });

    document.addEventListener('mousemove', function (e) {
        if (!drag) return;

        // The button was released somewhere we couldn't see it (outside the
        // window, over a native dialog); stop tracking rather than keep resizing.
        if (!(e.buttons & 1)) {
            endDrag();
            return;
        }

        var newWidth = Math.max(MIN_WIDTH, drag.startWidth + (e.clientX - drag.startX));
        drag.col.style.width = newWidth + 'px';
        if (drag.startTableWidth) {
            drag.table.style.width = (drag.startTableWidth + newWidth - drag.startWidth) + 'px';
        }
        e.preventDefault();
    });

    // Double-click a handle to size its column to the widest thing in it.
    // Fixed layout clips the cells, so nothing in the rendered table reports a
    // content width: drop the pinned widths and let the browser lay the table
    // out automatically for one measurement, then put everything back. It all
    // happens in one handler, so the intermediate layout is never painted.
    document.addEventListener('dblclick', function (e) {
        var t = target(e);
        if (!t) return;

        var scroller = t.table.parentElement;
        var scrollLeft = scroller ? scroller.scrollLeft : 0;
        var savedWidths = t.cols.map(function (c) { return c.style.width; });
        var savedLayout = t.table.style.tableLayout;
        var savedTableWidth = t.table.style.width;

        t.cols.forEach(function (c) { c.style.width = ''; });
        t.table.style.tableLayout = 'auto';
        // Shrink-to-fit rather than the stylesheet's 100%, so each column ends
        // up at its own content width instead of a share of the panel.
        t.table.style.width = 'auto';
        var natural = t.th.getBoundingClientRect().width;

        t.cols.forEach(function (c, i) { c.style.width = savedWidths[i]; });
        t.table.style.tableLayout = savedLayout;
        t.table.style.width = savedTableWidth;
        if (scroller) scroller.scrollLeft = scrollLeft;

        if (!natural) return;
        // A hair of slack: collapsed borders are shared between cells, so the
        // measured box can land a fraction under what the text needs.
        natural = Math.ceil(natural) + 1;
        var newWidth = Math.min(MAX_AUTOFIT_WIDTH, Math.max(MIN_WIDTH, natural));
        t.col.style.width = newWidth + 'px';

        // A table pinned to a pixel width by an earlier drag (one with no filler
        // column) has to follow, or the width the column gave up is left over
        // and shared out over the columns again.
        var pinned = parseFloat(savedTableWidth);
        var previous = parseFloat(savedWidths[t.index]);
        if (pinned && previous) {
            t.table.style.width = (pinned + newWidth - previous) + 'px';
        }
        e.preventDefault();
    });

    document.addEventListener('mouseup', endDrag);
    window.addEventListener('blur', endDrag);
})();
