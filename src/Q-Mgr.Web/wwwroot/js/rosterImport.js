// Client-side Excel/CSV parsing for the visiting-day roster bulk import — via SheetJS (CDN,
// loaded in App.razor), not a server-side library. The standing project rule is no third-party
// dependencies on the server/deploy target; a CDN-loaded browser library ships to the browser,
// not the server, so parsing happens here and only structured JSON rows ever reach the API.

window.rosterImport = (function () {
    // Header aliases a school's own spreadsheet or SMIS export realistically uses — matched
    // case/whitespace-insensitively so "Student Code", "student_code", and "StudentCode" all work.
    const HEADER_ALIASES = {
        studentcode: ['studentcode', 'student code', 'admissionno', 'admission no', 'admissionnumber', 'rollno', 'roll no'],
        studentfullname: ['studentfullname', 'studentname', 'student name', 'student full name', 'name'],
        classname: ['classname', 'class name', 'class', 'grade', 'stream'],
        guardianfullname: ['guardianfullname', 'guardianname', 'guardian name', 'guardian full name', 'parentname', 'parent name'],
        guardianphone: ['guardianphone', 'guardian phone', 'phone', 'phonenumber', 'phone number', 'mobile', 'contact'],
        guardianemail: ['guardianemail', 'guardian email', 'email', 'emailaddress', 'email address'],
        relationship: ['relationship', 'relation']
    };

    function normalizeHeader(h) {
        return String(h || '').trim().toLowerCase().replace(/[_-]/g, ' ').replace(/\s+/g, ' ');
    }

    function buildHeaderMap(headers) {
        const map = {};
        const normalized = headers.map(normalizeHeader);
        for (const [field, aliases] of Object.entries(HEADER_ALIASES)) {
            const idx = normalized.findIndex(h => aliases.includes(h));
            if (idx >= 0) map[field] = idx;
        }
        return map;
    }

    // Minimal RFC-4180-ish CSV line splitter: handles quoted fields (including embedded commas
    // and escaped "" quotes) without pulling in a library for something this small. Used instead
    // of routing .csv through SheetJS's own CSV-to-sheet conversion, which auto-detects
    // numeric-looking cells and silently drops leading zeros from phone numbers like
    // "0701234567" — CSV has no real cell types to tell "071..." apart from the number 71, so
    // that ambiguity has to be resolved by never letting it guess in the first place, not by any
    // read-option flag (raw:false on sheet_to_json alone was found live not to fix it).
    function parseCsvText(text) {
        const rows = [];
        let row = [], field = '', inQuotes = false;
        // Normalize line endings, then walk character by character.
        text = text.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
        for (let i = 0; i < text.length; i++) {
            const c = text[i];
            if (inQuotes) {
                if (c === '"') {
                    if (text[i + 1] === '"') { field += '"'; i++; } else { inQuotes = false; }
                } else {
                    field += c;
                }
            } else if (c === '"') {
                inQuotes = true;
            } else if (c === ',') {
                row.push(field); field = '';
            } else if (c === '\n') {
                row.push(field); field = '';
                rows.push(row); row = [];
            } else {
                field += c;
            }
        }
        if (field.length > 0 || row.length > 0) { row.push(field); rows.push(row); }
        return rows.filter(r => r.length > 1 || (r[0] ?? '').trim() !== '');
    }

    // Shared by both the XLSX path (raw = SheetJS's 2D array output) and the CSV path (raw = this
    // file's own hand-parsed 2D array) — everything past "get me a grid of strings" is identical.
    function rowsFromArray(raw) {
        if (raw.length === 0) return { rows: [], headerErrors: ['The file appears to be empty.'] };

        const headerMap = buildHeaderMap(raw[0]);
        const headerErrors = [];
        if (headerMap.studentfullname === undefined) headerErrors.push('No "Student Name" column found.');
        if (headerMap.guardianfullname === undefined) headerErrors.push('No "Guardian Name" column found.');
        if (headerMap.guardianphone === undefined && headerMap.guardianemail === undefined)
            headerErrors.push('No "Guardian Phone" or "Guardian Email" column found — at least one is required.');

        if (headerErrors.length > 0) return { rows: [], headerErrors };

        const rows = [];
        for (let i = 1; i < raw.length; i++) {
            const r = raw[i];
            const cell = (field) => headerMap[field] !== undefined ? String(r[headerMap[field]] ?? '').trim() : '';
            const row = {
                studentCode: cell('studentcode') || null,
                studentFullName: cell('studentfullname'),
                className: cell('classname') || null,
                guardianFullName: cell('guardianfullname'),
                guardianPhone: cell('guardianphone') || null,
                guardianEmail: cell('guardianemail') || null,
                relationship: cell('relationship') || null
            };
            // Skip fully blank rows (common at the end of a spreadsheet) rather than sending them
            // to the server just to have them fail validation there.
            if (row.studentFullName || row.guardianFullName || row.studentCode) rows.push(row);
        }

        return { rows, headerErrors: [] };
    }

    // Reads the given <input type="file"> element's currently-selected file and returns
    // { fileName, rows, headerErrors } as a JSON string — parsing happens entirely here so the
    // Blazor side only ever deals with already-structured data, not file I/O.
    async function parseFile(inputElementId) {
        const input = document.getElementById(inputElementId);
        const file = input?.files?.[0];
        if (!file) return JSON.stringify({ fileName: null, rows: [], headerErrors: ['No file selected.'] });

        const isCsv = /\.csv$/i.test(file.name) || file.type === 'text/csv';

        try {
            let raw;
            if (isCsv) {
                raw = parseCsvText(await file.text());
            } else {
                const buffer = await file.arrayBuffer();
                const workbook = XLSX.read(buffer, { type: 'array' });
                raw = XLSX.utils.sheet_to_json(workbook.Sheets[workbook.SheetNames[0]], { header: 1, defval: '', blankrows: false, raw: false });
            }
            const { rows, headerErrors } = rowsFromArray(raw);
            return JSON.stringify({ fileName: file.name, rows, headerErrors });
        } catch (err) {
            return JSON.stringify({ fileName: file.name, rows: [], headerErrors: ['Could not read this file — is it a valid .xlsx/.xls/.csv file? (' + err.message + ')'] });
        }
    }

    return { parseFile };
})();
