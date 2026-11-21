const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const test = require('node:test');

const root = path.resolve(__dirname, '..', '..', '..', '..');
const source = fs.readFileSync(path.join(root, 'src', 'Apps', 'Lineup.App.Web', 'wwwroot', 'js', 'streaming-flow-graph.js'), 'utf8');
const manifest = fs.readFileSync(path.join(root, 'build', 'browser-assets.json'));
const cache = path.join(root, 'out', 'browser-assets', crypto.createHash('sha256').update(manifest).digest('hex').toUpperCase());
const cytoscape = require(path.join(cache, 'cytoscape', 'cytoscape.min.js'));

class Element {
    constructor() {
        this.attributes = new Map();
        this.children = [];
        this.classes = new Set();
        this.classList = {
            toggle: (name, enabled) => enabled ? this.classes.add(name) : this.classes.delete(name),
            remove: name => this.classes.delete(name)
        };
        this.rect = { left: 0, top: 0, right: 120, bottom: 100, width: 120, height: 100 };
    }
    setAttribute(name, value) { this.attributes.set(name, value); }
    getAttribute(name) { return this.attributes.get(name); }
    getBoundingClientRect() { return this.rect; }
    append(...children) { children.forEach(child => { child.parent = this; this.children.push(child); }); }
    replaceChildren() { this.children = []; }
    remove() { this.parent.children = this.parent.children.filter(child => child !== this); }
}

function fixture() {
    let cy;
    const timers = new Map();
    const listeners = new Map();
    const observers = [];
    const errors = [];
    let nextTimer = 0;
    class Observer {
        constructor(callback) { this.callback = callback; this.connected = false; this.elements = new Set(); observers.push(this); }
        observe(element) { this.connected = true; this.elements.add(element); }
        unobserve(element) { this.elements.delete(element); }
        disconnect() { this.connected = false; this.elements.clear(); }
    }
    const document = {
        visibilityState: 'visible', documentElement: {},
        createElementNS: () => new Element(),
        addEventListener: (name, callback) => listeners.set(name, callback),
        removeEventListener: (name, callback) => { if (listeners.get(name) === callback) listeners.delete(name); }
    };
    const window = {
        cytoscape: options => {
            cy = cytoscape(options);
            return cy;
        }
    };
    const globals = {
        window, document, console: { error: (...args) => errors.push(args) },
        ResizeObserver: Observer, MutationObserver: Observer,
        setInterval: (callback, delay) => { timers.set(++nextTimer, { callback, delay }); return nextTimer; },
        clearInterval: id => timers.delete(id)
    };
    new Function(...Object.keys(globals), source)(...Object.values(globals));
    const svg = new Element();
    const cards = new Map();
    const container = new Element();
    Object.assign(container, { scrollWidth: 1000, scrollHeight: 500, scrollLeft: 45, scrollTop: 80, hidden: false,
        querySelector: () => svg, querySelectorAll: () => [...cards.values()] });
    const setCards = model => {
        const ids = new Set(model.nodes.map(node => node.id));
        for (const id of cards.keys()) if (!ids.has(id)) cards.delete(id);
        model.nodes.forEach((node, index) => {
            if (cards.has(node.id)) return;
            const card = new Element();
            card.setAttribute('data-flow-node-id', node.id);
            const left = Math.min(index, 2) * 250;
            const top = Math.max(index - 2, 0) * 150;
            card.rect = { left, top, right: left + 120, bottom: top + 100, width: 120, height: 100 };
            cards.set(node.id, card);
        });
    };
    return { api: window.lineupFlowGraph, window, container, svg, cards, setCards, document, timers, listeners, observers, errors,
        get cy() { return cy; } };
}

function snapshot() {
    return {
        nodes: ['source', 'shared', 'consumer-a', 'consumer-b'].map(id => ({ id, label: id, state: 'active' })),
        edges: [{ id: 'upstream', source: 'source', target: 'shared', state: 'active' },
            { id: 'a', source: 'shared', target: 'consumer-a', state: 'active' },
            { id: 'b', source: 'shared', target: 'consumer-b', state: 'idle' }],
        paths: [{ id: 'path-a', nodeIds: ['source', 'shared', 'consumer-a'], edgeIds: ['upstream', 'a'] }],
        selectedNodeId: null
    };
}

test('real graph connects the original card bounds and preserves DOM identity, scrolling and exact path selection', () => {
    // Arrange
    const f = fixture();
    const model = snapshot();
    f.setCards(model);
    try {
        f.api.update(f.container, model);
        const original = f.svg.children[0];
        const originalCard = f.cards.get('shared');
        model.selectedNodeId = 'shared';

        // Act
        f.api.update(f.container, model);

        // Assert
        assert.equal(f.container.scrollLeft, 45);
        assert.equal(f.container.scrollTop, 80);
        assert.equal(f.svg.children[0], original);
        assert.equal(f.cards.get('shared'), originalCard);
        assert.equal(original.children[0].getAttribute('d'), 'M 120 50 C 185 50 185 50 250 50');
        assert.match(original.getAttribute('class'), /state-active.*highlighted/);
        assert.match(f.svg.children[2].getAttribute('class'), /state-idle.*muted/);
        assert.ok(f.cards.get('consumer-b').classes.has('graph-muted'));
        assert.ok(f.cy.getElementById('shared').selected());
        assert.ok(f.cy.getElementById('a').hasClass('highlighted'));
        assert.ok(f.cy.getElementById('b').hasClass('muted'));
        assert.equal(f.cy.nodes().length, 4);
    } finally {
        f.api.dispose(f.container);
    }
    assert.ok(f.cy.destroyed());
    assert.ok(f.observers.every(observer => !observer.connected));
    assert.equal(f.svg.children.length, 0);
    assert.ok([...f.cards.values()].every(card => !card.classes.has('graph-muted')));
});

test('any selected node highlights all its exact producer and consumer routes without crossing shared device cards', () => {
    // Arrange
    const f = fixture();
    const model = {
        nodes: ['physical', 'source-a', 'source-b', 'virtual', 'processor-a', 'processor-b', 'consumer-a1', 'consumer-a2', 'consumer-b'].map(id => ({ id })),
        edges: [
            { id: 'physical-a', source: 'physical', target: 'source-a', state: 'active' },
            { id: 'physical-b', source: 'physical', target: 'source-b', state: 'active' },
            { id: 'source-a', source: 'source-a', target: 'virtual', state: 'active' },
            { id: 'source-b', source: 'source-b', target: 'virtual', state: 'active' },
            { id: 'processing-a', source: 'virtual', target: 'processor-a', state: 'active' },
            { id: 'processing-b', source: 'virtual', target: 'processor-b', state: 'active' },
            { id: 'consumer-a1', source: 'processor-a', target: 'consumer-a1', state: 'active' },
            { id: 'consumer-a2', source: 'processor-a', target: 'consumer-a2', state: 'active' },
            { id: 'consumer-b', source: 'processor-b', target: 'consumer-b', state: 'active' }
        ].map(edge => ({ ...edge, id: 'edge-' + edge.id })),
        paths: [
            { id: 'path-a1', nodeIds: ['physical', 'source-a', 'virtual', 'processor-a', 'consumer-a1'], edgeIds: ['edge-physical-a', 'edge-source-a', 'edge-processing-a', 'edge-consumer-a1'] },
            { id: 'path-a2', nodeIds: ['physical', 'source-a', 'virtual', 'processor-a', 'consumer-a2'], edgeIds: ['edge-physical-a', 'edge-source-a', 'edge-processing-a', 'edge-consumer-a2'] },
            { id: 'path-b', nodeIds: ['physical', 'source-b', 'virtual', 'processor-b', 'consumer-b'], edgeIds: ['edge-physical-b', 'edge-source-b', 'edge-processing-b', 'edge-consumer-b'] }
        ],
        selectedNodeId: 'processor-a'
    };
    f.setCards(model);
    try {
        // Act
        f.api.update(f.container, model);

        // Assert
        assert.equal(f.cy.getElementById('processor-a').outgoers('node').length, 2);
        assert.equal(f.cy.getElementById('processor-b').outgoers('node').length, 1);
        assert.ok(f.cy.getElementById('processor-a').selected());
        assert.ok(f.cy.getElementById('edge-processing-a').hasClass('highlighted'));
        assert.ok(f.cy.getElementById('edge-processing-b').hasClass('muted'));
        assert.ok(f.cy.getElementById('edge-consumer-a2').hasClass('highlighted'));
        assert.ok(f.cy.getElementById('edge-physical-a').hasClass('highlighted'));
        assert.ok(f.cy.getElementById('edge-physical-b').hasClass('muted'));
        assert.ok(!f.cards.get('processor-a').classes.has('graph-muted'));
        assert.ok(f.cards.get('processor-b').classes.has('graph-muted'));
        assert.equal(f.svg.children.length, 9);
        const aNodes = ['physical', 'source-a', 'virtual', 'processor-a', 'consumer-a1', 'consumer-a2'];
        const aEdges = ['edge-physical-a', 'edge-source-a', 'edge-processing-a', 'edge-consumer-a1', 'edge-consumer-a2'];
        const bNodes = ['physical', 'source-b', 'virtual', 'processor-b', 'consumer-b'];
        const bEdges = ['edge-physical-b', 'edge-source-b', 'edge-processing-b', 'edge-consumer-b'];
        const firstNodes = ['physical', 'source-a', 'virtual', 'processor-a', 'consumer-a1'];
        const firstEdges = ['edge-physical-a', 'edge-source-a', 'edge-processing-a', 'edge-consumer-a1'];
        const cases = [
            ['source-a', aNodes, aEdges],
            ['source-b', bNodes, bEdges],
            ['processor-b', bNodes, bEdges],
            ['consumer-a1', firstNodes, firstEdges],
            ['consumer-b', bNodes, bEdges],
            ['physical', model.nodes.map(node => node.id), model.edges.map(edge => edge.id)],
            ['virtual', model.nodes.map(node => node.id), model.edges.map(edge => edge.id)]
        ];
        for (const [selectedNodeId, nodes, edges] of cases) {
            model.selectedNodeId = selectedNodeId;
            f.api.update(f.container, model);
            assert.deepEqual(f.cy.nodes('.highlighted').map(node => node.id()).sort(), [...nodes].sort(), selectedNodeId);
            assert.deepEqual(f.cy.edges('.highlighted').map(edge => edge.id()).sort(), [...edges].sort(), selectedNodeId);
            assert.deepEqual([...f.cards].filter(([, card]) => !card.classes.has('graph-muted')).map(([id]) => id).sort(), [...nodes].sort(), selectedNodeId);
            assert.deepEqual(f.svg.children.filter(edge => edge.getAttribute('class').includes('highlighted'))
                .map(edge => edge.getAttribute('data-flow-edge-id')).sort(), [...edges].sort(), selectedNodeId);
        }
        for (const selectedNodeId of [null, 'removed-producer']) {
            model.selectedNodeId = selectedNodeId;
            f.api.update(f.container, model);
            assert.equal(f.cy.elements('.muted, .highlighted, :selected').length, 0);
            assert.ok([...f.cards.values()].every(card => !card.classes.has('graph-muted')));
            assert.ok(f.svg.children.every(edge => !/muted|highlighted/.test(edge.getAttribute('class'))));
        }
    } finally {
        f.api.dispose(f.container);
    }
});

test('consumer expansion and removal reconcile connections and resizing updates the measured endpoints', () => {
    // Arrange
    const f = fixture();
    const model = snapshot();
    f.setCards(model);
    try {
        f.api.update(f.container, model);
        model.selectedNodeId = 'shared';
        const removedCard = f.cards.get('consumer-b');
        model.nodes = model.nodes.filter(node => node.id !== 'consumer-b');
        model.edges = model.edges.filter(edge => edge.id !== 'b');
        model.nodes.push({ id: 'consumer-c', label: 'New consumer' });
        model.edges.push({ id: 'c', source: 'shared', target: 'consumer-c', state: 'active' });
        model.paths.push({ id: 'path-c', nodeIds: ['source', 'shared', 'consumer-c'], edgeIds: ['upstream', 'c'] });
        f.setCards(model);

        // Act
        f.api.update(f.container, model);
        f.cards.get('consumer-c').rect.top += 50;
        f.cards.get('consumer-c').rect.bottom += 50;
        f.container.scrollHeight = 750;
        f.observers[0].callback();

        // Assert
        assert.equal(f.cy.nodes().length, 4);
        assert.equal(f.cy.edges().length, 3);
        assert.equal(f.cy.getElementById('consumer-b').length, 0);
        assert.equal(f.svg.getAttribute('viewBox'), '0 0 1000 750');
        assert.equal(f.svg.children.length, 3);
        assert.ok(!f.svg.children.some(element => element.getAttribute('data-flow-edge-id') === 'b'));
        const added = f.svg.children.find(element => element.getAttribute('data-flow-edge-id') === 'c');
        assert.match(added.children[0].getAttribute('d'), /500 250$/);
        assert.ok(!f.observers[0].elements.has(removedCard));
        assert.deepEqual(f.cy.edges('.highlighted').map(edge => edge.id()).sort(), ['a', 'c', 'upstream']);
        assert.ok(!f.cards.get('consumer-c').classes.has('graph-muted'));
        model.paths = model.paths.filter(path => path.id !== 'path-a');
        f.api.update(f.container, model);
        assert.ok(f.cy.getElementById('a').hasClass('muted'));
        assert.ok(f.cy.getElementById('c').hasClass('highlighted'));
        f.cards.get('consumer-c').rect.left = 250;
        f.cards.get('consumer-c').rect.right = 370;
        f.observers[0].callback();
        assert.equal(added.children[0].getAttribute('d'), 'M 370 50 C 386 50 386 250 370 250');
    } finally {
        f.api.dispose(f.container);
        f.api.dispose(f.container);
    }
    assert.ok(f.observers.every(observer => !observer.connected));
    f.observers[0].callback();
    assert.equal(f.svg.children.length, 0);
});

test('missing graph library fails explicitly without removing the original accessible cards', () => {
    // Arrange
    const f = fixture();
    const model = snapshot();
    f.setCards(model);
    delete f.window.cytoscape;

    // Act
    const update = () => f.api.update(f.container, model);

    // Assert
    assert.throws(update, /graph library is unavailable/);
    assert.equal(f.cards.size, 4);
    assert.equal(f.observers.length, 0);
});

test('foreground refresh honors cadence, collapse, visibility, manual-only and cleanup', async () => {
    // Arrange
    const f = fixture();
    let calls = 0;
    const receiver = { invokeMethodAsync: async () => { calls++; } };
    const flush = () => new Promise(setImmediate);
    try {
        f.api.observe('one', f.container, receiver, 3);
        await flush();
        const timer = f.timers.values().next().value;
        assert.equal(timer.delay, 3000);
        f.container.hidden = true;
        f.observers[0].callback();
        timer.callback();
        f.container.hidden = false;
        f.observers[0].callback();
        await flush();
        f.document.visibilityState = 'hidden';
        f.listeners.get('visibilitychange')();
        timer.callback();
        f.document.visibilityState = 'visible';
        f.listeners.get('visibilitychange')();
        await flush();

        // Act
        f.api.observe('one', f.container, receiver, 0);
        timer.callback();
        await flush();

        // Assert
        assert.equal(calls, 3);
        assert.equal(f.timers.size, 0);
        assert.equal(f.listeners.size, 0);
        assert.ok(f.observers.every(observer => !observer.connected));
    } finally {
        f.api.stopObserving('one');
    }
});

test('slow refreshes do not overlap and a foreground transition catches up after completion', async () => {
    // Arrange
    const f = fixture();
    let release;
    let calls = 0;
    const receiver = { invokeMethodAsync: () => { calls++; return new Promise(resolve => { release = resolve; }); } };
    f.api.observe('one', f.container, receiver, 1);
    const timer = f.timers.values().next().value;
    timer.callback();
    timer.callback();
    f.document.visibilityState = 'hidden';
    f.listeners.get('visibilitychange')();
    f.document.visibilityState = 'visible';
    f.listeners.get('visibilitychange')();

    // Act
    const inFlightCalls = calls;
    release();
    await new Promise(setImmediate);
    f.api.stopObserving('one');
    release();
    timer.callback();
    await new Promise(setImmediate);

    // Assert
    assert.equal(inFlightCalls, 1);
    assert.equal(calls, 2);
    assert.equal(f.timers.size, 0);
    assert.equal(f.listeners.size, 0);
    assert.equal(f.errors.length, 0);
});
