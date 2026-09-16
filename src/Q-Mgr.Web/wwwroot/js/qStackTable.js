// qStackTable — a table marked `q-stack` turns into a list of cards on a phone.
//
// The CSS (q-components.css, "Stacked tables") needs each cell to carry the label of its column,
// and hand-writing data-label on every <td> of every list page is how labels drift from headers.
// So the labels are derived here, once, from the table's own <thead>: whenever Blazor adds or
// re-renders rows, a MutationObserver stamps data-label onto the new cells. Blazor never sets
// data-label itself, so its render diff leaves the attribute alone.
//
// Desktop is untouched — the attribute is only read by the phone-width CSS.
(function () {
    function label(table) {
        var head = table.tHead && table.tHead.rows[0];
        if (!head) return;
        var names = [];
        for (var i = 0; i < head.cells.length; i++) {
            var cell = head.cells[i];
            var span = cell.colSpan || 1;
            var text = (cell.innerText || cell.textContent || "").trim();
            for (var s = 0; s < span; s++) names.push(text);
        }
        for (var b = 0; b < table.tBodies.length; b++) {
            var rows = table.tBodies[b].rows;
            for (var r = 0; r < rows.length; r++) {
                var col = 0;
                for (var c = 0; c < rows[r].cells.length; c++) {
                    var td = rows[r].cells[c];
                    var name = names[col] || "";
                    if (td.getAttribute("data-label") !== name) td.setAttribute("data-label", name);
                    col += td.colSpan || 1;
                }
            }
        }
    }

    function scan(root) {
        var tables = (root.querySelectorAll ? root : document).querySelectorAll("table.q-stack");
        for (var i = 0; i < tables.length; i++) label(tables[i]);
        if (root.matches && root.matches("table.q-stack")) label(root);
    }

    var pending = false;
    function schedule() {
        if (pending) return;
        pending = true;
        requestAnimationFrame(function () { pending = false; scan(document); });
    }

    function start() {
        scan(document);
        new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true, characterData: true });
    }

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start);
    else start();
})();
