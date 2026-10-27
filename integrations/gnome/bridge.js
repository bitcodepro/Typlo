// Kept separate from the Shell adapter so source activation can be regression-tested without GNOME.
export function createBridge(manager, isLocked) {
    return {
        Ping() { return true; },
        GetCurrent() {
            const source = manager.currentSource;
            return source?.type === 'xkb' ? source.id : '';
        },
        Activate(id) {
            if (isLocked())
                return false;
            const source = Object.values(manager.inputSources)
                .find(item => item.type === 'xkb' && item.id === id);
            if (!source)
                return false;
            source.activate(true);
            return manager.currentSource === source;
        },
    };
}
