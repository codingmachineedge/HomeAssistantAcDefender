import { chromium } from 'playwright';
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1360, height: 900 } });
const errors = [];
page.on('console', m => { if (m.type() === 'error') errors.push(m.text().slice(0, 200)); });
await page.goto('http://127.0.0.1:8899/login', { waitUntil: 'domcontentloaded' });
await page.fill('input[name="username"]', 'owner');
await page.fill('input[name="password"]', 'defender123');
await page.click('button[type="submit"]');
await page.waitForTimeout(3000);
await page.goto('http://127.0.0.1:8899/energy', { waitUntil: 'domcontentloaded' });
await page.waitForTimeout(3500);
// open the Alectra Hui tab which has the desk-filter MudSelect
await page.getByText('ALECTRA HUI', { exact: false }).first().click();
await page.waitForTimeout(1200);
const sel = page.locator('.mud-select').first();
console.log('select count:', await page.locator('.mud-select').count());
await sel.click();
await page.waitForTimeout(1000);
const m = await page.evaluate(() => {
  const pop = document.querySelector('.mud-popover-open');
  const anyPop = document.querySelectorAll('.mud-popover');
  const list = document.querySelector('.mud-popover-open .mud-list');
  const cs = pop ? getComputedStyle(pop) : null;
  return {
    popovers: anyPop.length,
    openPop: !!pop,
    listItems: list ? list.children.length : 0,
    popStyle: cs ? { display: cs.display, zIndex: cs.zIndex, visibility: cs.visibility, height: pop.getBoundingClientRect().height, width: pop.getBoundingClientRect().width, top: pop.getBoundingClientRect().top } : null,
  };
});
console.log(JSON.stringify(m));
console.log('console errors:', JSON.stringify(errors.slice(0,5)));
await page.screenshot({ path: 'shots/dropdown.png' });
await browser.close();
