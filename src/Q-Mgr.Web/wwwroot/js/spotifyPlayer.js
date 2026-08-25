// Spotify Web Playback SDK interop — turns the current browser tab into a
// playable Spotify Connect device, for background music behind image-gallery
// signage content. Must be loaded BEFORE https://sdk.scdn.co/spotify-player.js
// so window.onSpotifyWebPlaybackSDKReady exists when the SDK script finishes.

window.spotifyPlayerInterop = {
    _player: null,
    _deviceId: null,
    _dotNetRef: null,
    _sdkReady: false,
    _pendingInit: null,

    init: async function (accessToken, dotNetRef) {
        this._dotNetRef = dotNetRef;

        if (!this._sdkReady) {
            // The SDK script may still be loading — queue init for onSpotifyWebPlaybackSDKReady.
            this._pendingInit = () => this._createPlayer(accessToken);
            return;
        }

        this._createPlayer(accessToken);
    },

    _createPlayer: function (accessToken) {
        if (this._player) {
            this.disconnect();
        }

        this._player = new window.Spotify.Player({
            name: 'Q-Mgr Signage',
            getOAuthToken: cb => cb(accessToken),
            volume: 0.4
        });

        this._player.addListener('ready', ({ device_id }) => {
            this._deviceId = device_id;
            this._dotNetRef?.invokeMethodAsync('OnPlayerReady', device_id);
        });

        this._player.addListener('not_ready', () => {
            this._deviceId = null;
        });

        this._player.addListener('initialization_error', ({ message }) => this._reportError(message));
        this._player.addListener('authentication_error', ({ message }) => this._reportError(message));
        this._player.addListener('account_error', ({ message }) => this._reportError('Spotify Premium is required for playback: ' + message));
        this._player.addListener('playback_error', ({ message }) => this._reportError(message));

        this._player.connect();
    },

    _reportError: function (message) {
        console.warn('Spotify playback error:', message);
        this._dotNetRef?.invokeMethodAsync('OnPlayerError', message);
    },

    // Starts playback of a playlist on this tab's device via the standard Web
    // API (the SDK itself only creates the device — playback is controlled
    // the same way any Spotify Connect device is controlled).
    playPlaylist: async function (accessToken, playlistId) {
        if (!this._deviceId) return false;

        try {
            const response = await fetch(`https://api.spotify.com/v1/me/player/play?device_id=${this._deviceId}`, {
                method: 'PUT',
                headers: {
                    'Authorization': `Bearer ${accessToken}`,
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({ context_uri: `spotify:playlist:${playlistId}` })
            });
            return response.ok || response.status === 204;
        } catch (e) {
            console.warn('Spotify play request failed:', e);
            return false;
        }
    },

    pause: async function (accessToken) {
        if (!this._deviceId) return;
        try {
            await fetch(`https://api.spotify.com/v1/me/player/pause?device_id=${this._deviceId}`, {
                method: 'PUT',
                headers: { 'Authorization': `Bearer ${accessToken}` }
            });
        } catch (e) { /* best-effort */ }
    },

    disconnect: function () {
        if (this._player) {
            try { this._player.disconnect(); } catch (e) { /* already gone */ }
            this._player = null;
        }
        this._deviceId = null;
    }
};

window.onSpotifyWebPlaybackSDKReady = () => {
    window.spotifyPlayerInterop._sdkReady = true;
    if (window.spotifyPlayerInterop._pendingInit) {
        const fn = window.spotifyPlayerInterop._pendingInit;
        window.spotifyPlayerInterop._pendingInit = null;
        fn();
    }
};
