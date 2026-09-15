// A fake WebView2 host for previewing panel.html in an ordinary browser.
//
// tools/preview-panel.ps1 injects this ahead of the page's own script. The
// scenario comes from the URL hash (#empty, #working, #done, #error, #batch,
// #setup, #setup-error) and the theme from ?theme=light|dark. Messages the
// page posts are kept in window.__posted; window.__emit(msg) sends one in.
(function () {
  'use strict';

  var listeners = [];
  var params = new URLSearchParams(location.search);
  var scenario = (location.hash || '#empty').slice(1);
  var theme = params.get('theme');
  var width = parseInt(params.get('w'), 10);

  // Headless Edge will not make a window narrower than about 500px, and a
  // task pane is often nearer 300. Pin the page to the requested width, with
  // a hairline where the pane would end.
  if (width > 0) {
    document.documentElement.style.width = width + 'px';
    document.documentElement.style.boxShadow = '1px 0 0 #c9ced6';
  }

  // ?still=1 turns entrance animations off. A headless screenshot can land
  // mid-fade, which makes a finished screen look half-drawn.
  if (params.get('still')) {
    var still = document.createElement('style');
    still.textContent = '*, *::before, *::after { animation: none !important; transition: none !important; }';
    document.head.appendChild(still);
  }

  function emit(msg) {
    listeners.forEach(function (fn) { fn({ data: msg }); });
  }

  function type(text) {
    var input = document.getElementById('input');
    input.value = text;
    input.dispatchEvent(new Event('input'));
    document.getElementById('send').click();
  }

  function tool(name, message, ok) {
    return [
      { type: 'agent', kind: 'toolCall', toolName: name },
      { type: 'agent', kind: 'toolResult', toolName: name, message: message, ok: ok !== false }
    ];
  }

  var init = {
    type: 'init', hasKey: true, maskedKey: 'sk-ant-...hAAA',
    model: 'claude-opus-5', version: '0.1.0', theme: theme, sessionCostUsd: 0.33
  };

  var houseSteps = [].concat(
    [{ type: 'agent', kind: 'text', message: 'I\u2019ll plan the house first, then build the shell, roof and openings.' }],
    tool('sw_new_part', 'Created a new part (mm).'),
    tool('sw_declare_intent', 'Intent declared: envelope 120 \u00d7 80 \u00d7 95 mm, volume 118\u2013160 cm\u00b3.'),
    tool('sw_sketch_open', 'Sketch open on Top Plane.'),
    tool('sw_sketch_rect', 'Rectangle 120 \u00d7 80 mm centred on the origin.'),
    tool('sw_sketch_close', 'Sketch closed.'),
    tool('sw_extrude', 'Boss-Extrude1: 60 mm, rebuild clean. Volume 576.0 cm\u00b3.'),
    tool('sw_shell', 'Shell1: 4 mm, top face removed. Volume 139.4 cm\u00b3.'),
    tool('sw_sketch_open_on_face', 'Sketch on front face (area 7200 mm\u00b2, normal -Y).'),
    tool('sw_cut', 'Cut-Extrude1 failed: the sketch is not closed.', false),
    tool('sw_sketch_rect', 'Rectangle 20 \u00d7 30 mm at (-30, 10).'),
    tool('sw_cut', 'Cut-Extrude1: through next, rebuild clean. Volume 137.0 cm\u00b3.'),
    tool('sw_screenshot', 'Captured the isometric view (640 \u00d7 480).'),
    tool('sw_check_intent', 'Intent satisfied: envelope within 0.1 mm, volume 137.0 cm\u00b3 inside the band.')
  );

  var scenarios = {
    'setup': [
      { type: 'init', hasKey: false, model: 'claude-opus-5', version: '0.1.0', theme: theme }
    ],

    'setup-error': [
      { type: 'init', hasKey: false, model: 'claude-opus-5', version: '0.1.0', theme: theme },
      function () { document.getElementById('keyInput').value = 'sk-ant-api03-EXAMPLE'; },
      { type: 'keyResult', ok: false, code: 'no_credit',
        message: 'This key works, but the account has no credit. Add credit at console.anthropic.com, then try again.' }
    ],

    'empty': [init],

    'working': [init, function () { type('Make a small model house with a door and two windows.'); }]
      .concat(houseSteps.slice(0, 15), [{ type: 'agent', kind: 'toolCall', toolName: 'sw_sketch_open_on_face' }]),

    'stopped': [init, function () { type('Make a small model house with a door and two windows.'); }]
      .concat(houseSteps.slice(0, 11), [
        { type: 'agent', kind: 'toolCall', toolName: 'sw_extrude' },
        { type: 'done', completed: false, stopped: true, stoppedBecause: 'Cancelled.', runCostUsd: 0.21, sessionCostUsd: 0.54 }
      ]),

    'menu': [init, function () { document.getElementById('menuBtn').click(); }],

    'settings': [init, function () {
      document.getElementById('menuBtn').click();
      document.querySelector('[data-act=settings]').click();
    }],

    'done': [init, function () { type('Make a small model house with a door and two windows.'); }]
      .concat(houseSteps, [
        { type: 'agent', kind: 'text', message:
          'The house is built and matches the plan.\n\n' +
          '| Check | Planned | Measured |\n|---|---|---|\n' +
          '| Envelope | 120 \u00d7 80 \u00d7 95 mm | 120 \u00d7 80 \u00d7 95 mm |\n' +
          '| Volume | 118\u2013160 cm\u00b3 | **137.0 cm\u00b3** |\n\n' +
          '**What\u2019s in the tree**\n' +
          '- `Boss-Extrude1` for the walls, then `Shell1` at 4 mm\n' +
          '- The door and both windows cut **through next**, so each stops at its own wall\n' +
          '- A pitched roof from two extrudes\n\n' +
          'Want a drawing and a STEP next?' },
        { type: 'done', completed: true, runCostUsd: 1.1, sessionCostUsd: 1.43,
          costDetail: '24 requests, 136 in / 11,645 out, cache 388,283 read / 150,676 written' }
      ]),

    'error': [init, function () { type('Make a small model house with a door and two windows.'); }]
      .concat(houseSteps.slice(0, 7), [
        { type: 'agent', kind: 'failed', code: 'no_credit',
          message: 'Your Anthropic account is out of credit. Add credit at console.anthropic.com, then try again.' },
        { type: 'done', completed: false, stoppedBecause: 'The request to Anthropic could not be completed.',
          runCostUsd: 0.33, sessionCostUsd: 0.33 }
      ]),

    'batch': [init, function () { type('Set Revision to B on every part in C:\\Jobs\\1234'); }]
      .concat(tool('sw_batch_index', '14 parts, 3 assemblies, 6 drawings; 1 read-only.'),
        [{ type: 'agent', kind: 'toolCall', toolName: 'sw_batch_preview' },
         { type: 'batchPlan', id: 'B1', title: 'Set custom property Revision = B', folder: 'C:\\Jobs\\1234',
           writesFiles: true, ready: 3, unchanged: 1, skipped: 2,
           rows: [
             { n: 1, file: 'bracket-left.SLDPRT', status: 'ready', current: 'A', proposed: 'B' },
             { n: 2, file: 'bracket-right.SLDPRT', status: 'ready', current: 'A', proposed: 'B' },
             { n: 3, file: 'base-plate.SLDPRT', status: 'unchanged', current: 'B', proposed: 'B' },
             { n: 4, file: 'spacer-40.SLDPRT', status: 'ready', current: null, proposed: 'B',
               note: 'Last saved by SOLIDWORKS 2022; applying upgrades it.', warn: true },
             { n: 5, file: 'cover.SLDPRT', status: 'skipped', note: 'Read-only.' },
             { n: 6, file: 'housing.SLDPRT', status: 'skipped', note: 'Open in SOLIDWORKS.' }
           ] },
         { type: 'agent', kind: 'toolResult', toolName: 'sw_batch_preview', ok: true,
           message: 'Plan B1: 3 will change, 1 already correct, 2 skipped.' },
         { type: 'agent', kind: 'text', message:
           'Plan **B1** is ready. Three parts will get Revision B. One is already B, and two are skipped ' +
           '(one read-only, one open). Nothing changes until you press **Apply** on the card.' },
         { type: 'done', completed: true, runCostUsd: 0.14, sessionCostUsd: 0.47 }])
  };

  window.__posted = [];
  window.__emit = emit;
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    addEventListener: function (kind, fn) { if (kind === 'message') listeners.push(fn); },
    postMessage: function (raw) {
      var msg = JSON.parse(raw);
      window.__posted.push(msg);
      if (msg.type === 'ready') {
        (scenarios[scenario] || scenarios.empty).forEach(function (step) {
          if (typeof step === 'function') step(); else emit(step);
        });
      }
    }
  };
})();
