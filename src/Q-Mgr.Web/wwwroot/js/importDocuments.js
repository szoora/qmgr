// Documents for the imports (plan TERM_PROGRAMME_CALENDAR_AND_GATES §4): the browser's half.
//
// A Word document (.docx, .doc) is read by the Shared C# readers — this file only hands them the file's bytes.
// A PDF is laid out here, by pdf.js (already shipped for the flip-book, loaded ON DEMAND from the same CDN and the
// same version), and only its TEXT RUNS go back: page, left edge, baseline from the top, width, height and text.
// PdfTextGrid then turns those into a table. A PDF with no text runs is a scan, and the C# side refuses it in
// words (decision D3: no OCR). Spreadsheets for the programme import go through SheetJS exactly as the other
// imports do (rosterImport.js), one sheet at a time.
window.importDocuments = (function () {
    'use strict';

    var PDFJS_JS = 'https://cdn.jsdelivr.net/npm/pdfjs-dist@3.11.174/build/pdf.min.js';
    var PDFJS_WORKER = 'https://cdn.jsdelivr.net/npm/pdfjs-dist@3.11.174/build/pdf.worker.min.js';
    var MAX_PAGES = 40;
    var pdfPromise = null;

    function fileAt(inputId, index) {
        var input = document.getElementById(inputId);
        var files = input && input.files ? input.files : null;
        if (!files || files.length === 0) return null;
        return files[index || 0] || null;
    }

    function loadScript(src) {
        return new Promise(function (resolve, reject) {
            var existing = document.querySelector('script[src="' + src + '"]');
            if (existing && window.pdfjsLib) { resolve(); return; }
            var el = document.createElement('script');
            el.src = src;
            el.async = true;
            el.addEventListener('load', function () { resolve(); });
            el.addEventListener('error', function () { reject(new Error('Failed to load ' + src)); });
            document.head.appendChild(el);
        });
    }

    function ensurePdf() {
        if (!pdfPromise) {
            pdfPromise = (window.pdfjsLib ? Promise.resolve() : loadScript(PDFJS_JS)).then(function () {
                if (!window.pdfjsLib) throw new Error('The PDF reader could not be loaded. Check the connection and try again.');
                window.pdfjsLib.GlobalWorkerOptions.workerSrc = PDFJS_WORKER;
            }).catch(function (err) { pdfPromise = null; throw err; });
        }
        return pdfPromise;
    }

    /// The names of the files chosen on the input, in order.
    function fileNames(inputId) {
        var input = document.getElementById(inputId);
        if (!input || !input.files) return [];
        return Array.prototype.map.call(input.files, function (f) { return f.name; });
    }

    /// The bytes of the index-th chosen file (a Uint8Array arrives in .NET as byte[]).
    async function readFileBytes(inputId, index) {
        var file = fileAt(inputId, index);
        if (!file) return new Uint8Array(0);
        return new Uint8Array(await file.arrayBuffer());
    }

    /// The text runs of a PDF: [{ page, x, y, width, height, str }], y measured from the top of the page.
    async function pdfTextRuns(inputId, index) {
        var file = fileAt(inputId, index);
        if (!file) return [];
        await ensurePdf();
        var pdf = await window.pdfjsLib.getDocument({ data: new Uint8Array(await file.arrayBuffer()) }).promise;
        var runs = [];
        var pages = Math.min(pdf.numPages, MAX_PAGES);
        for (var p = 1; p <= pages; p++) {
            var page = await pdf.getPage(p);
            var viewport = page.getViewport({ scale: 1 });
            var content = await page.getTextContent();
            // Pages are stacked so a table that runs onto page two keeps its rows in order.
            var offset = (p - 1) * (viewport.height + 20);
            content.items.forEach(function (item) {
                if (!item.str || !item.str.trim()) return;
                var t = window.pdfjsLib.Util.transform(viewport.transform, item.transform);
                var height = item.height || Math.abs(t[3]) || 10;
                runs.push({ page: p, x: t[4], y: offset + t[5], width: item.width || 0, height: height, str: item.str });
            });
        }
        try { pdf.destroy(); } catch (e) { /* nothing to clean */ }
        return runs;
    }

    /// Every sheet of the index-th file as CSV: [{ name, csv }]. A CSV file is one sheet.
    async function sheetsAt(inputId, index) {
        var file = fileAt(inputId, index);
        if (!file) return [];
        if (/\.csv$/i.test(file.name) || file.type === 'text/csv') return [{ name: file.name, csv: await file.text() }];
        if (typeof XLSX === 'undefined') throw new Error('The spreadsheet reader has not loaded yet. Try again in a moment.');
        var workbook = XLSX.read(await file.arrayBuffer(), { type: 'array' });
        return workbook.SheetNames.map(function (name) {
            return { name: name, csv: XLSX.utils.sheet_to_csv(workbook.Sheets[name], { blankrows: false, rawNumbers: false }) };
        });
    }

    return { fileNames: fileNames, readFileBytes: readFileBytes, pdfTextRuns: pdfTextRuns, sheetsAt: sheetsAt };
})();
