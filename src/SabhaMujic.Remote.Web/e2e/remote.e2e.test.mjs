// Browser e2e for the SabhaMujic web remote: Chromium with iPhone 13 emulation (WebKit is not available here; real
// iPhone Safari is a manual checklist item) against tests/SabhaMujic.RemoteE2EHost.
// The host's HTTPS certificate comes from its own local CA, which Chromium does not trust: the contexts use
// ignoreHTTPSErrors. On a real iPhone the CA is trusted via the bootstrap profile (manual checklist).
import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { chromium, devices } from 'playwright';
import { E2EHost } from './host.mjs';

const host = new E2EHost();
/** @type {import('playwright').Browser} */
let browser;
/** @type {import('playwright').BrowserContext} */
let adminContext;
/** @type {import('playwright').BrowserContext} */
let volunteerContext;
/** @type {import('playwright').Page} */
let admin;
/** @type {import('playwright').Page} */
let volunteer;
/** @type {string} */
let origin;
/** @type {string[]} */
const cspViolations = [];
/** @type {string[]} */
const pageErrors = [];

const TIMEOUT = 15000;

/** @param {string} label */
async function newPhone(label) {
  const context = await browser.newContext({ ...devices['iPhone 13'], ignoreHTTPSErrors: true });
  await context.addInitScript(() => {
    document.addEventListener('securitypolicyviolation', (e) => {
      console.error(`CSP violation: ${e.violatedDirective} blocked ${e.blockedURI}`);
    });
  });
  const page = await context.newPage();
  page.setDefaultTimeout(TIMEOUT);
  page.on('console', (message) => {
    const text = message.text();
    if (/Content Security Policy|CSP violation|Refused to (load|execute|apply|connect)/i.test(text)) cspViolations.push(`${label}: ${text}`);
  });
  page.on('pageerror', (error) => pageErrors.push(`${label}: ${error.message}`));
  return { context, page };
}

/**
 * @param {import('playwright').Page} page
 * @param {'now' | 'queue' | 'library' | 'playlists' | 'admin'} tab
 */
async function openTab(page, tab) {
  await page.locator(`#tabbar button[data-tab="${tab}"]`).click();
  await page.locator(`#view-${tab}`).waitFor({ state: 'visible' });
}

/** @param {import('playwright').Page} page */
async function expectConnected(page) {
  await page.locator('#conn[data-status="connected"]').waitFor({ timeout: 20000 });
}

/** @param {import('playwright').Page} page @param {string} status */
async function expectStatus(page, status) {
  await page.locator('#np-status', { hasText: status }).waitFor();
}

/** @param {import('playwright').Page} page @param {string} title */
async function expectTitle(page, title) {
  await page.locator('#np-title', { hasText: title }).waitFor();
}

/** @param {import('playwright').Page} page @param {string} title @param {string} action */
async function libraryAction(page, title, action) {
  await openTab(page, 'library');
  const row = page.locator('#lib-list li', { hasText: title });
  await row.getByRole('button', { name: action, exact: true }).click();
}

/** A PCM WAV (stereo, 16-bit, 44.1 kHz) of `seconds` with a 440 Hz tone. */
function wav(seconds) {
  const rate = 44100;
  const frames = Math.floor(rate * seconds);
  const data = frames * 4;
  const buffer = Buffer.alloc(44 + data);
  buffer.write('RIFF', 0, 'ascii');
  buffer.writeUInt32LE(36 + data, 4);
  buffer.write('WAVE', 8, 'ascii');
  buffer.write('fmt ', 12, 'ascii');
  buffer.writeUInt32LE(16, 16);
  buffer.writeUInt16LE(1, 20);
  buffer.writeUInt16LE(2, 22);
  buffer.writeUInt32LE(rate, 24);
  buffer.writeUInt32LE(rate * 4, 28);
  buffer.writeUInt16LE(4, 32);
  buffer.writeUInt16LE(16, 34);
  buffer.write('data', 36, 'ascii');
  buffer.writeUInt32LE(data, 40);
  for (let i = 0; i < frames; i++) {
    const s = Math.round(Math.sin((2 * Math.PI * 440 * i) / rate) * 8000);
    buffer.writeInt16LE(s, 44 + i * 4);
    buffer.writeInt16LE(s, 46 + i * 4);
  }
  return buffer;
}

before(async () => {
  const ready = await host.start();
  origin = `https://127.0.0.1:${ready.httpsPort}`;
  browser = await chromium.launch();
  ({ context: adminContext, page: admin } = await newPhone('admin'));
  ({ context: volunteerContext, page: volunteer } = await newPhone('volunteer'));
});

after(async () => {
  await browser?.close();
  await host.stop();
});

test('admin pairs through the bootstrap page and the #code fragment', { timeout: 60000 }, async () => {
  const { code, pairingUrl } = await host.pairingCode('Admin');
  assert.match(pairingUrl, new RegExp(`^http://127\\.0\\.0\\.1:${host.ready.bootstrapPort}/#code=${code}$`));

  await admin.goto(pairingUrl);
  await admin.locator('#fingerprint', { hasText: host.ready.fingerprint }).waitFor();
  assert.equal(await admin.locator('#code').textContent(), code);
  const href = await admin.locator('#continue').getAttribute('href');
  assert.equal(href, `${origin}/pair#code=${code}`);

  await admin.locator('#continue').click();
  await admin.waitForURL(`${origin}/pair*`);
  await admin.locator('#view-pair').waitFor({ state: 'visible' });
  assert.equal(await admin.locator('#pair-code').inputValue(), code);
  await admin.locator('#pair-name').fill('Admin iPhone');
  await admin.locator('#pair-submit').click();

  await admin.locator('#view-now').waitFor({ state: 'visible' });
  await expectConnected(admin);
  assert.equal(new URL(admin.url()).pathname, '/');
  assert.equal(await admin.locator('#tab-admin').isVisible(), true);

  // The device token is an HttpOnly, Secure, SameSite=Strict cookie and never visible to script.
  const cookies = await adminContext.cookies(origin);
  const cookie = cookies.find((c) => c.name === '__Host-SabhaMujic');
  assert.ok(cookie, 'device cookie set');
  assert.equal(cookie.httpOnly, true);
  assert.equal(cookie.secure, true);
  assert.equal(cookie.sameSite, 'Strict');
  assert.equal(await admin.evaluate(() => document.cookie), '');

  // The code is single-use.
  await volunteer.goto(`${origin}/pair#code=${code}`);
  await volunteer.locator('#pair-name').fill('Reuse attempt');
  await volunteer.locator('#pair-submit').click();
  await volunteer.locator('#pair-error', { hasText: 'invalid or has expired' }).waitFor();
});

test('volunteer pairs and gets no Admin tab', { timeout: 60000 }, async () => {
  const { code } = await host.pairingCode('Volunteer');
  await volunteer.goto(`${origin}/pair#code=${code}`);
  await volunteer.locator('#pair-name').fill('Sevak iPhone');
  await volunteer.locator('#pair-submit').click();
  await volunteer.locator('#view-now').waitFor({ state: 'visible' });
  await expectConnected(volunteer);
  assert.equal(await volunteer.locator('#tab-admin').isVisible(), false);
  assert.equal(await volunteer.locator('#tabbar button[data-tab]:visible').count(), 4);
});

test('play, pause and next reflect live on both phones', { timeout: 60000 }, async () => {
  await libraryAction(admin, 'Morning Aarti', 'Play now');
  await libraryAction(admin, 'Shanti Path', 'Add to queue');
  await openTab(admin, 'now');

  for (const page of [admin, volunteer]) {
    await expectTitle(page, 'Morning Aarti');
    await expectStatus(page, 'Playing');
  }
  // Position is interpolated between pushes.
  await admin.waitForFunction(() => document.getElementById('np-pos')?.textContent !== '0:00');

  await admin.locator('#np-play').click(); // pause
  for (const page of [admin, volunteer]) await expectStatus(page, 'Paused');

  await volunteer.locator('#np-play').click(); // resume from the other phone
  for (const page of [admin, volunteer]) await expectStatus(page, 'Playing');

  await admin.locator('#np-next').click();
  for (const page of [admin, volunteer]) await expectTitle(page, 'Shanti Path');

  await openTab(volunteer, 'queue');
  await volunteer.locator('#queue-list li[aria-current="true"]', { hasText: 'Shanti Path' }).waitFor();
  assert.equal(await volunteer.locator('#queue-list li').count(), 2);

  // Settings round-trip through the hub and show on the other phone.
  await admin.locator('[data-loop="SmartLoop"]').click();
  await openTab(volunteer, 'now');
  await volunteer.locator('[data-loop="SmartLoop"][aria-pressed="true"]').waitFor();
});

test('admin uploads a WAV with visible progress; duplicates are reported', { timeout: 90000 }, async () => {
  await openTab(admin, 'admin');
  const file = { name: 'test-tone.wav', mimeType: 'audio/wav', buffer: wav(8) }; // ~1.4 MB → 3 chunks
  await admin.locator('#up-files').setInputFiles(file);
  const item = admin.locator('#up-list li').last();
  await item.getByLabel('Title').fill('E2E Tone');
  await item.getByLabel('Category').selectOption({ label: 'Dhun' });

  await admin.evaluate(() => {
    const seen = /** @type {number[]} */ ([]);
    /** @type {any} */ (window).__progress = seen;
    const progress = document.querySelector('#up-list li:last-child progress');
    if (!progress) throw new Error('no progress bar');
    new MutationObserver(() => seen.push(Number(progress.getAttribute('value')))).observe(progress, { attributes: true, attributeFilter: ['value'] });
  });
  await admin.locator('#up-start').click();
  await item.locator('.up-status', { hasText: 'Added to the library.' }).waitFor({ timeout: 30000 });

  const values = /** @type {number[]} */ (await admin.evaluate(() => /** @type {any} */ (window).__progress));
  const max = file.buffer.length;
  assert.ok(values.some((v) => v > 0 && v < max), `intermediate progress seen: ${values.join(', ')}`);
  assert.equal(values.at(-1), max);

  // The new track is in the library for everyone (LibraryChanged push).
  await openTab(volunteer, 'library');
  await volunteer.locator('#lib-search').fill('E2E Tone');
  await volunteer.locator('#lib-list li', { hasText: 'E2E Tone' }).waitFor();

  // Same audio again → reported as a duplicate, not added twice.
  await admin.locator('#up-files').setInputFiles(file);
  await admin.locator('#up-start').click();
  await admin.locator('#up-list li').last().locator('.up-status', { hasText: 'Already in the library' }).waitFor({ timeout: 30000 });
});

test('volunteer upload attempt is refused by the server', { timeout: 30000 }, async () => {
  const result = await volunteer.evaluate(async () => {
    const response = await fetch('/api/uploads', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-SabhaMujic': '1' },
      body: JSON.stringify({ fileName: 'x.wav', totalBytes: 10 }),
    });
    return { status: response.status, body: await response.json() };
  });
  assert.equal(result.status, 403);
  assert.equal(result.body.code, 'Forbidden');
});

test('connection pill shows reconnecting while the hub restarts, then recovers', { timeout: 90000 }, async () => {
  await openTab(admin, 'now');
  await openTab(volunteer, 'now');
  host.send('restart');
  for (const page of [admin, volunteer]) {
    await page.locator('#conn[data-status="reconnecting"]').waitFor({ timeout: 20000 });
  }
  await host.next('restarted', 30000);
  for (const page of [admin, volunteer]) await expectConnected(page);

  // Still in control after reconnecting, and state is re-fetched.
  await admin.locator('#np-play').click();
  for (const page of [admin, volunteer]) await expectStatus(page, 'Paused');
});

test('no CSP violations or script errors on any page', { timeout: 30000 }, async () => {
  const seen = [...cspViolations];
  const errors = [...pageErrors];

  // Positive control: the detector does catch a violation (an injected inline script is blocked by the policy).
  const { context, page } = await newPhone('probe');
  await page.goto(`${origin}/`);
  await page.evaluate(() => {
    const script = document.createElement('script');
    script.textContent = 'window.__inlineRan = true;';
    document.body.append(script);
  });
  await new Promise((resolve) => setTimeout(resolve, 500));
  assert.equal(await page.evaluate(() => /** @type {any} */ (window).__inlineRan === true), false, 'inline script must be blocked');
  assert.ok(cspViolations.some((v) => v.startsWith('probe:')), 'the CSP violation detector works');
  await context.close();

  assert.deepEqual(seen, []);
  assert.deepEqual(errors, []);
});
