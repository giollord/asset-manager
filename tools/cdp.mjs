#!/usr/bin/env node
// Minimal Chrome DevTools Protocol client for debugging the MAUI app's WebView2 (Debug builds expose
// --remote-debugging-port, default 9333). Uses only Node built-ins (fetch + WebSocket, Node 22+).
//
// Usage: node tools/cdp.mjs <command> [args]   (env CDP_PORT overrides the port)
//   targets                      list debuggable pages
//   eval "<js expression>"       evaluate in the page (awaits promises), prints JSON result
//   text [selector]              innerText of selector (default body)
//   html [selector]              outerHTML of selector (default body)
//   click "<css selector>"       click the first matching element
//   type "<css selector>" "txt"  focus element, set value and fire input/change events
//   nav <path>                   navigate with Blazor's router (e.g. browse, settings, "" for dashboard)
//   shot <out.png>               screenshot of the page
//   console [seconds]            stream console messages and exceptions (default 10 s)
//   wait "<css selector>" [ms]   wait until a selector exists (default 10000 ms)

const port = process.env.CDP_PORT ?? '9333';
const [cmd, ...args] = process.argv.slice(2);

async function targets() {
  const res = await fetch(`http://127.0.0.1:${port}/json`);
  return (await res.json()).filter(t => t.type === 'page');
}

async function connect() {
  const page = (await targets()).find(t => !t.url.startsWith('devtools://'));
  if (!page) throw new Error(`No page target on port ${port}. Is the Debug app running?`);
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((ok, fail) => { ws.onopen = ok; ws.onerror = fail; });
  let id = 0;
  const pending = new Map();
  const listeners = [];
  ws.onmessage = ev => {
    const msg = JSON.parse(ev.data);
    if (msg.id && pending.has(msg.id)) {
      const { ok, fail } = pending.get(msg.id);
      pending.delete(msg.id);
      msg.error ? fail(new Error(msg.error.message)) : ok(msg.result);
    } else if (msg.method) listeners.forEach(l => l(msg));
  };
  const send = (method, params = {}) => new Promise((ok, fail) => {
    const mid = ++id;
    pending.set(mid, { ok, fail });
    ws.send(JSON.stringify({ id: mid, method, params }));
  });
  return { send, on: l => listeners.push(l), close: () => ws.close() };
}

async function evaluate(c, expression) {
  const r = await c.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text);
  return r.result.value;
}

const q = s => JSON.stringify(s);

// Git Bash (MSYS) rewrites "/browse" into "C:/Program Files/Git/browse"; undo that and drop the leading slash.
const routePath = p => (p ?? '').replace(/^[A-Za-z]:[\/].*?Git[\/]/, '').replace(/^\//, '');

async function main() {
  if (!cmd || cmd === 'targets') {
    console.log(JSON.stringify((await targets()).map(t => ({ title: t.title, url: t.url })), null, 2));
    return;
  }
  const c = await connect();
  try {
    switch (cmd) {
      case 'eval':
        console.log(JSON.stringify(await evaluate(c, args[0]), null, 2));
        break;
      case 'text':
        console.log(await evaluate(c, `document.querySelector(${q(args[0] ?? 'body')})?.innerText ?? '(no match)'`));
        break;
      case 'html':
        console.log(await evaluate(c, `document.querySelector(${q(args[0] ?? 'body')})?.outerHTML ?? '(no match)'`));
        break;
      case 'click':
        console.log(await evaluate(c, `(() => { const e = document.querySelector(${q(args[0])}); if (!e) return 'no match'; e.click(); return 'clicked'; })()`));
        break;
      case 'type':
        console.log(await evaluate(c, `(() => { const e = document.querySelector(${q(args[0])}); if (!e) return 'no match';
          e.focus(); e.value = ${q(args[1] ?? '')}; e.dispatchEvent(new Event('input', { bubbles: true }));
          e.dispatchEvent(new Event('change', { bubbles: true })); return 'typed'; })()`));
        break;
      case 'nav':
        // Blazor.navigateTo is ignored inside BlazorWebView; a clicked link goes through the Blazor router.
        console.log(await evaluate(c, `(async () => { const a = document.createElement('a'); a.href = ${q(routePath(args[0]))};
          (document.querySelector('#app') ?? document.body).appendChild(a); a.click(); a.remove(); await new Promise(r => setTimeout(r, 300)); return location.href; })()`));
        break;
      case 'wait': {
        const limit = Number(args[1] ?? 10000);
        const start = Date.now();
        while (!(await evaluate(c, `!!document.querySelector(${q(args[0])})`))) {
          if (Date.now() - start > limit) throw new Error(`timeout waiting for ${args[0]}`);
          await new Promise(r => setTimeout(r, 200));
        }
        console.log('found');
        break;
      }
      case 'drop': {
        // Simulated file drop inside the renderer (does not exercise the OS -> WebView2 drag path).
        const data = { items: [], files: args, dragOperationsMask: 1 };
        for (const type of ['dragEnter', 'dragOver', 'drop'])
          await c.send('Input.dispatchDragEvent', { type, x: 700, y: 250, data });
        console.log('dropped', args.length);
        break;
      }
      case 'shot': {
        const { data } = await c.send('Page.captureScreenshot', { format: 'png' });
        const fs = await import('node:fs');
        fs.writeFileSync(args[0] ?? 'shot.png', Buffer.from(data, 'base64'));
        console.log(`saved ${args[0] ?? 'shot.png'}`);
        break;
      }
      case 'console': {
        await c.send('Runtime.enable');
        await c.send('Log.enable');
        c.on(m => {
          if (m.method === 'Runtime.consoleAPICalled')
            console.log(`[${m.params.type}]`, m.params.args.map(a => a.value ?? a.description).join(' '));
          else if (m.method === 'Runtime.exceptionThrown')
            console.log('[exception]', m.params.exceptionDetails.exception?.description ?? m.params.exceptionDetails.text);
          else if (m.method === 'Log.entryAdded')
            console.log(`[${m.params.entry.level}]`, m.params.entry.text);
        });
        await new Promise(r => setTimeout(r, Number(args[0] ?? 10) * 1000));
        break;
      }
      default:
        throw new Error(`unknown command ${cmd}`);
    }
  } finally {
    c.close();
  }
}

main().catch(e => { console.error(e.message); process.exit(1); });
