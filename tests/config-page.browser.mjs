// Run with: node tests/config-page.browser.mjs (set CHROMIUM to override the executable).
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

// Jellyfin localizes the entire page source before executing inline scripts.
const page = readFileSync(new URL('../Jellyfin.Plugin.MagicCollections/Configuration/configPage.html', import.meta.url), 'utf8')
    .replace(/\$\{([^}]+)\}/g, '$1');
const theme = `<style>
    input[is="emby-input"] { display: block; border: 0.16em solid #292929; }
    input[is="emby-input"]:focus { border-color: #00a4dc; }
</style>`;
const harness = `${theme}<script>(${testPage})()</script>`;
const fixture = page.replace('</body>', `${harness}</body>`).replaceAll('&', '&amp;').replaceAll('"', '&quot;');
// Give the configuration a viewport for hit testing even in --dump-dom mode.
const testDocument = `<!doctype html><pre id="test-result">PENDING</pre><iframe width="250" height="800" srcdoc="${fixture}"></iframe>`;
const url = `data:text/html;charset=utf-8;base64,${Buffer.from(testDocument).toString('base64')}`;
const profile = mkdtempSync(join(tmpdir(), 'magic-collections-browser-test-'));
try {
    const browser = spawnSync(process.env.CHROMIUM || 'chromium', [
        '--headless', '--no-first-run', '--disable-background-networking', `--user-data-dir=${profile}`,
        '--dump-dom', '--virtual-time-budget=8000', url
    ], { encoding: 'utf8', timeout: 15000 });
    if (browser.error) throw browser.error;
    assert.equal(browser.status, 0, browser.stderr);
    const verdict = browser.stdout.match(/<pre id="test-result">(.*)<\/pre>/)?.[1];
    assert.equal(verdict, 'PASS', verdict || browser.stderr);
} finally {
    rmSync(profile, { recursive: true, force: true });
}

async function testPage() {
    const result = parent.document.querySelector('#test-result');
    const supported = 'https://letterboxd.com/example/list/movies/';
    const unsupported = 'https://letterboxd.com/film/the-matrix/';
    const uncheckable = 'https://letterboxd.com/example/list/check-fails/';
    const delayed = 'https://example.com/delayed';
    const checked = [];
    let saved = [unsupported];
    let saveCount = 0;
    let pendingCheck;
    window.Dashboard = {
        showLoadingMsg() {},
        hideLoadingMsg() {},
        processPluginConfigurationUpdateResult() {}
    };
    window.ApiClient = {
        async getPluginConfiguration() { return { SourceUrls: saved }; },
        async updatePluginConfiguration(_, config) {
            saved = config.SourceUrls;
            saveCount++;
        },
        getUrl(path) { return path; },
        async ajax(request) {
            const url = JSON.parse(request.data).Url;
            checked.push(url);
            if (url === uncheckable) throw new Error('Check failed');
            if (url === delayed) {
                pendingCheck = Promise.withResolvers();
                return pendingCheck.promise;
            }
            return url === supported;
        }
    };
    document.querySelector('#magicCollectionsPage').dispatchEvent(new Event('pageshow'));

    function enter(input, url) {
        input.value = url;
        input.dispatchEvent(new Event('input', { bubbles: true }));
    }

    const wait = ms => new Promise(resolve => setTimeout(resolve, ms));

    async function until(predicate, message) {
        const deadline = performance.now() + 1200;
        while (!predicate()) {
            if (performance.now() > deadline) throw new Error(message);
            await wait(20);
        }
    }

    try {
        const form = document.querySelector('form');
        const save = form.querySelector('button[type="submit"]');
        const field = (index = 1) => form.querySelector('input[aria-label="Source list URL ' + index + '"]');
        const invalid = (index = 1) => field(index)?.getAttribute('aria-invalid') === 'true';
        const borderColor = (index = 1) => getComputedStyle(field(index)).borderTopColor;
        const red = 'rgb(229, 57, 53)';
        await until(() => field(), 'The configuration did not load.');
        await wait(500);
        if (field().value !== unsupported || checked.length || invalid() || borderColor() === red) {
            throw new Error('Loading a saved URL should not trigger a check or mark it invalid.');
        }
        enter(field(2), '   ');
        await wait(500);
        if (checked.length || form.querySelectorAll('input').length !== 2) {
            throw new Error('A whitespace-only edit should not trigger a check or add an input.');
        }
        field().focus({ preventScroll: true });
        const focusedBorder = borderColor();
        enter(field(), '  ' + unsupported + '  ');
        save.scrollIntoView({ block: 'center' });
        const beforeWarning = save.getBoundingClientRect();
        const pointerX = beforeWarning.left + beforeWarning.width / 2;
        const pointerY = beforeWarning.top + 2;
        function verifySaveStayedPut(on) {
            const after = save.getBoundingClientRect();
            const target = document.elementFromPoint(pointerX, pointerY);
            if (after.top !== beforeWarning.top || after.left !== beforeWarning.left || !save.contains(target)) {
                throw new Error('Save moved out from under the pointer ' + on + ': ' +
                    beforeWarning.top + ' → ' + after.top + '; hit ' + target?.tagName +
                    ' at ' + pointerX + ',' + pointerY + ' in ' + innerWidth + 'x' + innerHeight + '.');
            }
        }
        await until(() => invalid() && borderColor() === red, 'The unsupported URL did not get a red border while focused.');
        const row = field().closest('.inputContainer');
        if (row.textContent.trim() || row.getBoundingClientRect().height !== field().getBoundingClientRect().height) {
            throw new Error('The invalid state should not add a message or reserve space below the input.');
        }
        if (checked[0] !== unsupported) throw new Error('The checked URL should be trimmed.');
        verifySaveStayedPut('when the border turned red');
        enter(field(), supported);
        await until(() => checked.includes(supported) && !invalid() && borderColor() === focusedBorder, 'A supported URL did not restore the normal border.');
        verifySaveStayedPut('when the normal border returned');
        enter(field(), unsupported);
        await until(() => invalid() && borderColor() === red, 'No red border after editing the URL.');
        field().blur();
        if (borderColor() !== red) throw new Error('The invalid border color should remain when the input loses focus.');
        save.click();
        await until(() => saveCount === 1 && saved.length === 1 && saved[0] === unsupported, 'Save did not keep the unsupported URL.');
        const checksAtSave = checked.length;
        await wait(500);
        if (checked.length !== checksAtSave) throw new Error('Saving should not trigger a check.');
        enter(field(), uncheckable);
        await until(() => checked.includes(uncheckable), 'The uncheckable URL was not checked.');
        if (invalid() || borderColor() === red) throw new Error('A failed check should not mark the URL invalid.');
        verifySaveStayedPut('when the check failed');
        save.click();
        await until(() => saveCount === 2 && saved.length === 1 && saved[0] === uncheckable, 'Save did not keep the URL after a failed check.');

        for (const finishCheck of [
            check => check.resolve(false),
            check => check.reject(new Error('Late failure'))
        ]) {
            pendingCheck = null;
            enter(field(), delayed);
            await until(() => pendingCheck, 'The delayed check did not start.');
            enter(field(), supported);
            await until(() => checked.at(-1) === supported, 'The replacement URL was not checked.');
            finishCheck(pendingCheck);
            await wait(0);
            if (invalid() || borderColor() === red) throw new Error('An outdated check changed the border for the replacement URL.');
        }

        pendingCheck = null;
        enter(field(), delayed);
        await until(() => pendingCheck, 'The check did not start before saving.');
        save.click();
        await until(() => saveCount === 3 && saved.length === 1 && saved[0] === delayed, 'An in-flight check blocked Save.');
        pendingCheck.resolve(false);
        await wait(0);
        if (invalid() || borderColor() === red) throw new Error('An in-flight check changed the border on a newly rendered row.');

        enter(field(2), unsupported);
        if (field(3)?.value !== '') throw new Error('Editing the trailing input should add a new blank input.');
        await until(() => invalid(2) && borderColor(2) === red, 'No red border when adding an unsupported URL.');
        if (invalid() || invalid(3)) throw new Error('Only the unsupported input should be marked invalid.');
        enter(field(2), '');
        if (invalid(2) || borderColor(2) === red) throw new Error('Clearing the URL should reset its border.');
        field(2).dispatchEvent(new Event('blur'));
        if (field(2)?.value !== '' || field(3)) throw new Error('Clearing an input and leaving it should remove the row.');
        result.textContent = 'PASS';
    } catch (error) {
        result.textContent = error.message;
    }
}
