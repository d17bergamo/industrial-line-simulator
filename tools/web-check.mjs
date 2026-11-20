// Prova della versione web con un browser da telefono: carica la pagina, lancia una simulazione, salva le schermate.
// Uso: node web-check.mjs <url> <cartella di uscita>
import { chromium } from 'playwright';
import fs from 'node:fs';

const [url, out] = process.argv.slice(2);
fs.mkdirSync(out, { recursive: true });
const log = [];
const say = m => { log.push(m); console.log(m); };
const browser = await chromium.launch({ args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] });
const ctx = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true });
const page = await ctx.newPage();
page.on('console', m => { if (m.type() === 'error' || m.type() === 'warning') say(`[console ${m.type()}] ${m.text()}`); });
page.on('pageerror', e => say(`[errore pagina] ${e.message}`));
page.on('requestfailed', r => say(`[richiesta fallita] ${r.url()} ${r.failure()?.errorText}`));
let ok = false;
try {
  let t0 = Date.now();
  await page.goto(url);
  await page.waitForSelector('#pronto', { timeout: 120000 });
  say(`avvio: ${((Date.now() - t0) / 1000).toFixed(1)} s`);
  await page.screenshot({ path: `${out}/web-1-avvio.png` });
  for (const [name, setup] of [['base', async () => {}]]) {
    await setup();
    t0 = Date.now();
    await page.click('#simula');
    await page.waitForSelector('#risultati, #errori', { timeout: 600000 });
    say(`simulazione ${name}: ${((Date.now() - t0) / 1000).toFixed(1)} s`);
    say(`risultato ${name}: ${(await page.locator('#risultati, #errori').first().innerText()).replace(/\s+/g, ' ').slice(0, 600)}`);
    await page.screenshot({ path: `${out}/web-2-${name}-risultato.png`, fullPage: true });
    if (await page.locator('#play').count()) {
      await page.click('#play');
      await page.waitForTimeout(9000);
      await page.locator('svg.vista').scrollIntoViewIfNeeded();
      await page.screenshot({ path: `${out}/web-3-${name}-animazione.png` });
      await page.click('#play');
    }
  }
  // vista 3D: accensione, riproduzione, viste predefinite
  await page.click('#vista3d');
  await page.waitForTimeout(2500);
  say(`3D: ${await page.locator('.vista3d canvas').count() ? 'canvas presente' : 'canvas assente'}; avviso: ${await page.locator('.vista3d + p.esito').count()}`);
  await page.click('#play');
  await page.waitForTimeout(22000);
  await page.click('#play');
  await page.locator('.vista3d').scrollIntoViewIfNeeded();
  await page.screenshot({ path: `${out}/web-4-3d-insieme.png` });
  for (const v of ['Legatura', 'Trasferitore', 'Alto']) {
    await page.click(`.viste button:has-text("${v}")`);
    await page.waitForTimeout(700);
    await page.screenshot({ path: `${out}/web-4-3d-${v.toLowerCase()}.png` });
  }
  // tema scuro
  await page.click('#pronto');   // tema scuro scelto con il pulsante
  say(`tema: ${await page.locator('#pronto').innerText()}`);
  await page.click('#simula');
  await page.waitForSelector('#risultati', { timeout: 600000 });
  await page.waitForTimeout(2500);
  await page.screenshot({ path: `${out}/web-5-scuro-3d.png`, fullPage: true });
  await page.click('#vista2d');
  await page.click('#play');
  await page.waitForTimeout(15000);
  await page.click('#play');
  await page.screenshot({ path: `${out}/web-5-scuro-2d.png`, fullPage: true });
  // modifica del progetto: layout personalizzato, pacchi, vie e componenti, poi simulazione
  await page.selectOption('#esempio', '-2');
  await page.fill('.griglia input[type=text]', '6, 8, 12');
  await page.click('button:has-text("Crea il layout")');
  await page.waitForTimeout(800);
  say(`layout personalizzato: ${(await page.locator('#pacchi').innerText()).replace(/\s+/g, ' ').slice(0, 300)}`);
  await page.screenshot({ path: `${out}/web-6-pacchi.png`, fullPage: true });
  await page.locator('#pacchi details.pacco-scheda > summary').first().click();
  const len = page.locator('#pacchi details.pacco-scheda input[type=number]').first();
  await len.fill('7000');
  await len.dispatchEvent('change');
  await page.waitForTimeout(500);
  await page.click('#linea .schede button:has-text("Vie a rulli")');
  await page.screenshot({ path: `${out}/web-6-vie.png`, fullPage: true });
  await page.click('#linea .schede button:has-text("Componenti")');
  await page.screenshot({ path: `${out}/web-6-componenti.png`, fullPage: true });
  await page.click('#simula');
  await page.waitForSelector('#risultati, #errori', { timeout: 600000 });
  say(`risultato layout personalizzato: ${(await page.locator('#risultati, #errori').first().innerText()).replace(/\s+/g, ' ').slice(0, 400)}`);
  await page.screenshot({ path: `${out}/web-6-risultato.png`, fullPage: true });
  // configurazione completa: tutte le proprietà di un componente
  await page.click('#linea .schede button:has-text("Tutte le proprietà")');
  const comp = page.locator('#linea select:not(#esempio)').first();
  const opts = await comp.locator('option').allInnerTexts();
  say(`componenti configurabili: ${opts.length} (${opts.slice(0, 6).join(' | ')} …)`);
  await comp.selectOption({ index: Math.min(3, opts.length - 1) });
  await page.waitForTimeout(300);
  say(`proprietà mostrate: ${await page.locator('#linea .prop').count()}`);
  await page.screenshot({ path: `${out}/web-7-proprieta.png`, fullPage: true });
  // pacchi: vista 3D del pacco e double banking nascosto se non ci sta
  await page.locator('#pacchi').scrollIntoViewIfNeeded();
  await page.waitForTimeout(800);
  say(`vista 3D del pacco: ${await page.locator('#cpack').count() === 1 ? 'presente' : 'ASSENTE'}`);
  say(`dati area: ${await page.locator('#linea h4.sotto').count()} sezioni, famiglie ${await page.locator('#linea details.famiglia').count()}, righello ${await page.locator('#rpack svg').count()}, schede pacco ${await page.locator('details.pacco-scheda').count()}`);
  {
    const g = page.locator('.vista-config .trascina[data-k=strapper]').first();
    await g.scrollIntoViewIfNeeded();
    const before = await page.locator('svg.vista-config').innerHTML();
    const b = await g.boundingBox();
    if (b) {
      await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2);
      await page.mouse.down();
      await page.mouse.move(b.x + b.width / 2 + 60, b.y + b.height / 2, { steps: 6 });
      await page.mouse.up();
      await page.waitForTimeout(800);
    }
    const after = await page.locator('svg.vista-config').innerHTML();
    say(`trascinamento legatrice nella vista 2D: ${before !== after ? 'ok (vista cambiata)' : 'NESSUN CAMBIAMENTO'}`);
  }
  say(`vista di configurazione: ${await page.locator('svg.vista-config').count()}`);
  const dbl = await page.locator('#pacchi .riga-mod select option[value="DoubleBanking"]').count();
  const sing = await page.locator('#pacchi .riga-mod select option[value="SingleBanking"]').count();
  say(`opzioni banking: single ${sing}, double ${dbl}`);
  await page.locator('#cpack').scrollIntoViewIfNeeded();
  await page.screenshot({ path: `${out}/web-6-pacchi3d.png`, fullPage: false });
  // zone comprimibili: Comprimi tutto / Espandi tutto
  await page.locator('button:has-text("Comprimi tutto"):visible').first().click();
  say(`zone aperte dopo "Comprimi tutto": ${await page.locator('details.zona[open]').count()}`);
  await page.screenshot({ path: `${out}/web-7-compresse.png`, fullPage: true });
  await page.locator('button:has-text("Espandi tutto"):visible').first().click();
  say(`zone aperte dopo "Espandi tutto": ${await page.locator('details.zona[open]').count()}`);
  // documenti: PDF, progetto, Gantt, Excel e tutto in un file ZIP vengono scaricati
  await page.click('header.testa button:has-text("Documenti")');
  await page.waitForTimeout(600);
  for (const label of ['Vista dall\'alto', 'Salva il progetto', 'Gantt dei pacchi e gradini', 'Risultati della simulazione', 'Tutto (.zip)']) {
    const btn = page.locator(`#uscite button:has-text("${label}")`);
    if (await btn.count() === 0 || await btn.first().isDisabled()) { say(`uscita non disponibile: ${label}`); continue; }
    const [dl] = await Promise.all([page.waitForEvent('download', { timeout: 120000 }), btn.first().click()]);
    say(`scaricato: ${dl.suggestedFilename()}`);
  }
  await page.screenshot({ path: `${out}/web-8-uscite.png`, fullPage: true });
  ok = true;
} catch (e) {
  say(`[prova fallita] ${e.message}`);
  try { await page.screenshot({ path: `${out}/web-errore.png`, fullPage: true }); } catch {}
} finally {
  fs.writeFileSync(`${out}/web-prova.txt`, log.join('\n') + '\n');
  await browser.close();
}
process.exit(ok ? 0 : 1);
