// The browser half of every bulk import: it turns the chosen workbook into CSV text and stops
// there. SheetJS is loaded from a CDN in App.razor rather than added to the server — the standing
// project rule is no third-party dependencies on the deploy target, and a browser library ships to
// the browser, not the server.
//
// It used to do much more. A `parseFile(inputId, kind)` carried a header-alias map, its own idea
// of a bad row and its own typed output, in TWO shapes ('roster' and 'welfare'), while the staff
// import parsed the same kind of sheet in C#. Those three journeys are one now: every import runs
// through QImportPanel and ImportParsing, which own the aliases, the mapping, the sanitising, the
// preview and the progress. The last caller of parseFile — the welfare history import — moved on
// 2026-09-19 and it was deleted with its alias maps, so there is no second place for a column name
// to be recognised differently.
//
// Cells are read as their DISPLAYED text, so a phone number keeps its leading zero (duty rota
// plan §12.5).
//
// A WORKBOOK IS NOT ONE SHEET. Reading SheetNames[0] and saying nothing is how a school imports the
// "Summary" tab of a file whose staff are on the second one, so `sheets()` reports what is in the
// file and `sheetToCsv` takes the one the reader picked. A .xls that is really an HTML table — what
// most school management systems actually export — is sniffed by SheetJS and needs nothing here.
window.rosterImport = (function () {
    function fileOf(inputElementId) {
        const input = document.getElementById(inputElementId);
        return input?.files?.[0] ?? null;
    }

    function isCsv(file) {
        return /\.csv$/i.test(file.name) || file.type === 'text/csv';
    }

    function isJson(file) {
        return /\.(json|ndjson|jsonl)$/i.test(file.name) || file.type === 'application/json';
    }

    // ---- JSON ----------------------------------------------------------------------------------
    // A .json file is what another SYSTEM produces, where .xlsx is what a PERSON produces, so it is
    // the lightest integration path a school has. It is turned into exactly the grid every other
    // format produces — nested keys flattened to dot paths, the union of every row's keys as the
    // header row — so the mapping, the cleaning and the validation never learn it exists.
    const MAX_DEPTH = 5;
    const ARRAY_KEYS = ['data', 'records', 'items', 'rows', 'results', 'values'];

    function rowsOf(parsed) {
        if (Array.isArray(parsed)) return parsed;
        if (parsed && typeof parsed === 'object') {
            for (const key of ARRAY_KEYS) {
                if (Array.isArray(parsed[key])) return parsed[key];
            }
            const arrays = Object.values(parsed).filter(v => Array.isArray(v));
            if (arrays.length === 1) return arrays[0];
            // A single object is one record; anything else we cannot read as rows.
            if (Object.values(parsed).every(v => v === null || typeof v !== 'object')) return [parsed];
        }
        throw new Error('This JSON is not a list of records. Send an array of objects, an object holding one array (for example { "data": [ … ] }), or one JSON object per line.');
    }

    // An array INSIDE a row is joined rather than turned into rows of its own: inventing rows from a
    // nested array silently changes the record count, which is the one thing an import must not do.
    function flatten(value, prefix, out, depth) {
        if (depth > MAX_DEPTH) throw new Error(`This JSON nests deeper than ${MAX_DEPTH} levels. Flatten it first, or export it as a spreadsheet.`);
        if (value === null || value === undefined) { out[prefix] = ''; return; }
        if (Array.isArray(value)) {
            out[prefix] = value.map(v => (v !== null && typeof v === 'object') ? JSON.stringify(v) : String(v)).join('; ');
            return;
        }
        if (typeof value === 'object') {
            const keys = Object.keys(value);
            if (keys.length === 0) { out[prefix] = ''; return; }
            for (const k of keys) flatten(value[k], prefix ? `${prefix}.${k}` : k, out, depth + 1);
            return;
        }
        // Numbers are written as they came, never re-formatted: a phone number keeps its leading zero.
        out[prefix] = String(value);
    }

    function csvCell(v) {
        const s = v === null || v === undefined ? '' : String(v);
        return /[",\n\r]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s;
    }

    async function jsonToCsv(file) {
        const text = (await file.text()).trim();
        if (!text) return '';

        let parsed = null;
        try { parsed = JSON.parse(text); }
        catch {
            // NDJSON / JSON Lines: one object per line, what a streamed export emits.
            const lines = text.split(/\r?\n/).filter(l => l.trim().length);
            parsed = lines.map((line, i) => {
                try { return JSON.parse(line); }
                catch { throw new Error(`Line ${i + 1} is not valid JSON. A .ndjson file holds one JSON object per line.`); }
            });
        }

        const records = rowsOf(parsed).filter(r => r !== null && typeof r === 'object' && !Array.isArray(r));
        if (!records.length) throw new Error('This JSON holds no records.');

        const headers = [];
        const flat = records.map(r => {
            const out = {};
            flatten(r, '', out, 1);
            for (const k of Object.keys(out)) if (!headers.includes(k)) headers.push(k);
            return out;
        });

        const lines = [headers.map(csvCell).join(',')];
        for (const row of flat) lines.push(headers.map(h => csvCell(row[h])).join(','));
        return lines.join('\n');
    }

    async function workbookOf(file) {
        return XLSX.read(await file.arrayBuffer(), { type: 'array' });
    }

    /// Every sheet in the chosen workbook, with its row count, so the picker can say which one
    /// holds the data. A CSV is one unnamed sheet and answers with a single entry.
    async function sheets(inputElementId) {
        const file = fileOf(inputElementId);
        if (!file) return [];
        if (isCsv(file) || isJson(file)) {
            const text = await file.text();
            return [{ name: file.name, rows: text.split(/\r?\n/).filter(l => l.trim().length).length }];
        }
        const workbook = await workbookOf(file);
        return workbook.SheetNames.map(name => {
            const ref = workbook.Sheets[name]?.['!ref'];
            const rows = ref ? (XLSX.utils.decode_range(ref).e.r + 1) : 0;
            return { name, rows };
        });
    }

    /// The chosen sheet as CSV text. An unknown or absent name falls back to the first sheet, so a
    /// caller that does not offer the picker behaves exactly as it always did.
    async function sheetToCsv(inputElementId, sheetName) {
        const file = fileOf(inputElementId);
        if (!file) return '';
        if (isCsv(file)) return await file.text();
        if (isJson(file)) return await jsonToCsv(file);
        const workbook = await workbookOf(file);
        const name = sheetName && workbook.SheetNames.includes(sheetName) ? sheetName : workbook.SheetNames[0];
        return XLSX.utils.sheet_to_csv(workbook.Sheets[name], { blankrows: false, rawNumbers: false });
    }

    return { sheets, sheetToCsv };
})();
