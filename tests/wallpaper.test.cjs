const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', 'wallpaper.js'), 'utf8');
const label = 'data-codex-start-region';
const blue = 'data:image/png;base64,Ymx1ZQ==';
const green = 'data:image/jpeg;base64,Z3JlZW4=';

function element() {
    const attributes = new Map();
    return {
        getAttribute: (name) => attributes.get(name),
        setAttribute: (name, value) => attributes.set(name, value),
        removeAttribute: (name) => attributes.delete(name),
        closest: () => null,
        getClientRects: () => [{}],
    };
}

function renderer(protocol = 'app:') {
    const root = element();
    const regions = new Map();
    const players = [];
    const urls = new Set();
    const visibility = new Set();
    const document = {
        documentElement: root, querySelector: () => root,
        adoptedStyleSheets: [], querySelectorAll: (selector) => regions.get(selector) || [],
        hidden: false,
        body: { prepend: (player) => players.push(player) },
        addEventListener: (_, callback) => visibility.add(callback),
        removeEventListener: (_, callback) => visibility.delete(callback),
        createElement() {
            const events = new Map();
            return Object.assign(element(), {
                videoWidth: 1920, videoHeight: 1080, paused: true,
                addEventListener: (name, callback) => events.set(name, callback),
                fire: (name) => events.get(name)?.(),
                async play() { this.paused = false; },
                pause() { this.paused = true; },
                remove() { players.splice(players.indexOf(this), 1); },
                load() {},
            });
        },
    };
    let mutation, timer;
    const context = vm.createContext({
        document, location: { protocol },
        URL: {
            createObjectURL() { const url = `blob:video-${urls.size}`; urls.add(url); return url; },
            revokeObjectURL: (url) => urls.delete(url),
        },
        CSSStyleSheet: class { replaceSync(css) { this.css = css; } },
        Image: class { async decode() {} },
        MutationObserver: class {
            constructor(callback) { this.callback = callback; }
            observe() { mutation = this.callback; }
            disconnect() { if (mutation === this.callback) mutation = null; }
        },
        setTimeout(callback) { timer = callback; return 1; },
        clearTimeout() { timer = null; },
    });
    return {
        root, document, regions, players, urls,
        apply: vm.runInContext(`(${source})`, context),
        refresh() { mutation?.(); timer?.(); },
        show(visible) { document.hidden = !visible; for (const callback of visibility) callback(); },
        state: () => context.__codexStartWallpaper,
    };
}

test('a new sidebar and right panel inherit or receive their own pictures after navigation', async () => {
    const view = renderer();
    const sidebar = element();
    view.regions.set('.app-shell-left-panel', [sidebar]);
    assert.equal(await view.apply({
        Window: { image: blue, strength: 65, veil: 20 },
        Sidebar: { inherit: true, strength: 30, veil: 50 },
        RightPanel: { image: green, strength: 70, veil: 10 },
    }, 'first'), true);
    assert.equal(view.root.getAttribute(label), 'Window');
    assert.equal(sidebar.getAttribute(label), 'Sidebar');
    assert.match(view.document.adoptedStyleSheets[0].css, /Ymx1ZQ==/);
    assert.match(view.document.adoptedStyleSheets[0].css, /Z3JlZW4=/);

    const replacement = element();
    const panel = element();
    view.regions.set('.app-shell-left-panel', [replacement]);
    view.regions.set('[data-app-shell-trailing-sidebar]', [panel]);
    view.refresh();
    assert.equal(sidebar.getAttribute(label), undefined);
    assert.equal(replacement.getAttribute(label), 'Sidebar');
    assert.equal(panel.getAttribute(label), 'RightPanel');

    assert.equal(await view.apply({}, 'off'), true);
    assert.equal(view.document.adoptedStyleSheets.length, 0);
    assert.equal(view.root.getAttribute(label), undefined);
    assert.equal(panel.getAttribute(label), undefined);
});

test('non-app pages are untouched and an external image URL is refused', async () => {
    const web = renderer('https:');
    assert.equal(await web.apply({ Window: { image: blue } }, 'new'), false);
    const app = renderer();
    assert.equal(await app.apply({ Window: { image: 'https://example.com/photo.png' } }, 'new'), false);
    assert.equal(app.document.adoptedStyleSheets.length, 0);
});

test('video has one shared player, starts muted and pauses while hidden', async () => {
    const view = renderer();
    const sidebar = element();
    const panel = element();
    view.regions.set('.app-shell-left-panel', [sidebar]);
    view.regions.set('[data-app-shell-trailing-sidebar]', [panel]);
    const settings = {
        Window: { video: true, strength: 65, veil: 20 },
        Sidebar: { inherit: true },
        RightPanel: { image: green, strength: 70, veil: 10 },
    };
    await view.apply(settings, 'video', {});
    const [player] = view.players;
    assert.equal(player.muted, true);
    assert.equal(player.loop, true);
    assert.equal(player.paused, false);
    assert.equal(view.state().rendered, false);
    player.fire('loadeddata');
    assert.equal(view.state().rendered, true);
    assert.equal(sidebar.getAttribute(label), 'Sidebar');
    assert.equal(panel.getAttribute(label), 'RightPanel');
    assert.match(view.document.adoptedStyleSheets[0].css, /Z3JlZW4=/);
    view.show(false);
    assert.equal(player.paused, true);
    view.show(true);
    assert.equal(player.paused, false);

    settings.Window.sound = true;
    await view.apply(settings, 'sound', {});
    assert.equal(player.paused, true);
    assert.equal(view.players.length, 1);
    assert.equal(view.players[0].muted, false);
    assert.equal(view.urls.size, 1);
    await view.apply({ Window: { image: blue } }, 'picture');
    assert.equal(view.players.length, 0);
    assert.equal(view.urls.size, 0);
    assert.match(view.document.adoptedStyleSheets[0].css, /Ymx1ZQ==/);
});

test('a video error restores the themed base and keeps regional pictures', async () => {
    const view = renderer();
    const panel = element();
    view.regions.set('[data-app-shell-trailing-sidebar]', [panel]);
    await view.apply({ Window: { video: true }, RightPanel: { image: green } }, 'broken', {});
    view.players[0].fire('error');
    assert.equal(view.state().videoFailed, true);
    assert.equal(view.players.length, 0);
    assert.equal(view.urls.size, 0);
    assert.equal(view.root.getAttribute(label), undefined);
    assert.equal(panel.getAttribute(label), 'RightPanel');
});
