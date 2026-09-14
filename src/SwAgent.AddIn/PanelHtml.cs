namespace SwAgent.AddIn
{
    /// <summary>
    /// The task pane's UI.
    ///
    /// Two screens: key setup, and chat. Setup gets disproportionate care
    /// because it is the conversion-critical path - we are asking a mechanical
    /// engineer to go and create an Anthropic account before they have seen the
    /// tool do anything - and because the promise about where their data goes
    /// has to be stated plainly, in the UI, at the moment they hand over a
    /// credential.
    ///
    /// Served via NavigateToString, so there is no origin, no network fetch and
    /// nothing external to load.
    /// </summary>
    internal static class PanelHtml
    {
        public const string Text = @"<!doctype html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>SwAgent</title>
<style>
  :root {
    color-scheme: light dark;
    --bg: #ffffff;
    --fg: #1b1b1b;
    --muted: #6a6a6a;
    --line: #e2e2e2;
    --panel: #f6f7f8;
    --accent: #1f6feb;
    --accent-fg: #ffffff;
    --ok: #1a7f37;
    --err: #b42318;
    --code: #f0f1f3;
    --warn: #9a6700;
  }
  @media (prefers-color-scheme: dark) {
    :root {
      --bg: #1e1e1e; --fg: #e6e6e6; --muted: #9a9a9a; --line: #333;
      --panel: #252526; --accent: #4c8dff; --accent-fg: #10243f;
      --ok: #3fb950; --err: #f85149; --code: #2a2a2b; --warn: #d29922;
    }
  }

  * { box-sizing: border-box; }
  html, body { height: 100%; }
  body {
    margin: 0; background: var(--bg); color: var(--fg);
    font: 13px/1.5 'Segoe UI', system-ui, sans-serif;
    display: flex; flex-direction: column; overflow: hidden;
  }

  header {
    padding: 10px 12px; border-bottom: 1px solid var(--line);
    display: flex; align-items: baseline; gap: 8px; flex: 0 0 auto;
  }
  header h1 { font-size: 13px; margin: 0; font-weight: 600; }
  header .model { font-size: 11px; color: var(--muted); margin-left: auto; }

  main { flex: 1 1 auto; overflow-y: auto; padding: 12px; }
  footer { flex: 0 0 auto; border-top: 1px solid var(--line); padding: 8px 12px; }

  .hidden { display: none !important; }

  /* ---- setup ---- */
  .setup h2 { font-size: 14px; margin: 0 0 6px; }
  .setup p { margin: 0 0 12px; color: var(--muted); }
  .steps { margin: 0 0 14px; padding-left: 18px; }
  .steps li { margin-bottom: 6px; }
  .steps code {
    background: var(--code); padding: 1px 5px; border-radius: 3px;
    font-family: Consolas, monospace; font-size: 12px;
  }
  .privacy {
    border-left: 3px solid var(--accent); background: var(--panel);
    padding: 9px 11px; margin: 14px 0; font-size: 12px;
  }
  .privacy strong { display: block; margin-bottom: 3px; }
  .privacy ul { margin: 6px 0 0; padding-left: 16px; color: var(--muted); }

  input[type=password], input[type=text], textarea {
    width: 100%; padding: 7px 9px; border: 1px solid var(--line);
    border-radius: 4px; background: var(--bg); color: var(--fg);
    font: inherit; font-family: Consolas, monospace;
  }
  textarea { font-family: inherit; resize: none; }
  input:focus, textarea:focus { outline: 2px solid var(--accent); outline-offset: -1px; }

  button {
    padding: 7px 14px; border: 1px solid transparent; border-radius: 4px;
    background: var(--accent); color: var(--accent-fg);
    font: inherit; font-weight: 600; cursor: pointer;
  }
  button:disabled { opacity: .5; cursor: default; }
  button.secondary {
    background: transparent; color: var(--muted); border-color: var(--line); font-weight: 400;
  }
  .row { display: flex; gap: 8px; align-items: center; margin-top: 10px; }

  .msg { font-size: 12px; margin-top: 10px; }
  .msg.ok { color: var(--ok); }
  .msg.err { color: var(--err); }

  /* ---- chat ---- */
  .turn { margin-bottom: 14px; }
  .turn .who { font-size: 11px; text-transform: uppercase; letter-spacing: .04em; color: var(--muted); margin-bottom: 3px; }
  .turn .body { white-space: pre-wrap; word-wrap: break-word; }
  .turn.user .body {
    background: var(--panel); padding: 7px 10px; border-radius: 5px;
  }

  .activity { margin: 6px 0 0; font-size: 12px; }
  .act { display: flex; gap: 7px; padding: 2px 0; color: var(--muted); }
  .act .dot { flex: 0 0 auto; width: 14px; text-align: center; }
  .act.fail { color: var(--err); }
  .act .detail { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .act code { font-family: Consolas, monospace; color: var(--fg); }

  .status { font-size: 11px; color: var(--muted); display: flex; gap: 10px; align-items: center; }
  .status .cost { margin-left: auto; }

  .composer { display: flex; gap: 8px; align-items: flex-end; margin-bottom: 6px; }
  .composer textarea { min-height: 34px; max-height: 120px; }

  .empty { color: var(--muted); }
  .empty ul { padding-left: 18px; margin: 8px 0 0; }
  .empty li { margin-bottom: 4px; }

  /* ---- batch plans ---- */
  .batch { border: 1px solid var(--line); border-radius: 6px; margin: 8px 0 14px; background: var(--panel); }
  .batch-head { padding: 8px 10px 2px; }
  .batch-head .id { font-size: 11px; color: var(--muted); text-transform: uppercase; letter-spacing: .04em; }
  .batch-head .title { font-weight: 600; word-wrap: break-word; }
  .batch-sum { padding: 0 10px 8px; font-size: 12px; color: var(--muted); word-wrap: break-word; }
  .batch-sum .writes { color: var(--fg); }
  .batch-rows {
    max-height: 240px; overflow-y: auto; background: var(--bg);
    border-top: 1px solid var(--line); border-bottom: 1px solid var(--line);
  }
  .brow {
    display: grid; grid-template-columns: 32px 1fr; gap: 0 6px;
    padding: 4px 10px; font-size: 12px; border-bottom: 1px solid var(--line);
  }
  .brow:last-child { border-bottom: 0; }
  .brow .n { color: var(--muted); font-family: Consolas, monospace; }
  .brow .file { font-family: Consolas, monospace; word-break: break-all; }
  .brow .chg, .brow .note { grid-column: 2; color: var(--muted); word-wrap: break-word; }
  .brow .state { font-size: 11px; text-transform: uppercase; letter-spacing: .03em; margin-right: 6px; }
  .brow.ready .state { color: var(--accent); }
  .brow.working .state { color: var(--fg); }
  .brow.applied .state { color: var(--ok); }
  .brow.failed .state, .brow.failed .note { color: var(--err); }
  .brow.skipped, .brow.unchanged { opacity: .75; }
  .brow .note.warn { color: var(--warn); }
  .batch .row { padding: 8px 10px 0; margin-top: 0; }
  .batch .msg { padding: 6px 10px 8px; margin-top: 0; }
</style>
</head>
<body>

<header>
  <h1>SwAgent</h1>
  <span class='model' id='modelLabel'></span>
</header>

<!-- ============ SETUP ============ -->
<main id='setup' class='setup hidden'>
  <h2>Connect your Anthropic key</h2>
  <p>SwAgent uses your own Anthropic account, so you pay Anthropic directly and
     there is no subscription here.</p>

  <ol class='steps'>
    <li>Go to <code>console.anthropic.com</code> and sign in.</li>
    <li>Open <strong>API keys</strong> in the left sidebar.</li>
    <li>Click <strong>Create key</strong>, name it <code>SwAgent</code>.</li>
    <li>Copy it and paste it below. It starts with <code>sk-ant-</code>.</li>
  </ol>

  <input type='password' id='keyInput' placeholder='sk-ant-...' autocomplete='off' spellcheck='false'>

  <div class='row'>
    <button id='saveKey'>Check and save</button>
    <span class='msg' id='keyMsg'></span>
  </div>

  <div class='privacy'>
    <strong>What leaves this machine</strong>
    Your description of the part, and the measurements and viewport images of
    what gets built, are sent to api.anthropic.com so the model can work. That
    is all.
    <ul>
      <li>Your key is encrypted with your Windows account and never leaves this PC.</li>
      <li>No file names, file paths or saved files are sent.</li>
      <li>There is no SwAgent server, and no analytics of any kind.</li>
    </ul>
  </div>
</main>

<!-- ============ CHAT ============ -->
<main id='chat' class='hidden'>
  <div id='transcript'></div>
  <div class='empty' id='empty'>
    Describe a part and it will be modelled in the active session, or ask for a
    change across a folder of existing files.
    <ul>
      <li>A 60 x 40 x 10 mm plate with a 10 mm hole in the middle</li>
      <li>A 100 x 60 x 6 mm mounting plate with four M6 clearance holes 15 mm in from each corner</li>
      <li>An 80 mm square spacer, 12 mm thick, with a 40 mm bore</li>
      <li>Set Revision to B on every part in C:\Jobs\1234</li>
    </ul>
  </div>
</main>

<footer id='composerBar' class='hidden'>
  <div class='composer'>
    <textarea id='input' rows='1' placeholder='Describe a part...'></textarea>
    <button id='send'>Send</button>
    <button id='stop' class='secondary hidden'>Stop</button>
  </div>
  <div class='status'>
    <span id='keyState'></span>
    <button id='changeKey' class='secondary' style='padding:1px 6px;font-size:11px;'>Change</button>
    <span class='cost' id='cost'></span>
  </div>
</footer>

<script>
(function () {
  'use strict';

  var host = window.chrome && window.chrome.webview;
  var el = function (id) { return document.getElementById(id); };

  var setup = el('setup'), chat = el('chat'), composerBar = el('composerBar');
  var transcript = el('transcript'), empty = el('empty');
  var input = el('input'), sendBtn = el('send'), stopBtn = el('stop');
  var busy = false;
  var activityBox = null;

  function post(msg) { if (host) host.postMessage(JSON.stringify(msg)); }

  function show(which) {
    setup.classList.toggle('hidden', which !== 'setup');
    chat.classList.toggle('hidden', which !== 'chat');
    composerBar.classList.toggle('hidden', which !== 'chat');
  }

  function addTurn(who, text, cls) {
    empty.classList.add('hidden');
    var d = document.createElement('div');
    d.className = 'turn ' + (cls || '');
    var w = document.createElement('div'); w.className = 'who'; w.textContent = who;
    var b = document.createElement('div'); b.className = 'body'; b.textContent = text;
    d.appendChild(w); d.appendChild(b);
    transcript.appendChild(d);
    scroll();
    return b;
  }

  function newActivity() {
    var d = document.createElement('div');
    d.className = 'activity';
    transcript.appendChild(d);
    return d;
  }

  function addActivity(box, symbol, label, detail, failed) {
    var row = document.createElement('div');
    row.className = 'act' + (failed ? ' fail' : '');

    var dot = document.createElement('span'); dot.className = 'dot'; dot.textContent = symbol;
    var name = document.createElement('code'); name.textContent = label;
    var det = document.createElement('span'); det.className = 'detail'; det.textContent = detail || '';

    row.appendChild(dot); row.appendChild(name); row.appendChild(det);
    box.appendChild(row);
    scroll();
    return row;
  }

  function scroll() { chat.scrollTop = chat.scrollHeight; }

  function setBusy(b) {
    busy = b;
    sendBtn.disabled = b;
    input.disabled = b;
    stopBtn.classList.toggle('hidden', !b);
    if (!b) input.focus();
  }

  // ---- sending ----
  function send() {
    var text = input.value.trim();
    if (!text || busy) return;

    addTurn('You', text, 'user');
    input.value = '';
    input.style.height = 'auto';

    activityBox = newActivity();
    setBusy(true);
    post({ type: 'send', text: text });
  }

  sendBtn.addEventListener('click', send);
  stopBtn.addEventListener('click', function () { post({ type: 'cancel' }); });

  input.addEventListener('keydown', function (e) {
    // Enter sends; Shift+Enter is a newline. Engineers type fast and expect this.
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); }
  });

  input.addEventListener('input', function () {
    input.style.height = 'auto';
    input.style.height = Math.min(input.scrollHeight, 120) + 'px';
  });

  // ---- key setup ----
  el('saveKey').addEventListener('click', function () {
    var key = el('keyInput').value.trim();
    if (!key) return;
    var msg = el('keyMsg');
    msg.className = 'msg';
    msg.textContent = 'Checking...';
    el('saveKey').disabled = true;
    post({ type: 'saveKey', key: key });
  });

  el('keyInput').addEventListener('keydown', function (e) {
    if (e.key === 'Enter') el('saveKey').click();
  });

  el('changeKey').addEventListener('click', function () {
    el('keyInput').value = '';
    el('keyMsg').textContent = '';
    el('keyMsg').className = 'msg';
    el('saveKey').disabled = false;
    show('setup');
  });

  // ---- batch plans ----
  // File names appear here and nowhere else. This page is local; the
  // conversation with the model refers to files by row number instead.
  var batches = {};
  var runningBatch = null;
  var stateLabel = {
    ready: 'will change', unchanged: 'no change', skipped: 'skipped',
    applied: 'done', failed: 'failed', working: 'working'
  };

  function node(tag, cls, value) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (value != null) e.textContent = value;
    return e;
  }

  function renderBatch(m) {
    empty.classList.add('hidden');
    var card = node('div', 'batch');

    var head = node('div', 'batch-head');
    head.appendChild(node('div', 'id', 'Batch ' + m.id + ' \u00b7 preview, nothing changed yet'));
    head.appendChild(node('div', 'title', m.title));
    card.appendChild(head);

    var sum = node('div', 'batch-sum', m.folder + ' \u00b7 ' + m.ready + ' will change, ' +
      m.unchanged + ' already correct, ' + m.skipped + ' skipped. ');
    if (m.writesFiles && m.ready > 0) sum.appendChild(node('span', 'writes', 'Applying saves each changed file in place.'));
    card.appendChild(sum);

    var list = node('div', 'batch-rows');
    var rowEls = {};
    (m.rows || []).forEach(function (r) {
      var row = node('div', 'brow ' + r.status);
      row.appendChild(node('span', 'n', '#' + r.n));
      row.appendChild(node('span', 'file', r.file));

      var chg = node('span', 'chg');
      chg.appendChild(node('span', 'state', stateLabel[r.status] || r.status));
      if (r.proposed != null) {
        var from = m.writesFiles ? (r.current == null ? '(none)' : r.current) + ' ' : '';
        chg.appendChild(document.createTextNode(from + '\u2192 ' + r.proposed));
      }
      row.appendChild(chg);

      var note = node('span', 'note' + (r.warn ? ' warn' : ''), r.note || '');
      if (!r.note) note.classList.add('hidden');
      row.appendChild(note);

      list.appendChild(row);
      rowEls[r.n] = row;
    });
    card.appendChild(list);

    var buttons = node('div', 'row');
    var apply = node('button', null,
      m.ready > 0 ? 'Apply ' + m.ready + (m.ready === 1 ? ' change' : ' changes') : 'Nothing to apply');
    var discard = node('button', 'secondary', 'Discard');
    var stop = node('button', 'secondary hidden', 'Stop');
    apply.disabled = m.ready === 0;
    buttons.appendChild(apply);
    buttons.appendChild(discard);
    buttons.appendChild(stop);
    card.appendChild(buttons);

    var msg = node('div', 'msg');
    card.appendChild(msg);

    batches[m.id] = { rows: rowEls, apply: apply, discard: discard, stop: stop, msg: msg };

    apply.addEventListener('click', function () {
      if (busy || runningBatch) {
        msg.className = 'msg err';
        msg.textContent = busy ? 'Wait for SwAgent to finish first.' : 'Another batch is being applied.';
        return;
      }
      apply.disabled = true;
      discard.disabled = true;
      msg.className = 'msg';
      msg.textContent = 'Applying...';
      post({ type: 'batchApply', id: m.id });
    });

    discard.addEventListener('click', function () {
      apply.disabled = true;
      discard.disabled = true;
      post({ type: 'batchDiscard', id: m.id });
    });

    stop.addEventListener('click', function () {
      stop.disabled = true;
      msg.textContent = 'Stopping after the current file...';
      post({ type: 'batchStop' });
    });

    // The card arrives mid-turn; later activity belongs below it.
    transcript.appendChild(card);
    activityBox = null;
    scroll();
  }

  function batchStarted(m) {
    var b = batches[m.id];
    if (!b) return;
    runningBatch = m.id;
    b.stop.classList.remove('hidden');
    b.stop.disabled = false;
    sendBtn.disabled = true;
    input.disabled = true;
  }

  function batchRow(m) {
    var b = batches[m.id];
    var row = b && b.rows[m.n];
    if (!row) return;
    row.className = 'brow ' + m.status;
    row.querySelector('.state').textContent = stateLabel[m.status] || m.status;
    var note = row.querySelector('.note');
    note.textContent = m.note || '';
    note.className = 'note' + (m.note ? '' : ' hidden');
    if (m.status === 'working') row.scrollIntoView({ block: 'nearest' });
  }

  function batchDone(m) {
    var b = batches[m.id];
    if (!b) return;
    if (runningBatch === m.id) {
      runningBatch = null;
      sendBtn.disabled = busy;
      input.disabled = busy;
    }
    b.stop.classList.add('hidden');
    b.msg.className = 'msg ' + (m.ok ? 'ok' : 'err');
    b.msg.textContent = m.message || '';
    var finished = !!(m.ok || m.discarded);
    b.apply.disabled = finished;
    b.discard.disabled = finished;
    scroll();
  }

  // ---- host messages ----
  if (host) {
    host.addEventListener('message', function (e) {
      var m = e.data;
      if (typeof m === 'string') { try { m = JSON.parse(m); } catch (err) { return; } }

      switch (m.type) {
        case 'init':
          el('modelLabel').textContent = m.model || '';
          el('keyState').textContent = m.maskedKey ? 'Key ' + m.maskedKey : '';
          show(m.hasKey ? 'chat' : 'setup');
          if (m.hasKey) input.focus();
          break;

        case 'keyResult':
          var msg = el('keyMsg');
          el('saveKey').disabled = false;
          msg.textContent = m.message || '';
          msg.className = 'msg ' + (m.ok ? 'ok' : 'err');
          if (m.ok) {
            el('keyInput').value = '';
            el('keyState').textContent = m.maskedKey ? 'Key ' + m.maskedKey : '';
            setTimeout(function () { show('chat'); input.focus(); }, 500);
          }
          break;

        case 'agent':
          if (!activityBox) activityBox = newActivity();
          if (m.kind === 'toolCall') {
            addActivity(activityBox, '', m.toolName, '');
          } else if (m.kind === 'toolResult') {
            var rows = activityBox.querySelectorAll('.act');
            var last = rows[rows.length - 1];
            if (last) {
              last.querySelector('.dot').textContent = m.ok ? '✓' : '✗';
              last.querySelector('.detail').textContent = m.message || '';
              if (!m.ok) last.className = 'act fail';
            }
          } else if (m.kind === 'retry') {
            addActivity(activityBox, '⋯', '', m.message);
          } else if (m.kind === 'text') {
            addTurn('SwAgent', m.message);
            activityBox = newActivity();
          }
          break;

        case 'done':
          setBusy(false);
          if (m.stoppedBecause) addTurn('SwAgent', m.stoppedBecause);
          if (m.cost) el('cost').textContent = m.cost;
          activityBox = null;
          break;

        case 'batchPlan':
          renderBatch(m);
          break;

        case 'batchStarted':
          batchStarted(m);
          break;

        case 'batchRow':
          batchRow(m);
          break;

        case 'batchDone':
          batchDone(m);
          break;

        case 'cost':
          el('cost').textContent = m.text || '';
          break;
      }
    });
  }

  post({ type: 'ready' });
})();
</script>
</body>
</html>";
    }
}
