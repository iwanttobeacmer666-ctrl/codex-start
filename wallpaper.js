async function (settings, revision, clip = null) {
    if (location.protocol !== 'app:' || !document.documentElement) return false;

    const attribute = 'data-codex-start-region';
    const appShell = '[data-app-shell-frame], [data-app-shell-active-page], [data-testid="app-shell"], main';
    if (!document.querySelector(appShell)) return false;

    const previous = globalThis.__codexStartWallpaper;
    const sheet = new CSSStyleSheet();
    const root = document.documentElement;
    const selectors = {
        Sidebar: ['.app-shell-left-panel', '[data-app-shell-left-panel-appearance]', '[data-testid="left-panel"]', '[data-testid="sidebar"]',
            '[class*="leftPanel" i]', '[class*="sidebarRoot" i]', 'aside[aria-label]', 'nav[aria-label]'],
        Conversation: ['[data-app-shell-active-page="true"] [data-app-shell-main-surface]', '[data-app-shell-main-surface]',
            '[data-app-shell-active-page="true"] main', '[data-testid="shell-main"]',
            '[data-testid="conversation-panel"]', '[class*="mainContent" i]', 'main', '[role="main"]'],
        Composer: ['.composer-surface-chrome', '[data-composer-surface-variant]', '[data-testid="composer"]',
            '[class*="ComposerLayoutRoot" i]', 'form:has([contenteditable="true"])'],
        RightPanel: ['[data-app-shell-trailing-sidebar]', '[data-app-shell-right-panel-full-width="true"]',
            '[data-testid="right-panel"]', '[data-testid="auxiliary-sidebar"]',
            '[class*="rightPanel" i]', '[class*="rightSidebar" i]', '[data-testid="terminal-panel"]', '[data-testid="diff-panel"]'],
    };

    const regions = Object.entries(settings).filter(([name]) => name === 'Window' || name in selectors);
    const sources = new Map();
    const decode = async (url) => {
        if (!/^data:image\/(png|jpeg);base64,[a-z0-9+/=]+$/i.test(url)) return null;
        try {
            const picture = new Image();
            picture.src = url;
            await picture.decode();
            return url;
        } catch { return null; }
    };
    for (const [, region] of regions) {
        if (region.image && !sources.has(region.image)) sources.set(region.image, decode(region.image));
    }
    const images = new Map(await Promise.all([...sources].map(async ([url, promise]) => [url, await promise])));
    const rules = [];
    const pictureRegions = new Set();
    const videoRegions = new Set();
    const scale = (number) => Math.max(0, Math.min(100, Number(number) || 0));
    const tint = (percent) => `color-mix(in srgb, var(--color-token-bg-primary, Canvas) ${percent}%, transparent)`;
    const shade = (percent) => `linear-gradient(${tint(percent)}, ${tint(percent)})`;
    for (const [name, region] of regions) {
        const inherited = name !== 'Window' && region.inherit;
        const image = images.get(inherited ? settings.Window?.image : region.image);
        if (!image) {
            if (clip && settings.Window.video && (inherited || name === 'Window')) {
                videoRegions.add(name);
                rules.push(`[${attribute}="${name}"] { background: transparent !important; }`);
            }
            continue;
        }
        pictureRegions.add(name);
        rules.push(`[${attribute}="${name}"] {
            background-color: var(--color-token-bg-primary, Canvas) !important;
            background-image: ${shade(scale(region.veil))}, ${shade(100 - scale(region.strength))}, url("${image}") !important;
            background-size: cover !important;
            background-repeat: no-repeat !important;
            background-position: center !important;
            background-attachment: ${inherited || name === 'Window' ? 'fixed' : 'scroll'} !important;
        }`);
    }
    if (videoRegions.has('Window')) {
        const { strength, veil } = settings.Window;
        const opacity = scale(strength) * (100 - scale(veil)) / 10000;
        rules.push(`html[${attribute}="Window"] { background: var(--color-token-bg-primary, Canvas) !important; }
            video[data-codex-start-wallpaper-video] {
                position: fixed !important; inset: 0 !important;
                width: 100% !important; height: 100% !important;
                object-fit: cover !important; z-index: -1 !important;
                opacity: ${opacity} !important; pointer-events: none !important;
            }`);
    }
    if (pictureRegions.has('Window') || videoRegions.has('Window')) rules.push(`html[${attribute}="Window"] body,
        html[${attribute}="Window"] #root,
        html[${attribute}="Window"] [data-app-shell-frame],
        html[${attribute}="Window"] [data-app-shell-main-surface]:not([${attribute}]),
        html[${attribute}="Window"] [data-app-shell-active-page="true"]:not([${attribute}]) {
            background-color: transparent !important;
        }`);
    sheet.replaceSync(rules.join('\n'));

    let video, videoUrl;
    let videoReady = false;
    let videoFailed = false;
    const marked = new Map();
    const pick = (list) => {
        for (const selector of list) {
            const match = [...document.querySelectorAll(selector)].find((node) =>
                !node.closest('[data-app-shell-active-page="false"], [role="dialog"], [aria-hidden="true"]')
                && node.getClientRects().length > 0);
            if (match) return match;
        }
        return null;
    };
    const refresh = () => {
        const next = new Map();
        const active = (name) => pictureRegions.has(name) || (videoReady && videoRegions.has(name));
        if (active('Window')) next.set(root, 'Window');
        for (const [name, choices] of Object.entries(selectors)) {
            if (!active(name)) continue;
            const node = pick(choices);
            if (node && !next.has(node)) next.set(node, name);
        }
        for (const node of marked.keys()) if (!next.has(node)) node.removeAttribute(attribute);
        marked.clear();
        for (const [node, name] of next) {
            if (node.getAttribute(attribute) !== name) node.setAttribute(attribute, name);
            marked.set(node, name);
        }
    };
    let pending = 0;
    const observer = new MutationObserver(() => {
        if (pending) return;
        pending = setTimeout(() => { pending = 0; refresh(); }, 200);
    });

    const syncPlayback = () => {
        const player = video;
        if (!player) return;
        if (document.hidden) player.pause();
        else player.play().catch((error) => {
            if (player === video && !document.hidden && error.name !== 'AbortError') failVideo();
        });
    };
    const removeVideo = () => {
        if (!video) return;
        const player = video;
        video = null;
        document.removeEventListener('visibilitychange', syncPlayback);
        player.pause();
        player.remove();
        player.removeAttribute('src');
        player.load();
        URL.revokeObjectURL(videoUrl);
    };
    const failVideo = () => {
        if (!video) return;
        videoReady = false;
        videoFailed = true;
        removeVideo();
        refresh();
    };

    previous?.dispose?.();
    if (pictureRegions.size || videoRegions.size) {
        document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
        refresh();
        observer.observe(root, { subtree: true, childList: true, attributes: true,
            attributeFilter: ['hidden', 'aria-hidden', 'data-app-shell-active-page'] });
    }
    globalThis.__codexStartWallpaper = {
        revision,
        get rendered() { return videoRegions.size ? videoReady : !regions.length || marked.size > 0; },
        get videoFailed() { return videoFailed; },
        dispose() {
            observer.disconnect();
            clearTimeout(pending);
            removeVideo();
            for (const node of marked.keys()) node.removeAttribute(attribute);
            document.adoptedStyleSheets = document.adoptedStyleSheets.filter((item) => item !== sheet);
        },
    };
    if (videoRegions.has('Window')) {
        const player = video = document.createElement('video');
        player.setAttribute('data-codex-start-wallpaper-video', '');
        player.setAttribute('aria-hidden', 'true');
        player.tabIndex = -1;
        player.loop = true;
        player.muted = settings.Window.sound !== true;
        player.preload = 'auto';
        player.hidden = true;
        player.addEventListener('loadeddata', () => {
            if (video !== player) return;
            if (!player.videoWidth || !player.videoHeight) return failVideo();
            videoReady = true;
            player.hidden = false;
            refresh();
        }, { once: true });
        player.addEventListener('error', () => { if (video === player) failVideo(); }, { once: true });
        document.addEventListener('visibilitychange', syncPlayback);
        videoUrl = URL.createObjectURL(clip);
        player.src = videoUrl;
        document.body.prepend(player);
        syncPlayback();
    }
    return globalThis.__codexStartWallpaper.rendered;
}
