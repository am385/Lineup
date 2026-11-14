window.lineupFactoryResetWatchGeneration = 0;
window.lineupFactoryResetInitialInstanceId = null;
window.lineupRestartDestination = '/settings#physical-devices';

function setLineupFactoryResetWatchState(timedOut) {
    var progress = document.getElementById('factoryResetRestartProgress');
    var timeout = document.getElementById('factoryResetRestartTimeout');
    progress?.classList.toggle('d-none', timedOut);
    timeout?.classList.toggle('d-none', !timedOut);
}

function runLineupFactoryResetWatch() {
    var generation = ++window.lineupFactoryResetWatchGeneration;
    setLineupFactoryResetWatchState(false);

    void (async function() {
        var deadline = Date.now() + 120000;
        while (Date.now() < deadline && generation === window.lineupFactoryResetWatchGeneration) {
            try {
                var response = await fetch('/api/runtime?factory-reset-status=' + Date.now(), { cache: 'no-store' });
                if (response.ok && (await response.json()).instanceId !== window.lineupFactoryResetInitialInstanceId) {
                    window.location.replace(window.lineupRestartDestination);
                    return;
                }
            } catch {}

            await new Promise(resolve => setTimeout(resolve, 1000));
        }

        if (generation === window.lineupFactoryResetWatchGeneration) {
            setLineupFactoryResetWatchState(true);
        }
    })();
}

async function beginLineupRestartWatch(destination) {
    var initialResponse = await fetch('/api/runtime', { cache: 'no-store' });
    if (!initialResponse.ok) {
        throw new Error('Unable to identify the running Lineup instance.');
    }

    if (!initialResponse.headers.get('content-type')?.includes('application/json')) {
        throw new Error('The Lineup runtime endpoint returned an unexpected response.');
    }

    window.lineupFactoryResetInitialInstanceId = (await initialResponse.json()).instanceId;
    window.lineupRestartDestination = typeof destination === 'string' && destination.startsWith('/') && !destination.startsWith('//') ? destination : '/';
    runLineupFactoryResetWatch();
}

function beginLineupFactoryResetWatch() {
    return beginLineupRestartWatch('/settings#physical-devices');
}

function retryLineupFactoryResetWatch() {
    if (!window.lineupFactoryResetInitialInstanceId) {
        window.location.reload();
        return;
    }

    runLineupFactoryResetWatch();
}
