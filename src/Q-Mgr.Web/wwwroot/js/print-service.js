// Q-Mgr Print Service
// Supports: Browser Print, QZ Tray, WebUSB

window.QMgrPrint = {
    // Current print method
    currentMethod: 'browser',

    // QZ Tray connection status
    qzConnected: false,

    // WebUSB device
    usbDevice: null,

    // Initialize print service
    init: function (method) {
        this.currentMethod = method || 'browser';
        console.log('Print service initialized with method:', this.currentMethod);

        if (this.currentMethod === 'qztray') {
            this.initQZTray();
        }
    },

    // Main print function - routes to appropriate method
    print: async function (data) {
        console.log('Printing with method:', this.currentMethod);

        switch (this.currentMethod) {
            case 'browser':
                return await this.browserPrint(data.htmlContent);
            case 'qztray':
                return await this.qzTrayPrint(data.escPosData, data.printerName);
            case 'webusb':
                return await this.webUSBPrint(data.escPosData);
            case 'network':
            case 'server':
                // These are handled server-side
                return { success: true, message: 'Sent to server' };
            default:
                return { success: false, message: 'Unknown print method' };
        }
    },

    // ===================
    // Browser Print
    // ===================
    browserPrint: function (htmlContent) {
        return new Promise((resolve) => {
            try {
                // Create print window
                const printWindow = window.open('', '_blank', 'width=400,height=600');

                if (!printWindow) {
                    resolve({ success: false, message: 'Popup blocked. Please allow popups for printing.' });
                    return;
                }

                printWindow.document.write(htmlContent);
                printWindow.document.close();

                // Wait for content to load then print
                printWindow.onload = function () {
                    setTimeout(() => {
                        printWindow.print();
                        setTimeout(() => {
                            printWindow.close();
                            resolve({ success: true, message: 'Print dialog opened' });
                        }, 1000);
                    }, 250);
                };
            } catch (error) {
                resolve({ success: false, message: error.message });
            }
        });
    },

    // ===================
    // QZ Tray Integration
    // ===================
    initQZTray: async function () {
        if (typeof qz === 'undefined') {
            console.warn('QZ Tray library not loaded. Loading dynamically...');
            await this.loadQZTrayScript();
        }

        try {
            if (!qz.websocket.isActive()) {
                await qz.websocket.connect();
                this.qzConnected = true;
                console.log('QZ Tray connected');
            }
        } catch (error) {
            console.error('Failed to connect to QZ Tray:', error);
            this.qzConnected = false;
        }
    },

    loadQZTrayScript: function () {
        return new Promise((resolve, reject) => {
            const script = document.createElement('script');
            script.src = 'https://cdn.jsdelivr.net/npm/qz-tray@2.2.4/qz-tray.min.js';
            script.onload = resolve;
            script.onerror = reject;
            document.head.appendChild(script);
        });
    },

    qzTrayPrint: async function (escPosDataBase64, printerName) {
        try {
            if (!this.qzConnected) {
                await this.initQZTray();
            }

            if (!this.qzConnected) {
                return { success: false, message: 'QZ Tray not connected. Make sure QZ Tray is running.' };
            }

            // Find printer
            const printers = await qz.printers.find();
            let targetPrinter = printerName;

            if (!targetPrinter) {
                // Use default printer
                targetPrinter = await qz.printers.getDefault();
            }

            if (!targetPrinter) {
                return { success: false, message: 'No printer found' };
            }

            // Configure printer
            const config = qz.configs.create(targetPrinter, {
                encoding: 'UTF-8'
            });

            // Convert base64 to raw data
            const rawData = [{
                type: 'raw',
                format: 'base64',
                data: escPosDataBase64
            }];

            // Print
            await qz.print(config, rawData);

            return { success: true, message: 'Printed successfully via QZ Tray' };
        } catch (error) {
            console.error('QZ Tray print error:', error);
            return { success: false, message: error.message || 'QZ Tray print failed' };
        }
    },

    getQZPrinters: async function () {
        try {
            if (!this.qzConnected) {
                await this.initQZTray();
            }
            if (this.qzConnected) {
                return await qz.printers.find();
            }
            return [];
        } catch (error) {
            console.error('Error getting printers:', error);
            return [];
        }
    },

    // ===================
    // WebUSB Printing
    // ===================
    webUSBPrint: async function (escPosDataBase64) {
        try {
            // Check WebUSB support
            if (!navigator.usb) {
                return { success: false, message: 'WebUSB not supported. Use Chrome browser.' };
            }

            // Get or request device
            if (!this.usbDevice || !this.usbDevice.opened) {
                await this.requestUSBDevice();
            }

            if (!this.usbDevice) {
                return { success: false, message: 'No USB printer selected' };
            }

            // Convert base64 to Uint8Array
            const binaryString = atob(escPosDataBase64);
            const bytes = new Uint8Array(binaryString.length);
            for (let i = 0; i < binaryString.length; i++) {
                bytes[i] = binaryString.charCodeAt(i);
            }

            // Find output endpoint (usually endpoint 1 or 2)
            let outputEndpoint = null;
            for (const iface of this.usbDevice.configuration.interfaces) {
                for (const alt of iface.alternates) {
                    for (const endpoint of alt.endpoints) {
                        if (endpoint.direction === 'out') {
                            outputEndpoint = endpoint;
                            break;
                        }
                    }
                }
            }

            if (!outputEndpoint) {
                return { success: false, message: 'No output endpoint found on USB device' };
            }

            // Send data
            await this.usbDevice.transferOut(outputEndpoint.endpointNumber, bytes);

            return { success: true, message: 'Printed via USB' };
        } catch (error) {
            console.error('WebUSB print error:', error);
            return { success: false, message: error.message || 'USB print failed' };
        }
    },

    requestUSBDevice: async function () {
        try {
            // Request USB device (thermal printers usually use these vendor IDs)
            // Common thermal printer vendor IDs: Epson (0x04b8), Star (0x0519), Citizen (0x1f5), etc.
            this.usbDevice = await navigator.usb.requestDevice({
                filters: [
                    { classCode: 7 }, // Printer class
                    { vendorId: 0x04b8 }, // Epson
                    { vendorId: 0x0519 }, // Star Micronics
                    { vendorId: 0x0416 }, // Winbond (common for thermal printers)
                    { vendorId: 0x0483 }, // STMicroelectronics
                    { vendorId: 0x1504 }, // Citizen
                    { vendorId: 0x154f }, // SNBC
                    { vendorId: 0x0fe6 }, // ICS
                ]
            });

            if (this.usbDevice) {
                await this.usbDevice.open();
                await this.usbDevice.selectConfiguration(1);
                await this.usbDevice.claimInterface(0);
                console.log('USB printer connected:', this.usbDevice.productName);
            }
        } catch (error) {
            console.error('Error requesting USB device:', error);
            this.usbDevice = null;
        }
    },

    disconnectUSB: async function () {
        if (this.usbDevice) {
            try {
                await this.usbDevice.close();
                this.usbDevice = null;
                console.log('USB printer disconnected');
            } catch (error) {
                console.error('Error disconnecting USB:', error);
            }
        }
    },

    // ===================
    // Utility Functions
    // ===================

    // Convert method enum to string
    getMethodName: function (methodValue) {
        const methods = {
            0: 'browser',
            1: 'qztray',
            2: 'webusb',
            3: 'server',
            4: 'network'
        };
        return methods[methodValue] || 'browser';
    },

    // Check if QZ Tray is available
    isQZTrayAvailable: function () {
        return typeof qz !== 'undefined' && this.qzConnected;
    },

    // Check if WebUSB is available
    isWebUSBAvailable: function () {
        return !!navigator.usb;
    }
};

// Auto-initialize when document is ready
document.addEventListener('DOMContentLoaded', function() {
    console.log('Q-Mgr Print Service loaded');
});
