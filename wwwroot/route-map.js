window.ascentRouteMap = {
    openDeleteDialog(dialog) { if (!dialog.open) dialog.showModal(); dialog.querySelector('[data-cancel-delete]').focus(); },
    closeDeleteDialog(dialog) { if (dialog && dialog.open) dialog.close(); },
    // Called once per opened route, never when a milestone is completed.
    startAtBase(element) { element.scrollTop = element.scrollHeight; },
    // Run after Blazor renders the selected task. Only stacked layouts need a page scroll.
    revealCheckpoint(panel) {
        if (!panel || !panel.isConnected) return;
        const grid = panel.closest('.expedition-grid');
        if (!grid || getComputedStyle(grid).gridTemplateColumns.trim().split(/\s+/).length !== 1) return;
        panel.focus({ preventScroll: true });
        panel.scrollIntoView({
            block: 'start',
            behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth'
        });
    }
};
