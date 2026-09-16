import { chromium } from '../frontend/node_modules/@playwright/test/index.mjs';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import assert from 'node:assert/strict';

const root = process.cwd();
const config = await mkdtemp(path.join(os.tmpdir(), 'grimarr-ui-'));
await mkdir('test-results', { recursive: true });
const server = spawn(process.env.DOTNET_EXE || 'dotnet', [
  path.join(root, 'src/Grimarr/bin/Debug/net10.0/Grimarr.dll'),
  '--contentRoot', path.join(root, 'src/Grimarr'),
], {
  env: { ...process.env, ASPNETCORE_URLS: 'http://127.0.0.1:8787', GRIMARR_CONFIG: config, GRIMARR_PASSWORD: 'local-preview-password' },
  stdio: 'pipe', windowsHide: true,
});
let logs = '';
server.stdout.on('data', d => logs += d);
server.stderr.on('data', d => logs += d);
let browser, page;
try {
  let ready = false;
  for (let i = 0; i < 50; i++) {
    if (server.exitCode !== null) throw Error('Server exited: ' + logs);
    if (await fetch('http://127.0.0.1:8787/health', { signal: AbortSignal.timeout(1000) }).then(r => r.ok).catch(() => false)) { ready = true; break; }
    await new Promise(r => setTimeout(r, 200));
  }
  assert(ready, 'Server did not start');
  browser = await chromium.launch({ headless: true });
  page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.goto('http://127.0.0.1:8787');
  await page.getByLabel('Password', { exact: true }).fill('local-preview-password');
  await page.getByRole('button', { name: 'Open my library' }).click();
  await page.getByRole('heading', { name: 'Audiobooks', exact: true }).waitFor();
  await page.screenshot({ path: 'test-results/library-desktop.png', fullPage: true });
  await page.getByRole('navigation').getByRole('button', { name: 'Settings', exact: true }).click();
  await page.getByRole('heading', { name: 'Settings', exact: true }).waitFor();
  await page.screenshot({ path: 'test-results/settings-desktop.png', fullPage: true });
  await page.getByRole('button', { name: 'Library', exact: true }).click();
  await page.getByRole('button', { name: 'Add audiobook', exact: true }).first().click();
  await page.getByRole('button', { name: 'Add by title and author' }).click();
  await page.getByLabel('Book title', { exact: true }).fill('The Hobbit');
  await page.getByLabel('Author', { exact: true }).fill('J R R Tolkien');
  await page.getByRole('button', { name: 'Add & download' }).click();
  await page.getByRole('heading', { name: 'The Hobbit', exact: true }).waitFor();
  await page.getByRole('button', { name: /The Hobbit.*Monitored/ }).click();
  await page.getByRole('dialog').waitFor();
  await page.getByLabel('Narration', { exact: true }).selectOption('dramatized');
  await page.getByRole('button', { name: 'Save preferences' }).click();
  await page.getByRole('button', { name: 'Save preferences' }).waitFor({ state: 'visible' });
  await page.waitForFunction(async () => (await (await fetch('/api/books')).json())[0].preferences.narration === 'dramatized');
  await page.getByRole('button', { name: 'Close dialog' }).click();
  await page.screenshot({ path: 'test-results/library-desktop.png', fullPage: true });
  await page.getByRole('button', { name: 'List view', exact: true }).click();
  await page.locator('.book-list').waitFor();
  await page.getByText('Dramatized / full cast', { exact: true }).waitFor();
  await page.screenshot({ path: 'test-results/library-list.png', fullPage: true });
  await page.getByLabel('Sort audiobooks').selectOption('author');
  await page.getByLabel('Filter audiobooks').selectOption('available');
  await page.getByRole('heading', { name: 'No matching audiobooks' }).waitFor();
  await page.getByLabel('Filter audiobooks').selectOption('all');
  await page.getByRole('button', { name: 'Poster view', exact: true }).click();
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({ path: 'test-results/library-mobile.png', fullPage: true });
  assert(!(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)), 'Mobile layout overflows horizontally');
  await page.getByRole('button', { name: 'Activity', exact: true }).click();
  await page.getByText('Added and monitored for automatic download').waitFor();
  assert.deepEqual(errors, [], 'Browser runtime errors');
  console.log('PASS: Browser login, navigation, settings, add book, saved edition preference, poster/list switching, sort/filter controls, activity, and mobile layout. No runtime errors.');
} catch (error) {
  if (page) await page.screenshot({ path: 'test-results/ui-failure.png', fullPage: true });
  throw error;
} finally {
  await browser?.close();
  if (server.exitCode === null) { server.kill(); await once(server, 'exit'); }
  await rm(config, { recursive: true, force: true });
}
