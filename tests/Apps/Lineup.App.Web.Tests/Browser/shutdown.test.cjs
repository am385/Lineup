const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.resolve(__dirname, '../../../../src/Apps/Lineup.App.Web/wwwroot/js/cmaf-playback.js'), 'utf8');

function fixture(respond) {
    const requests = [];
    const sandbox = {
        window: {},
        AbortController,
        AbortSignal,
        Event: class {},
        document: { getElementById: () => null },
        fetch: async (url, options) => {
            requests.push({ url, options });
            return respond(url);
        }
    };
    vm.createContext(sandbox);
    vm.runInContext(source, sandbox);
    return { requests, stop: sandbox.stopCmafSession, stopPlayer: sandbox.stopMediaPlayer, sandbox };
}

function attachPlayer(shutdown, destroy, useUi = false) {
    const video = {
        _lineupPlayerToken: Symbol('original'),
        textTracks: [],
        src: 'original',
        loads: 0,
        pause() {},
        dispatchEvent() {},
        removeAttribute(name) { if (name === 'src') this.src = null; },
        load() { this.loads++; }
    };
    const { window } = shutdown.sandbox;
    shutdown.sandbox.document.getElementById = () => video;
    window.lineupMediaPlayers = new Map([['player', video]]);
    window.lineupMediaPlayerOwners.set('player', 'owner');
    window.lineupCmafPlayers.set('player', { destroy });
    if (useUi) window.lineupCmafUis.set('player', { destroy });
    return video;
}

for (const useUi of [false, true]) {
    test(`failed ${useUi ? 'UI' : 'player'} destruction is reported after clearing owned browser state`, async () => {
        // Arrange
        const shutdown = fixture(() => assert.fail('No request expected'));
        const video = attachPlayer(shutdown, () => Promise.reject(new Error('Destroy failed')), useUi);

        // Act
        const stopping = shutdown.stopPlayer('player', 'owner');

        // Assert
        await assert.rejects(stopping, /Destroy failed/);
        assert.equal(video.src, null);
        assert.equal(video.loads, 1);
        assert.equal(shutdown.sandbox.window.lineupCmafPlayers.size, 0);
        assert.equal(shutdown.sandbox.window.lineupCmafUis.size, 0);
        assert.equal(shutdown.sandbox.window.lineupMediaPlayers.size, 0);
        assert.equal(shutdown.sandbox.window.lineupMediaPlayerOwners.size, 0);
    });
}

test('delayed destruction cannot clear a replacement player or its video source', async () => {
    // Arrange
    const shutdown = fixture(() => assert.fail('No request expected'));
    let release;
    const destroyed = new Promise(resolve => { release = resolve; });
    const video = attachPlayer(shutdown, () => destroyed, true);
    const { window } = shutdown.sandbox;
    const replacement = { destroy() { assert.fail('Replacement must survive'); } };

    // Act
    const stopping = shutdown.stopPlayer('player', 'owner');
    video._lineupPlayerToken = Symbol('replacement');
    video.src = 'replacement';
    window.lineupCmafPlayers.set('player', replacement);
    window.lineupCmafUis.set('player', replacement);
    window.lineupMediaPlayerOwners.set('player', 'replacement-owner');
    release();
    await stopping;
    await shutdown.stopPlayer('player', 'owner');

    // Assert
    assert.equal(video.src, 'replacement');
    assert.equal(video.loads, 0);
    assert.equal(window.lineupCmafPlayers.get('player'), replacement);
    assert.equal(window.lineupCmafUis.get('player'), replacement);
    assert.equal(window.lineupMediaPlayers.get('player'), video);
    assert.equal(window.lineupMediaPlayerOwners.get('player'), 'replacement-owner');
});

test('repeated browser stop destroys the owned player once and leaves other players alone', async () => {
    // Arrange
    const shutdown = fixture(() => assert.fail('No request expected'));
    let destroys = 0;
    attachPlayer(shutdown, async () => { destroys++; });
    const unrelated = { destroy() { assert.fail('Unrelated player must survive'); } };
    shutdown.sandbox.window.lineupCmafPlayers.set('other-player', unrelated);

    // Act
    await shutdown.stopPlayer('player', 'owner');
    await shutdown.stopPlayer('player', 'owner');

    // Assert
    assert.equal(destroys, 1);
    assert.equal(shutdown.sandbox.window.lineupCmafPlayers.get('other-player'), unrelated);
});

for (const status of [200, 204, 404]) {
    test(`owned stop accepts HTTP ${status} and preserves explicit routing and keepalive`, async () => {
        // Arrange
        const shutdown = fixture(() => ({ ok: status < 300, status }));
        const url = 'http://worker.test/media/stop/owned';

        // Act
        await shutdown.stop('owned', url);
        await shutdown.stop('owned', url);

        // Assert
        assert.equal(shutdown.requests.length, 2);
        for (const request of shutdown.requests) {
            assert.equal(request.url, url);
            assert.equal(request.options.method, 'POST');
            assert.equal(request.options.keepalive, true);
            assert.equal(request.options.cache, 'no-store');
        }
    });
}

for (const status of [400, 401, 403, 429, 500, 503]) {
    test(`HTTP ${status} is a failed stop, not successful cleanup`, async () => {
        // Arrange
        const shutdown = fixture(() => ({ ok: false, status }));

        // Act
        const stopping = shutdown.stop('owned', '/api/stream/cmaf/stop/owned');

        // Assert
        await assert.rejects(stopping, new RegExp(`HTTP ${status}`));
        assert.equal(shutdown.requests.length, 1);
    });
}

test('network failure is propagated without stopping another session', async () => {
    // Arrange
    const shutdown = fixture(() => { throw new Error('Connection lost'); });

    // Act
    const stopping = shutdown.stop('owned', '/api/stream/cmaf/stop/owned');

    // Assert
    await assert.rejects(stopping, /Connection lost/);
    assert.deepEqual(shutdown.requests.map(request => request.url), ['/api/stream/cmaf/stop/owned']);
});

test('missing stop routing is explicit failure instead of a success-shaped no-op', async () => {
    // Arrange
    const shutdown = fixture(() => assert.fail('No request expected'));

    // Act
    const stopping = shutdown.stop('owned');

    // Assert
    await assert.rejects(stopping, /stop URL is missing/);
    assert.equal(shutdown.requests.length, 0);
});

for (const [code, retryable] of [['source_unavailable', true], ['protected_content', false], ['source_invalid', false]]) {
    test(`recovery startup preserves explicit ${code} classification`, async () => {
        // Arrange
        const shutdown = fixture(() => ({ ok: false, status: 503, json: async () => ({ error: 'Startup failed', code, retryable }) }));

        // Act
        const result = await shutdown.sandbox.tryStartCmafSession('/start', {}, 'owned');

        // Assert
        assert.equal(result.session, null);
        assert.equal(result.failure.code, code);
        assert.equal(result.failure.retryable, retryable);
        assert.equal(shutdown.sandbox.window.lineupCmafStarts.size, 0);
    });
}

test('network failure is not invented retryable startup metadata', async () => {
    // Arrange
    const shutdown = fixture(() => { throw new Error('Connection lost'); });

    // Act
    const starting = shutdown.sandbox.tryStartCmafSession('/start', {}, 'owned');

    // Assert
    await assert.rejects(starting, /Connection lost/);
    assert.equal(shutdown.sandbox.window.lineupCmafStarts.size, 0);
});

test('passive session status does not infer a retryable outcome from an HTTP status', async () => {
    // Arrange
    const shutdown = fixture(() => ({ ok: false, status: 404 }));

    // Act
    const reading = shutdown.sandbox.getCmafSessionStatus('/status/owned');

    // Assert
    await assert.rejects(reading, /HTTP 404/);
    assert.equal(shutdown.requests[0].options.cache, 'no-store');
    assert.ok(shutdown.requests[0].options.signal instanceof AbortSignal);
});

test('fresh presentation restores volume, mute, audio and subtitles without undoing video fallback', () => {
    // Arrange
    const shutdown = fixture(() => assert.fail('No request expected'));
    const video = attachPlayer(shutdown, async () => {});
    video._lineupSessionId = 'original';
    video.volume = 0.35;
    video.muted = true;
    video.textTracks = [{ label: 'English', language: 'eng', mode: 'showing' }];
    const { window } = shutdown.sandbox;
    window.lineupCmafPlayers.set('player', { getVariantTracks: () => [{ active: true, label: 'French', language: 'fra' }] });
    const preferences = shutdown.sandbox.getCmafPlayerPreferences('player', 'original');
    let selected;
    const fallbackVideo = { active: true, videoId: 2, label: 'English', language: 'eng' };
    const expectedAudio = { videoId: 2, label: 'French', language: 'fra' };
    window.lineupCmafPlayers.set('player', {
        getVariantTracks: () => [{ videoId: 1, label: 'French', language: 'fra' }, fallbackVideo, expectedAudio],
        selectVariantTrack: track => { selected = track; }
    });
    video._lineupSessionId = 'replacement';
    video.volume = 1;
    video.muted = false;
    video.textTracks[0].mode = 'hidden';

    // Act
    shutdown.sandbox.restoreCmafPlayerPreferences('player', 'replacement', preferences);

    // Assert
    assert.equal(video.volume, 0.35);
    assert.equal(video.muted, true);
    assert.equal(selected, expectedAudio);
    assert.equal(video.textTracks[0].mode, 'showing');
    assert.equal(shutdown.sandbox.getCmafPlayerPreferences('player', 'original'), null);
    video.volume = 0.8;
    shutdown.sandbox.restoreCmafPlayerPreferences('player', 'original', preferences);
    assert.equal(video.volume, 0.8);
});
