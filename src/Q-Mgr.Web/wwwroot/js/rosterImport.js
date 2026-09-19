// The browser half of every bulk import: it turns the chosen workbook into CSV text and stops
// there. SheetJS is loaded from a CDN in App.razor rather than added to the server — the standing
// project rule is no third-party dependencies on the deploy target, and a browser library ships to
// the browser, not the server.
//
// It used to do much more. A `parseFile(inputId, kind)` carried a header-alias map, its own idea
// of a bad row and its own typed output, in TWO shapes ('roster' and 'welfare'), while the staff
// import parsed the same kind of sheet in C#. Those three journeys are one now: every import runs
// through QImportPanel and ImportParsing, which own the aliases, the validation, the preview and
// the progress. The last caller of parseFile — the welfare history import — moved on 2026-09-19
// and it was deleted with its alias maps, so there is no second place for a column name to be
// recognised differently.
//
// Cells are read as their DISPLAYED text, so a phone number keeps its leading zero (duty rota
// plan §12.5).
window.rosterImport = (function () {
    async function sheetToCsv(inputElementId) {
        const input = document.getElementById(inputElementId);
        const file = input?.files?.[0];
        if (!file) return '';
        if (/\.csv$/i.test(file.name) || file.type === 'text/csv') return await file.text();
        const workbook = XLSX.read(await file.arrayBuffer(), { type: 'array' });
        return XLSX.utils.sheet_to_csv(workbook.Sheets[workbook.SheetNames[0]], { blankrows: false, rawNumbers: false });
    }

    return { sheetToCsv };
})();
