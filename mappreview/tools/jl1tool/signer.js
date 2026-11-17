// signer.js —— 常驻签名进程
// 用法: node signer.js
// stdin 每行一个 JSON: {"paths":["/maptile/cn/2025/v2/1/2/18", ...]}
// stdout 每行一个 JSON: {"sigs":["<ts>-<rand>-0-<md5>", ...]}
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const readline = require('readline');

const DIR = __dirname;

// 伪造浏览器环境（wrapPath 会校验 location.host 白名单）
global.window = global;
global.self = global;
global.location = {
  host: 'pc.earthdq.com', hostname: 'pc.earthdq.com', protocol: 'https:',
  href: 'https://pc.earthdq.com/', origin: 'https://pc.earthdq.com',
};
global.navigator = { userAgent: 'Mozilla/5.0 Node', platform: 'Win32' };
global.document = {
  createElement: () => ({ style: {}, setAttribute() {}, appendChild() {} }),
  documentElement: { style: {} }, body: { appendChild() {} },
  addEventListener() {}, cookie: '',
};
global.performance = global.performance || { now: () => Date.now() };
if (!global.crypto) {
  global.crypto = {
    getRandomValues: (a) => {
      for (let i = 0; i < a.length; i++) a[i] = Math.floor(Math.random() * 256);
      return a;
    },
  };
}

vm.runInThisContext(fs.readFileSync(path.join(DIR, 'wasm_exec_tiny.js'), 'utf8'), { filename: 'wasm_exec_tiny.js' });
const go = new global.Go();
const wasmBytes = fs.readFileSync(path.join(DIR, 'security.wasm'));

const rl = readline.createInterface({ input: process.stdin, terminal: false });

function reply(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n');
}

WebAssembly.instantiate(wasmBytes, go.importObject).then(({ instance }) => {
  go.run(instance);
  setTimeout(() => {
    if (typeof global.wrapPath !== 'function') {
      reply({ error: 'wrapPath 未注册，wasm 加载失败' });
      process.exit(1);
    }
    reply({ ready: true });
    rl.on('line', (line) => {
      if (!line.trim()) return;
      try {
        const req = JSON.parse(line);
        const ts = Math.floor(Date.now() / 1000);
        const paths = req.paths || [];
        const sigs = paths.map((p) => global.wrapPath(p, ts));
        reply({ sigs, ts });
      } catch (e) {
        reply({ error: String(e && e.message || e) });
      }
    });
  }, 800);
}).catch((e) => {
  reply({ error: 'wasm 实例化失败: ' + String(e) });
  process.exit(1);
});
