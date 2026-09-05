// Q-Mgr shared data export — the browser half.
//
// Every format this app offers is produced HERE, in the browser, and that is a deliberate
// architectural choice rather than a convenience: the project's standing rule is no third-party
// dependencies on the server, and real .xlsx or .pdf writing on the server means a NuGet package
// in the deploy target. Client-side libraries are explicitly outside that rule and already used
// throughout, and SheetJS is already loaded app-wide for parsing roster uploads — so the workbook
// writer below costs nothing new at all.
//
// C# builds the rows; this file turns them into a file the browser saves.

window.qmgrExport = (function () {
    'use strict';

    // A data: URL is fine for a few kilobytes of CSV and wrong for a 900-row workbook — the URL
    // length limit bites long before the data does. Everything here goes through a Blob and an
    // object URL, revoked on the next tick so the download has taken the reference.
    function saveBlob(blob, filename) {
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }

    function saveText(text, mimeType, filename) {
        saveBlob(new Blob([text], { type: mimeType + ';charset=utf-8' }), filename);
        return true;
    }

    /**
     * Writes a real .xlsx workbook.
     *
     * `columns` carries a type per column so a student code stays text instead of being read as a
     * number and losing its leading zeros — the single most common way a spreadsheet export
     * corrupts an identifier. Rows arrive as arrays of strings; this is where they become typed
     * cells.
     */
    function saveWorkbook(sheetName, header, rows, columnTypes, columnWidths, filename) {
        if (!window.XLSX) {
            console.error('[export] SheetJS is not loaded');
            return false;
        }

        const aoa = [header];
        for (const row of rows) {
            aoa.push(row.map((value, i) => coerce(value, columnTypes[i])));
        }

        const sheet = window.XLSX.utils.aoa_to_sheet(aoa, { cellDates: true });

        // Column widths, so nobody opens the file to a column of ####.
        sheet['!cols'] = columnWidths.map(w => ({ wch: w }));

        // Freeze the header row — a roster is hundreds of rows and the header is what makes them
        // readable.
        sheet['!freeze'] = { xSplit: 0, ySplit: 1 };
        sheet['!autofilter'] = { ref: window.XLSX.utils.encode_range({
            s: { r: 0, c: 0 },
            e: { r: aoa.length - 1, c: Math.max(0, header.length - 1) }
        }) };

        const book = window.XLSX.utils.book_new();
        // Excel refuses a sheet name over 31 characters or containing : \ / ? * [ ]
        const safeSheet = (sheetName || 'Data').replace(/[:\\/?*[\]]/g, ' ').slice(0, 31);
        window.XLSX.utils.book_append_sheet(book, sheet, safeSheet);

        const buffer = window.XLSX.write(book, { bookType: 'xlsx', type: 'array' });
        saveBlob(new Blob([buffer], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' }), filename);
        return true;
    }

    function coerce(value, type) {
        if (value === null || value === undefined || value === '') return '';
        switch (type) {
            case 'number': {
                const n = Number(String(value).replace(/,/g, ''));
                return Number.isFinite(n) ? n : value;
            }
            case 'date': {
                const d = new Date(value);
                return isNaN(d.getTime()) ? value : d;
            }
            case 'bool':
                return /^(true|yes|y|1)$/i.test(String(value));
            default:
                // Text stays text. This is the branch that protects "007" and "+256771234567".
                return String(value);
        }
    }

    /**
     * A printable document, handed to the browser's own print dialog — which is also where "Save
     * as PDF" lives. No PDF library: this reuses the path roster cards and incident packs already
     * print through, and gives better typography for a document carrying a letterhead than a
     * generated PDF would.
     */
    function printDocument(html) {
        if (window.QMgrPrint && typeof window.QMgrPrint.browserPrint === 'function') {
            return window.QMgrPrint.browserPrint(html, true);
        }
        // QMgrPrint is loaded app-wide, but a print window is worth opening even if it is not.
        const w = window.open('', '_blank', 'width=1000,height=760');
        if (!w) return false;
        w.document.write(html);
        w.document.close();
        w.onload = () => { w.focus(); w.print(); };
        return true;
    }

    return { saveText, saveWorkbook, printDocument };
})();
