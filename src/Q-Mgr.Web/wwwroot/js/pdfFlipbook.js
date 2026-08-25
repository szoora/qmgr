// PDF Flip-Book JavaScript Interop Module
// Renders a PDF's pages to images via pdf.js, then presents them as an
// interactive page-turning book via page-flip (St.PageFlip), plus a
// thumbnail rail, fullscreen, and zoom.

window.pdfFlipbookInterop = {
    _instances: new Map(),
    _workerConfigured: false,

    _ensureWorker: function () {
        if (this._workerConfigured) return;
        if (window.pdfjsLib) {
            window.pdfjsLib.GlobalWorkerOptions.workerSrc =
                'https://cdn.jsdelivr.net/npm/pdfjs-dist@3.11.174/build/pdf.worker.min.js';
            this._workerConfigured = true;
        }
    },

    // Loads the PDF, renders every page to a JPEG image, builds the page-flip
    // book inside containerId, and populates the thumbnail rail inside
    // thumbsContainerId. Returns { success, pageCount, errorMessage }.
    init: async function (containerId, thumbsContainerId, pdfUrl, dotNetRef, autoAdvance, pageDurationSeconds) {
        const container = document.getElementById(containerId);
        if (!container) {
            return { success: false, pageCount: 0, errorMessage: 'Container not found' };
        }

        this.dispose(containerId);

        try {
            this._ensureWorker();
            if (!window.pdfjsLib || !window.St) {
                return { success: false, pageCount: 0, errorMessage: 'PDF viewer libraries failed to load' };
            }

            const pdf = await window.pdfjsLib.getDocument(pdfUrl).promise;
            const pageCount = pdf.numPages;

            const pageImages = [];
            let baseWidth = 600;
            let baseHeight = 800;

            for (let i = 1; i <= pageCount; i++) {
                const page = await pdf.getPage(i);
                // Scale 2 renders at roughly double resolution for crisp text/images
                // on modern displays without the file itself needing to be large.
                const viewport = page.getViewport({ scale: 2 });

                if (i === 1) {
                    baseWidth = viewport.width;
                    baseHeight = viewport.height;
                }

                const canvas = document.createElement('canvas');
                canvas.width = viewport.width;
                canvas.height = viewport.height;
                const ctx = canvas.getContext('2d');
                await page.render({ canvasContext: ctx, viewport: viewport }).promise;
                pageImages.push(canvas.toDataURL('image/jpeg', 0.88));
            }

            const flipEl = document.createElement('div');
            flipEl.className = 'pdf-flipbook-surface';
            container.appendChild(flipEl);

            const pageFlip = new window.St.PageFlip(flipEl, {
                width: baseWidth,
                height: baseHeight,
                size: 'stretch',
                minWidth: 260,
                maxWidth: 1400,
                minHeight: 350,
                maxHeight: 1800,
                showCover: false,
                maxShadowOpacity: 0.5,
                mobileScrollSupport: false,
                usePortrait: pageCount === 1
            });

            pageFlip.loadFromImages(pageImages);

            pageFlip.on('flip', (e) => {
                this._setActiveThumb(thumbsContainerId, e.data);
                dotNetRef.invokeMethodAsync('OnPageFlipped', e.data + 1, pageCount);
            });

            let autoTimer = null;
            if (autoAdvance && pageCount > 1) {
                autoTimer = setInterval(() => {
                    const inst = this._instances.get(containerId);
                    if (!inst) return;
                    const current = inst.pageFlip.getCurrentPageIndex();
                    if (current < pageCount - 1) {
                        inst.pageFlip.flipNext();
                    } else {
                        clearInterval(inst.autoTimer);
                        inst.autoTimer = null;
                        dotNetRef.invokeMethodAsync('OnFlipbookEnded');
                    }
                }, Math.max(1, pageDurationSeconds) * 1000);
            }

            const fullscreenHandler = () => {
                dotNetRef.invokeMethodAsync('OnFullscreenChanged', !!document.fullscreenElement);
            };
            document.addEventListener('fullscreenchange', fullscreenHandler);

            this._instances.set(containerId, { pageFlip, autoTimer, dotNetRef, pageCount, fullscreenHandler });

            this._buildThumbnails(thumbsContainerId, containerId, pageImages);

            return { success: true, pageCount: pageCount, errorMessage: null };
        } catch (e) {
            console.error('PDF flipbook init failed:', e);
            return { success: false, pageCount: 0, errorMessage: e.message || 'Failed to load PDF' };
        }
    },

    _buildThumbnails: function (thumbsContainerId, containerId, pageImages) {
        const thumbsContainer = document.getElementById(thumbsContainerId);
        if (!thumbsContainer) return;

        thumbsContainer.innerHTML = '';
        pageImages.forEach((src, index) => {
            const thumb = document.createElement('button');
            thumb.type = 'button';
            thumb.className = 'pdf-flipbook-thumb' + (index === 0 ? ' pdf-flipbook-thumb--active' : '');
            thumb.setAttribute('data-page-index', String(index));
            thumb.title = `Page ${index + 1}`;

            const img = document.createElement('img');
            img.src = src;
            img.alt = `Page ${index + 1}`;
            thumb.appendChild(img);

            const label = document.createElement('span');
            label.textContent = index + 1;
            thumb.appendChild(label);

            thumb.addEventListener('click', () => this.goToPage(containerId, index));
            thumbsContainer.appendChild(thumb);
        });
    },

    _setActiveThumb: function (thumbsContainerId, pageIndex) {
        const thumbsContainer = document.getElementById(thumbsContainerId);
        if (!thumbsContainer) return;

        const active = thumbsContainer.querySelector('.pdf-flipbook-thumb--active');
        if (active) active.classList.remove('pdf-flipbook-thumb--active');

        const target = thumbsContainer.querySelector(`[data-page-index="${pageIndex}"]`);
        if (target) {
            target.classList.add('pdf-flipbook-thumb--active');
            target.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
        }
    },

    next: function (containerId) {
        const inst = this._instances.get(containerId);
        if (inst) inst.pageFlip.flipNext();
    },

    prev: function (containerId) {
        const inst = this._instances.get(containerId);
        if (inst) inst.pageFlip.flipPrev();
    },

    goToPage: function (containerId, pageIndex) {
        const inst = this._instances.get(containerId);
        if (inst) inst.pageFlip.flip(pageIndex);
    },

    toggleFullscreen: function (elementId) {
        const el = document.getElementById(elementId);
        if (!el) return false;

        if (!document.fullscreenElement) {
            (el.requestFullscreen || el.webkitRequestFullscreen || el.msRequestFullscreen)?.call(el);
            return true;
        } else {
            (document.exitFullscreen || document.webkitExitFullscreen || document.msExitFullscreen)?.call(document);
            return false;
        }
    },

    isFullscreen: function () {
        return !!document.fullscreenElement;
    },

    dispose: function (containerId) {
        const inst = this._instances.get(containerId);
        if (inst) {
            if (inst.autoTimer) clearInterval(inst.autoTimer);
            if (inst.fullscreenHandler) document.removeEventListener('fullscreenchange', inst.fullscreenHandler);
            try { inst.pageFlip.destroy(); } catch (e) { /* already gone */ }
            this._instances.delete(containerId);
        }
        const container = document.getElementById(containerId);
        if (container) container.innerHTML = '';
    }
};
