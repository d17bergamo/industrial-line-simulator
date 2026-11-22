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
const tab = async n => { await page.click('#tab-' + n); await page.waitForTimeout(400); };
try {
  let t0 = Date.now();
  await page.goto(url);
  await page.waitForSelector('#tab-progetto', { timeout: 120000 });
  say(`avvio: ${((Date.now() - t0) / 1000).toFixed(1)} s`);
  await page.screenshot({ path: `${out}/web-1-avvio.png` });
  for (const [name, setup] of [['base', async () => {}]]) {
    await setup();
    t0 = Date.now();
    say(`nome progetto: "${await page.locator('#nome-progetto').inputValue()}" (vuoto all'inizio); safety: ${await page.locator('#safety').inputValue()}; bozza 2D: ${await page.locator('.bozza svg.vista-config').count()}`);
    await tab('simulazione');
    say(`pacchi da simulare: ${await page.locator('.barra-sim .scelta.attiva').allInnerTexts()}`);
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
  await page.click('#simula');
  await page.waitForSelector('#risultati', { timeout: 600000 });
  await page.waitForTimeout(2500);
  await page.screenshot({ path: `${out}/web-5-3d.png`, fullPage: true });
  await page.click('#vista2d');
  await page.click('#play');
  await page.waitForTimeout(15000);
  await page.click('#play');
  await page.screenshot({ path: `${out}/web-5-2d.png`, fullPage: true });
  // ciclogramma, Gantt e comandi di riproduzione
  say(`Gantt: ${await page.locator('#ciclogramma svg.ciclogramma').count()} grafici, ${await page.locator('#ciclogramma svg.ciclogramma rect').count()} tratti; pacchi simulati: ${await page.locator('#pacchisim tbody tr').count()} righe; registro eventi: ${await page.locator('.blocco:has(h2:has-text("Registro eventi")) tbody tr').count()} righe`);
  await page.click('#inizio');
  await page.screenshot({ path: `${out}/web-5-ciclogramma.png`, fullPage: true });
  // recap per misura
  await page.click('button:has-text("Simula tutte le misure")');
  await page.waitForSelector('#recap tbody tr', { timeout: 600000 });
  say(`recap: ${await page.locator('#recap tbody tr').count()} righe; ${(await page.locator('#recap').innerText()).replace(/\s+/g, ' ').slice(0, 300)}`);
  await tab('linea');
  say(`gruppi selezionabili nel 2D: ${await page.locator('.vista-config .trascina').count()}`);
  await tab('pacchi');
  // modifica del progetto: layout personalizzato, pacchi, vie e componenti, poi simulazione
  await tab('progetto');
  await page.click('button.scelta:has-text("Layout da zero")');
  await page.fill('.griglia input[type=text]', '6, 8, 12');
  await page.click('button:has-text("Crea il layout")');
  await page.waitForTimeout(800);
  await tab('pacchi');
  say(`layout personalizzato: ${(await page.locator('#pacchi').innerText()).replace(/\s+/g, ' ').slice(0, 300)}`);
  await page.screenshot({ path: `${out}/web-6-pacchi.png`, fullPage: true });
  const len = page.locator('#pacchi table.configurazioni input.cella').first();
  await len.fill('7');
  await len.dispatchEvent('change');
  await page.waitForTimeout(500);
  // legature: + e quote tra le legature
  {
    const n = async () => Number((await page.locator('.legature-ctrl h3').innerText()).replace(/\D+/g, ''));
    const n0 = await n();
    await page.click('.legature-ctrl button.quadro.azzurro');
    await page.waitForTimeout(400);
    const n1 = await n();
    const q = page.locator('.quote-leg input').first();
    await q.fill('1500'); await q.dispatchEvent('change');
    await page.waitForTimeout(400);
    say(`legature: ${n0} → ${n1} con +; prima quota ${await page.locator('.quote-leg input').first().inputValue()} mm`);
    if (n1 !== n0 + 1) throw new Error('il + delle legature non funziona');
  }
  await tab('simulazione');
  await page.click('#simula');
  await page.waitForSelector('#risultati, #errori', { timeout: 600000 });
  say(`risultato layout personalizzato: ${(await page.locator('#risultati, #errori').first().innerText()).replace(/\s+/g, ' ').slice(0, 400)}`);
  await page.screenshot({ path: `${out}/web-6-risultato.png`, fullPage: true });
  // proprietà di un componente: finestra che si apre toccandolo nel disegno
  await tab('linea');
  {
    const g = page.locator('.vista-config .trascina[data-k=strapper]').first();
    await g.scrollIntoViewIfNeeded();
    const b = await g.boundingBox();
    await page.mouse.click(b.x + b.width / 2, b.y + b.height / 2);
    await page.waitForSelector('.modale', { timeout: 10000 });
    say(`finestra proprietà: ${(await page.locator('.modale-testa h2').innerText())}; proprietà mostrate: ${await page.locator('.modale .prop').count()}`);
    await page.screenshot({ path: `${out}/web-7-proprieta.png`, fullPage: false });
    await page.click('.modale-pie button:has-text("Fatto")');
    await page.waitForTimeout(300);
    if (await page.locator('.modale').count()) throw new Error('la finestra delle proprietà non si chiude');
  }
  // prova di tocco su OGNI tipo di componente presente nel disegno: la finestra deve aprirsi sul componente giusto
  {
    const kinds = await page.locator('.config-vista:not(.bozza) .trascina').evaluateAll(els => [...new Set(els.map(e => e.getAttribute('data-k')))]);
    const falliti = [];
    for (const k of kinds) {
      const els = page.locator(`.config-vista:not(.bozza) .trascina[data-k=${k}]`);
      const n = await els.count();
      let ok = 0;
      for (let i = 0; i < n; i++) {
        const el = els.nth(i);
        const id = await el.getAttribute('data-id');
        await el.scrollIntoViewIfNeeded();
        const b = await el.boundingBox();
        if (!b) continue;
        await page.mouse.click(b.x + b.width / 2, b.y + b.height / 2);
        await page.waitForTimeout(250);
        const aperta = await page.locator('.modale').count();
        const titolo = aperta ? await page.locator('.modale-testa h2').innerText() : '';
        if (aperta && (!id || titolo.startsWith(id) || ['clamp', 'compactor', 'rejector'].includes(k))) ok++;
        else say(`  tocco su ${k} ${id}: ${aperta ? 'si apre su "' + titolo + '"' : 'NON si apre'}`);
        if (!aperta) {
          const info = await page.evaluate(([x, y, i2]) => { const e = document.elementFromPoint(x, y); const g = e && e.closest('.trascina'); const r = document.querySelector('.modale'); const cat = []; for (let n = e; n && cat.length < 4; n = n.parentElement) cat.push(n.tagName + '.' + (n.getAttribute('class') || '') + (n.getAttribute('data-k') ? '[' + n.getAttribute('data-k') + ']' : '')); return `${cat.join(' < ')} @@ ${e ? e.tagName + '.' + (e.getAttribute('class') || '') : 'nulla'} gruppo=${g ? g.getAttribute('data-k') + ':' + g.getAttribute('data-id') + ' ' + g.getAttribute('class') : '-'}`; }, [b.x + b.width / 2, b.y + b.height / 2, 0]);
          say(`    sotto il tocco (${Math.round(b.x)},${Math.round(b.y)} ${Math.round(b.width)}x${Math.round(b.height)}): ${info}`);
        }
        if (aperta) { await page.click('.modale-pie button:has-text("Fatto")'); await page.waitForTimeout(150); }
      }
      say(`tocco ${k}: ${ok}/${n} aprono la finestra giusta`);
      if (ok < n) falliti.push(k);
    }
    say(`tipi di componente provati: ${kinds.join(', ')}`);
    if (falliti.length) throw new Error('il tocco non apre la finestra per: ' + falliti.join(', '));
  }
  say(`controlli: ${(await page.locator('.blocco:has(h2:has-text("Controlli"))').innerText()).replace(/\s+/g, ' ').slice(0, 200)}; fotocellule: ${await page.locator('.blocco:has(h2:has-text("Fotocellule")) tbody tr').count()}`);
  // pacchi: vista 3D del pacco e double banking nascosto se non ci sta
  await tab('pacchi');
  await page.waitForTimeout(800);
  say(`vista 3D del pacco: ${await page.locator('#cpack').count() === 1 ? 'presente' : 'ASSENTE'}`);
  say(`righello ${await page.locator('#rpack svg').count()}, righe pacchi ${await page.locator('#pacchi table.configurazioni tbody tr').count()}`);
  await tab('linea');
  {
    const g = page.locator('.vista-config .trascina[data-k=strapper]').first();
    await g.scrollIntoViewIfNeeded();
    const before = await page.locator('.config-vista:not(.bozza) svg.vista-config').innerHTML();
    const b = await g.boundingBox();
    if (b) {
      await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2);
      await page.mouse.down();
      await page.mouse.move(b.x + b.width / 2 + 60, b.y + b.height / 2, { steps: 6 });
      await page.mouse.up();
      await page.waitForTimeout(800);
    }
    const after = await page.locator('.config-vista:not(.bozza) svg.vista-config').innerHTML();
    say(`trascinamento legatrice nella vista 2D: ${before !== after ? 'ok (vista cambiata)' : 'NESSUN CAMBIAMENTO'}`);
  }
  {
    const fc = () => page.locator('.vista-config .trascina[data-k=photocell]').count();
    const n0 = await fc();
    await page.locator('.aggiungi-pannello button:has-text("Fotocellula")').click();
    await page.waitForTimeout(600);
    const n1 = await fc();
    await page.waitForSelector('.modale', { timeout: 10000 });
    await page.click('.modale-pie button:has-text("Rimuovi")');
    await page.waitForTimeout(600);
    const n2 = await fc();
    say(`vista 2D, fotocellule: ${n0} → aggiunta ${n1} → eliminata ${n2}`);
    if (!(n1 === n0 + 1 && n2 === n0)) throw new Error('aggiunta/eliminazione dalla vista 2D non riuscita');
  }
  say(`vista di configurazione: ${await page.locator('.config-vista:not(.bozza) svg.vista-config').count()}`);
  const dbl = await page.locator('#pacchi table.configurazioni select option[value="DoubleBanking"]').count();
  const sing = await page.locator('#pacchi table.configurazioni select option[value="SingleBanking"]').count();
  say(`opzioni banking: single ${sing}, double ${dbl}`);
  await tab('pacchi');
  await page.locator('#cpack').scrollIntoViewIfNeeded();
  await page.screenshot({ path: `${out}/web-6-pacchi3d.png`, fullPage: false });
  // documenti: PDF, progetto, Gantt, Excel e tutto in un file ZIP vengono scaricati
  await tab('simulazione');
  await page.waitForTimeout(600);
  for (const label of ['Vista dall\'alto (PDF)', 'Progetto (JSON)', 'Gantt (PDF)', 'Risultati (Excel)', 'Tutto (.zip)']) {
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
