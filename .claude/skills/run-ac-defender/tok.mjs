import { chromium } from 'playwright';
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1360, height: 900 } });
await page.goto('http://127.0.0.1:8899/login', { waitUntil: 'domcontentloaded' });
await page.fill('input[name="username"]', 'owner');
await page.fill('input[name="password"]', 'defender123');
await page.click('button[type="submit"]');
await page.waitForTimeout(3000);
await page.goto('http://127.0.0.1:8899/settings', { waitUntil: 'domcontentloaded' });
await page.waitForTimeout(3000);
const warn = await page.getByText('No Home Assistant token found', { exact: false }).count();
console.log('warning shown:', warn > 0);
// save a token and confirm the state flips
await page.locator('input[type="password"]').first().fill('test-token-abc123');
await page.getByText('SAVE TOKEN', { exact: false }).first().click();
await page.waitForTimeout(1500);
const saved = await page.getByText('website-entered token is saved', { exact: false }).count();
console.log('saved state shown:', saved > 0);
// clear it again so local dev stays clean
await page.getByText('CLEAR', { exact: true }).first().click();
await page.waitForTimeout(1000);
const cleared = await page.getByText('No Home Assistant token found', { exact: false }).count();
console.log('cleared back to warning:', cleared > 0);
await page.screenshot({ path: 'shots/ha-token.png' });
await browser.close();
