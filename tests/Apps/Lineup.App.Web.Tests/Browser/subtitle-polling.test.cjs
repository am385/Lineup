const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.resolve(__dirname, '../../../../src/Apps/Lineup.App.Web/wwwroot/js/cmaf-playback.js'), 'utf8');
const caption = 'x'.repeat(65491) + '\u00e9'.repeat(40);
const sidecar = `WEBVTT\n\n00:00:00.000 --> 00:00:01.000\n${caption}\n\n00:00:02.000 --> 00:00:03.000\nTAIL-SENTINEL\n\n`;

function fixture(header, documents = { '/captions.vtt': sidecar }) {
    const timers = new Set();
    const requests = [];
    const warnings = [];
    const tracks = [];
    const sandbox = {
        window: {},
        TextDecoder,
        AbortController,
        Event: class {},
        VTTCue: class {
            constructor(startTime, endTime, text) {
                Object.assign(this, { startTime, endTime, text });
            }
        },
        console: { warn: message => warnings.push(message) },
        document: {
            createElement: () => ({
                addEventListener() {},
                remove() {},
                track: {
                    cues: [],
                    addCue(cue) { this.cues.push(cue); },
                    removeCue(cue) { this.cues.splice(this.cues.indexOf(cue), 1); }
                }
            })
        },
        setInterval: callback => { timers.add(callback); return callback; },
        clearInterval: callback => timers.delete(callback),
        fetch: async url => {
            const parsed = new URL(url, 'http://lineup.test');
            const offset = Number(parsed.searchParams.get('offset'));
            const bytes = Buffer.from(documents[parsed.pathname], 'utf8');
            const chunk = bytes.subarray(offset, offset + 65536);
            requests.push({ path: parsed.pathname, offset });
            return {
                ok: true,
                status: 200,
                headers: { get: () => header(offset + chunk.length, offset) },
                arrayBuffer: async () => Uint8Array.from(chunk).buffer
            };
        }
    };
    vm.createContext(sandbox);
    vm.runInContext(source, sandbox);
    return {
        requests,
        warnings,
        async start(url = '/captions.vtt', video = null) {
            video ??= {
                isConnected: true,
                currentTime: 0,
                appendChild(element) { tracks.push(element.track); },
                dispatchEvent() {}
            };
            sandbox.startLineupSubtitlePolling(video, url, url, 'eng', 'showing', false);
            await new Promise(setImmediate);
            return video;
        },
        async tick() { await Promise.all([...timers].map(callback => callback())); },
        tracks,
        stop(video) { sandbox.stopLineupSubtitlePolling(video); },
        get timerCount() { return timers.size; }
    };
}

test('readable offset headers advance beyond 64 KiB, preserve split UTF-8 and stop at EOF', async () => {
    // Arrange
    const polling = fixture(next => String(next));
    const video = await polling.start();
    const length = Buffer.byteLength(sidecar);

    // Act
    await polling.tick();
    await polling.tick();
    await polling.tick();
    const cues = polling.tracks[0].cues.map(cue => cue.text);
    polling.stop(video);

    // Assert
    assert.deepEqual([...Buffer.from(sidecar).subarray(65535, 65537)], [0xc3, 0xa9]);
    assert.deepEqual(polling.requests.map(request => request.offset), [0, 65536, length, length]);
    assert.deepEqual(cues, [caption, 'TAIL-SENTINEL']);
    assert.equal(polling.tracks[0].cues.length, 0);
    assert.equal(polling.timerCount, 0);
    assert.equal(polling.warnings.length, 0);
});

for (const [name, value] of [
    ['missing', null],
    ['empty', ''],
    ['whitespace', '  '],
    ['malformed', 'invalid'],
    ['negative', '-1'],
    ['fractional', '65536.5'],
    ['unsafe', '9007199254740992'],
    ['regressing', '0'],
    ['ahead of the body', '999999']
]) {
    test(`${name} offset header cannot reset or stall caption reads`, async () => {
        // Arrange
        const polling = fixture((next, offset) => offset === 0 ? String(next) : value);
        await polling.start();
        const length = Buffer.byteLength(sidecar);

        // Act
        await polling.tick();
        await polling.tick();
        await polling.tick();

        // Assert
        assert.deepEqual(polling.requests.map(request => request.offset), [0, 65536, length, length]);
        assert.deepEqual(polling.tracks[0].cues.map(cue => cue.text), [caption, 'TAIL-SENTINEL']);
        assert.equal(polling.warnings.length, 1);
        assert.match(polling.warnings[0], /offset.*received byte count/i);
    });
}

test('hidden offset headers from the first response still reach the tail cue', async () => {
    // Arrange
    const polling = fixture(() => null);
    await polling.start();

    // Act
    await polling.tick();
    await polling.tick();

    // Assert
    assert.deepEqual(polling.requests.map(request => request.offset), [0, 65536, Buffer.byteLength(sidecar)]);
    assert.deepEqual(polling.tracks[0].cues.map(cue => cue.text), [caption, 'TAIL-SENTINEL']);
    assert.equal(polling.warnings.length, 1);
});

test('multiple caption tracks retain independent byte offsets', async () => {
    // Arrange
    const short = 'WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nShort caption\n\n';
    const polling = fixture(() => null, { '/captions.vtt': sidecar, '/short.vtt': short });
    const video = await polling.start();
    await polling.start('/short.vtt', video);

    // Act
    await polling.tick();
    await polling.tick();

    // Assert
    assert.deepEqual(polling.requests.filter(request => request.path === '/captions.vtt').map(request => request.offset), [0, 65536, Buffer.byteLength(sidecar)]);
    assert.deepEqual(polling.requests.filter(request => request.path === '/short.vtt').map(request => request.offset), [0, Buffer.byteLength(short), Buffer.byteLength(short)]);
    assert.equal(polling.tracks[0].cues.at(-1).text, 'TAIL-SENTINEL');
    assert.equal(polling.tracks[1].cues[0].text, 'Short caption');
    assert.equal(polling.warnings.length, 2);
});
