// Run with: node tests/config-page.browser.mjs (set CHROMIUM to override the executable).
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

// Jellyfin localizes the entire page source before executing inline scripts.
const page = readFileSync(new URL('../Jellyfin.Plugin.MagicCollections/Configuration/configPage.html', import.meta.url), 'utf8')
    .replace(/\$\{([^}]+)\}/g, '$1');
const harness = `<pre id="test-result">PENDING</pre><script>(${testPage})()</script>`;
const url = `data:text/html;base64,${Buffer.from(page.replace('</body>', `${harness}</body>`)).toString('base64')}`;
const profile = mkdtempSync(join(tmpdir(), 'magic-collections-browser-test-'));
try {
    const browser = spawnSync(process.env.CHROMIUM || 'chromium', [
        '--headless', '--no-first-run', '--disable-background-networking', `--user-data-dir=${profile}`,
        '--dump-dom', '--virtual-time-budget=5000', url
    ], { encoding: 'utf8', timeout: 15000 });
    if (browser.error) throw browser.error;
    assert.equal(browser.status, 0, browser.stderr);
    assert.equal(browser.stdout.match(/<pre id="test-result">(.*)<\/pre>/)?.[1], 'PASS', browser.stderr);
} finally {
    rmSync(profile, { recursive: true, force: true });
}

async function testPage() {
    const result = document.querySelector('#test-result');
    const supported = 'https://letterboxd.com/example/list/movies/';
    const unsupported = 'https://letterboxd.com/film/the-matrix/';
    const checked = [];
    let saved;
    window.Dashboard = {
        showLoadingMsg() {},
        hideLoadingMsg() {},
        processPluginConfigurationUpdateResult() {}
    };
    window.ApiClient = {
        async getPluginConfiguration() { return { SourceUrls: saved || [] }; },
        async updatePluginConfiguration(_, config) { saved = config.SourceUrls; },
        getUrl(path) { return path; },
        async ajax(request) {
            const url = JSON.parse(request.data).Url;
            checked.push(url);
            return url === supported;
        }
    };
    document.querySelector('#magicCollectionsPage').dispatchEvent(new Event('pageshow'));

    function enter(input, url) {
        input.value = url;
        input.dispatchEvent(new Event('input', { bubbles: true }));
    }

    async function until(predicate, message) {
        const deadline = performance.now() + 1200;
        while (!predicate()) {
            if (performance.now() > deadline) throw new Error(message);
            await new Promise(resolve => setTimeout(resolve, 20));
        }
    }

    try {
        const field = () => document.querySelector('input[aria-label="Source list URL 1"]');
        const warning = () => document.getElementById(field()?.getAttribute('aria-describedby'))?.innerText;
        await until(field, 'The configuration did not load.');
        enter(field(), unsupported);
        await until(() => warning()?.includes('supported source list'), 'No warning for an unsupported URL.');
        enter(field(), supported);
        await until(() => checked.includes(supported) && warning() === '', 'A supported URL did not clear the warning.');
        enter(field(), unsupported);
        await until(() => warning()?.includes('supported source list'), 'No warning after editing the URL.');
        document.querySelector('button[type="submit"]').click();
        await until(() => saved?.length === 1 && saved[0] === unsupported, 'Save did not keep the unsupported URL.');
        await until(() => warning()?.includes('supported source list'), 'The saved URL lost its warning.');
        result.textContent = 'PASS';
    } catch (error) {
        result.textContent = error.message;
    }
}
