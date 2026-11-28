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
    .block { display: block; }
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
        '--dump-dom', '--virtual-time-budget=12000', url
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
    const pastedTraktList = 'https://trakt.tv/users/Justin/lists/OPEN-YOUR-EYES/?access_token=example-token#example-fragment';
    const cleanTraktList = 'https://app.trakt.tv/users/justin/lists/open-your-eyes';
    const radarr = 'https://radarr.example.com/radarr';
    const anotherRadarr = 'https://another.example.com';
    const sonarr = 'https://sonarr.example.com/sonarr';
    const arrSource = radarr + '/movie-night';
    const arrChecks = [];
    let checkContainedKey = false;
    const checked = [];
    let saved = { SourceUrls: [unsupported], PosterBackgroundImage: true, PosterListName: true };
    let saveCount = 0;
    let pendingCheck;
    let connected = false;
    let expired = false;
    let submittedResponse;
    let rejectTraktResponse = true;
    window.Dashboard = {
        showLoadingMsg() {},
        hideLoadingMsg() {},
        processPluginConfigurationUpdateResult() {}
    };
    window.ApiClient = {
        async getPluginConfiguration() { return structuredClone(saved); },
        async updatePluginConfiguration(_, config) {
            // Jellyfin persists the normalized configuration, returning no config in the POST response.
            saved = { ...config, SourceUrls: config.SourceUrls.map(url => url === pastedTraktList ? cleanTraktList : url) };
            saveCount++;
        },
        getUrl(path) { return path; },
        async ajax(request) {
            if (request.url === 'MagicCollections/Trakt/Status') {
                if (expired) return { Connected: false, Account: 'Example Owner', Notice: 'Trakt authorization expired.' };
                return { Connected: connected, Account: connected ? 'Example Owner' : null };
            }
            if (request.url === 'MagicCollections/Trakt/Start') {
                if (saved.TraktClientId || saveCount !== 7) throw new Error('Connecting should not require a Client ID or another save.');
                return { AuthorizationUrl: 'https://auth.trakt.tv/oauth/authorize?state=test-state' };
            }
            if (request.url === 'MagicCollections/Trakt/Complete') {
                submittedResponse = JSON.parse(request.data);
                if (rejectTraktResponse) {
                    rejectTraktResponse = false;
                    throw Response.json({ Error: 'Trakt rejected the one-time code. Start a new connection.' }, { status: 400 });
                }
                connected = true;
                return { Connected: connected, Account: 'Example Owner' };
            }
            if (request.url === 'MagicCollections/Trakt/Disconnect') {
                connected = false;
                return { Connected: false };
            }
            const body = JSON.parse(request.data);
            const url = body.Url;
            checkContainedKey ||= request.data.includes('draft-key');
            checked.push(url);
            if (url === uncheckable) throw new Error('Check failed');
            if (url === delayed) {
                pendingCheck = Promise.withResolvers();
                return pendingCheck.promise;
            }
            if (url === arrSource) {
                arrChecks.push(body);
                return body.RadarrBaseUrls?.includes(radarr) ?? false;
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
        const posterToggle = form.querySelector('#magicCollectionsPoster > summary');
        const posterOptions = [...form.querySelectorAll('#magicCollectionsPoster input[type="checkbox"]')];
        const traktToggle = [...form.querySelectorAll('summary')].find(summary => summary.textContent.trim() === 'Trakt');
        const traktClientId = form.elements.TraktClientId;
        const advancedToggle = [...form.querySelectorAll('summary')].find(summary => summary.textContent.trim() === 'Advanced');
        const verboseLogging = form.elements.VerboseLogging;
        const visiblePosterOptions = () => posterOptions.filter(option => option.checkVisibility()).length;
        const field = (index = 1) => form.querySelector('input[aria-label="Source list URL ' + index + '"]');
        const invalid = (index = 1) => field(index)?.getAttribute('aria-invalid') === 'true';
        const borderColor = (index = 1) => getComputedStyle(field(index)).borderTopColor;
        const red = 'rgb(229, 57, 53)';
        await until(() => field(), 'The configuration did not load.');
        if (posterOptions.length !== 3 || form.elements.PosterIcon.checked ||
                !form.elements.PosterBackgroundImage.checked || !form.elements.PosterListName.checked) {
            throw new Error('Only the poster background image and list name should start checked.');
        }
        if (posterToggle?.textContent.trim() !== 'Poster' || visiblePosterOptions() !== 0) {
            throw new Error('The Poster section should start collapsed.');
        }
        if (!traktToggle || traktToggle.parentElement.open) {
            throw new Error('The Trakt section should start collapsed.');
        }
        traktToggle.click();
        const traktPanel = traktToggle.parentElement;
        if (!traktPanel.open ||
                !traktPanel.innerText.includes("Public lists and charts don't require a Trakt account") ||
                !traktPanel.innerText.includes('Magic Collections uses Trakt but is not endorsed or certified by Trakt')) {
            throw new Error('Expanding Trakt should show the setup guidance and notice.');
        }
        if (traktPanel.contains(traktClientId)) throw new Error('The Client ID field should not be in the Trakt panel.');
        traktToggle.click();
        if (verboseLogging?.type !== 'checkbox' || verboseLogging.checked) {
            throw new Error('Verbose logging should be an unchecked checkbox when absent from saved settings.');
        }
        if (!advancedToggle || !traktClientId || verboseLogging.checkVisibility() || traktClientId.checkVisibility()) {
            throw new Error('Advanced should start collapsed and hide verbose logging and the Client ID override.');
        }
        await wait(500);
        if (field().value !== unsupported || checked.length || invalid() || borderColor() === red) {
            throw new Error('Loading a saved URL should not trigger a check or mark it invalid.');
        }
        enter(field(2), '   ');
        await wait(500);
        if (checked.length || document.querySelectorAll('#magicCollectionsUrls input').length !== 2) {
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
        posterToggle.click();
        if (visiblePosterOptions() !== 3) throw new Error('Expanding Poster should show all three options.');
        form.elements.PosterIcon.click();
        posterToggle.click();
        if (visiblePosterOptions() !== 0) throw new Error('Collapsing Poster should hide the options again.');
        advancedToggle.click();
        const advancedPanel = advancedToggle.parentElement;
        const loggingLabel = verboseLogging.labels[0];
        if (!loggingLabel?.checkVisibility() || !advancedPanel.contains(verboseLogging) ||
                !traktClientId.checkVisibility() || !advancedPanel.contains(traktClientId) ||
                !advancedPanel.innerText.includes('Paste only the Client ID, or leave blank to use the bundled ID.')) {
            throw new Error('Expanding Advanced should show verbose logging and the optional Client ID field.');
        }
        const loggingGap = loggingLabel.getBoundingClientRect().top - advancedToggle.getBoundingClientRect().bottom;
        if (loggingGap < 8) throw new Error('The verbose logging checkbox needs space below the section heading.');
        loggingLabel.click();
        advancedToggle.click();
        if (verboseLogging.checkVisibility() || traktClientId.checkVisibility()) {
            throw new Error('Collapsing Advanced should hide both options again.');
        }
        save.click();
        await until(() => saveCount === 1 && saved.SourceUrls.length === 1 && saved.SourceUrls[0] === unsupported, 'Save did not keep the unsupported URL.');
        if (saved.PosterIcon !== true || saved.PosterBackgroundImage !== true || saved.PosterListName !== true) {
            throw new Error('Save did not keep the selected poster options.');
        }
        if (saved.VerboseLogging !== true) throw new Error('Save did not enable verbose logging.');
        const checksAtSave = checked.length;
        await wait(500);
        if (checked.length !== checksAtSave) throw new Error('Saving should not trigger a check.');
        enter(field(), uncheckable);
        await until(() => checked.includes(uncheckable), 'The uncheckable URL was not checked.');
        if (invalid() || borderColor() === red) throw new Error('A failed check should not mark the URL invalid.');
        verifySaveStayedPut('when the check failed');
        save.click();
        await until(() => saveCount === 2 && saved.SourceUrls.length === 1 && saved.SourceUrls[0] === uncheckable, 'Save did not keep the URL after a failed check.');

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
        await until(() => saveCount === 3 && saved.SourceUrls.length === 1 && saved.SourceUrls[0] === delayed, 'An in-flight check blocked Save.');
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
        posterToggle.click();
        form.elements.PosterIcon.click();
        advancedToggle.click();
        verboseLogging.click();
        document.querySelector('#magicCollectionsPage').dispatchEvent(new Event('pageshow'));
        await until(() => form.elements.PosterIcon.checked && verboseLogging.checked, 'Reloading did not restore the saved checkbox states.');
        verboseLogging.click();
        save.click();
        await until(() => saveCount === 4 && saved.VerboseLogging === false, 'Save did not disable verbose logging.');

        const clientId = 'example-trakt-client-id';
        const checksBeforeOverride = checked.length;
        if (traktClientId.value !== '') throw new Error('The override should be blank when unset.');
        enter(traktClientId, '  ' + clientId + '  ');
        save.click();
        await until(() => saveCount === 5, 'Save did not accept the Trakt Client ID override.');
        if (saved.TraktClientId !== clientId || saved.SourceUrls.length !== 1 || saved.SourceUrls[0] !== delayed) {
            throw new Error('Save should trim the override and store it separately from source list URLs.');
        }
        enter(traktClientId, 'unsaved');
        document.querySelector('#magicCollectionsPage').dispatchEvent(new Event('pageshow'));
        await until(() => traktClientId.value === clientId, 'Reloading did not restore the saved Client ID.');
        enter(traktClientId, '  ');
        save.click();
        await until(() => saveCount === 6 && saved.TraktClientId === '', 'Saving a blank override should restore the default.');
        await wait(500);
        if (checked.length !== checksBeforeOverride) throw new Error('Editing the override should not trigger URL validation.');
        advancedToggle.click();
        traktToggle.click();

        enter(field(2), pastedTraktList);
        save.click();
        await until(() => saveCount === 7 && field(2)?.value === cleanTraktList,
            'Save should display the canonical Trakt URL returned by Jellyfin, without tokens.');
        if (field().value !== delayed || invalid(2)) throw new Error('Save should keep the other URL and render the canonical Trakt URL normally.');
        const connect = traktPanel.querySelector('button[id="magicTraktConnect"]');
        const disconnect = traktPanel.querySelector('button[id="magicTraktDisconnect"]');
        const status = traktPanel.querySelector('#magicTraktStatus');
        if (!connect || !disconnect || !status) throw new Error('The Trakt panel is missing its connection controls.');
        if (!connect.checkVisibility() || disconnect.checkVisibility()) throw new Error('Only Connect Trakt should appear before connecting.');
        await until(() => !connect.disabled, 'Connect Trakt stayed disabled.');
        connect.click();
        const openTrakt = traktPanel.querySelector('#magicTraktOpen');
        await until(() => openTrakt?.checkVisibility() && openTrakt.href.startsWith('https://auth.trakt.tv/'),
            'Connect did not display the Trakt authorization link with a blank Client ID.');
        const response = traktPanel.querySelector('#magicTraktResponse');
        const finish = traktPanel.querySelector('#magicTraktFinish');
        enter(response, '{"code":"rejected-code","state":"test-state"}');
        finish.click();
        await until(() => status.textContent === 'Trakt rejected the one-time code. Start a new connection.',
            'Finish should show the Jellyfin error instead of a generic message.');
        if (response.value || openTrakt.checkVisibility()) throw new Error('Finish should clear a rejected one-time response.');
        connect.click();
        await until(() => openTrakt.checkVisibility(), 'Connect should let the user start a new sign-in after an error.');
        enter(response, '{"code":"one-time-code","state":"test-state"}');
        finish.click();
        await until(() => status.textContent.includes('Example Owner'), 'Connected account name was not shown.');
        if (connect.checkVisibility() || !disconnect.checkVisibility()) {
            throw new Error('Only Disconnect Trakt should appear while connected.');
        }
        if (submittedResponse?.Code !== 'one-time-code' || submittedResponse.State !== 'test-state' ||
                response.value || openTrakt.checkVisibility()) {
            throw new Error('The one-time response was not submitted and cleared.');
        }
        if (form.elements.PrivateTraktLists) throw new Error('Private Trakt lists should not have a second URL field.');
        const personalList = 'https://app.trakt.tv/users/example/lists/private-films';
        enter(field(2), personalList);
        save.click();
        await until(() => saveCount === 8 && saved.SourceUrls.includes(personalList) && !disconnect.disabled,
            'The private list was not saved from the ordinary URL field.');
        if (JSON.stringify(saved).includes('one-time-code')) throw new Error('The OAuth response leaked into plugin settings.');
        window.confirm = () => true;
        disconnect.click();
        await until(() => status.textContent.includes('Not connected'), 'Disconnect did not update the account status.');
        if (!connect.checkVisibility() || disconnect.checkVisibility() || !saved.SourceUrls.includes(personalList)) {
            throw new Error('Disconnect should restore Connect Trakt and keep the saved list URL.');
        }
        expired = true;
        document.querySelector('#magicCollectionsPage').dispatchEvent(new Event('pageshow'));
        await until(() => status.textContent.includes('expired'), 'The expired connection status did not load.');
        if (!connect.checkVisibility() || disconnect.checkVisibility() || connect.textContent.trim() !== 'Connect Trakt') {
            throw new Error('An expired connection should show only Connect Trakt, even with a stored account.');
        }

        const arrToggle = [...form.querySelectorAll('summary')].find(summary => summary.textContent.trim() === '*arr');
        if (!arrToggle || arrToggle.parentElement.open) throw new Error('The *arr panel should start collapsed.');
        arrToggle.click();
        const arrPanel = arrToggle.parentElement;
        const bases = service => [...arrPanel.querySelectorAll('input[aria-label^="' + service + ' base URL"]')];
        const keys = service => [...arrPanel.querySelectorAll('input[aria-label^="' + service + ' API key"]')];
        const add = service => [...arrPanel.querySelectorAll('button')].find(button => button.textContent.trim() === 'Add ' + service + ' server');
        if (!bases('Radarr')[0]?.checkVisibility() || !bases('Sonarr')[0]?.checkVisibility()) {
            throw new Error('Opening *arr should show the Radarr and Sonarr sections.');
        }
        for (const service of ['Radarr', 'Sonarr']) {
            const base = bases(service)[0].getBoundingClientRect();
            const key = keys(service)[0].getBoundingClientRect();
            if (keys(service)[0].type !== 'password' || key.left < base.right || key.top !== base.top) {
                throw new Error('Each *arr row should have a masked API key beside its base URL.');
            }
        }
        enter(field(3), arrSource);
        await until(() => invalid(3), 'A Radarr list without a configured base should be marked unsupported.');
        enter(bases('Radarr')[0], '  ' + radarr + '  ');
        save.click();
        await wait(30);
        if (saveCount !== 8 || !arrPanel.querySelector('[role="alert"]')?.textContent) {
            throw new Error('Saving an incomplete pair should keep the draft and explain what is missing.');
        }
        enter(keys('Radarr')[0], '  radarr-draft-key  ');
        const badBase = 'https://radarr.example.com/radarr?apikey=query-draft-key';
        enter(bases('Radarr')[0], badBase);
        save.click();
        await until(() => invalid(3) && arrPanel.querySelector('[role="alert"]')?.textContent.includes('without credentials'),
            'A base URL with a query string should not be saved.');
        if (saveCount !== 8 || checkContainedKey) throw new Error('A credential in a base URL should not be sent.');
        enter(bases('Radarr')[0], '  ' + radarr + '  ');
        add('Radarr').click();
        enter(bases('Radarr')[1], anotherRadarr);
        enter(keys('Radarr')[1], 'second-radarr-draft-key');
        enter(bases('Sonarr')[0], sonarr);
        enter(keys('Sonarr')[0], 'sonarr-draft-key');
        await until(() => arrChecks.at(-1)?.RadarrBaseUrls?.includes(anotherRadarr) && !invalid(3),
            'A source URL should be rechecked against the edited base before saving.');
        const draftCheck = arrChecks.at(-1);
        if (checkContainedKey || Object.keys(draftCheck).sort().join() !== 'RadarrBaseUrls,SonarrBaseUrls,Url' ||
                draftCheck.RadarrBaseUrls.join() !== [radarr, anotherRadarr].join() || draftCheck.SonarrBaseUrls.join() !== sonarr) {
            throw new Error('A draft URL check should send only the source URL and base URLs, never API keys.');
        }
        save.click();
        await until(() => saveCount === 9 && field(3)?.value === arrSource && keys('Radarr')[0]?.value === 'radarr-draft-key',
            'Saving should persist and reload both the source URL and the Radarr settings.');
        if (JSON.stringify(saved.RadarrServers) !== JSON.stringify([
                { BaseUrl: radarr, ApiKey: 'radarr-draft-key' },
                { BaseUrl: anotherRadarr, ApiKey: 'second-radarr-draft-key' }
            ]) || JSON.stringify(saved.SonarrServers) !== JSON.stringify([{ BaseUrl: sonarr, ApiKey: 'sonarr-draft-key' }]) ||
                keys('Sonarr')[0]?.value !== 'sonarr-draft-key') {
            throw new Error('The configuration should keep pairs together and the services separate.');
        }
        arrPanel.querySelector('button[aria-label="Remove Radarr server 1"]').click();
        arrPanel.querySelector('button[aria-label="Remove Sonarr server 1"]').click();
        await until(() => invalid(3), 'Removing a base should recheck its source URLs.');
        save.click();
        await until(() => saveCount === 10 && bases('Radarr')[0]?.value === anotherRadarr,
            'Saving should remove only the selected pairs.');
        if (saved.RadarrServers.length !== 1 || saved.RadarrServers[0].ApiKey !== 'second-radarr-draft-key' ||
                saved.SonarrServers.length !== 0 || !saved.SourceUrls.includes(arrSource)) {
            throw new Error('Removing a row should not remove the other pair or its source URL.');
        }
        result.textContent = 'PASS';
    } catch (error) {
        result.textContent = error.message;
    }
}
