// Q-Mgr PWA Registration and Update Management

(function () {
    'use strict';

    const PWA = {
        registration: null,
        updateCheckInterval: 60 * 60 * 1000, // Check every hour
        checkIntervalId: null,

        // Initialize PWA
        async init() {
            if (!('serviceWorker' in navigator)) {
                console.log('[PWA] Service workers not supported');
                return;
            }

            try {
                await this.registerServiceWorker();
                this.setupUpdateChecker();
                this.setupInstallPrompt();
                this.setupOnlineOfflineHandlers();
            } catch (error) {
                console.error('[PWA] Initialization failed:', error);
            }
        },

        // Register service worker
        async registerServiceWorker() {
            try {
                this.registration = await navigator.serviceWorker.register('/service-worker.js', {
                    scope: '/'
                });

                console.log('[PWA] Service worker registered:', this.registration.scope);

                // Listen for updates
                this.registration.addEventListener('updatefound', () => {
                    this.handleUpdateFound();
                });

                // Check if there's already a waiting worker
                if (this.registration.waiting) {
                    this.showUpdatePrompt();
                }

                // Listen for messages from service worker
                navigator.serviceWorker.addEventListener('message', (event) => {
                    this.handleServiceWorkerMessage(event);
                });

                // Listen for controller change (update activated)
                navigator.serviceWorker.addEventListener('controllerchange', () => {
                    console.log('[PWA] Controller changed, reloading...');
                    window.location.reload();
                });

            } catch (error) {
                console.error('[PWA] Service worker registration failed:', error);
                throw error;
            }
        },

        // Handle update found event
        handleUpdateFound() {
            console.log('[PWA] Update found');

            const newWorker = this.registration.installing;
            if (!newWorker) return;

            newWorker.addEventListener('statechange', () => {
                console.log('[PWA] New worker state:', newWorker.state);

                if (newWorker.state === 'installed' && navigator.serviceWorker.controller) {
                    // New version is ready
                    this.showUpdatePrompt();
                }
            });
        },

        // Show update prompt to user
        showUpdatePrompt() {
            console.log('[PWA] Showing update prompt');

            // Dispatch custom event for Blazor to handle
            window.dispatchEvent(new CustomEvent('pwa-update-available', {
                detail: { registration: this.registration }
            }));

            // Also show native UI if available
            this.createUpdateBanner();
        },

        // Create update banner UI
        createUpdateBanner() {
            // Remove existing banner if present
            const existingBanner = document.getElementById('pwa-update-banner');
            if (existingBanner) {
                existingBanner.remove();
            }

            const banner = document.createElement('div');
            banner.id = 'pwa-update-banner';
            banner.innerHTML = `
                <div class="pwa-update-content">
                    <div class="pwa-update-icon">
                        <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/>
                            <polyline points="7 10 12 15 17 10"/>
                            <line x1="12" y1="15" x2="12" y2="3"/>
                        </svg>
                    </div>
                    <div class="pwa-update-text">
                        <strong>Update Available</strong>
                        <span>A new version of Q-Mgr is ready to install</span>
                    </div>
                    <div class="pwa-update-actions">
                        <button class="pwa-btn-later" onclick="PWA.dismissUpdate()">Later</button>
                        <button class="pwa-btn-update" onclick="PWA.applyUpdate()">Update Now</button>
                    </div>
                </div>
            `;

            // Add styles
            const style = document.createElement('style');
            style.textContent = `
                #pwa-update-banner {
                    position: fixed;
                    bottom: 20px;
                    left: 50%;
                    transform: translateX(-50%);
                    background: linear-gradient(135deg, #1e1e2d 0%, #252536 100%);
                    border: 1px solid #0058cc;
                    border-radius: 16px;
                    padding: 16px 20px;
                    box-shadow: 0 8px 32px rgba(0, 212, 255, 0.2), 0 0 0 1px rgba(0, 212, 255, 0.1);
                    z-index: 10000;
                    animation: pwa-slide-up 0.3s ease-out;
                    max-width: 90%;
                    width: 480px;
                }

                @keyframes pwa-slide-up {
                    from {
                        opacity: 0;
                        transform: translateX(-50%) translateY(20px);
                    }
                    to {
                        opacity: 1;
                        transform: translateX(-50%) translateY(0);
                    }
                }

                .pwa-update-content {
                    display: flex;
                    align-items: center;
                    gap: 16px;
                }

                .pwa-update-icon {
                    width: 48px;
                    height: 48px;
                    background: linear-gradient(135deg, #0058cc, #26374a);
                    border-radius: 12px;
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    flex-shrink: 0;
                }

                .pwa-update-icon svg {
                    color: #0d1117;
                }

                .pwa-update-text {
                    flex: 1;
                    min-width: 0;
                }

                .pwa-update-text strong {
                    display: block;
                    color: #fff;
                    font-size: 15px;
                    margin-bottom: 2px;
                }

                .pwa-update-text span {
                    display: block;
                    color: #a0a0a0;
                    font-size: 13px;
                }

                .pwa-update-actions {
                    display: flex;
                    gap: 8px;
                    flex-shrink: 0;
                }

                .pwa-btn-later,
                .pwa-btn-update {
                    padding: 10px 16px;
                    border-radius: 8px;
                    font-size: 13px;
                    font-weight: 600;
                    cursor: pointer;
                    transition: all 0.2s;
                    border: none;
                }

                .pwa-btn-later {
                    background: rgba(255, 255, 255, 0.05);
                    color: #a0a0a0;
                    border: 1px solid rgba(255, 255, 255, 0.1);
                }

                .pwa-btn-later:hover {
                    background: rgba(255, 255, 255, 0.1);
                    color: #fff;
                }

                .pwa-btn-update {
                    background: linear-gradient(135deg, #0058cc, #26374a);
                    color: #0d1117;
                }

                .pwa-btn-update:hover {
                    box-shadow: 0 4px 16px rgba(0, 212, 255, 0.4);
                    transform: translateY(-1px);
                }

                @media (max-width: 540px) {
                    #pwa-update-banner {
                        bottom: 10px;
                        padding: 14px 16px;
                    }

                    .pwa-update-content {
                        flex-wrap: wrap;
                    }

                    .pwa-update-icon {
                        width: 40px;
                        height: 40px;
                    }

                    .pwa-update-text {
                        flex: 1 1 auto;
                    }

                    .pwa-update-actions {
                        width: 100%;
                        margin-top: 12px;
                    }

                    .pwa-btn-later,
                    .pwa-btn-update {
                        flex: 1;
                    }
                }
            `;

            document.head.appendChild(style);
            document.body.appendChild(banner);
        },

        // Dismiss update prompt
        dismissUpdate() {
            const banner = document.getElementById('pwa-update-banner');
            if (banner) {
                banner.style.animation = 'pwa-slide-up 0.2s ease-in reverse';
                setTimeout(() => banner.remove(), 200);
            }

            // Remind again in 4 hours
            setTimeout(() => {
                if (this.registration && this.registration.waiting) {
                    this.showUpdatePrompt();
                }
            }, 4 * 60 * 60 * 1000);
        },

        // Apply update
        applyUpdate() {
            console.log('[PWA] Applying update...');

            if (this.registration && this.registration.waiting) {
                // Tell service worker to skip waiting
                this.registration.waiting.postMessage({ type: 'SKIP_WAITING' });
            }

            // Remove banner
            const banner = document.getElementById('pwa-update-banner');
            if (banner) {
                banner.innerHTML = `
                    <div class="pwa-update-content" style="justify-content: center;">
                        <div class="pwa-update-text" style="text-align: center;">
                            <strong>Updating...</strong>
                            <span>Please wait while the update is being installed</span>
                        </div>
                    </div>
                `;
            }
        },

        // Setup periodic update checker
        setupUpdateChecker() {
            // Check for updates periodically
            this.checkIntervalId = setInterval(() => {
                this.checkForUpdates();
            }, this.updateCheckInterval);

            // Also check when app becomes visible
            document.addEventListener('visibilitychange', () => {
                if (document.visibilityState === 'visible') {
                    this.checkForUpdates();
                }
            });

            // Check on page focus
            window.addEventListener('focus', () => {
                this.checkForUpdates();
            });
        },

        // Check for updates
        async checkForUpdates() {
            if (!this.registration) return;

            try {
                await this.registration.update();
                console.log('[PWA] Update check completed');
            } catch (error) {
                console.log('[PWA] Update check failed (likely offline):', error.message);
            }
        },

        // Handle messages from service worker
        handleServiceWorkerMessage(event) {
            console.log('[PWA] Message from service worker:', event.data);

            if (event.data && event.data.type === 'UPDATE_AVAILABLE') {
                this.showUpdatePrompt();
            }
        },

        // Setup install prompt (A2HS - Add to Home Screen)
        setupInstallPrompt() {
            let deferredPrompt = null;

            window.addEventListener('beforeinstallprompt', (event) => {
                console.log('[PWA] Install prompt available');
                event.preventDefault();
                deferredPrompt = event;

                // Dispatch event for app to show install button
                window.dispatchEvent(new CustomEvent('pwa-install-available', {
                    detail: { prompt: deferredPrompt }
                }));
            });

            window.addEventListener('appinstalled', () => {
                console.log('[PWA] App installed');
                deferredPrompt = null;
                window.dispatchEvent(new CustomEvent('pwa-installed'));
            });

            // Expose install function
            window.pwaInstall = async () => {
                if (!deferredPrompt) {
                    console.log('[PWA] Install prompt not available');
                    return false;
                }

                deferredPrompt.prompt();
                const { outcome } = await deferredPrompt.userChoice;
                console.log('[PWA] Install prompt outcome:', outcome);

                deferredPrompt = null;
                return outcome === 'accepted';
            };
        },

        // Setup online/offline handlers
        setupOnlineOfflineHandlers() {
            const updateOnlineStatus = () => {
                const isOnline = navigator.onLine;
                console.log('[PWA] Online status:', isOnline);

                window.dispatchEvent(new CustomEvent('pwa-online-status', {
                    detail: { online: isOnline }
                }));

                // Show offline indicator if needed
                this.toggleOfflineIndicator(!isOnline);
            };

            window.addEventListener('online', updateOnlineStatus);
            window.addEventListener('offline', updateOnlineStatus);

            // Initial check
            updateOnlineStatus();
        },

        // Toggle offline indicator
        toggleOfflineIndicator(show) {
            let indicator = document.getElementById('pwa-offline-indicator');

            if (show && !indicator) {
                indicator = document.createElement('div');
                indicator.id = 'pwa-offline-indicator';
                indicator.innerHTML = `
                    <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                        <line x1="1" y1="1" x2="23" y2="23"/>
                        <path d="M16.72 11.06A10.94 10.94 0 0 1 19 12.55"/>
                        <path d="M5 12.55a10.94 10.94 0 0 1 5.17-2.39"/>
                        <path d="M10.71 5.05A16 16 0 0 1 22.58 9"/>
                        <path d="M1.42 9a15.91 15.91 0 0 1 4.7-2.88"/>
                        <path d="M8.53 16.11a6 6 0 0 1 6.95 0"/>
                        <line x1="12" y1="20" x2="12.01" y2="20"/>
                    </svg>
                    <span>You're offline</span>
                `;

                const style = document.createElement('style');
                style.id = 'pwa-offline-style';
                style.textContent = `
                    #pwa-offline-indicator {
                        position: fixed;
                        top: 10px;
                        left: 50%;
                        transform: translateX(-50%);
                        background: rgba(239, 68, 68, 0.9);
                        color: white;
                        padding: 8px 16px;
                        border-radius: 20px;
                        font-size: 13px;
                        font-weight: 500;
                        display: flex;
                        align-items: center;
                        gap: 8px;
                        z-index: 10001;
                        backdrop-filter: blur(8px);
                        animation: pwa-fade-in 0.3s ease-out;
                    }

                    @keyframes pwa-fade-in {
                        from { opacity: 0; transform: translateX(-50%) translateY(-10px); }
                        to { opacity: 1; transform: translateX(-50%) translateY(0); }
                    }
                `;

                document.head.appendChild(style);
                document.body.appendChild(indicator);
            } else if (!show && indicator) {
                indicator.remove();
                document.getElementById('pwa-offline-style')?.remove();
            }
        },

        // Get service worker version
        async getVersion() {
            if (!this.registration || !this.registration.active) {
                return null;
            }

            return new Promise((resolve) => {
                const channel = new MessageChannel();
                channel.port1.onmessage = (event) => {
                    resolve(event.data.version);
                };

                this.registration.active.postMessage(
                    { type: 'GET_VERSION' },
                    [channel.port2]
                );

                // Timeout after 1 second
                setTimeout(() => resolve(null), 1000);
            });
        }
    };

    // Expose PWA globally
    window.PWA = PWA;

    // Initialize when DOM is ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => PWA.init());
    } else {
        PWA.init();
    }
})();
