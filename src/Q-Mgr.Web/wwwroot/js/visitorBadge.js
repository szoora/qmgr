// Visitor badge QR: rendering (qrcodejs) and camera-based scanning (jsQR), both CDN libraries
// loaded in App.razor. Kept deliberately separate from print-service.js / mediaPlayer.js since
// this is its own small, self-contained concern.

window.visitorBadge = (function () {
    let scanState = null; // { stream, videoEl, canvasEl, ctx, dotNetRef, rafId }

    // qrcodejs renders into a container element it manages itself (not a bare <canvas> — it
    // creates its own canvas/table child), so the target element in the .razor markup is a
    // plain <div>, cleared here before each render so re-opening the badge doesn't stack QR codes.
    function renderQr(containerId, text) {
        const container = document.getElementById(containerId);
        if (!container || !window.QRCode) return;
        container.innerHTML = '';
        new window.QRCode(container, { text: text, width: 220, height: 220, correctLevel: window.QRCode.CorrectLevel.M });
    }

    // Renders into a detached (never-appended) container so a badge can be turned into a data
    // URL for printing without needing a visible QR element on the page at all.
    function getQrDataUrl(text) {
        if (!window.QRCode) return null;
        const temp = document.createElement('div');
        new window.QRCode(temp, { text: text, width: 220, height: 220, correctLevel: window.QRCode.CorrectLevel.M });
        const canvas = temp.querySelector('canvas');
        return canvas ? canvas.toDataURL('image/png') : null;
    }

    async function startScanner(videoId, canvasId, dotNetRef) {
        await stopScanner();

        const videoEl = document.getElementById(videoId);
        const canvasEl = document.getElementById(canvasId);
        if (!videoEl || !canvasEl || !window.jsQR) return false;

        try {
            const stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' } });
            videoEl.srcObject = stream;
            await videoEl.play();

            const ctx = canvasEl.getContext('2d', { willReadFrequently: true });
            scanState = { stream, videoEl, canvasEl, ctx, dotNetRef };
            tick();
            return true;
        } catch (err) {
            console.error('[visitorBadge] Camera access failed', err);
            return false;
        }
    }

    function tick() {
        if (!scanState) return;
        const { videoEl, canvasEl, ctx, dotNetRef } = scanState;

        if (videoEl.readyState === videoEl.HAVE_ENOUGH_DATA) {
            canvasEl.width = videoEl.videoWidth;
            canvasEl.height = videoEl.videoHeight;
            ctx.drawImage(videoEl, 0, 0, canvasEl.width, canvasEl.height);
            const imageData = ctx.getImageData(0, 0, canvasEl.width, canvasEl.height);
            const code = window.jsQR(imageData.data, imageData.width, imageData.height, { inversionAttempts: 'dontInvert' });
            if (code && code.data) {
                dotNetRef.invokeMethodAsync('OnQrDetected', code.data);
                return; // caller decides whether to keep scanning after handling a hit
            }
        }
        scanState.rafId = requestAnimationFrame(tick);
    }

    async function stopScanner() {
        if (!scanState) return;
        if (scanState.rafId) cancelAnimationFrame(scanState.rafId);
        if (scanState.stream) scanState.stream.getTracks().forEach(t => t.stop());
        if (scanState.videoEl) scanState.videoEl.srcObject = null;
        scanState = null;
    }

    function resumeScanner() {
        if (scanState && !scanState.rafId) tick();
    }

    // ---- Check-in headshot capture (separate from QR scanning above: no decode loop, front
    // camera preferred, single still frame on demand rather than continuous frames) ----
    let photoState = null; // { stream, videoEl }

    async function startPhotoCamera(videoId) {
        await stopPhotoCamera();
        const videoEl = document.getElementById(videoId);
        if (!videoEl) return false;

        try {
            const stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'user' } });
            videoEl.srcObject = stream;
            await videoEl.play();
            photoState = { stream, videoEl };
            return true;
        } catch (err) {
            console.error('[visitorBadge] Photo camera access failed', err);
            return false;
        }
    }

    function capturePhoto(videoId, canvasId) {
        const videoEl = document.getElementById(videoId);
        const canvasEl = document.getElementById(canvasId);
        if (!videoEl || !canvasEl || videoEl.readyState < videoEl.HAVE_CURRENT_DATA) return null;

        canvasEl.width = videoEl.videoWidth;
        canvasEl.height = videoEl.videoHeight;
        const ctx = canvasEl.getContext('2d');
        ctx.drawImage(videoEl, 0, 0, canvasEl.width, canvasEl.height);
        return canvasEl.toDataURL('image/jpeg', 0.85);
    }

    async function stopPhotoCamera() {
        if (!photoState) return;
        if (photoState.stream) photoState.stream.getTracks().forEach(t => t.stop());
        if (photoState.videoEl) photoState.videoEl.srcObject = null;
        photoState = null;
    }

    return { renderQr, getQrDataUrl, startScanner, stopScanner, resumeScanner, startPhotoCamera, capturePhoto, stopPhotoCamera };
})();
