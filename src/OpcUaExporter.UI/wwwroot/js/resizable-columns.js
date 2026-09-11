// Drag-to-resize for <table> columns via a handle on the right edge of each
// resizable <th>. Resizes just that column's <col> element (table-layout:fixed),
// leaving the rest of the table alone. Uses event delegation so it keeps
// working across Blazor re-renders without any interop calls.
(function () {
    var drag = null;

    document.addEventListener('mousedown', function (e) {
        var handle = e.target.closest('.col-resize-handle');
        if (!handle) return;

        var th = handle.closest('th');
        var table = th && th.closest('table');
        var colgroup = table && table.querySelector('colgroup');
        if (!th || !table || !colgroup) return;

        var index = Array.prototype.indexOf.call(th.parentElement.children, th);
        var col = colgroup.children[index];
        if (!col) return;

        drag = {
            col: col,
            startX: e.clientX,
            startWidth: col.getBoundingClientRect().width
        };

        handle.classList.add('dragging');
        document.body.classList.add('col-resizing');
        e.preventDefault();
    });

    document.addEventListener('mousemove', function (e) {
        if (!drag) return;
        var minWidth = 32;
        var newWidth = Math.max(minWidth, drag.startWidth + (e.clientX - drag.startX));
        drag.col.style.width = newWidth + 'px';
    });

    document.addEventListener('mouseup', function () {
        if (!drag) return;
        document.querySelectorAll('.col-resize-handle.dragging').forEach(function (h) {
            h.classList.remove('dragging');
        });
        document.body.classList.remove('col-resizing');
        drag = null;
    });
})();
