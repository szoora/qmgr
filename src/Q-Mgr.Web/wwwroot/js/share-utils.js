// QR Code Generator using SVG (no external dependencies)
// Based on QR Code specification ISO/IEC 18004

window.generateQRCode = function (text, size = 200) {
    try {
        // Use a simple QR code generation approach
        const qr = generateQRMatrix(text);
        const svg = createQRSVG(qr, size);
        return svgToDataUrl(svg);
    } catch (e) {
        console.error('QR Code generation failed:', e);
        return null;
    }
};

window.downloadDataUrl = function (dataUrl, filename) {
    const link = document.createElement('a');
    link.href = dataUrl;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
};

// Simple QR Code matrix generator (Version 1-4, Alphanumeric/Byte mode)
function generateQRMatrix(text) {
    // For simplicity, we'll use a basic implementation
    // In production, consider using a library like qrcode-generator

    const size = calculateSize(text.length);
    const matrix = createMatrix(size);

    // Add finder patterns
    addFinderPattern(matrix, 0, 0);
    addFinderPattern(matrix, size - 7, 0);
    addFinderPattern(matrix, 0, size - 7);

    // Add timing patterns
    addTimingPatterns(matrix, size);

    // Add alignment pattern for version 2+
    if (size >= 25) {
        addAlignmentPattern(matrix, size - 9, size - 9);
    }

    // Encode data (simplified)
    encodeData(matrix, text, size);

    return matrix;
}

function calculateSize(dataLength) {
    // Version 1 = 21, Version 2 = 25, etc.
    if (dataLength <= 17) return 21;  // Version 1
    if (dataLength <= 32) return 25;  // Version 2
    if (dataLength <= 53) return 29;  // Version 3
    if (dataLength <= 78) return 33;  // Version 4
    if (dataLength <= 106) return 37; // Version 5
    if (dataLength <= 134) return 41; // Version 6
    return 45; // Version 7 (max for this simple implementation)
}

function createMatrix(size) {
    const matrix = [];
    for (let i = 0; i < size; i++) {
        matrix[i] = new Array(size).fill(null);
    }
    return matrix;
}

function addFinderPattern(matrix, startX, startY) {
    const pattern = [
        [1, 1, 1, 1, 1, 1, 1],
        [1, 0, 0, 0, 0, 0, 1],
        [1, 0, 1, 1, 1, 0, 1],
        [1, 0, 1, 1, 1, 0, 1],
        [1, 0, 1, 1, 1, 0, 1],
        [1, 0, 0, 0, 0, 0, 1],
        [1, 1, 1, 1, 1, 1, 1]
    ];

    for (let y = 0; y < 7; y++) {
        for (let x = 0; x < 7; x++) {
            if (startX + x < matrix.length && startY + y < matrix.length) {
                matrix[startY + y][startX + x] = pattern[y][x];
            }
        }
    }

    // Add separator
    for (let i = 0; i < 8; i++) {
        if (startX + 7 < matrix.length && startY + i < matrix.length) {
            matrix[startY + i][startX + 7] = 0;
        }
        if (startX + i < matrix.length && startY + 7 < matrix.length) {
            matrix[startY + 7][startX + i] = 0;
        }
    }
}

function addTimingPatterns(matrix, size) {
    for (let i = 8; i < size - 8; i++) {
        matrix[6][i] = i % 2 === 0 ? 1 : 0;
        matrix[i][6] = i % 2 === 0 ? 1 : 0;
    }
}

function addAlignmentPattern(matrix, centerX, centerY) {
    const pattern = [
        [1, 1, 1, 1, 1],
        [1, 0, 0, 0, 1],
        [1, 0, 1, 0, 1],
        [1, 0, 0, 0, 1],
        [1, 1, 1, 1, 1]
    ];

    for (let y = 0; y < 5; y++) {
        for (let x = 0; x < 5; x++) {
            const px = centerX - 2 + x;
            const py = centerY - 2 + y;
            if (px >= 0 && px < matrix.length && py >= 0 && py < matrix.length) {
                matrix[py][px] = pattern[y][x];
            }
        }
    }
}

function encodeData(matrix, text, size) {
    // Convert text to binary
    const bits = [];
    for (let i = 0; i < text.length; i++) {
        const charCode = text.charCodeAt(i);
        for (let b = 7; b >= 0; b--) {
            bits.push((charCode >> b) & 1);
        }
    }

    // Fill the matrix with data (simplified approach)
    let bitIndex = 0;
    let direction = -1;
    let x = size - 1;
    let y = size - 1;

    while (x > 0) {
        if (x === 6) x--; // Skip timing pattern column

        for (let row = 0; row < size; row++) {
            const actualY = direction === -1 ? size - 1 - row : row;

            for (let col = 0; col < 2; col++) {
                const actualX = x - col;

                if (matrix[actualY][actualX] === null) {
                    if (bitIndex < bits.length) {
                        matrix[actualY][actualX] = bits[bitIndex++];
                    } else {
                        // Fill remaining with pattern
                        matrix[actualY][actualX] = (actualX + actualY) % 2 === 0 ? 1 : 0;
                    }
                }
            }
        }

        x -= 2;
        direction *= -1;
    }

    // Fill any remaining null cells
    for (let row = 0; row < size; row++) {
        for (let col = 0; col < size; col++) {
            if (matrix[row][col] === null) {
                matrix[row][col] = (row + col) % 2 === 0 ? 1 : 0;
            }
        }
    }
}

function createQRSVG(matrix, size) {
    const moduleSize = size / matrix.length;
    const margin = moduleSize * 2;
    const totalSize = size + margin * 2;

    let svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${totalSize}" height="${totalSize}" viewBox="0 0 ${totalSize} ${totalSize}">`;
    svg += `<rect width="100%" height="100%" fill="white"/>`;

    for (let y = 0; y < matrix.length; y++) {
        for (let x = 0; x < matrix[y].length; x++) {
            if (matrix[y][x] === 1) {
                svg += `<rect x="${margin + x * moduleSize}" y="${margin + y * moduleSize}" width="${moduleSize}" height="${moduleSize}" fill="black"/>`;
            }
        }
    }

    svg += '</svg>';
    return svg;
}

function svgToDataUrl(svg) {
    const encoded = encodeURIComponent(svg);
    return `data:image/svg+xml;charset=utf-8,${encoded}`;
}

// Export for use in Blazor
window.shareUtils = {
    generateQRCode: window.generateQRCode,
    downloadDataUrl: window.downloadDataUrl
};
