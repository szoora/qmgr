/*
    "Publish to Library" for the printable report routes.

    Renders a report element to PDF bytes IN THE BROWSER (html2pdf.js = html2canvas + jsPDF,
    pinned, loaded on demand from cdnjs the first time it is needed) and hands the bytes back to
    Blazor, which uploads them into the Document Library with their provenance. Nothing renders
    on the server: the project's no-server-dependencies rule is why the browser's own "Save as
    PDF" was the only route before this, and this is the client-side carve-out that rule allows.

    Known limits, stated rather than hidden: html2canvas rasterises the page, so the result is an
    image-per-page PDF (not selectable text), and very long reports produce large files. The
    browser's print dialog remains the fallback for anything this renders badly.
*/
window.reportPublish = (function () {
    var LIB = 'https://cdnjs.cloudflare.com/ajax/libs/html2pdf.js/0.10.1/html2pdf.bundle.min.js';
    var loading = null;

    function ensureLib() {
        if (window.html2pdf) return Promise.resolve();
        if (loading) return loading;
        loading = new Promise(function (resolve, reject) {
            var el = document.createElement('script');
            el.src = LIB;
            el.async = true;
            el.addEventListener('load', function () { resolve(); });
            el.addEventListener('error', function () { loading = null; reject(new Error('Could not load the PDF renderer.')); });
            document.head.appendChild(el);
        });
        return loading;
    }

    return {
        /**
         * Renders the element with the given id to an A4 PDF and returns its bytes.
         * @param {string} elementId
         * @param {string} fileName  used only for the PDF's own metadata title
         * @returns {Promise<Uint8Array>}
         */
        render: async function (elementId, fileName) {
            await ensureLib();
            var el = document.getElementById(elementId);
            if (!el) throw new Error('Nothing to publish: the report element was not found.');

            // Print rules are what make the sheet look like a document; apply them for the capture.
            document.body.classList.add('report-publishing');
            try {
                var buffer = await window.html2pdf()
                    .set({
                        margin: [8, 8, 8, 8],
                        filename: fileName || 'report.pdf',
                        image: { type: 'jpeg', quality: 0.92 },
                        html2canvas: { scale: 2, useCORS: true, logging: false, backgroundColor: '#ffffff' },
                        jsPDF: { unit: 'mm', format: 'a4', orientation: 'portrait' },
                        pagebreak: { mode: ['css', 'legacy'], avoid: ['.wr-record', 'tr'] }
                    })
                    .from(el)
                    .outputPdf('arraybuffer');
                return new Uint8Array(buffer);
            } finally {
                document.body.classList.remove('report-publishing');
            }
        }
    };
})();
