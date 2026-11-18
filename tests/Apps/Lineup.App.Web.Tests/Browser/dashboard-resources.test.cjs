const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.resolve(__dirname, '../../../../src/Apps/Lineup.App.Web/wwwroot/js/dashboard-resources.js'), 'utf8');

function fixture(receiver) {
    const timers = new Map();
    const listeners = new Map();
    const document = {
        visibilityState: 'visible',
        addEventListener: (name, callback) => listeners.set(name, callback),
        removeEventListener: (name, callback) => {
            if (listeners.get(name) === callback) listeners.delete(name);
        }
    };
    let nextTimer = 0;
    const sandbox = {
        window: {},
        console: { debug() {} },
        document,
        setInterval: callback => { timers.set(++nextTimer, callback); return nextTimer; },
        clearInterval: id => timers.delete(id)
    };
    vm.runInNewContext(source, sandbox);
    return { monitor: sandbox.window.lineupResources, timers, listeners, document, receiver };
}

test('hidden dashboards release interest and do not send periodic refreshes', async () => {
    // Arrange
    const calls = [];
    const f = fixture({ invokeMethodAsync: async (method, visible) => calls.push({ method, visible }) });

    // Act
    f.monitor.start('dashboard', f.receiver);
    await new Promise(setImmediate);
    f.document.visibilityState = 'hidden';
    await f.listeners.get('visibilitychange')();
    f.timers.values().next().value();
    f.document.visibilityState = 'visible';
    await f.listeners.get('visibilitychange')();
    f.monitor.stop('dashboard');

    // Assert
    assert.deepEqual(calls.map(call => call.visible), [true, false, true]);
    assert.ok(calls.every(call => call.method === 'ObserveVisibility'));
    assert.equal(f.timers.size, 0);
    assert.equal(f.listeners.size, 0);
});

test('slow observation never overlaps and replacement or disposal removes owned callbacks', async () => {
    // Arrange
    let complete;
    let calls = 0;
    const f = fixture({ invokeMethodAsync: () => { calls++; return new Promise(resolve => { complete = resolve; }); } });

    // Act
    f.monitor.start('dashboard', f.receiver);
    const originalTick = f.timers.values().next().value;
    originalTick();
    originalTick();
    const beforeCompletion = calls;
    complete();
    await Promise.resolve();
    f.monitor.start('dashboard', f.receiver);
    const afterReplacement = f.timers.size;
    f.monitor.stop('dashboard');
    originalTick();
    complete();
    await Promise.resolve();

    // Assert
    assert.equal(beforeCompletion, 1);
    assert.equal(calls, 2);
    assert.equal(afterReplacement, 1);
    assert.equal(f.timers.size, 0);
    assert.equal(f.listeners.size, 0);
});

test('hiding during an in-flight call sends the final visibility after that call completes', async () => {
    // Arrange
    const calls = [];
    let complete;
    const f = fixture({
        invokeMethodAsync: (method, visible) => {
            calls.push(visible);
            return visible ? new Promise(resolve => { complete = resolve; }) : Promise.resolve();
        }
    });

    // Act
    f.monitor.start('dashboard', f.receiver);
    f.document.visibilityState = 'hidden';
    await f.listeners.get('visibilitychange')();
    complete();
    await new Promise(setImmediate);
    f.monitor.stop('dashboard');

    // Assert
    assert.deepEqual(calls, [true, false]);
    assert.equal(f.timers.size, 0);
    assert.equal(f.listeners.size, 0);
});
