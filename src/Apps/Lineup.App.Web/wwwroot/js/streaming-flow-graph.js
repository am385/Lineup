(function (root) {
    "use strict";
    const instances = new Map();
    const refreshers = new Map();
    const svgNamespace = "http://www.w3.org/2000/svg";

    function connection(source, target) {
        const horizontal = target.left >= source.right;
        const start = { x: source.right, y: source.y };
        const end = horizontal ? { x: target.left, y: target.y }
            : { x: target.right, y: target.y };
        const middle = horizontal ? (start.x + end.x) / 2 : Math.max(start.x, end.x) + 16;
        const controls = `${middle} ${start.y} ${middle} ${end.y}`;
        const dx = horizontal ? 1 : -1;
        return {
            line: `M ${start.x} ${start.y} C ${controls} ${end.x} ${end.y}`,
            arrow: `M ${end.x} ${end.y} L ${end.x - dx * 8} ${end.y - 4} L ${end.x - dx * 8} ${end.y + 4} Z`
        };
    }

    function draw(instance) {
        if (instance.stopped) return;
        const bounds = instance.container.getBoundingClientRect();
        const positions = new Map();
        for (const [id, card] of instance.cards) {
            const rect = card.getBoundingClientRect();
            positions.set(id, { left: rect.left - bounds.left, right: rect.right - bounds.left,
                top: rect.top - bounds.top, bottom: rect.bottom - bounds.top,
                x: rect.left - bounds.left + rect.width / 2, y: rect.top - bounds.top + rect.height / 2 });
        }
        instance.svg.setAttribute("viewBox", `0 0 ${instance.container.scrollWidth} ${instance.container.scrollHeight}`);
        const ids = new Set();
        instance.cy.edges().forEach(edge => {
            const source = positions.get(edge.data("source"));
            const target = positions.get(edge.data("target"));
            if (!source || !target) return;
            ids.add(edge.id());
            let shape = instance.lines.get(edge.id());
            if (!shape) {
                const group = document.createElementNS(svgNamespace, "g");
                group.setAttribute("data-flow-edge-id", edge.id());
                const line = document.createElementNS(svgNamespace, "path");
                const arrow = document.createElementNS(svgNamespace, "path");
                line.setAttribute("class", "flow-edge-line");
                arrow.setAttribute("class", "flow-edge-arrow");
                group.append(line, arrow);
                instance.svg.append(group);
                shape = { group, line, arrow };
                instance.lines.set(edge.id(), shape);
            }
            const curve = connection(source, target);
            shape.line.setAttribute("d", curve.line);
            shape.arrow.setAttribute("d", curve.arrow);
            shape.group.setAttribute("class", `flow-edge state-${String(edge.data("state")).toLowerCase()} ${edge.classes().join(" ")}`);
        });
        for (const [id, shape] of instance.lines) {
            if (!ids.has(id)) {
                shape.group.remove();
                instance.lines.delete(id);
            }
        }
    }

    root.lineupFlowGraph = {
        update(container, snapshot) {
            let instance = instances.get(container);
            if (!instance) {
                if (!root.cytoscape) throw new Error("The streaming graph library is unavailable.");
                const svg = container.querySelector("[data-flow-edges]");
                if (!svg) throw new Error("The streaming graph connection surface is unavailable.");
                const cy = root.cytoscape({ headless: true, elements: [] });
                instance = { cy, container, svg, cards: new Map(), lines: new Map(), stopped: false };
                instances.set(container, instance);
                instance.resize = new ResizeObserver(() => draw(instance));
                instance.resize.observe(container);
            }
            const cy = instance.cy;
            const elements = [
                ...snapshot.nodes.map(node => ({ group: "nodes", data: node })),
                ...snapshot.edges.map(edge => ({ group: "edges", data: edge }))
            ];
            const ids = new Set(elements.map(element => element.data.id));
            cy.batch(() => {
                cy.elements().filter(element => !ids.has(element.id())).remove();
                for (const element of elements) {
                    const current = cy.getElementById(element.data.id);
                    if (current.length) {
                        current.data(element.data);
                    } else {
                        cy.add(element);
                    }
                }
                cy.elements().unselect().removeClass("muted highlighted");
                const selected = cy.getElementById(snapshot.selectedNodeId || "");
                if (selected.length) {
                    selected.select();
                    // Declared routes avoid crossing unrelated channels at shared device cards.
                    const paths = snapshot.paths.filter(path => path.nodeIds.includes(snapshot.selectedNodeId));
                    const members = new Set([snapshot.selectedNodeId, ...paths.flatMap(path => [...path.nodeIds, ...path.edgeIds])]);
                    cy.elements().forEach(element => element.addClass(members.has(element.id()) ? "highlighted" : "muted"));
                }
            });
            const cards = new Map([...container.querySelectorAll("[data-flow-node-id]")].map(card => [card.getAttribute("data-flow-node-id"), card]));
            for (const card of instance.cards.values()) {
                if (!cards.has(card.getAttribute("data-flow-node-id"))) instance.resize.unobserve(card);
            }
            for (const [id, card] of cards) {
                if (instance.cards.get(id) !== card) {
                    if (instance.cards.has(id)) instance.resize.unobserve(instance.cards.get(id));
                    instance.resize.observe(card);
                }
                card.classList.toggle("graph-muted", cy.getElementById(id).hasClass("muted"));
            }
            instance.cards = cards;
            draw(instance);
        },
        dispose(container) {
            const instance = instances.get(container);
            if (!instance) return;
            instance.stopped = true;
            instance.resize.disconnect();
            instance.cards.forEach(card => card.classList.remove("graph-muted"));
            instance.svg.replaceChildren();
            instance.cy.destroy();
            instances.delete(container);
        },
        observe(id, element, receiver, seconds) {
            this.stopObserving(id);
            if (seconds <= 0) return;
            let pending = false;
            let stopped = false;
            let catchUp = false;
            let visible = false;
            const isVisible = () => document.visibilityState === "visible" && !element.hidden;
            const refresh = async () => {
                if (stopped || !isVisible()) return;
                if (pending) { catchUp = true; return; }
                pending = true;
                try {
                    await receiver.invokeMethodAsync("RefreshVisibleStreams");
                } catch (error) {
                    if (!stopped) console.error("Streaming graph refresh failed", error);
                } finally {
                    pending = false;
                    if (catchUp && !stopped && isVisible()) {
                        catchUp = false;
                        void refresh();
                    }
                }
            };
            const changed = () => {
                const next = isVisible();
                if (next && !visible) void refresh();
                visible = next;
            };
            const observer = new MutationObserver(changed);
            observer.observe(element, { attributes: true, attributeFilter: ["hidden"] });
            document.addEventListener("visibilitychange", changed);
            const timer = setInterval(() => { if (!pending) void refresh(); }, seconds * 1000);
            refreshers.set(id, () => {
                stopped = true;
                clearInterval(timer);
                observer.disconnect();
                document.removeEventListener("visibilitychange", changed);
            });
            changed();
        },
        stopObserving(id) {
            const stop = refreshers.get(id);
            if (stop) { stop(); refreshers.delete(id); }
        }
    };
})(window);
