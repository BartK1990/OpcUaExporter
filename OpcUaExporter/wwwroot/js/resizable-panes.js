// Drag-to-resize for the Tag Browser's two panes. Uses event delegation
// so it keeps working across Blazor re-renders without any interop calls.
(function () {
    var dragging = null;

    document.addEventListener('mousedown', function (e) {
        var divider = e.target.closest('.pane-divider');
        if (!divider) return;

        var layout = divider.parentElement;
        var left = divider.previousElementSibling;
        var right = divider.nextElementSibling;
        if (!layout || !left || !right) return;

        dragging = {
            left: left,
            startX: e.clientX,
            startLeftWidth: left.getBoundingClientRect().width,
            layoutWidth: layout.getBoundingClientRect().width
        };

        divider.classList.add('dragging');
        document.body.classList.add('pane-resizing');
        e.preventDefault();
    });

    document.addEventListener('mousemove', function (e) {
        if (!dragging) return;

        var minWidth = 200;
        var maxWidth = Math.max(minWidth, dragging.layoutWidth - minWidth - 6);
        var newWidth = dragging.startLeftWidth + (e.clientX - dragging.startX);
        newWidth = Math.min(maxWidth, Math.max(minWidth, newWidth));

        dragging.left.style.flex = '0 0 ' + newWidth + 'px';
    });

    document.addEventListener('mouseup', function () {
        if (!dragging) return;

        document.querySelectorAll('.pane-divider.dragging').forEach(function (d) {
            d.classList.remove('dragging');
        });
        document.body.classList.remove('pane-resizing');
        dragging = null;
    });
})();
