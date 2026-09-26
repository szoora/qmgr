// Small browser helpers for the calendar page (plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING E1).
window.qmgrCalendar = (function () {
    return {
        /** Brings today's row into view in the Term and Agenda lists. Instant, not smooth: the page is arriving. */
        scrollTo(id) {
            const el = document.getElementById(id);
            if (!el) return false;
            el.scrollIntoView({ block: 'start', behavior: 'instant' });
            return true;
        }
    };
})();
