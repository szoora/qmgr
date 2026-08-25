// Media Player JavaScript Interop Module
// Handles media end detection for seamless playlist transitions

window.mediaPlayerInterop = {
    // Store references to registered elements and their handlers
    _registeredElements: new Map(),

    // Register a media element (video/audio) for end detection
    registerMediaElement: function (elementId, dotNetRef) {
        const element = document.getElementById(elementId);
        if (!element) {
            console.warn(`Media element not found: ${elementId}`);
            return false;
        }

        // Remove existing listener if any
        this.unregisterMediaElement(elementId);

        const endedHandler = async () => {
            try {
                await dotNetRef.invokeMethodAsync('OnMediaEndedCallback');
            } catch (e) {
                console.error('Error invoking media ended callback:', e);
            }
        };

        const errorHandler = async () => {
            try {
                await dotNetRef.invokeMethodAsync('OnMediaErrorCallback');
            } catch (e) {
                console.error('Error invoking media error callback:', e);
            }
        };

        element.addEventListener('ended', endedHandler);
        element.addEventListener('error', errorHandler);

        this._registeredElements.set(elementId, {
            element: element,
            endedHandler: endedHandler,
            errorHandler: errorHandler,
            dotNetRef: dotNetRef
        });

        return true;
    },

    // Unregister a media element
    unregisterMediaElement: function (elementId) {
        const registration = this._registeredElements.get(elementId);
        if (registration) {
            registration.element.removeEventListener('ended', registration.endedHandler);
            registration.element.removeEventListener('error', registration.errorHandler);
            this._registeredElements.delete(elementId);
        }
    },

    // Play a media element
    playMedia: function (elementId) {
        const element = document.getElementById(elementId);
        if (element && element.play) {
            return element.play().catch(e => {
                console.warn('Auto-play blocked:', e);
                return false;
            });
        }
        return Promise.resolve(false);
    },

    // Pause a media element
    pauseMedia: function (elementId) {
        const element = document.getElementById(elementId);
        if (element && element.pause) {
            element.pause();
            return true;
        }
        return false;
    },

    // Get current playback time
    getCurrentTime: function (elementId) {
        const element = document.getElementById(elementId);
        return element ? element.currentTime : 0;
    },

    // Get total duration
    getDuration: function (elementId) {
        const element = document.getElementById(elementId);
        return element && !isNaN(element.duration) ? element.duration : 0;
    },

    // Set muted state
    setMuted: function (elementId, muted) {
        const element = document.getElementById(elementId);
        if (element) {
            element.muted = muted;
            return true;
        }
        return false;
    },

    // Register YouTube iframe for end detection via postMessage API
    registerYouTubePlayer: function (iframeId, dotNetRef) {
        const iframe = document.getElementById(iframeId);
        if (!iframe) return false;

        // YouTube iframe API message handler
        const messageHandler = async (event) => {
            if (event.origin !== 'https://www.youtube.com') return;

            try {
                const data = JSON.parse(event.data);
                // YouTube sends state change: 0 = ended
                if (data.event === 'onStateChange' && data.info === 0) {
                    await dotNetRef.invokeMethodAsync('OnMediaEndedCallback');
                }
            } catch (e) {
                // Not a JSON message or not from YouTube player
            }
        };

        window.addEventListener('message', messageHandler);

        // Enable JS API by updating iframe src
        if (!iframe.src.includes('enablejsapi=1')) {
            const separator = iframe.src.includes('?') ? '&' : '?';
            iframe.src = iframe.src + separator + 'enablejsapi=1';
        }

        this._registeredElements.set(iframeId, {
            element: iframe,
            messageHandler: messageHandler,
            dotNetRef: dotNetRef,
            type: 'youtube'
        });

        return true;
    },

    // Cleanup all registered elements
    cleanup: function () {
        for (const [elementId, registration] of this._registeredElements) {
            if (registration.type === 'youtube') {
                window.removeEventListener('message', registration.messageHandler);
            } else {
                registration.element.removeEventListener('ended', registration.endedHandler);
                registration.element.removeEventListener('error', registration.errorHandler);
            }
        }
        this._registeredElements.clear();
    }
};
