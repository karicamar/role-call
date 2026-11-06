// Drives RoleCall in headless Chrome over the DevTools protocol: signs in as different people, makes decisions,
// fails on any console error or CSP violation, and saves screenshots to docs/.
// Start the app first (dotnet run --project src/RoleCall.Web), then: node demo/screenshots.mjs [chrome path] [base url]
// Needs Node 22+ (built-in WebSocket).

import { spawn } from "node:child_process";
import { mkdirSync, writeFileSync, mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const chrome = process.argv[2] ?? "C:/Program Files/Google/Chrome/Application/chrome.exe";
const base = process.argv[3] ?? "http://localhost:5095";
const docs = new URL("../docs/", import.meta.url);
mkdirSync(docs, { recursive: true });

const port = 9300 + Math.floor(Math.random() * 500);
const profile = mkdtempSync(join(tmpdir(), "rolecall-chrome-"));
const proc = spawn(chrome, ["--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, "--window-size=1360,900", "--hide-scrollbars", "about:blank"], { stdio: "ignore" });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let targets;
for (let i = 0; i < 50 && !targets; i++) {
  try { targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); } catch { await sleep(200); }
}
const page = targets.find((t) => t.type === "page");
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((r) => ws.addEventListener("open", r));

let id = 0;
const pending = new Map();
const problems = [];
ws.addEventListener("message", (e) => {
  const msg = JSON.parse(e.data);
  if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
  if (msg.method === "Runtime.consoleAPICalled" && msg.params.type === "error")
    problems.push(msg.params.args.map((a) => a.value ?? a.description).join(" "));
  if (msg.method === "Runtime.exceptionThrown") problems.push(msg.params.exceptionDetails.text);
  if (msg.method === "Log.entryAdded" && msg.params.entry.level === "error") problems.push(msg.params.entry.text);
});
const send = (method, params = {}) => new Promise((resolve, reject) => {
  const n = ++id;
  pending.set(n, (m) => (m.error ? reject(new Error(`${method}: ${m.error.message}`)) : resolve(m.result)));
  ws.send(JSON.stringify({ id: n, method, params }));
});
const evaluate = async (expression) => (await send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true })).result.value;
const waitFor = async (expression, what, timeout = 15000) => {
  for (const start = Date.now(); Date.now() - start < timeout; await sleep(150))
    if (await evaluate(expression)) return;
  throw new Error(`Timed out waiting for ${what}`);
};
const loaded = () => waitFor("document.readyState === 'complete'", "page load");
const go = async (path) => { await send("Page.navigate", { url: base + path }); await sleep(300); await loaded(); };
const submit = async (expression, what) => {
  if (!(await evaluate(`(() => { const el = ${expression}; if (!el) return false; el.click(); return true; })()`))) throw new Error(`Not found: ${what}`);
  await sleep(400);
  await loaded();
};
const shot = async (name, full = true) => {
  await sleep(300);
  const { data } = await send("Page.captureScreenshot", { format: "png", captureBeyondViewport: full });
  writeFileSync(new URL(`${name}.png`, docs), Buffer.from(data, "base64"));
  console.log(`  saved docs/${name}.png`);
};
const text = () => evaluate("document.body.innerText");
const expect = async (snippet) => { if (!(await text()).includes(snippet)) throw new Error(`Expected "${snippet}" on ${await evaluate("location.pathname")}`); };
const signIn = async (user) => {
  await go("/login");
  await submit(`[...document.querySelectorAll('.users form')].find(f => f.user.value === ${JSON.stringify(user)})?.querySelector('button')`, `demo user ${user}`);
  await waitFor("location.pathname === '/'", "sign-in");
};
const signOut = () => submit("document.querySelector('form[action=\"/account/logout\"] button')", "sign out");
// The decision form of one entitlement of one person
const itemForm = (person, entitlement) =>
  `[...([...document.querySelectorAll('.person')].find(p => p.querySelector('h3').textContent.includes(${JSON.stringify(person)}))?.querySelectorAll('.item') ?? [])]` +
  `.find(i => i.querySelector('.ent').textContent === ${JSON.stringify(entitlement)})?.querySelector('form')`;

try {
  await send("Page.enable");
  await send("Runtime.enable");
  await send("Log.enable");
  await send("Emulation.setDeviceMetricsOverride", { width: 1360, height: 900, deviceScaleFactor: 1, mobile: false });

  console.log("Login");
  await go("/login");
  await shot("login", false);

  console.log("Overview and findings as the IAM team");
  await signIn("lena.iam");
  await expect("critical findings");
  await shot("overview");
  await go("/findings");
  await expect("Leaver still active");
  await shot("findings");
  await go("/admin/import");
  await expect("Segregation-of-duties rules");
  await shot("policy");
  await signOut();

  console.log("Tom reviews the payments team");
  await signIn("tom.payments");
  await go("/reviews");
  await expect("Mia Schulz");
  await shot("reviews");
  // Keeping an SoD conflict without a reason is refused...
  await submit(`${itemForm("Mia Schulz", "Payments:Approve")}?.querySelector('button[value=Keep]')`, "keep Mia's approval right");
  await expect("compensating control");
  // ...so Tom revokes it, and keeps the rest that has no risk in one go
  await submit(`${itemForm("Mia Schulz", "Payments:Approve")}?.querySelector('button[value=Revoke]')`, "revoke Mia's approval right");
  await expect("Marked for revocation");
  await submit("[...document.querySelectorAll('button')].find(b => b.textContent.includes('low-risk'))", "keep low-risk");
  await expect("low-risk entitlements confirmed");
  await signOut();

  console.log("Nina: the leaver can only be revoked");
  await signIn("nina.treasury");
  await go("/reviews");
  await expect("Left the bank: revoke only");
  await shot("leaver");
  await submit(`${itemForm("Lukas Braun", "Directory:VPN")}?.querySelector('button[value=Revoke]')`, "revoke the leaver's VPN");
  await expect("Marked for revocation");
  await signOut();

  console.log("Internal audit: campaign, review quality, audit trail");
  await signIn("anna.audit");
  await go("/campaign");
  await expect("may not have been done properly");
  await shot("campaign");
  await go("/audit");
  await expect("Hash chain intact");
  await expect("review.decide");
  await shot("audit");

  console.log("Narrow overview (portfolio card, small screens)");
  await send("Emulation.setDeviceMetricsOverride", { width: 1040, height: 760, deviceScaleFactor: 1, mobile: false });
  await go("/");
  await shot("overview-narrow", false);
  await send("Emulation.setDeviceMetricsOverride", { width: 390, height: 844, deviceScaleFactor: 2, mobile: true });
  await go("/campaign");
  if (await evaluate("document.documentElement.scrollWidth > window.innerWidth + 1")) throw new Error("The campaign page scrolls sideways on a phone");

  if (problems.length) throw new Error("Browser errors:\n" + problems.join("\n"));
  console.log("\nAll screens work, no console errors or CSP violations.");
} catch (e) {
  console.error(e.message);
  if (problems.length) console.error("Browser errors:\n" + problems.join("\n"));
  process.exitCode = 1;
} finally {
  ws.close();
  proc.kill();
}
