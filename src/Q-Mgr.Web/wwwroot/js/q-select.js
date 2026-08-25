window.qSelectInterop = {
    getDropdownPosition: function (element) {
        if (!element) return null;
        const rect = element.getBoundingClientRect();
        const spaceBelow = window.innerHeight - rect.bottom;
        const maxDropdownHeight = 250;
        const openUpward = spaceBelow < maxDropdownHeight && rect.top > spaceBelow;

        return {
            left: rect.left,
            width: rect.width,
            top: openUpward ? null : rect.bottom + 4,
            bottom: openUpward ? (window.innerHeight - rect.top + 4) : null
        };
    }
};
