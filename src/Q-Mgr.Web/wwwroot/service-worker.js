// Q-Mgr Service Worker
// Handles caching, offline support, and update notifications

const CACHE_NAME = 'qmgr-cache-v3';
const CACHE_VERSION = '1.2.0';

// Resources to cache immediately on install.
// Deliberately does NOT include '/' (or any navigation/document URL) — see
// the fetch handler below for why.
const PRECACHE_URLS = [
    '/css/layout.css',
    '/css/qm-theme.css',
    '/css/app.css',
    '/css/components/admin.css',
    '/css/components/shared.css',
    '/css/components/queue.css',
    '/css/components/content.css',
    '/css/components/reports.css',
    '/images/logo.svg',
    '/images/icon-512.svg',
    '/favicon.svg',
    '/manifest.json'
];

// Install event - precache static assets
self.addEventListener('install', event => {
    console.log('[ServiceWorker] Installing version:', CACHE_VERSION);

    event.waitUntil(
        caches.open(CACHE_NAME)
            .then(cache => {
                console.log('[ServiceWorker] Precaching app shell');
                return cache.addAll(PRECACHE_URLS);
            })
            .then(() => {
                console.log('[ServiceWorker] Skip waiting on install');
                // Don't skip waiting - let user decide when to update
                // return self.skipWaiting();
            })
            .catch(error => {
                console.error('[ServiceWorker] Precache failed:', error);
            })
    );
});

// Activate event - cleanup old caches
self.addEventListener('activate', event => {
    console.log('[ServiceWorker] Activating version:', CACHE_VERSION);

    event.waitUntil(
        caches.keys()
            .then(cacheNames => {
                return Promise.all(
                    cacheNames
                        .filter(cacheName => cacheName !== CACHE_NAME)
                        .map(cacheName => {
                            console.log('[ServiceWorker] Deleting old cache:', cacheName);
                            return caches.delete(cacheName);
                        })
                );
            })
            .then(() => {
                console.log('[ServiceWorker] Claiming clients');
                return self.clients.claim();
            })
    );
});

// Fetch event - serve from cache, fallback to network
self.addEventListener('fetch', event => {
    const { request } = event;
    const url = new URL(request.url);

    // Skip non-GET requests
    if (request.method !== 'GET') {
        return;
    }

    // Skip API calls - always go to network
    if (url.pathname.startsWith('/api/') ||
        url.pathname.startsWith('/_blazor') ||
        url.pathname.startsWith('/_framework')) {
        return;
    }

    // Skip SignalR connections
    if (url.pathname.includes('/_blazor')) {
        return;
    }

    // Navigation requests (the '/' document and any other page load) must
    // always go to the network for a Blazor Server app: the HTML response
    // bootstraps a specific server-side circuit, and serving a stale cached
    // copy after a server restart/redeploy breaks the SignalR handshake
    // (client and server disagree on render-tree state) — the page hangs
    // forever on "Initializing...". Cache is only a last-resort fallback
    // for genuine offline use, never the first choice, for documents.
    if (request.mode === 'navigate') {
        event.respondWith(
            fetch(request).catch(() => caches.match(request).then(cached => cached || caches.match('/')))
        );
        return;
    }

    event.respondWith(
        caches.match(request)
            .then(cachedResponse => {
                if (cachedResponse) {
                    // Return cached response and update cache in background
                    event.waitUntil(updateCache(request));
                    return cachedResponse;
                }

                // Not in cache - fetch from network
                return fetch(request)
                    .then(response => {
                        // Don't cache non-successful responses
                        if (!response || response.status !== 200 || response.type !== 'basic') {
                            return response;
                        }

                        // Cache the fetched response
                        const responseToCache = response.clone();
                        caches.open(CACHE_NAME)
                            .then(cache => {
                                cache.put(request, responseToCache);
                            });

                        return response;
                    })
                    .catch(() => {
                        return new Response('Offline', { status: 503, statusText: 'Service Unavailable' });
                    });
            })
    );
});

// Update cache in background (stale-while-revalidate)
async function updateCache(request) {
    try {
        const response = await fetch(request);
        if (response && response.status === 200) {
            const cache = await caches.open(CACHE_NAME);
            await cache.put(request, response);
        }
    } catch (error) {
        // Network error - ignore, we'll use cached version
    }
}

// Message event - handle messages from the app
self.addEventListener('message', event => {
    console.log('[ServiceWorker] Message received:', event.data);

    if (event.data && event.data.type === 'SKIP_WAITING') {
        console.log('[ServiceWorker] Skip waiting requested by user');
        self.skipWaiting();
    }

    if (event.data && event.data.type === 'GET_VERSION') {
        event.ports[0].postMessage({ version: CACHE_VERSION });
    }

    if (event.data && event.data.type === 'CHECK_UPDATE') {
        checkForUpdates();
    }
});

// Check for updates periodically
async function checkForUpdates() {
    try {
        const registration = await self.registration;
        await registration.update();
        console.log('[ServiceWorker] Update check completed');
    } catch (error) {
        console.error('[ServiceWorker] Update check failed:', error);
    }
}

// Notify all clients about available update
function notifyClientsAboutUpdate() {
    self.clients.matchAll({ type: 'window' }).then(clients => {
        clients.forEach(client => {
            client.postMessage({
                type: 'UPDATE_AVAILABLE',
                version: CACHE_VERSION
            });
        });
    });
}

// Background sync for offline actions (if supported)
self.addEventListener('sync', event => {
    console.log('[ServiceWorker] Background sync:', event.tag);

    if (event.tag === 'sync-queue-data') {
        event.waitUntil(syncQueueData());
    }
});

async function syncQueueData() {
    // Placeholder for syncing offline queue actions
    console.log('[ServiceWorker] Syncing queue data...');
}

// Push notifications (if needed in future)
self.addEventListener('push', event => {
    if (!event.data) return;

    const data = event.data.json();
    const options = {
        body: data.body || 'New notification from Q-Mgr',
        icon: '/images/icon-512.svg',
        badge: '/favicon.svg',
        vibrate: [100, 50, 100],
        data: {
            url: data.url || '/'
        },
        actions: data.actions || []
    };

    event.waitUntil(
        self.registration.showNotification(data.title || 'Q-Mgr', options)
    );
});

// Handle notification click
self.addEventListener('notificationclick', event => {
    event.notification.close();

    const url = event.notification.data?.url || '/';

    event.waitUntil(
        self.clients.matchAll({ type: 'window' })
            .then(clients => {
                // Focus existing window if available
                for (const client of clients) {
                    if (client.url === url && 'focus' in client) {
                        return client.focus();
                    }
                }
                // Open new window
                if (self.clients.openWindow) {
                    return self.clients.openWindow(url);
                }
            })
    );
});

console.log('[ServiceWorker] Service worker loaded, version:', CACHE_VERSION);
