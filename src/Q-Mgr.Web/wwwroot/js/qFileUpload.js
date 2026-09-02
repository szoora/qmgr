// Shared upload widget behind the QFileUpload.razor component (see its own doc comment for why
// this bypasses Blazor's <InputFile>/IBrowserFile entirely). Handles native drag-and-drop (the
// input itself IS the drop target — MediaLibrary.razor's established "invisible overlay" trick,
// see its own file-input CSS), client-side image compression via Canvas (no server-side image
// library — this project's standing "no new server dependencies" rule, see CLAUDE.md), and the
// actual upload via fetch() with the caller's JWT attached as a normal Authorization header
// (fetch, unlike a plain browser navigation, can set headers — no query-string token needed here).
window.qFileUpload = (function () {
    function readAsDataUrl(file) {
        return new Promise((resolve, reject) => {
            const reader = new FileReader();
            reader.onload = () => resolve(reader.result);
            reader.onerror = () => reject(reader.error);
            reader.readAsDataURL(file);
        });
    }

    // Downscales to maxDimension on the longest side and re-encodes as JPEG at the given
    // quality. Never upscales a smaller image. Falls back to the original file untouched if
    // anything goes wrong (a broken compression step should never block a real upload).
    async function compressImage(file, maxDimension, quality) {
        try {
            const dataUrl = await readAsDataUrl(file);
            const img = await new Promise((resolve, reject) => {
                const el = new Image();
                el.onload = () => resolve(el);
                el.onerror = reject;
                el.src = dataUrl;
            });

            const scale = Math.min(1, maxDimension / Math.max(img.width, img.height));
            const canvas = document.createElement('canvas');
            canvas.width = Math.round(img.width * scale);
            canvas.height = Math.round(img.height * scale);
            const ctx = canvas.getContext('2d');
            ctx.drawImage(img, 0, 0, canvas.width, canvas.height);

            const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/jpeg', quality));
            if (!blob || blob.size >= file.size) return file; // compression didn't actually help — keep the original

            const newName = file.name.replace(/\.[^.]+$/, '') + '.jpg';
            return new File([blob], newName, { type: 'image/jpeg' });
        } catch {
            return file; // never let a compression failure block the upload
        }
    }

    async function uploadOne(file, options) {
        const form = new FormData();
        form.append('file', file, file.name);

        const response = await fetch(options.uploadUrl, {
            method: 'POST',
            headers: options.accessToken ? { 'Authorization': 'Bearer ' + options.accessToken } : {},
            body: form
        });

        const text = await response.text();
        return { ok: response.ok, status: response.status, body: text, fileName: file.name };
    }

    /**
     * @param {HTMLInputElement} inputEl
     * @param {object} options { uploadUrl, accessToken, maxSizeBytes, compressImages, maxDimension, quality, dotNetRef }
     */
    function init(inputEl, options) {
        if (!inputEl) return;

        inputEl.addEventListener('change', async () => {
            const files = Array.from(inputEl.files || []);
            inputEl.value = ''; // allow re-selecting the same file name later
            if (files.length === 0) return;

            for (const original of files) {
                const isImage = original.type.startsWith('image/');
                const file = (options.compressImages && isImage)
                    ? await compressImage(original, options.maxDimension, options.quality)
                    : original;

                if (options.maxSizeBytes && file.size > options.maxSizeBytes) {
                    await options.dotNetRef.invokeMethodAsync('OnFileTooLarge', file.name, file.size);
                    continue;
                }

                await options.dotNetRef.invokeMethodAsync('OnUploadStarted', file.name);
                try {
                    const result = await uploadOne(file, options);
                    await options.dotNetRef.invokeMethodAsync('OnUploadFinished', result.fileName, result.ok, result.status, result.body);
                } catch (err) {
                    await options.dotNetRef.invokeMethodAsync('OnUploadFinished', file.name, false, 0, String(err));
                }
            }
        });
    }

    return { init };
})();
