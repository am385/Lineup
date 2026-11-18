(function (root) {
    "use strict";
    const observers = new Map();
    root.lineupResources = {
        start(id, receiver) {
            this.stop(id);
            let pending = false;
            let stopped = false;
            const observe = async () => {
                if (pending || stopped) return;
                pending = true;
                const visible = document.visibilityState === "visible";
                try {
                    await receiver.invokeMethodAsync("ObserveVisibility", visible);
                } catch (error) {
                    console.debug("Dashboard resource observation unavailable", error);
                } finally {
                    pending = false;
                    if (!stopped && visible !== (document.visibilityState === "visible")) void observe();
                }
            };
            const timer = setInterval(() => {
                if (document.visibilityState === "visible") void observe();
            }, 2000);
            document.addEventListener("visibilitychange", observe);
            observers.set(id, () => {
                stopped = true;
                clearInterval(timer);
                document.removeEventListener("visibilitychange", observe);
            });
            void observe();
        },
        stop(id) {
            const stop = observers.get(id);
            if (stop) {
                stop();
                observers.delete(id);
            }
        }
    };
})(window);
