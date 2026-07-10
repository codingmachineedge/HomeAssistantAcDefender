// AC Defender driver — drives the running Blazor Server app with Playwright.
//
// The app is auth-gated: the FIRST account created is the owner (no code needed),
// so the driver creates that owner account if none exists yet, otherwise it signs
// in with the same credentials. Then it screenshots whichever pages you ask for.
//
// Prereqs: the app must already be running (see SKILL.md), and `npm install` must
// have been run in this directory so `playwright` resolves. Chromium ships with
// Playwright (no system Chrome needed).
//
// Usage (from this skill directory):
//   node driver.mjs shots            # sign in + screenshot every page  (default)
//   node driver.mjs shot /energy     # sign in + screenshot one route
//   node driver.mjs probe            # sign in + print the page list, no shots
//
// Env knobs (all optional):
//   BASE_URL   default http://127.0.0.1:8899
//   AC_USER    default owner
//   AC_PASS    default defender123   (>= 6 chars; the owner form requires it)
//   SHOT_DIR   default ./shots
//   VIEWPORT   "desktop" (1440x900, default) or "mobile" (390x844)
//   HEADED     set to "1" to watch a real window (default headless)

import { chromium } from 'playwright';
import { mkdir } from 'node:fs/promises';
import { dirname, join } from 'node:path';

const BASE_URL = (process.env.BASE_URL || 'http://127.0.0.1:8899').replace(/\/$/, '');
const USER = process.env.AC_USER || 'owner';
const PASS = process.env.AC_PASS || 'defender123';
const SHOT_DIR = process.env.SHOT_DIR || './shots';
const HEADED = process.env.HEADED === '1';
const VIEWPORT = process.env.VIEWPORT === 'mobile'
  ? { width: 390, height: 844 }
  : { width: 1440, height: 900 };

// Authenticated routes. Dashboard is "/"; the rest are the nav pages.
const PAGES = [
  ['/', 'dashboard'],
  ['/defense', 'defense'],
  ['/energy', 'energy'],
  ['/comfort', 'comfort'],
  ['/controls', 'controls'],
  ['/logs', 'logs'],
  ['/settings', 'settings'],
  ['/guide', 'guide'],
  ['/api-docs', 'api-docs'],
];

const slug = (r) => (r === '/' ? 'dashboard' : r.replace(/[^a-z0-9]+/gi, '-').replace(/^-|-$/g, ''));

// Blazor Server keeps a SignalR/SSE connection open, so "networkidle" never
// fires. Wait for the DOM + a short settle instead.
async function settle(page) {
  await page.waitForLoadState('domcontentloaded').catch(() => {});
  await page.waitForTimeout(1200);
}

async function signIn(page) {
  await page.goto(`${BASE_URL}/login`, { waitUntil: 'domcontentloaded' });
  await settle(page);

  const body = await page.textContent('body');
  const isOwnerSignup = /Create the owner account/i.test(body || '');

  await page.fill('input[name="username"]', USER);
  await page.fill('input[name="password"]', PASS);
  if (isOwnerSignup) {
    await page.fill('input[name="confirmPassword"]', PASS);
    console.log(`[auth] no account yet -> creating owner "${USER}"`);
  } else {
    console.log(`[auth] account exists -> signing in as "${USER}"`);
  }

  await Promise.all([
    page.waitForNavigation({ waitUntil: 'domcontentloaded' }).catch(() => {}),
    page.click('button[type="submit"]'),
  ]);
  await settle(page);

  if (/\/login/.test(page.url())) {
    const err = (await page.textContent('.login-error').catch(() => null))?.trim();
    throw new Error(`sign-in failed (still on /login): ${err || 'unknown error'}`);
  }
  console.log(`[auth] signed in, landed on ${page.url()}`);
}

// Accept "energy", "/energy", or even a Git-Bash-mangled "C:/.../energy"
// (MSYS rewrites a leading-slash argument into a Windows path). Reduce any of
// these to a clean "/route".
function normalizeRoute(route) {
  if (route === '/' || route === '') return '/';
  const tail = route.replace(/\\/g, '/').split('/').filter(Boolean).pop().toLowerCase();
  // "dashboard" is served at "/". The alias also lets Git Bash users screenshot
  // the dashboard without typing a bare "/" (which MSYS rewrites into a path).
  if (tail === 'dashboard') return '/';
  return '/' + tail;
}

async function shoot(page, route) {
  route = normalizeRoute(route);
  const url = `${BASE_URL}${route}`;
  await page.goto(url, { waitUntil: 'domcontentloaded' });
  await settle(page);
  const out = join(SHOT_DIR, `${slug(route)}.png`);
  await mkdir(dirname(out), { recursive: true });
  await page.screenshot({ path: out, fullPage: true });
  const title = await page.title();
  console.log(`[shot] ${route.padEnd(10)} -> ${out}   (${title})`);
  return out;
}

async function main() {
  const cmd = process.argv[2] || 'shots';
  const arg = process.argv[3];

  const browser = await chromium.launch({ headless: !HEADED });
  const context = await browser.newContext({ viewport: VIEWPORT });
  const page = await context.newPage();
  const consoleErrors = [];
  page.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(m.text()); });

  try {
    await signIn(page);

    if (cmd === 'probe') {
      console.log('[probe] authenticated; pages available:', PAGES.map(([r]) => r).join(' '));
    } else if (cmd === 'shot') {
      if (!arg) throw new Error('usage: node driver.mjs shot <route>');
      await shoot(page, arg);
    } else if (cmd === 'shots') {
      for (const [route] of PAGES) await shoot(page, route);
    } else {
      throw new Error(`unknown command "${cmd}" (use: shots | shot <route> | probe)`);
    }

    if (consoleErrors.length) {
      console.log(`\n[console] ${consoleErrors.length} browser console error(s) (first 5):`);
      consoleErrors.slice(0, 5).forEach((e) => console.log('  - ' + e.replace(/\s+/g, ' ').slice(0, 200)));
    } else {
      console.log('\n[console] no browser console errors');
    }
  } finally {
    await browser.close();
  }
}

main().catch((e) => { console.error('[driver] FAILED:', e.message); process.exit(1); });
