// Drag-to-resize for <table> columns via a handle on the right edge of each
// resizable <th>. Resizes just that column's <col> element (table-layout:fixed),
// leaving the rest of the table alone. Uses event delegation so it keeps
// working across Blazor re-renders without any interop calls.
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

    function endDrag() {
        if (!drag) return;
        drag.handle.classList.remove('dragging');
        document.body.classList.remove('col-resizing');
        drag = null;
    }

    document.addEventListener('mousedown', function (e) {
        if (e.button !== 0) return;

        var handle = e.target.closest ? e.target.closest('.col-resize-handle') : null;
        if (!handle) return;

        var th = handle.closest('th');
        var table = th && th.closest('table');
        var colgroup = table && table.querySelector('colgroup');
        if (!th || !table || !colgroup) return;

        var cols = Array.prototype.slice.call(colgroup.children);
        var index = Array.prototype.indexOf.call(th.parentElement.children, th);
        var col = cols[index];
        if (!col || isFiller(col)) return;

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

    document.addEventListener('mouseup', endDrag);
    window.addEventListener('blur', endDrag);
})();
