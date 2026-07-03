// Captures the wiki documentation screenshots into docs/wiki/images/.
// Usage: node wiki-shots.mjs  (server must be running on 8899; owner account must exist)
import { chromium } from 'playwright';
import { mkdirSync } from 'fs';

const OUT = '../../../docs/wiki/images';
mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 1050 } });

await page.goto('http://127.0.0.1:8899/login');
if (page.url().includes('/login')) {
  await page.fill('input[name=username]', 'owner');
  await page.fill('input[name=password]', 'defender123');
  await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }), page.click('button[type=submit]')]);
}

async function shot(route, file, extra) {
  await page.goto('http://127.0.0.1:8899' + route, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(2200);
  if (extra) {
    await extra();
  }
  await page.screenshot({ path: `${OUT}/${file}` });
  console.log(`[shot] ${route.padEnd(10)} -> ${file}`);
}

await shot('/', 'dashboard.png');
await shot('/defense', 'defense.png');
await shot('/comfort', 'comfort.png');
await shot('/energy', 'energy-overview.png');
await shot('/energy', 'energy-calendar.png', async () => {
  await page.click('.mud-tab:has-text("Calendar")');
  await page.waitForTimeout(1000);
  const hot = await page.$('.cal-cell.cal-heat-4');
  if (hot) {
    await hot.click();
    await page.waitForTimeout(500);
  }
});
await shot('/logs', 'logs.png');
await shot('/controls', 'controls.png');
await shot('/settings', 'settings.png');
await shot('/settings', 'settings-budget.png', async () => {
  const panel = page.locator('.mud-expand-panel', { hasText: 'Electricity budget' }).first();
  await panel.locator('.mud-expand-panel-header').click();
  await page.waitForTimeout(600);
  await panel.scrollIntoViewIfNeeded();
  await page.waitForTimeout(400);
});
await shot('/guide', 'guide.png');

await browser.close();
console.log('done');
