export function median(samples) {
    const sorted = samples.filter(x => Number.isFinite(x) && x >= 0).sort((a, b) => a - b);
    if (!sorted.length) return null;
    const middle = Math.floor(sorted.length / 2);
    return sorted.length % 2 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
}
const sessions = new Map();
let sequence = 0;
export function start(dotnet, nodes) {
    const id = ++sequence;
    const state = { dotnet, nodes, samples: new Map(), results: {}, controllers: new Set(), disposed: false, timer: null };
    sessions.set(id, state);
    void round(state, 3);
    return id;
}
export function update(id, nodes) { const state = sessions.get(id); if (state) state.nodes = nodes; }
export function close(element) { element.open = false; }
export function stop(id) {
    const state = sessions.get(id);
    if (!state) return;
    state.disposed = true;
    clearTimeout(state.timer);
    for (const controller of state.controllers) controller.abort();
    sessions.delete(id);
}
async function round(state, count) {
    await Promise.all(state.nodes.map(async node => {
        if (state.disposed) return;
        if (!node.publicProbeUrl) { state.results[node.id] = { ms: null, status: 'Not measured' }; return; }
        let successful = false;
        for (let i = 0; i < count && !state.disposed; i++) {
            const controller = new AbortController();
            state.controllers.add(controller);
            const timer = setTimeout(() => controller.abort(), 2500);
            try {
                const url = new URL(node.publicProbeUrl, location.href);
                if (url.protocol !== 'https:' && !(url.protocol === 'http:' && ['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname)))
                    throw new Error('HTTPS required');
                url.searchParams.set('_probe', crypto.randomUUID());
                const started = performance.now();
                const response = await fetch(url, { method: 'GET', cache: 'no-store', credentials: 'omit', redirect: 'error', signal: controller.signal });
                if (!response.ok) throw new Error('Probe unavailable');
                await response.arrayBuffer();
                const samples = state.samples.get(node.id) || [];
                samples.push(performance.now() - started);
                if (samples.length > 5) samples.shift();
                state.samples.set(node.id, samples);
                state.results[node.id] = { ms: median(samples), status: 'Measured' };
                successful = true;
            } catch (error) {
                if (!successful) state.results[node.id] = { ms: null, status: error.name === 'AbortError' ? 'Timeout' : 'Unreachable' };
            } finally { clearTimeout(timer); state.controllers.delete(controller); }
        }
        if (!successful) state.samples.delete(node.id); // Do not show an old low ping after an outage.
        if (!state.disposed) {
            try { await state.dotnet.invokeMethodAsync('LatenciesUpdated', state.results); }
            catch { state.disposed = true; }
        }
    }));
    if (!state.disposed) state.timer = setTimeout(() => void round(state, 1), 10000);
}
