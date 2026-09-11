// Drag-to-resize for the app's split panes. Uses event delegation so it
// keeps working across Blazor re-renders without any interop calls.
(function () {
    var colDrag = null; // column (left/right) resize via .pane-divider
    var rowDrag = null; // row (top/bottom) resize via .row-divider

    document.addEventListener('mousedown', function (e) {
        var colDivider = e.target.closest('.pane-divider');
        if (colDivider) {
            var layout = colDivider.parentElement;
            var left = colDivider.previousElementSibling;
            var right = colDivider.nextElementSibling;
            if (!layout || !left || !right) return;

            colDrag = {
                left: left,
                startX: e.clientX,
                startLeftWidth: left.getBoundingClientRect().width,
                layoutWidth: layout.getBoundingClientRect().width
            };

            colDivider.classList.add('dragging');
            document.body.classList.add('pane-resizing');
            e.preventDefault();
            return;
        }

        var rowDivider = e.target.closest('.row-divider');
        if (rowDivider) {
            var container = rowDivider.parentElement;
            var panel = rowDivider.nextElementSibling;
            if (!container || !panel) return;

            rowDrag = {
                panel: panel,
                startY: e.clientY,
                startHeight: panel.getBoundingClientRect().height,
                containerHeight: container.getBoundingClientRect().height
            };

            rowDivider.classList.add('dragging');
            document.body.classList.add('row-resizing');
            e.preventDefault();
        }
    });

    document.addEventListener('mousemove', function (e) {
        if (colDrag) {
            var minWidth = 200;
            var maxWidth = Math.max(minWidth, colDrag.layoutWidth - minWidth - 6);
            var newWidth = colDrag.startLeftWidth + (e.clientX - colDrag.startX);
            newWidth = Math.min(maxWidth, Math.max(minWidth, newWidth));
            colDrag.left.style.flex = '0 0 ' + newWidth + 'px';
        }

        if (rowDrag) {
            var minHeight = 120;
            var maxHeight = Math.max(minHeight, rowDrag.containerHeight - 200);
            var newHeight = rowDrag.startHeight - (e.clientY - rowDrag.startY);
            newHeight = Math.min(maxHeight, Math.max(minHeight, newHeight));
            rowDrag.panel.style.flex = '0 0 ' + newHeight + 'px';
        }
    });

    document.addEventListener('mouseup', function () {
        if (colDrag) {
            document.querySelectorAll('.pane-divider.dragging').forEach(function (d) {
                d.classList.remove('dragging');
            });
            document.body.classList.remove('pane-resizing');
            colDrag = null;
        }

        if (rowDrag) {
            document.querySelectorAll('.row-divider.dragging').forEach(function (d) {
                d.classList.remove('dragging');
            });
            document.body.classList.remove('row-resizing');
            rowDrag = null;
        }
    });
})();
