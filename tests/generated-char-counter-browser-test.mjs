import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');

const projectRoot = process.argv[2];
if (!projectRoot) throw new Error('generated project path is required');

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

const browser = await chromium.launch({
  headless: true,
  executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe'
});
try {
  const page = await browser.newPage();
  const pageErrors = [];
  page.on('pageerror', error => pageErrors.push(error.message));
  await page.goto(pathToFileURL(path.join(projectRoot, 'index.html')).href);

  await page.locator('#text-input').fill('abc\nあ');
  const readCounts = async () => {
    if (await page.locator('#stats').count()) return await page.locator('#stats').textContent();
    return `文字数: ${await page.locator('#word-count').textContent()}, 行数: ${await page.locator('#line-count').textContent()}`;
  };
  let counts = await readCounts();
  assert(counts.includes('文字数: 5'), '文字数が5になりません');
  assert(counts.includes('行数: 2'), '行数が2になりません');

  await page.reload();
  assert(await page.locator('#text-input').inputValue() === 'abc\nあ', '入力時の自動保存または再読込に失敗しました');
  counts = await readCounts();
  assert(counts.includes('文字数: 5') && counts.includes('行数: 2'), '再読込後の集計復元に失敗しました');

  await page.locator('#clear-button').click();
  assert(await page.locator('#text-input').inputValue() === '', '消去操作に失敗しました');
  await page.reload();
  assert(await page.locator('#text-input').inputValue() === '', '消去後の保存状態に失敗しました');
  assert(pageErrors.length === 0, `ブラウザ実行エラー: ${pageErrors.join(' / ')}`);
  console.log('PASS: realtime character/line count, automatic persistence, reload, and clear');
} finally {
  await browser.close();
}
