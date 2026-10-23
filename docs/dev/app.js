/* 2XPing developer dashboard (private page, https://ldo2x.github.io/2XPing/dev/).
 *
 * Security model (docs/WEB-DASHBOARD.md, tools/supabase-web-dashboard.sql):
 *  - Only the PUBLIC publishable key (the same one every copy of the app carries, src/Telemetry.cs) and a Supabase Auth
 *    session. No secret key ever belongs in this file.
 *  - Every number comes from the web_* RPCs, which refuse anyone who is not listed in dev_owners.
 *  - Nothing is shown without a session; all data is put on the page as text (never as HTML).
 *  - ?demo=1 uses demo.js (made-up data) and never loads the Supabase library or contacts Supabase.
 */
(function () {
  'use strict';

  var SUPABASE_URL = 'https://exzriktevuapbbgqwgle.supabase.co';
  var SUPABASE_KEY = 'sb_publishable_RN9M6tcRWFXbqfJdbSOo1A_h0upD6RQ'; // public (publishable) key, as in src/Telemetry.cs
  var LIB = {
    src: 'https://cdn.jsdelivr.net/npm/@supabase/supabase-js@2.117.3/dist/umd/supabase.js',
    integrity: 'sha256-1qXEQUpdTOZG2cHeIjqnBn0/9mTBU5T/63/P/HYzVKM='
  };
  var REFRESH_MS = 30000;
  var ONLINE_WINDOW_S = 270; // Telemetry.OnlineWindow (4.5 minutes)
  var DEMO = new URLSearchParams(location.search).get('demo') === '1';

  var sb = null;            // Supabase client (never in demo mode)
  var lang = store('txp-dev-lang') === 'en' ? 'en' : 'ar';
  var langParam = new URLSearchParams(location.search).get('lang');
  if (langParam === 'en' || langParam === 'ar') lang = langParam;
  var root = document.getElementById('app');

  // ================================================================== small helpers

  function store(k, v) {
    try {
      if (arguments.length === 1) return localStorage.getItem(k);
      if (v === null) localStorage.removeItem(k); else localStorage.setItem(k, v);
    } catch (e) { /* private window: per-browser conveniences are simply not kept */ }
    return null;
  }

  function tt(ar, en) { return lang === 'ar' ? ar : en; }

  // An element: el('div', {class: 'x', onclick: fn}, child, 'text', [more]). Text always goes in as text, never HTML.
  function el(tag, attrs) {
    var e = document.createElement(tag);
    if (attrs) for (var k in attrs) {
      var v = attrs[k];
      if (v === null || v === undefined || v === false) continue;
      if (k.slice(0, 2) === 'on' && typeof v === 'function') e.addEventListener(k.slice(2), v);
      else if (k === 'class') e.className = v;
      else if (k === 'text') e.textContent = v;
      else e.setAttribute(k, v === true ? '' : String(v));
    }
    for (var i = 2; i < arguments.length; i++) add(e, arguments[i]);
    return e;
  }

  function add(parent, c) {
    if (c === null || c === undefined || c === false) return;
    if (Array.isArray(c)) { c.forEach(function (x) { add(parent, x); }); return; }
    parent.appendChild(typeof c === 'string' || typeof c === 'number' ? document.createTextNode(String(c)) : c);
  }

  var SVGNS = 'http://www.w3.org/2000/svg';
  function sv(tag, attrs) {
    var e = document.createElementNS(SVGNS, tag);
    if (attrs) for (var k in attrs) if (attrs[k] !== null && attrs[k] !== undefined) e.setAttribute(k, String(attrs[k]));
    for (var i = 2; i < arguments.length; i++) {
      var c = arguments[i];
      if (c === null || c === undefined) continue;
      if (Array.isArray(c)) c.forEach(function (x) { if (x) e.appendChild(x); });
      else if (typeof c === 'string') e.appendChild(document.createTextNode(c));
      else e.appendChild(c);
    }
    return e;
  }

  // Icons: fixed strings from this file only (never data)
  var ICONS = {
    overview: '<path d="M4 4h7v7H4zM13 4h7v4h-7zM13 10h7v10h-7zM4 13h7v7H4z"/>',
    users: '<circle cx="9" cy="8" r="3.2"/><path d="M3 20c.6-3.6 3-5.5 6-5.5s5.4 1.9 6 5.5"/><path d="M16 4.8a3 3 0 0 1 0 6.1M18 14.8c1.6.7 2.6 2.4 3 5.2"/>',
    problems: '<path d="M12 3 2.5 20h19z"/><path d="M12 10v4.5M12 17.2v.3"/>',
    messages: '<path d="M4 5h16v11H9l-5 4z"/>',
    versions: '<path d="m12 3 9 5-9 5-9-5z"/><path d="m3 13 9 5 9-5"/>',
    log: '<path d="M8 6h12M8 12h12M8 18h12M4 6h.01M4 12h.01M4 18h.01"/>',
    refresh: '<path d="M20 11a8 8 0 1 0-2.3 5.7"/><path d="M20 4v7h-7"/>',
    out: '<path d="M14 4h5v16h-5"/><path d="M10 8l-4 4 4 4M6 12h10"/>',
    shield: '<path d="M12 3 5 6v5c0 4.5 3 8.3 7 10 4-1.7 7-5.5 7-10V6z"/><path d="m9 12 2 2 4-4"/>',
    copy: '<rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V5a1 1 0 0 0-1-1H5a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h3"/>',
    back: '<path d="M15 5l-7 7 7 7"/>',
    globe: '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c2.5 2.6 3.8 5.6 3.8 9s-1.3 6.4-3.8 9c-2.5-2.6-3.8-5.6-3.8-9S9.5 5.6 12 3z"/>'
  };
  function icon(name) {
    var s = el('span', { 'aria-hidden': 'true', class: 'ic' });
    s.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">' + ICONS[name] + '</svg>';
    return s.firstChild;
  }

  function toast(text) {
    var t = el('div', { class: 'toast', role: 'status', text: text });
    document.body.appendChild(t);
    setTimeout(function () { t.remove(); }, 1800);
  }

  function copyText(text) {
    function fallback() {
      var ta = el('textarea', { style: 'position:fixed;top:-1000px' });
      ta.value = text;
      document.body.appendChild(ta);
      ta.select();
      try { document.execCommand('copy'); } catch (e) { /* nothing more to try */ }
      ta.remove();
    }
    if (navigator.clipboard && window.isSecureContext) navigator.clipboard.writeText(text).catch(fallback);
    else fallback();
    toast(tt('تم النسخ', 'Copied'));
  }

  // ================================================================== numbers, times (Western digits always: 'en-US')

  function fmtN(n) {
    if (n === null || n === undefined || isNaN(n)) return '-';
    return Number(n).toLocaleString('en-US', { maximumFractionDigits: 1 });
  }
  function fmtPct(v) {
    if (v === null || v === undefined || isNaN(v)) return '-';
    if (v <= 0) return '0%';
    if (v < 1) return '<1%';
    if (v >= 99.5 && v < 100) return '>99%';
    return Math.round(v).toLocaleString('en-US') + '%';
  }
  function pad(n) { return (n < 10 ? '0' : '') + n; }
  function toDate(v) { return v instanceof Date ? v : new Date(v); }
  function fmtTime(d) { d = toDate(d); return pad(d.getHours()) + ':' + pad(d.getMinutes()); }
  function fmtDay(d) { d = toDate(d); return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()); }
  function fmtStamp(d) {
    d = toDate(d);
    var now = new Date();
    if (fmtDay(d) === fmtDay(now)) return fmtTime(d);
    if (d.getFullYear() === now.getFullYear()) return pad(d.getMonth() + 1) + '-' + pad(d.getDate()) + ' ' + fmtTime(d);
    return fmtDay(d) + ' ' + fmtTime(d);
  }
  function fmtDuration(s) {
    if (s === null || s === undefined || isNaN(s)) return '-';
    s = Math.round(s);
    return Math.floor(s / 60) + ':' + pad(s % 60);
  }

  // Arabic counted nouns: 1 alone, 2 the dual alone, 3..10 the plural, the rest the singular ("15 لاعب")
  function arCount(n, one, few, two, oneWord) {
    var a = Math.abs(n), m = a % 100;
    if (a === 1 && oneWord) return oneWord;
    if (a === 2 && two) return two;
    return fmtN(n) + ' ' + (m >= 3 && m <= 10 ? few : one);
  }
  function enCount(n, one, many) { return fmtN(n) + ' ' + (n === 1 ? one : many); }
  function times(n) { return tt(arCount(n, 'مرة', 'مرات', 'مرتين', 'مرة وحدة'), enCount(n, 'time', 'times')); }
  function players(n) { return tt(arCount(n, 'لاعب', 'لاعبين'), enCount(n, 'player', 'players')); }

  // the server's clock (from web_installs), so "3 minutes ago" is right even when this device's clock is off
  var skewMs = 0;
  function nowMs() { return Date.now() + skewMs; }

  function ago(v) {
    if (!v) return '-';
    var s = Math.max(0, (nowMs() - toDate(v).getTime()) / 1000);
    if (s < 45) return tt('دحين', 'just now');
    var m = Math.round(s / 60);
    if (m < 60) return tt('قبل ' + arCount(m, 'دقيقة', 'دقايق', 'دقيقتين', 'دقيقة'), m + ' min ago');
    var h = Math.round(s / 3600);
    if (h < 24) return tt('قبل ' + arCount(h, 'ساعة', 'ساعات', 'ساعتين', 'ساعة'), h + ' h ago');
    var d = Math.round(s / 86400);
    if (d === 1) return tt('أمس', 'yesterday');
    if (d < 31) return tt('قبل ' + arCount(d, 'يوم', 'أيام', 'يومين'), d + ' days ago');
    return fmtDay(v);
  }

  function isOnline(lastSeen) { return lastSeen && nowMs() - toDate(lastSeen).getTime() < ONLINE_WINDOW_S * 1000; }

  function ver(s) {
    var m = /(\d+)\.(\d+)\.(\d+)/.exec(s || '');
    return m ? [+m[1], +m[2], +m[3]] : [0, 0, 0];
  }
  function verCmp(a, b) {
    var x = ver(a), y = ver(b);
    for (var i = 0; i < 3; i++) if (x[i] !== y[i]) return x[i] - y[i];
    return 0;
  }

  // ================================================================== words

  var ISSUES = {
    lock_outside: ['لعب على سيرفر برا التثبيت', 'Played on a server outside the lock'],
    unknown_server: ['سيرفر اللعبة مو في أي قائمة داتا سنتر', 'Game server not in any datacenter list'],
    lock_rules_missing: ['التثبيت شغال بس قواعد الجدار الناري راحت', 'Lock is on but its firewall rules are gone'],
    lock_apply_failed: ['ما قدر يضيف قواعد الجدار الناري', "Couldn't add the firewall rules"],
    firewall_off: ['جدار حماية ويندوز مطفي، التثبيت ما يشتغل', "Windows Firewall is off, locks can't work"],
    exe_not_found: ['ما لقى ملف اللعبة للتثبيت', "Couldn't find the game's exe for the lock"],
    queue_long: ['مثبّت وما لقى مباراة من زمان', 'Locked and no match found for a long time'],
    region_down: ['داتا سنترات ما ترد', "Datacenters that don't answer"],
    all_down: ['ولا سيرفر رد على اختبار البنق', 'No server answered the ping test'],
    servers_load_failed: ['قائمة السيرفرات ما حمّلت', "Server list didn't load"],
    sdr_new_pop: ['فالف عندها داتا سنتر جدول ديدلوك ما يعرفه', "Valve lists a datacenter the Deadlock table doesn't know"],
    ip_lists_failed: ['قائمة العناوين الرسمية ما نزلت', "Official address list didn't download"],
    detect_failed: ['التقاط الترافك (pktmon) فشل', 'Traffic capture (pktmon) failed'],
    leftover_lock: ['قواعد تثبيت باقية من جلسة قبل', 'Lock rules left from a previous session'],
    leftover_tunnel: ['خدمة نفق الريلاي باقية من جلسة قبل', 'Relay tunnel service left from a previous session'],
    unlock_failed: ['القواعد لسا موجودة بعد فك التثبيت', 'Firewall rules still there after unlocking'],
    connection_loss: ['فقدان باكتات كثير وهو يلعب', 'Heavy packet loss while playing'],
    slow_startup: ['البرنامج طوّل لين فتح', 'The app took long to open'],
    update_failed: ['التحديث التلقائي فشل', 'Self-update failed'],
    crash: ['خطأ غير متوقع', 'Unexpected error']
  };
  var BAD_ISSUES = { crash: 1, lock_outside: 1, lock_rules_missing: 1 };
  function issueName(code) { var w = ISSUES[code]; return w ? tt(w[0], w[1]) : code; }

  var KINDS = {
    match: ['مباراة', 'Match'], issue: ['مشكلة', 'Problem'], error: ['خطأ', 'Error'], feedback: ['رسالة', 'Message'],
    settings: ['إعدادات', 'Settings'], lock: ['تثبيت', 'Lock'], unlock: ['فك التثبيت', 'Unlock'], test: ['اختبار بنق', 'Ping test'],
    detect: ['كشف السيرفر', 'Server check'], start: ['فتح البرنامج', 'App opened'], exit: ['قفل البرنامج', 'App closed'],
    use: ['استخدام', 'Used'], update: ['تحديث', 'Update'], locktest: ['اختبار تثبيت', 'Lock test'], traffic: ['ترافك', 'Traffic'],
    boost: ['تسريع', 'Boost'], reset: ['إعادة ضبط', 'Reset']
  };
  function kindName(k) { var w = KINDS[k]; return w ? tt(w[0], w[1]) : k; }

  var MSG_TYPES = { problem: ['مشكلة', 'Problem'], idea: ['فكرة', 'Idea'], question: ['سؤال', 'Question'] };
  function msgType(k) { var w = MSG_TYPES[k] || MSG_TYPES.problem; return tt(w[0], w[1]); }

  var VERDICTS = {
    inside: ['داخل التثبيت', 'Inside the lock', 'var(--aqua)'], outside: ['برا التثبيت', 'Outside the lock', 'var(--red)'],
    outside_gateway: ['برا عبر بوابة مفتوحة', 'Outside via kept gateway', 'var(--gold)'],
    nolock: ['بدون تثبيت', 'No lock', 'var(--lav)'], unknown: ['ما ندري', "Can't tell", 'var(--faint)']
  };
  function verdict(k) { var w = VERDICTS[k] || VERDICTS.unknown; return { name: tt(w[0], w[1]), color: w[2] }; }

  var REGIONS = { Europe: ['أوروبا', 'Europe'], SEAsia: ['جنوب شرق آسيا', 'SE Asia'], Row: ['أمريكا الشمالية', 'North America'],
                  SAmerica: ['أمريكا الجنوبية', 'South America'], Oceania: ['أوقيانوسيا', 'Oceania'], Russia: ['روسيا', 'Russia'],
                  '(none)': ['(بدون تثبيت)', '(no lock)'] };
  function regionName(k) { var w = REGIONS[k]; return w ? tt(w[0], w[1]) : k; }

  function anonTag() { return el('span', { class: 'tag anon', text: tt('مجهول', 'Anonymous') }); }
  function displayName(r) { return r.anonymous ? tt('مجهول', 'Anonymous') : (r.name || '?'); }
  function place(r) { return [r.city, r.country].filter(Boolean).join(', '); }

  // ================================================================== data

  var state = {
    days: +(store('txp-dev-days') || 30),
    issueDays: 7,
    route: { view: 'overview' },
    data: {},           // stats_<days>, installs, user_<id>, issues_<days>, feedback, versions, log
    loadedAt: {},
    updated: null,
    busy: false,
    error: null,
    blocked: false      // 'not allowed': no automatic retries until Refresh
  };
  if ([7, 30, 90].indexOf(state.days) < 0) state.days = 30;

  function call(fn, args) {
    if (DEMO) return window.TXP_DEMO[fn](args || {});
    return sb.rpc(fn, args || {}).then(function (r) {
      if (r.error) throw r.error;
      return r.data;
    });
  }

  var LOADERS = {
    stats: function () { return call('web_stats', { p_days: state.days, p_tz_minutes: -new Date().getTimezoneOffset() }); },
    installs: function () {
      return call('web_installs').then(function (d) {
        if (d && d.now) skewMs = new Date(d.now).getTime() - Date.now();
        return d;
      });
    },
    user: function (id) { return call('web_install', { p_id: id }); },
    issues: function () { return call('web_issues', { p_days: state.issueDays }); },
    feedback: function () { return call('web_feedback', { p_days: 365 }); },
    versions: function () { return call('web_versions'); },
    log: function () { return call('web_log', { p_limit: 300 }); }
  };

  function key(name) {
    if (name === 'stats') return 'stats_' + state.days;
    if (name === 'issues') return 'issues_' + state.issueDays;
    if (name === 'user') return 'user_' + state.route.id;
    return name;
  }
  function get(name) { return state.data[key(name)]; }

  function needs(view) {
    switch (view) {
      case 'overview': return ['stats'];
      case 'users': return ['installs'];
      case 'user': return ['user', 'installs'];
      case 'problems': return ['issues'];
      case 'messages': return ['feedback'];
      case 'versions': return ['versions'];
      case 'log': return ['log', 'installs'];
    }
    return [];
  }

  // What the error means, in plain words, and what to do.
  function explain(err) {
    var code = (err && err.code) || '', msg = String((err && (err.message || err.error_description)) || err || '');
    if (code === '42501' || /not allowed/i.test(msg)) return { kind: 'notallowed' };
    if (code === 'PGRST202' || /could not find the function/i.test(msg))
      return { kind: 'nosql', text: tt('دوال الويب مو موجودة في قاعدة البيانات لسا: شغّل tools/supabase-web-dashboard.sql في Supabase (الخطوة 3 في docs/WEB-DASHBOARD.md).',
                                        "The web_* functions aren't in the database yet: run tools/supabase-web-dashboard.sql in Supabase (step 3 in docs/WEB-DASHBOARD.md).") };
    if (/JWT|session|refresh token/i.test(msg) || code === 'PGRST301' || code === 'PGRST303')
      return { kind: 'session', text: tt('الجلسة انتهت، سجّل دخول مرة ثانية.', 'Your session ended, sign in again.') };
    if (err instanceof TypeError || /failed to fetch|networkerror|load failed|network/i.test(msg))
      return { kind: 'network', text: tt('ما فيه اتصال بـ Supabase. تأكد من النت واضغط حدّث.', 'No connection to Supabase. Check the internet and press Refresh.') };
    return { kind: 'other', text: tt('صار خطأ: ', 'Something failed: ') + (msg || code || '?') };
  }

  var refreshSeq = 0;
  function refresh(manual, force) {
    if (state.busy && !manual && !force) return Promise.resolve();
    if (manual) state.blocked = false;
    if (state.blocked) return Promise.resolve();
    var seq = ++refreshSeq;
    var r = state.route;
    var jobs = needs(r.view).filter(function (n) {
      // the installs list only rides along with a user or the log every 2 minutes
      if (n === 'installs' && r.view !== 'users' && get('installs') && Date.now() - (state.loadedAt.installs || 0) < 120000 && !manual) return false;
      return true;
    });
    if (!get('feedback') || (r.view !== 'messages' && Date.now() - (state.loadedAt.feedback || 0) > 300000)) {
      if (jobs.indexOf('feedback') < 0) jobs.push('feedback');
    }
    setBusy(true);
    return Promise.all(jobs.map(function (n) {
      var k = key(n);
      return LOADERS[n](r.id).then(function (d) { state.data[k] = d; state.loadedAt[k] = Date.now(); });
    })).then(function () {
      if (seq !== refreshSeq) return;
      state.updated = new Date();
      state.error = null;
    }, function (err) {
      if (seq !== refreshSeq) return;
      var e = explain(err);
      if (e.kind === 'session') { signOut(e.text); return; }
      if (e.kind === 'notallowed') state.blocked = true;
      state.error = e;
    }).then(function () {
      if (seq !== refreshSeq) return;
      setBusy(false);
      updateChrome();
      if (view && view.update) view.update();
    });
  }

  setInterval(function () {
    if (document.visibilityState === 'visible' && shell) refresh(false);
  }, REFRESH_MS);
  document.addEventListener('visibilitychange', function () {
    if (document.visibilityState === 'visible' && shell && (!state.updated || Date.now() - state.updated.getTime() > REFRESH_MS)) refresh(false);
  });

  // ================================================================== read marks for messages (this browser only)

  var readIds = (function () {
    try { return JSON.parse(store('txp-dev-read') || '[]'); } catch (e) { return []; }
  })();
  function isRead(id) { return readIds.indexOf(String(id)) >= 0; }
  function setRead(id, on) {
    id = String(id);
    var i = readIds.indexOf(id);
    if (on && i < 0) readIds.push(id);
    if (!on && i >= 0) readIds.splice(i, 1);
    if (readIds.length > 3000) readIds = readIds.slice(-3000);
    store('txp-dev-read', JSON.stringify(readIds));
    updateChrome();
  }
  function unreadCount() {
    var f = state.data.feedback;
    return f ? f.filter(function (m) { return !isRead(m.id); }).length : 0;
  }

  // ================================================================== sign in

  function applyLang() {
    document.documentElement.lang = lang;
    document.documentElement.dir = lang === 'ar' ? 'rtl' : 'ltr';
    document.title = tt('2XPing · لوحة المطور', '2XPing · Developer');
  }

  function langButton(cls) {
    return el('button', {
      class: 'btn ghost ' + (cls || ''), type: 'button', title: tt('English', 'عربي'),
      onclick: function () {
        lang = lang === 'ar' ? 'en' : 'ar';
        store('txp-dev-lang', lang);
        applyLang();
        rerender();
      }
    }, lang === 'ar' ? 'EN' : 'ع');
  }

  var screen = null; // 'login' | 'mfa' | 'app'
  var loginMessage = null;

  function rerender() {
    if (screen === 'app') { shell = null; startApp(); }
    else if (screen === 'mfa') showMfa();
    else showLogin(loginMessage);
  }

  function authFrame(title, lead, body) {
    root.textContent = '';
    shell = null; view = null;
    root.appendChild(el('div', { class: 'center' },
      el('div', { class: 'auth' },
        el('div', { class: 'row', style: 'margin:0 0 18px' },
          el('div', { class: 'logo', style: 'margin:0' }, el('img', { src: '../site/logo-192.png', alt: '' }), '2XPing'),
          langButton('small')),
        el('div', { class: 'card', style: 'padding:22px' },
          el('h1', { text: title }), lead ? el('p', { class: 'lead', text: lead }) : null, body))));
  }

  function showLogin(message) {
    screen = 'login';
    loginMessage = message || null;
    var err = el('div', { class: 'msg err', role: 'alert', hidden: !message }, message || '');
    var email = el('input', { type: 'email', autocomplete: 'username', required: true, dir: 'ltr', spellcheck: 'false' });
    var pass = el('input', { type: 'password', autocomplete: 'current-password', required: true, dir: 'ltr' });
    var go = el('button', { class: 'btn primary', type: 'submit' }, tt('ادخل', 'Sign in'));
    var form = el('form', {
      novalidate: true,
      onsubmit: function (e) {
        e.preventDefault();
        if (!email.value.trim() || !pass.value) { err.hidden = false; err.textContent = tt('اكتب الإيميل وكلمة السر.', 'Type the e-mail and the password.'); return; }
        go.disabled = true;
        err.hidden = true;
        sb.auth.signInWithPassword({ email: email.value.trim(), password: pass.value }).then(function (r) {
          pass.value = '';
          if (r.error) throw r.error;
          return afterSignIn();
        }).catch(function (e2) {
          go.disabled = false;
          err.hidden = false;
          err.textContent = signInError(e2);
        });
      }
    },
      err,
      el('label', { class: 'field' }, el('span', { text: tt('الإيميل', 'E-mail') }), email),
      el('label', { class: 'field' }, el('span', { text: tt('كلمة السر', 'Password') }), pass),
      go);
    authFrame(tt('لوحة المطور', 'Developer dashboard'), tt('خاصة بصاحب البرنامج. الحساب ينضاف من Supabase بس.', "For the app's owner only. Accounts are added in Supabase only."), form);
    if (!sb) { go.disabled = true; }
    setTimeout(function () { email.focus(); }, 0);
  }

  function signInError(e) {
    var code = (e && e.code) || '', msg = String((e && e.message) || e || ''), status = e && e.status;
    if (code === 'invalid_credentials' || /invalid login credentials/i.test(msg))
      return tt('الإيميل أو كلمة السر غلط.', 'Wrong e-mail or password.');
    if (code === 'email_not_confirmed' || /not confirmed/i.test(msg))
      return tt('الإيميل مو مأكد. في Supabase: Authentication > Users، أكده أو أضف المستخدم مع Auto Confirm.', 'The e-mail is not confirmed. In Supabase: Authentication > Users, confirm it or add the user with Auto Confirm.');
    if (status === 429 || /rate limit|too many/i.test(msg))
      return tt('محاولات كثير. استنى شوي وجرب مرة ثانية.', 'Too many tries. Wait a little and try again.');
    if (e instanceof TypeError || /failed to fetch|network/i.test(msg))
      return tt('ما فيه اتصال بـ Supabase. تأكد من النت.', 'No connection to Supabase. Check the internet.');
    return tt('ما قدرنا ندخلك: ', "Couldn't sign in: ") + (msg || code);
  }

  function afterSignIn() {
    return sb.auth.mfa.getAuthenticatorAssuranceLevel().then(function (r) {
      var a = r && r.data;
      if (a && a.nextLevel === 'aal2' && a.currentLevel !== 'aal2') showMfa();
      else startApp();
    });
  }

  function showMfa() {
    screen = 'mfa';
    var err = el('div', { class: 'msg err', role: 'alert', hidden: true });
    var code = el('input', { type: 'text', inputmode: 'numeric', autocomplete: 'one-time-code', maxlength: 6, dir: 'ltr', pattern: '[0-9]*' });
    var go = el('button', { class: 'btn primary', type: 'submit' }, tt('تأكيد', 'Verify'));
    var form = el('form', {
      novalidate: true,
      onsubmit: function (e) {
        e.preventDefault();
        go.disabled = true;
        err.hidden = true;
        sb.auth.mfa.listFactors().then(function (r) {
          if (r.error) throw r.error;
          var f = (r.data.totp || [])[0];
          if (!f) throw new Error('no factor');
          return sb.auth.mfa.challengeAndVerify({ factorId: f.id, code: code.value.replace(/\D/g, '') });
        }).then(function (r) {
          if (r.error) throw r.error;
          startApp();
        }).catch(function (e2) {
          go.disabled = false;
          err.hidden = false;
          err.textContent = /invalid|code/i.test(String(e2 && e2.message)) ? tt('الرمز غلط أو انتهى، جرب الرمز الجديد.', 'Wrong or expired code, try the new one.') : signInError(e2);
        });
      }
    },
      err,
      el('label', { class: 'field' }, el('span', { text: tt('رمز تطبيق التحقق (6 أرقام)', 'Code from the authenticator app (6 digits)') }), code),
      go,
      el('div', { class: 'row' }, el('button', { class: 'btn ghost small', type: 'button', onclick: function () { signOut(); } }, tt('اطلع', 'Sign out'))));
    authFrame(tt('التحقق بخطوتين', 'Two-step sign-in'), tt('افتح تطبيق التحقق واكتب الرمز.', 'Open your authenticator app and type the code.'), form);
    setTimeout(function () { code.focus(); }, 0);
  }

  function signOut(message) {
    var done = function () { state.data = {}; state.loadedAt = {}; state.updated = null; state.error = null; state.blocked = false; showLogin(message); };
    if (DEMO) { location.href = location.pathname; return; }
    sb.auth.signOut().then(done, done);
  }

  // Two-step sign-in (TOTP), set up from the header's shield button. Once a factor is verified, is_dev_owner() in the
  // database also asks every session to have passed it.
  function mfaDialog() {
    if (DEMO) { toast(tt('مو متاح في الوضع التجريبي', 'Not available in demo mode')); return; }
    var back = el('div', { class: 'modal-back', onclick: function (e) { if (e.target === back) close(); } });
    var box = el('div', { class: 'card modal', role: 'dialog', 'aria-modal': 'true' });
    back.appendChild(box);
    document.body.appendChild(back);
    var pending = null; // an enrolment not verified yet: removed when the dialog closes
    function close() {
      if (pending) sb.auth.mfa.unenroll({ factorId: pending }).catch(function () {});
      back.remove();
    }
    function fill() {
      box.textContent = '';
      box.appendChild(el('h2', { text: tt('التحقق بخطوتين', 'Two-step sign-in') }));
      box.appendChild(el('p', { text: tt('لحظة...', 'One moment...') }));
      sb.auth.mfa.listFactors().then(function (r) {
        if (r.error) throw r.error;
        var on = (r.data.totp || []).filter(function (f) { return f.status === 'verified'; })[0];
        box.textContent = '';
        box.appendChild(el('h2', { text: tt('التحقق بخطوتين', 'Two-step sign-in') }));
        if (on) {
          box.appendChild(el('p', { text: tt('شغّال. كل دخول يطلب رمز من تطبيق التحقق، والدوال في قاعدة البيانات ترفض أي جلسة ما مرت عليه.', 'On. Every sign-in asks for a code from the authenticator app, and the database functions refuse any session that has not passed it.') }));
          box.appendChild(el('div', { class: 'actions' },
            el('button', { class: 'btn', type: 'button', onclick: function () {
              if (!confirm(tt('أكيد تبي تطفي التحقق بخطوتين؟', 'Turn two-step sign-in off?'))) return;
              sb.auth.mfa.unenroll({ factorId: on.id }).then(function (x) { if (x.error) throw x.error; fill(); })
                .catch(function (e) { toast(String(e.message || e)); });
            } }, tt('طفّه', 'Turn off')),
            el('button', { class: 'btn primary', type: 'button', onclick: close }, tt('تمام', 'Done'))));
          return;
        }
        box.appendChild(el('p', { text: tt('امسح الرمز بتطبيق تحقق (مثل Google Authenticator أو 1Password)، واكتب الرمز اللي يطلع.', 'Scan the code with an authenticator app (such as Google Authenticator or 1Password), then type the code it shows.') }));
        var start = el('button', { class: 'btn primary', type: 'button', onclick: function () {
          start.disabled = true;
          sb.auth.mfa.enroll({ factorType: 'totp', friendlyName: '2XPing dev ' + Date.now() }).then(function (x) {
            if (x.error) throw x.error;
            pending = x.data.id;
            start.remove();
            var codeIn = el('input', { type: 'text', inputmode: 'numeric', autocomplete: 'one-time-code', maxlength: 6, dir: 'ltr' });
            var msg = el('div', { class: 'msg err', hidden: true });
            box.appendChild(el('div', { class: 'qr' }, el('img', { src: x.data.totp.qr_code, alt: 'QR' })));
            box.appendChild(el('p', { class: 'mono ltr', style: 'text-align:center;overflow-wrap:anywhere', text: x.data.totp.secret }));
            box.appendChild(msg);
            box.appendChild(el('label', { class: 'field' }, el('span', { text: tt('الرمز', 'Code') }), codeIn));
            box.appendChild(el('div', { class: 'actions' },
              el('button', { class: 'btn', type: 'button', onclick: close }, tt('إلغاء', 'Cancel')),
              el('button', { class: 'btn primary', type: 'button', onclick: function () {
                sb.auth.mfa.challengeAndVerify({ factorId: pending, code: codeIn.value.replace(/\D/g, '') }).then(function (v) {
                  if (v.error) throw v.error;
                  pending = null;
                  toast(tt('تم، التحقق بخطوتين شغّال', 'Done, two-step sign-in is on'));
                  fill();
                }).catch(function () { msg.hidden = false; msg.textContent = tt('الرمز غلط، جرب مرة ثانية.', 'Wrong code, try again.'); });
              } }, tt('شغّله', 'Turn on'))));
            setTimeout(function () { codeIn.focus(); }, 0);
          }).catch(function (e) { start.disabled = false; toast(String(e.message || e)); });
        } }, tt('ابدأ', 'Start'));
        box.appendChild(el('div', { class: 'actions' }, el('button', { class: 'btn', type: 'button', onclick: close }, tt('إلغاء', 'Cancel')), start));
      }).catch(function (e) {
        box.appendChild(el('div', { class: 'msg err', text: String(e.message || e) }));
      });
    }
    fill();
  }

  // ================================================================== app shell

  var shell = null, view = null;
  var NAV = [
    ['overview', 'overview', ['نظرة عامة', 'Overview']],
    ['users', 'users', ['المستخدمين', 'Users']],
    ['problems', 'problems', ['المشاكل', 'Problems']],
    ['messages', 'messages', ['الرسائل', 'Messages']],
    ['versions', 'versions', ['النسخ', 'Versions']],
    ['log', 'log', ['السجل', 'Log']]
  ];

  function parseRoute() {
    var h = (location.hash || '').replace(/^#\/?/, '').split('/');
    if (h[0] === 'user' && /^[0-9a-f-]{36}$/i.test(h[1] || '')) return { view: 'user', id: h[1].toLowerCase() };
    for (var i = 0; i < NAV.length; i++) if (NAV[i][0] === h[0]) return { view: h[0] };
    return { view: 'overview' };
  }

  window.addEventListener('hashchange', function () {
    if (screen !== 'app') return;
    state.route = parseRoute();
    showView();
    refresh(false, true);
  });

  function startApp() {
    screen = 'app';
    root.textContent = '';
    state.route = parseRoute();
    var updated = el('span', { class: 'updated hide-sm' });
    var refreshBtn = el('button', { class: 'btn ghost', type: 'button', title: tt('حدّث', 'Refresh'), 'aria-label': tt('حدّث', 'Refresh'),
                                    onclick: function () { refresh(true); } }, icon('refresh'), el('span', { class: 'hide-sm', text: tt('حدّث', 'Refresh') }));
    var nav = el('nav', { class: 'side', 'aria-label': tt('الأقسام', 'Sections') });
    var navLinks = {};
    NAV.forEach(function (n) {
      var a = el('a', { href: '#/' + n[0] }, icon(n[1]), el('span', { text: tt(n[2][0], n[2][1]) }));
      if (n[0] === 'messages') a.appendChild(el('span', { class: 'badge', hidden: true }));
      navLinks[n[0]] = a;
      nav.appendChild(a);
    });
    var main = el('main', { id: 'main' });
    shell = { updated: updated, refreshBtn: refreshBtn, nav: navLinks, main: main };
    root.appendChild(el('header', { class: 'bar' },
      el('a', { class: 'brand', href: '#/overview' }, el('img', { src: '../site/logo-192.png', alt: '' }), '2XPing',
         el('small', { text: tt('لوحة المطور', 'Developer') })),
      DEMO ? el('span', { class: 'demo-flag' }, el('span', { class: 'long', text: tt('تجريبي: بيانات وهمية', 'Demo: made-up data') }),
                 el('span', { class: 'short', text: tt('تجريبي', 'Demo') })) : null,
      el('span', { class: 'grow' }),
      updated, refreshBtn,
      langButton(),
      el('button', { class: 'btn ghost', type: 'button', title: tt('التحقق بخطوتين', 'Two-step sign-in'), 'aria-label': tt('التحقق بخطوتين', 'Two-step sign-in'), onclick: mfaDialog }, icon('shield')),
      el('button', { class: 'btn ghost', type: 'button', title: tt('اطلع', 'Sign out'), 'aria-label': tt('اطلع', 'Sign out'), onclick: function () { signOut(); } },
         icon('out'), el('span', { class: 'hide-sm', text: tt('اطلع', 'Sign out') }))));
    root.appendChild(el('div', { class: 'layout' }, nav, main));
    showView();
    refresh(false);
  }

  function setBusy(b) {
    state.busy = b;
    if (!shell) return;
    shell.refreshBtn.disabled = b;
    shell.refreshBtn.classList.toggle('spin', b);
  }

  function updateChrome() {
    if (!shell) return;
    shell.updated.textContent = state.updated ? tt('آخر تحديث ', 'Updated ') + fmtTime(state.updated) : '';
    Object.keys(shell.nav).forEach(function (k) {
      var on = k === state.route.view || (k === 'users' && state.route.view === 'user');
      if (on) shell.nav[k].setAttribute('aria-current', 'page'); else shell.nav[k].removeAttribute('aria-current');
    });
    var badge = shell.nav.messages.querySelector('.badge');
    var n = unreadCount();
    badge.hidden = n === 0;
    badge.textContent = n > 99 ? '99+' : String(n);
    if (view && view.status) view.status();
  }

  function showView() {
    var V = VIEWS[state.route.view] || VIEWS.overview;
    shell.main.textContent = '';
    view = V(shell.main);
    updateChrome();
    if (view.update) view.update();
    window.scrollTo(0, 0);
  }

  // the page title row: title, extra controls, and the "updated" line
  function pageHead(main, title, controls) {
    var status = el('span', { class: 'sub' });
    var head = el('div', { class: 'pagehead' }, el('h1', { text: title }), status, el('span', { class: 'grow' }), controls || null);
    main.appendChild(head);
    var banner = el('div', { class: 'banner' });
    main.appendChild(banner);
    return {
      status: function () {
        status.textContent = state.updated ? tt('آخر تحديث ' + fmtTime(state.updated) + ' · يتحدث كل 30 ثانية', 'Updated ' + fmtTime(state.updated) + ' · refreshes every 30 s')
                                           : (state.busy ? tt('لحظة، نجيب البيانات...', 'Loading...') : '');
        banner.textContent = '';
        if (state.error) banner.appendChild(errorBox(state.error));
      }
    };
  }

  function errorBox(e) {
    if (e.kind === 'notallowed') {
      return el('div', { class: 'msg err', role: 'alert' },
        el('b', { text: tt('الحساب هذا ما عنده صلاحية (not allowed).', 'This account has no access (not allowed).') }), ' ',
        tt('الدخول نجح، بس الحساب مو مضاف في جدول dev_owners. شغّل سطر المالك في آخر tools/supabase-web-dashboard.sql بإيميلك: الخطوة 3 في docs/WEB-DASHBOARD.md. وإذا شغّلت التحقق بخطوتين، اطلع وادخل من جديد.',
           "Sign-in worked, but this account is not in dev_owners. Run the owner line at the end of tools/supabase-web-dashboard.sql with your e-mail: step 3 in docs/WEB-DASHBOARD.md. If you turned on two-step sign-in, sign out and in again."));
    }
    return el('div', { class: 'msg err', role: 'alert', text: e.text });
  }

  function seg(options, value, onpick) {
    var s = el('div', { class: 'seg', role: 'group' });
    options.forEach(function (o) {
      s.appendChild(el('button', { type: 'button', 'aria-pressed': String(o[0] === value), onclick: function () {
        Array.prototype.forEach.call(s.children, function (b) { b.setAttribute('aria-pressed', 'false'); });
        this.setAttribute('aria-pressed', 'true');
        onpick(o[0]);
      } }, o[1]));
    });
    return s;
  }

  function chip(label, pressed, onclick) {
    var b = el('button', { type: 'button', class: 'chipbtn', 'aria-pressed': String(!!pressed) }, label);
    b.addEventListener('click', function () {
      var on = b.getAttribute('aria-pressed') !== 'true';
      b.setAttribute('aria-pressed', String(on));
      onclick(on);
    });
    return b;
  }

  function emptyRow(text) { return el('div', { class: 'empty', text: text }); }
  function waiting() { return emptyRow(state.error ? tt('ما فيه بيانات.', 'No data.') : tt('لحظة، نجيب البيانات...', 'Loading...')); }

  // ================================================================== charts (plain SVG)

  var COLORS = ['#ff6fc8', '#68eccc', '#b69eff', '#ffc65c', '#78beff', '#ff9678', '#c88cff', '#96dc78'];

  function niceStep(max, ticks) {
    var raw = Math.max(1, max) / ticks, p = Math.pow(10, Math.floor(Math.log10(raw))), n = raw / p;
    var step = (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * p;
    return Math.max(1, step);
  }

  function legend(items) {
    return el('div', { class: 'legend' }, items.map(function (it) {
      var i = el('i');
      i.style.background = it.color;
      return el('span', null, i, it.name);
    }));
  }

  // series: [{name, color, values}], labels: x labels (days)
  // charts are drawn at about their shown width, so their text stays readable on a phone
  function chartW() { return Math.max(330, Math.min(640, (window.innerWidth || 640) - 66)); }

  function lineChart(series, labels) {
    var W = chartW(), H = W < 500 ? 190 : 210, L = 34, R = 10, T = 10, B = 26;
    var n = labels.length, max = 0;
    series.forEach(function (s) { s.values.forEach(function (v) { if (v > max) max = v; }); });
    var step = niceStep(max, 4), top = step * Math.max(1, Math.ceil(max / step));
    var x = function (i) { return L + (n <= 1 ? (W - L - R) / 2 : i * (W - L - R) / (n - 1)); };
    var y = function (v) { return T + (H - T - B) * (1 - v / top); };
    var g = sv('svg', { class: 'plot', viewBox: '0 0 ' + W + ' ' + H, role: 'img' });
    for (var v = 0; v <= top + 1e-9; v += step) {
      g.appendChild(sv('line', { class: 'grid', x1: L, x2: W - R, y1: y(v), y2: y(v) }));
      g.appendChild(sv('text', { x: L - 6, y: y(v) + 4, 'text-anchor': 'end' }, fmtN(v)));
    }
    var every = Math.max(1, Math.ceil(n / (W < 500 ? 4 : 7)));
    labels.forEach(function (lb, i) {
      if (i % every === 0 || i === n - 1) {
        if (i === n - 1 && i % every !== 0 && i - Math.floor(i / every) * every < every / 2) return;
        g.appendChild(sv('text', { x: x(i), y: H - 6, 'text-anchor': 'middle' }, lb.slice(5)));
      }
    });
    series.forEach(function (s, si) {
      var pts = s.values.map(function (v2, i) { return x(i) + ',' + y(v2); }).join(' ');
      if (si === 0 && n > 1) {
        g.appendChild(sv('polygon', { points: x(0) + ',' + y(0) + ' ' + pts + ' ' + x(n - 1) + ',' + y(0), fill: s.color, 'fill-opacity': '0.08' }));
      }
      g.appendChild(sv('polyline', { points: pts, fill: 'none', stroke: s.color, 'stroke-width': 2.2, 'stroke-linejoin': 'round', 'stroke-linecap': 'round' }));
      if (n <= 31) s.values.forEach(function (v2, i) { g.appendChild(sv('circle', { cx: x(i), cy: y(v2), r: 2.6, fill: s.color })); });
    });
    // hover: one invisible column per day with the numbers
    labels.forEach(function (lb, i) {
      var w = n <= 1 ? W - L - R : (W - L - R) / (n - 1);
      var tip = lb + ': ' + series.map(function (s) { return s.name + ' ' + fmtN(s.values[i]); }).join(', ');
      g.appendChild(sv('rect', { x: x(i) - w / 2, y: T, width: w, height: H - T - B, fill: 'transparent' }, sv('title', null, tip)));
    });
    return g;
  }

  // columns: [{label, value, color}]
  function columnChart(cols) {
    var W = chartW(), H = W < 500 ? 180 : 200, L = 30, R = 6, T = 12, B = 28;
    var max = Math.max.apply(null, cols.map(function (c) { return c.value; }).concat([0]));
    var step = niceStep(max, 4), top = step * Math.max(1, Math.ceil(max / step));
    var y = function (v) { return T + (H - T - B) * (1 - v / top); };
    var g = sv('svg', { class: 'plot', viewBox: '0 0 ' + W + ' ' + H, role: 'img' });
    for (var v = 0; v <= top + 1e-9; v += step) {
      g.appendChild(sv('line', { class: 'grid', x1: L, x2: W - R, y1: y(v), y2: y(v) }));
      g.appendChild(sv('text', { x: L - 6, y: y(v) + 4, 'text-anchor': 'end' }, fmtN(v)));
    }
    var cw = (W - L - R) / Math.max(1, cols.length);
    cols.forEach(function (c, i) {
      var bx = L + i * cw + cw * 0.16, bw = cw * 0.68, by = y(c.value);
      g.appendChild(sv('rect', { x: bx, y: by, width: bw, height: Math.max(0, y(0) - by), rx: 4, fill: c.color || '#b69eff' },
                       sv('title', null, c.label + ': ' + fmtN(c.value))));
      if (c.value > 0) g.appendChild(sv('text', { x: bx + bw / 2, y: by - 4, 'text-anchor': 'middle' }, fmtN(c.value)));
      g.appendChild(sv('text', { x: bx + bw / 2, y: H - 8, 'text-anchor': 'middle', 'font-size': W < 500 ? 9.5 : null }, c.label));
    });
    return g;
  }

  // horizontal bars: [{label, value, pct, text, color}]
  function hbars(items) {
    if (!items.length) return emptyRow(tt('ما فيه شي في هالفترة.', 'Nothing in this period.'));
    var max = Math.max.apply(null, items.map(function (i) { return i.pct !== undefined ? i.pct : i.value; }).concat([1]));
    return el('div', { class: 'hbars' }, items.map(function (it) {
      var f = el('div', { class: 'fill' });
      var v = it.pct !== undefined ? it.pct : it.value;
      f.style.width = Math.max(0, Math.min(100, 100 * v / (it.pct !== undefined ? 100 : max))) + '%';
      if (it.color) f.style.background = it.color;
      return el('div', { class: 'hbar' }, el('span', { class: 'lbl', dir: 'auto', title: it.label, text: it.label }),
                el('div', { class: 'track' }, f), el('span', { class: 'val', dir: 'auto', text: it.text }));
    }));
  }

  function chartCard(title, note, body, leg) {
    return el('section', { class: 'card chart' }, el('h3', { text: title }), note ? el('div', { class: 'note', text: note }) : null, leg || null, body);
  }

  // ================================================================== views

  var VIEWS = {};

  // ---------------------------------------------------------------- overview
  VIEWS.overview = function (main) {
    var head = pageHead(main, tt('نظرة عامة', 'Overview'),
      seg([[7, tt('7 أيام', '7 days')], [30, tt('30 يوم', '30 days')], [90, tt('90 يوم', '90 days')]], state.days, function (d) {
        state.days = d;
        store('txp-dev-days', String(d));
        update();
        refresh(false, true);
      }));
    var body = el('div');
    main.appendChild(body);

    function delta(now, prev, what, lowerIsBetter) {
      if (now === undefined || prev === undefined || now === null || prev === null) return null;
      var d = now - prev;
      var cls = d === 0 ? 'faint' : ((d > 0) !== !!lowerIsBetter ? 'good' : 'bad');
      var pct = prev > 0 ? Math.round(100 * d / prev) : null;
      var txt = (d > 0 ? '+' : d < 0 ? '-' : '±') + fmtN(Math.abs(d)) + (pct !== null && d !== 0 ? ' (' + (pct > 0 ? '+' : '') + pct + '%)' : '');
      return el('div', { class: 'd' }, el('b', { class: cls + ' num', text: '‎' + txt }), ' ' + what);
    }
    function tile(value, label, deltaEl, hot, sub) {
      return el('div', { class: 'card tile' + (hot ? ' hot' : '') }, el('div', { class: 'v num', text: fmtN(value) }),
                el('div', { class: 'k', text: label }), deltaEl || (sub ? el('div', { class: 'd', text: sub }) : null));
    }

    function update() {
      body.textContent = '';
      var s = get('stats');
      if (!s) { body.appendChild(waiting()); return; }
      var c = s.counters || {}, dl = s.deltas || {}, p = s.period || {}, days = s.days || state.days;
      var vsDay = tt('عن أمس لنفس الوقت', 'vs yesterday, same time');
      var vsWeek = tt('عن الأسبوع اللي قبله', 'vs the week before');
      var vsPeriod = tt('عن الفترة اللي قبلها', 'vs the period before');
      var inDays = days === 7 ? tt('آخر 7 أيام', 'last 7 days') : tt('آخر ' + days + ' يوم', 'last ' + days + ' days');
      var pv = function (k) { return p[k] || {}; };
      var anonPct = c.installs_total ? 100 * c.installs_anonymous / c.installs_total : 0;
      body.appendChild(el('div', { class: 'tiles' },
        tile(c.online_now, tt('متصلين دحين', 'Online now'), null, true, tt('من ' + fmtN(c.installs_total) + ' جهاز', 'of ' + fmtN(c.installs_total) + ' installs')),
        tile(c.active_today, tt('نشطين اليوم', 'Active today'), delta(c.active_today, dl.active_today !== undefined ? c.active_today - dl.active_today : undefined, vsDay)),
        tile(c.active_7d, tt('نشطين 7 أيام', 'Active 7 days'), delta(c.active_7d, dl.active_7d !== undefined ? c.active_7d - dl.active_7d : undefined, vsWeek)),
        tile(c.active_30d, tt('نشطين 30 يوم', 'Active 30 days'), null, false, tt('شافهم السيرفر آخر 30 يوم', 'seen in the last 30 days')),
        tile(pv('new').now, tt('جدد', 'New users') + ' · ' + inDays, delta(pv('new').now, pv('new').prev, vsPeriod)),
        tile(pv('matches').now, tt('مباريات', 'Matches') + ' · ' + inDays, delta(pv('matches').now, pv('matches').prev, vsPeriod)),
        tile(pv('locks').now, tt('تثبيتات', 'Locks') + ' · ' + inDays, delta(pv('locks').now, pv('locks').prev, vsPeriod)),
        tile(c.issues_24h, tt('مشاكل آخر 24 ساعة', 'Problems, 24 h'), delta(c.issues_24h, dl.issues_24h !== undefined ? c.issues_24h - dl.issues_24h : undefined, tt('عن الـ24 ساعة اللي قبلها', 'vs the 24 h before'), true)),
        tile(pv('feedback').now, tt('رسائل', 'Messages') + ' · ' + inDays, delta(pv('feedback').now, pv('feedback').prev, vsPeriod)),
        tile(c.installs_anonymous, tt('أجهزة مجهولة', 'Anonymous installs'), null, false, fmtPct(anonPct) + tt(' من كل الأجهزة', ' of all installs'))));

      var series = s.series || [];
      var labels = series.map(function (d) { return d.day; });
      var col = function (k) { return series.map(function (d) { return d[k] || 0; }); };

      var g1 = el('div', { class: 'grid2' });
      var su = [{ name: tt('نشطين', 'Active'), color: COLORS[0], values: col('active') }, { name: tt('جدد', 'New'), color: COLORS[1], values: col('new') }];
      g1.appendChild(chartCard(tt('النشطين والجدد كل يوم', 'Active and new installs per day'), null, lineChart(su, labels), legend(su)));
      var sm = [{ name: tt('مباريات', 'Matches'), color: COLORS[2], values: col('matches') }, { name: tt('تثبيتات', 'Locks'), color: COLORS[3], values: col('locks') }];
      g1.appendChild(chartCard(tt('المباريات والتثبيتات كل يوم', 'Matches and locks per day'), null, lineChart(sm, labels), legend(sm)));
      body.appendChild(g1);

      // ping: how far the real ping was from what the card expected (as the app's chart), and what online users see now
      var m = s.match || {}, ping = m.ping || {};
      var buckets = [['< -20', function (d) { return d < -20; }], ['-20..-10', function (d) { return d >= -20 && d < -10; }],
                     ['-10..-3', function (d) { return d >= -10 && d < -3; }], ['±3', function (d) { return d >= -3 && d <= 3; }],
                     ['3..10', function (d) { return d > 3 && d <= 10; }], ['10..20', function (d) { return d > 10 && d <= 20; }],
                     ['20..40', function (d) { return d > 20 && d <= 40; }], ['> 40', function (d) { return d > 40; }]];
      var pairs = ping.pairs || [];
      var diffCols = buckets.map(function (b, i) {
        return { label: b[0], value: pairs.filter(function (p2) { return b[1](p2[1] - p2[0]); }).length, color: i === 3 ? COLORS[1] : i < 3 ? COLORS[4] : COLORS[0] };
      });
      var g2 = el('div', { class: 'grid2' });
      g2.appendChild(chartCard(tt('البنق الحقيقي ناقص المتوقع (ms)', 'Actual minus expected ping (ms)'),
        ping.n ? tt('المتوقع ' + fmtN(ping.avg_expected) + ' · الحقيقي ' + fmtN(ping.avg_actual) + ' · الفرق الوسيط ' + fmtN(ping.median_diff) + ' ms · ' + fmtN(ping.n) + ' مباراة',
                    'Expected ' + fmtN(ping.avg_expected) + ' · actual ' + fmtN(ping.avg_actual) + ' · median gap ' + fmtN(ping.median_diff) + ' ms · ' + fmtN(ping.n) + ' matches')
               : tt('ما فيه مباريات فيها البنقين في هالفترة.', 'No matches with both pings in this period.'),
        columnChart(diffCols)));
      var nowPing = s.now_ping || [];
      var pb = [['<40', 0, 40], ['40-60', 40, 60], ['60-80', 60, 80], ['80-100', 80, 100], ['100-150', 100, 150], ['150+', 150, 1e9]];
      g2.appendChild(chartCard(tt('بنق المتصلين دحين (ms)', 'Ping of users online now (ms)'),
        nowPing.length ? tt(arCount(nowPing.length, 'جهاز', 'أجهزة', 'جهازين', 'جهاز واحد') + ' يرسلون بنق', nowPing.length + ' online installs reporting a ping')
                       : tt('ولا أحد متصل يرسل بنق دحين.', 'Nobody online is reporting a ping right now.'),
        columnChart(pb.map(function (b, i) {
          return { label: b[0], value: nowPing.filter(function (v) { return v >= b[1] && v < b[2]; }).length, color: i < 2 ? COLORS[1] : i < 4 ? COLORS[3] : COLORS[0] };
        }))));
      body.appendChild(g2);

      var g3 = el('div', { class: 'grid2' });
      var total = m.total || 0;
      g3.appendChild(chartCard(tt('التثبيت مسك؟ (تقارير المباريات)', 'Did the lock hold? (match reports)'),
        tt(arCount(total, 'مباراة', 'مباريات', 'مباراتين', 'مباراة وحدة'), enCount(total, 'match', 'matches')) + (m.dup_suspect ? tt(' · +' + m.dup_suspect + ' يمكن مكررة', ' · +' + m.dup_suspect + ' maybe repeats') : ''),
        hbars((m.verdict || []).map(function (v2) { var w = verdict(v2.k); return { label: w.name, pct: total ? 100 * v2.n / total : 0, text: fmtN(v2.n) + ' · ' + fmtPct(total ? 100 * v2.n / total : 0), color: w.color }; }))));
      g3.appendChild(chartCard(tt('داخل التثبيت حسب المنطقة', 'Inside the lock, per picked region'), tt('من المباريات اللي التثبيت قرر فيها', 'Of the matches the lock decided'),
        hbars((m.by_region || []).filter(function (r) { return r.inside + r.outside > 0; }).map(function (r) {
          var pc = 100 * r.inside / (r.inside + r.outside);
          return { label: regionName(r.region), pct: pc, text: fmtPct(pc) + tt(' من ', ' of ') + fmtN(r.inside + r.outside) + (r.queue_median_s !== null && r.queue_median_s !== undefined ? ' · ' + fmtDuration(r.queue_median_s) : ''), color: pc >= 90 ? 'var(--aqua)' : pc >= 70 ? 'var(--gold)' : 'var(--red)' };
        }))));
      body.appendChild(g3);

      var g4 = el('div', { class: 'grid2' });
      var sp = [{ name: tt('مشاكل', 'Problems'), color: COLORS[3], values: col('issues') }, { name: tt('أخطاء', 'Errors'), color: '#ff5c7a', values: col('errors') }];
      g4.appendChild(chartCard(tt('المشاكل والأخطاء كل يوم', 'Problems and errors per day'), null, lineChart(sp, labels), legend(sp)));
      var iss = s.issues || [];
      g4.appendChild(chartCard(tt('أكثر المشاكل، آخر 7 أيام', 'Top problems, last 7 days'), null,
        hbars(iss.map(function (i) { return { label: issueName(i.code), value: i.n, text: times(i.n) + ' · ' + players(i.players), color: BAD_ISSUES[i.code] ? 'var(--red)' : 'var(--gold)' }; }))));
      body.appendChild(g4);

      // who: the breakdowns of the period
      body.appendChild(el('h2', { class: 'section', text: tt('مين يستخدمه', 'Who uses it') + ' · ' + inDays }));
      var br = s.breakdowns || {};
      var words = { ar: tt('عربي', 'Arabic'), en: tt('إنجليزي', 'English'), simple: tt('بسيطة', 'Simple'), advanced: tt('متقدمة', 'Advanced'), '?': tt('ما ندري', 'unknown') };
      function bd(name, title, mapper) {
        var b = br[name] || { total: 0, items: [] };
        var items = (b.items || []).slice(0, 8).map(function (it, i) {
          var pc = b.total ? 100 * it.n / b.total : 0;
          return { label: mapper ? mapper(it.k) : (words[it.k] || it.k), pct: pc, text: fmtN(it.n) + ' · ' + fmtPct(pc), color: COLORS[i % COLORS.length] };
        });
        return chartCard(title, b.total ? tt(fmtN(b.total) + ' جهاز', fmtN(b.total) + ' installs') + (b.multi ? tt('، ممكن يزيد عن 100%', ', can add past 100%') : '') : null, hbars(items));
      }
      var g5 = el('div', { class: 'grid2' });
      g5.appendChild(bd('version', tt('نسخة البرنامج', 'App version')));
      g5.appendChild(bd('country', tt('الدولة', 'Country')));
      g5.appendChild(bd('game', tt('الألعاب المستخدمة', 'Games used')));
      g5.appendChild(bd('lock_region', tt('مناطق ديدلوك المختارة', 'Deadlock regions picked'), regionName));
      g5.appendChild(bd('lang', tt('اللغة', 'Language')));
      g5.appendChild(bd('mode', tt('الواجهة', 'Layout')));
      body.appendChild(g5);
    }
    return { update: update, status: head.status };
  };

  // ---------------------------------------------------------------- users
  function latestVersion() {
    var list = get('installs');
    var best = null;
    if (list && list.installs) {
      var weekAgo = nowMs() - 7 * 86400000;
      list.installs.forEach(function (r) {
        if (r.version && toDate(r.last_seen).getTime() > weekAgo && (!best || verCmp(r.version, best) > 0)) best = r.version;
      });
    }
    var vs = state.data.versions;
    if (!best && vs) vs.forEach(function (v) { if (v.active_7d > 0 && (!best || verCmp(v.version, best) > 0)) best = v.version; });
    return best;
  }
  function outdated(v, latest) { return latest && v && verCmp(v, latest) < 0; }

  var userUi = { q: '', online: false, anon: false, old: false, locked: false, sort: 'last_seen', dir: -1, limit: 200 };

  VIEWS.users = function (main) {
    var head = pageHead(main, tt('المستخدمين', 'Users'));
    var search = el('input', { type: 'search', class: 'search', placeholder: tt('دوّر بالاسم، المدينة، السيرفر، مزود النت...', 'Search name, city, server, ISP...'), value: userUi.q });
    search.addEventListener('input', function () { userUi.q = search.value; userUi.limit = 200; update(); });
    var chips = {
      online: chip(tt('متصلين بس', 'Online only'), userUi.online, function (on) { userUi.online = on; update(); }),
      anon: chip(tt('مجهولين', 'Anonymous'), userUi.anon, function (on) { userUi.anon = on; update(); }),
      old: chip(tt('نسخة قديمة', 'Outdated version'), userUi.old, function (on) { userUi.old = on; update(); }),
      locked: chip(tt('مثبّتين', 'Locked'), userUi.locked, function (on) { userUi.locked = on; update(); })
    };
    var SORTS = [['last_seen', tt('آخر ظهور', 'Last seen')], ['name', tt('الاسم', 'Name')], ['place', tt('المكان', 'Place')],
                 ['version', tt('النسخة', 'Version')], ['game', tt('اللعبة', 'Game')], ['ping', tt('البنق', 'Ping')], ['first_seen', tt('أول ظهور', 'First seen')]];
    var sortSel = el('select', { class: 'sortsel', 'aria-label': tt('الترتيب', 'Sort') }, SORTS.map(function (s) { return el('option', { value: s[0], selected: s[0] === userUi.sort }, tt('ترتيب: ', 'Sort: ') + s[1]); }));
    sortSel.addEventListener('change', function () { userUi.sort = sortSel.value; userUi.dir = sortSel.value === 'last_seen' || sortSel.value === 'first_seen' ? -1 : 1; update(); });
    main.appendChild(el('div', { class: 'toolbar' }, search, el('div', { class: 'chips' }, chips.online, chips.anon, chips.old, chips.locked), sortSel));
    var count = el('div', { class: 'sub2', style: 'margin:-4px 0 8px' });
    main.appendChild(count);
    var box = el('div', { class: 'card tablewrap' });
    main.appendChild(box);

    function sortValue(r, k) {
      switch (k) {
        case 'name': return (r.anonymous ? '￿' : '') + (r.name || '').toLowerCase();
        case 'place': return (place(r) || '￿').toLowerCase();
        case 'version': return ver(r.version).map(function (x) { return 1000 + x; }).join('.');
        case 'game': return (r.game || '￿').toLowerCase();
        case 'ping': return r.online && r.ping > 0 ? r.ping : 1e9;
        case 'first_seen': return toDate(r.first_seen).getTime();
        default: return toDate(r.last_seen).getTime();
      }
    }

    function update() {
      var d = get('installs');
      box.textContent = '';
      if (!d) { box.appendChild(waiting()); return; }
      var all = (d.installs || []).map(function (r) { r.online = isOnline(r.last_seen); r.locked = r.locked && r.online; return r; });
      var latest = latestVersion();
      var cnt = function (f) { return all.filter(f).length; };
      var isOld = function (r) { return outdated(r.version, latest) && toDate(r.last_seen).getTime() > nowMs() - 30 * 86400000; };
      setCount(chips.online, cnt(function (r) { return r.online; }));
      setCount(chips.anon, cnt(function (r) { return r.anonymous; }));
      setCount(chips.old, cnt(isOld));
      setCount(chips.locked, cnt(function (r) { return r.locked; }));
      var q = userUi.q.trim().toLowerCase();
      var rows = all.filter(function (r) {
        if (userUi.online && !r.online) return false;
        if (userUi.anon && !r.anonymous) return false;
        if (userUi.old && !isOld(r)) return false;
        if (userUi.locked && !r.locked) return false;
        if (!q) return true;
        return [r.name, r.device, r.city, r.country, r.isp, r.server, r.game, r.version, r.id, r.anonymous ? tt('مجهول', 'anonymous') : '']
          .some(function (x) { return x && String(x).toLowerCase().indexOf(q) >= 0; });
      });
      var k = userUi.sort, dir = userUi.dir;
      rows.sort(function (a, b) {
        var x = sortValue(a, k), y = sortValue(b, k);
        if (x < y) return -dir; if (x > y) return dir;
        return toDate(b.last_seen) - toDate(a.last_seen);
      });
      count.textContent = '';
      add(count, [tt('العدد: ', 'Showing: ') + fmtN(rows.length) + tt(' من ', ' of ') + fmtN(all.length),
                  latest ? [tt(' · آخر نسخة ', ' · latest version '), el('span', { class: 'num', text: latest })] : null]);
      if (!rows.length) { box.appendChild(emptyRow(all.length ? tt('ولا أحد يطابق البحث.', 'Nobody matches.') : tt('ما فيه مستخدمين لسا.', 'No users yet.'))); return; }

      function th(label, sk) {
        var a = el('th', { class: sk ? 'sortable' : null, scope: 'col' }, label, sk === userUi.sort ? el('span', { class: 'arrow', text: userUi.dir > 0 ? '▲' : '▼' }) : null);
        if (sk) a.addEventListener('click', function () {
          if (userUi.sort === sk) userUi.dir = -userUi.dir; else { userUi.sort = sk; userUi.dir = sk === 'last_seen' ? -1 : 1; }
          sortSel.value = userUi.sort;
          update();
        });
        return a;
      }
      var tb = el('tbody');
      rows.slice(0, userUi.limit).forEach(function (r) {
        var tr = el('tr', { class: 'click', tabindex: '0' },
          el('td', null, el('div', { class: 'who' }, el('span', { class: 'dot' + (r.online ? ' on' : ''), title: r.online ? tt('متصل', 'Online') : tt('مو متصل', 'Offline') }),
            el('div', { style: 'min-width:0' },
              el('div', { class: 'nm' }, r.anonymous ? anonTag() : el('span', { dir: 'auto', text: r.name || '?' }),
                 r.locked ? [' ', el('span', { class: 'tag lock', text: tt('مثبّت', 'Locked') })] : null),
              el('div', { class: 'sub2 ellip', dir: 'auto', text: r.anonymous ? r.id.slice(0, 8) : (r.device || '') })))),
          el('td', { 'data-k': tt('المكان', 'Place'), class: 'ellip', dir: 'auto', text: place(r) || '-' }),
          el('td', { 'data-k': tt('النسخة', 'Version') }, el('span', { class: 'num', text: r.version || '-' }), outdated(r.version, latest) ? [' ', el('span', { class: 'tag old', text: tt('قديمة', 'old') })] : null),
          el('td', { 'data-k': tt('اللعبة', 'Game'), text: r.online && r.game ? r.game : '-' }),
          el('td', { 'data-k': tt('السيرفر والبنق', 'Server, ping') }, r.online && (r.server || r.ping > 0) ? [el('span', { text: r.server || '' }), r.server && r.ping > 0 ? ' · ' : '', r.ping > 0 ? el('span', { class: 'faint num', text: r.ping + ' ms' }) : null] : '-'),
          el('td', { 'data-k': tt('آخر ظهور', 'Last seen'), title: fmtDay(r.last_seen) + ' ' + fmtTime(r.last_seen), text: r.online ? tt('متصل دحين', 'online now') : ago(r.last_seen) }));
        var open = function () { location.hash = '#/user/' + r.id; };
        tr.addEventListener('click', open);
        tr.addEventListener('keydown', function (e) { if (e.key === 'Enter') open(); });
        tb.appendChild(tr);
      });
      box.appendChild(el('table', { class: 'data cards' },
        el('thead', null, el('tr', null, th(tt('الاسم', 'Name'), 'name'), th(tt('المكان', 'Place'), 'place'), th(tt('النسخة', 'Version'), 'version'),
                                     th(tt('اللعبة', 'Game'), 'game'), th(tt('السيرفر والبنق', 'Server, ping'), 'ping'), th(tt('آخر ظهور', 'Last seen'), 'last_seen'))),
        tb));
      if (rows.length > userUi.limit) box.appendChild(el('div', { class: 'more' }, el('button', { class: 'btn small', type: 'button', onclick: function () { userUi.limit += 300; update(); } },
        tt('اعرض أكثر', 'Show more') + ' (' + fmtN(rows.length - userUi.limit) + ')')));
    }
    function setCount(b, n) {
      var c = b.querySelector('.count') || b.appendChild(el('span', { class: 'count num' }));
      c.textContent = fmtN(n);
    }
    return { update: update, status: head.status };
  };

  // ---------------------------------------------------------------- one user
  var userFilter = 'all';
  VIEWS.user = function (main) {
    main.appendChild(el('div', { class: 'back' }, el('a', { href: '#/users', class: 'btn ghost small' }, icon('back'), tt('كل المستخدمين', 'All users'))));
    var banner = el('div', { class: 'banner' });
    main.appendChild(banner);
    var top = el('div');
    var tlTools = el('div', { class: 'toolbar' });
    var tl = el('div', { class: 'card timeline' });
    main.appendChild(top);
    main.appendChild(el('h2', { class: 'section', text: tt('اللي صار مؤخرا', 'Recent activity') }));
    main.appendChild(tlTools);
    main.appendChild(tl);

    var FILTERS = [['all', tt('الكل', 'All')], ['match', tt('مباريات', 'Matches')], ['issue', tt('مشاكل وأخطاء', 'Problems, errors')],
                   ['feedback', tt('رسائل', 'Messages')], ['settings', tt('إعدادات', 'Settings')], ['lock', tt('تثبيت', 'Locks')], ['other', tt('غيرها', 'Other')]];
    function inFilter(e) {
      switch (userFilter) {
        case 'all': return true;
        case 'issue': return e.kind === 'issue' || e.kind === 'error';
        case 'lock': return e.kind === 'lock' || e.kind === 'unlock' || e.kind === 'locktest';
        case 'other': return ['match', 'issue', 'error', 'feedback', 'settings', 'lock', 'unlock', 'locktest'].indexOf(e.kind) < 0;
        default: return e.kind === userFilter;
      }
    }
    tlTools.appendChild(seg(FILTERS, userFilter, function (f) { userFilter = f; drawTimeline(); }));

    function fact(k, v, cls) {
      if (v === null || v === undefined || v === '') return null;
      return el('div', { class: 'fact' }, el('div', { class: 'k', text: k }), el('div', { class: 'v ' + (cls || ''), dir: 'auto' }, v));
    }

    function update() {
      banner.textContent = '';
      if (state.error) banner.appendChild(errorBox(state.error));
      var d = get('user');
      top.textContent = '';
      if (d === undefined) { top.appendChild(waiting()); tl.textContent = ''; return; }
      if (d === null) { top.appendChild(el('div', { class: 'card' }, emptyRow(tt('الجهاز هذا مو موجود.', 'This install was not found.')))); tl.textContent = ''; return; }
      var u = d.install, info = u.info || {}, online = isOnline(u.last_seen), latest = latestVersion();
      var lk = d.lock, locked = online && lk && lk.kind === 'lock';
      var net = info.net ? info.net + (info.net_mbps ? ' · ' + info.net_mbps + ' Mbps' : '') : null;
      var screenTxt = info.screen ? info.screen + (info.scale ? ' @' + info.scale + '%' : '') + (info.screens > 1 ? ' · ' + info.screens + tt(' شاشات', ' screens') : '') : null;
      var arr = function (v) { return Array.isArray(v) ? v.join(', ') : v; };
      top.appendChild(el('section', { class: 'card uhead' },
        el('div', { class: 'top' }, el('span', { class: 'dot' + (online ? ' on' : '') }),
          el('h1', { dir: 'auto', text: displayName(u) }), u.anonymous ? anonTag() : null,
          locked ? el('span', { class: 'tag lock', text: tt('مثبّت', 'Locked') }) : null,
          outdated(u.version, latest) ? el('span', { class: 'tag old', text: tt('نسخة قديمة', 'Outdated') }) : null,
          el('span', { class: 'grow' }),
          el('span', { class: 'dim', text: online ? tt('متصل دحين', 'Online now') : tt('آخر ظهور ', 'Last seen ') + ago(u.last_seen) })),
        u.anonymous ? el('div', { class: 'sub2', style: 'margin-top:6px', text: tt('مشاركة الإحصائيات مطفية عنده: ما يوصلنا اسم ولا مكان ولا اسم جهاز.', 'Statistics sharing is off: no name, place or PC name is sent.') }) : null,
        el('div', { class: 'facts' },
          online ? fact(tt('دحين', 'Now'), [u.game || '-', u.server ? ' · ' + u.server : '', u.ping > 0 ? ' · ' + u.ping + ' ms' : ''].join('')) : null,
          fact(tt('التثبيت', 'Lock'), locked ? tt('على ', 'on ') + (lk.server || '?') + tt(' من ', ' since ') + fmtStamp(lk.at) : tt('مو مثبّت', 'Not locked')),
          fact(tt('الجهاز', 'PC name'), u.device),
          fact(tt('المكان', 'Place'), place(u)),
          fact(tt('مزود النت', 'ISP'), u.isp),
          fact(tt('الشبكة', 'Network (AS)'), u.asn),
          fact(tt('النسخة', 'Version'), u.version),
          fact(tt('الويندوز', 'Windows'), info.windows || u.os),
          fact(tt('اللغة والواجهة', 'Language, layout'), [u.lang === 'ar' ? tt('عربي', 'Arabic') : u.lang === 'en' ? tt('إنجليزي', 'English') : u.lang, u.mode === 'simple' ? tt('بسيطة', 'simple') : u.mode === 'advanced' ? tt('متقدمة', 'advanced') : u.mode].filter(Boolean).join(' · ')),
          fact(tt('الاتصال', 'Connection'), net),
          fact(tt('المعالج', 'CPU'), info.cpu ? info.cpu + (info.cores ? ' · ' + info.cores + tt(' أنوية', ' cores') : '') : null),
          fact(tt('كرت الشاشة', 'GPU'), info.gpu),
          fact(tt('الرام', 'RAM'), info.ram_gb ? info.ram_gb + ' GB' : null),
          fact(tt('الشاشة', 'Screen'), screenTxt),
          fact(tt('الألعاب المثبتة', 'Games installed'), arr(info.games_installed)),
          fact(tt('أول ظهور', 'First seen'), fmtDay(u.first_seen) + ' · ' + ago(u.first_seen)),
          fact(tt('آخر ظهور', 'Last seen'), fmtStamp(u.last_seen)),
          fact(tt('رقم الجهاز', 'Install ID'), el('span', { class: 'mono ltr', text: u.id }))),
        el('div', { class: 'counts' }, Object.keys(d.counts || {}).sort(function (a, b) { return d.counts[b] - d.counts[a]; }).map(function (k) {
          return el('span', { class: 'tag' }, kindName(k) + ' ', el('span', { class: 'num', text: fmtN(d.counts[k]) }));
        }))));
      drawTimeline();
    }

    function drawTimeline() {
      tl.textContent = '';
      var d = get('user');
      if (!d) return;
      var evs = (d.events || []).filter(inFilter);
      if (!evs.length) { tl.appendChild(emptyRow(tt('ما فيه شي هنا.', 'Nothing here.'))); return; }
      evs.forEach(function (e) { tl.appendChild(eventRow(e)); });
    }
    return { update: update, status: function () {} };
  };

  function dataBlock(data) {
    if (!data || typeof data !== 'object') return null;
    var txt = data._trimmed ? tt('البيانات كبيرة (' + fmtN(data._bytes) + ' بايت) وما انعرضت.', 'Data too big (' + fmtN(data._bytes) + ' bytes), not shown.') : JSON.stringify(data, null, 2);
    return el('details', { class: 'raw' }, el('summary', { text: tt('البيانات', 'Data') + ' ▾' }), el('pre', { class: 'json', text: txt }));
  }

  function eventRow(e) {
    var d = e.data && typeof e.data === 'object' ? e.data : {};
    var line1 = [el('span', { class: 'kind ' + e.kind, text: kindName(e.kind) })];
    var extra = [];
    var text = e.detail || '';
    if (e.kind === 'match') {
      var v = verdict(d.verdict === 'outside' && Array.isArray(d.gateways_open) && d.gateways_open.indexOf(d.dc) >= 0 ? 'outside_gateway' : (d.verdict || 'unknown'));
      if (d.game) line1.push(el('span', { text: d.game }));
      if (d.dc || d.region) line1.push(el('span', { class: 'mono', text: [d.dc, d.region].filter(Boolean).join(' · ') }));
      var vt = el('span', { class: 'tag', text: v.name });
      vt.style.color = v.color;
      line1.push(vt);
      var ex = d.expected_ms, ac = d.actual_ms;
      var bits = [];
      if (ex > 0 || ac > 0) {
        var diff = ex > 0 && ac > 0 ? Math.round(ac - ex) : null;
        var pc = el('div', { class: 'pingcmp' },
          tt('المتوقع ', 'Expected '), el('b', { class: 'num', text: ex > 0 ? Math.round(ex) + ' ms' : '-' }), ' · ',
          tt('الحقيقي ', 'actual '), el('b', { class: 'num', text: ac > 0 ? Math.round(ac) + ' ms' : '-' }),
          diff !== null ? ' ' : null, diff !== null ? el('span', { class: 'num ' + (Math.abs(diff) <= 10 ? 'good' : diff > 20 ? 'bad' : 'warn'), text: '(' + (diff > 0 ? '+' : '') + diff + ')' }) : null);
        extra.push(pc);
      }
      if (d.queue_s !== null && d.queue_s !== undefined) bits.push(tt('الانتظار ', 'queue ') + fmtDuration(d.queue_s));
      if (Array.isArray(d.picked_regions) && d.picked_regions.length) bits.push(tt('المختار: ', 'picked: ') + d.picked_regions.map(regionName).join(', '));
      if (d.source) bits.push(tt('المصدر: ', 'source: ') + d.source);
      if (d.same_server_as_prev) bits.push(tt('يمكن مكررة', 'maybe a repeat'));
      if (bits.length) extra.push(el('div', { class: 'sub2', text: bits.join(' · ') }));
      text = '';
    } else if (e.kind === 'issue') {
      var code = d.code || (text.split(':')[0] || '');
      line1.push(el('span', { class: BAD_ISSUES[code] ? 'bad' : 'warn', text: issueName(code) }));
    } else if (e.kind === 'feedback') {
      line1.push(el('span', { class: 'tag ' + (d.type || 'problem'), text: msgType(d.type) }));
      if (d.contact) line1.push(el('span', { class: 'dim', dir: 'auto', text: d.contact }));
    } else if (e.kind === 'exit' && d.minutes !== undefined) {
      text = tt('الجلسة ' + fmtN(d.minutes) + ' دقيقة', 'Session ' + fmtN(d.minutes) + ' min');
    }
    if (e.kind !== 'match' && (e.server || e.ping > 0)) line1.push(el('span', { class: 'dim' }, e.server || '', e.server && e.ping > 0 ? ' · ' : '', e.ping > 0 ? el('span', { class: 'num', text: e.ping + ' ms' }) : null));
    if (e.kind !== 'match' && e.game && e.kind !== 'feedback') line1.push(el('span', { class: 'faint', text: e.game }));
    return el('div', { class: 'ev' },
      el('div', { class: 'when', title: fmtDay(e.at) + ' ' + fmtTime(e.at) }, el('b', { text: fmtStamp(e.at) }), ago(e.at)),
      el('div', { class: 'what' }, el('div', { class: 'line1' }, line1), extra, text ? el('div', { class: 'txt', dir: 'auto', text: text }) : null,
         e.kind !== 'feedback' ? dataBlock(e.data) : (d.diagnostics ? el('details', { class: 'raw' }, el('summary', { text: tt('التشخيص', 'Diagnostics') + ' ▾' }), el('pre', { class: 'json', text: d.diagnostics })) : null)));
  }

  // ---------------------------------------------------------------- problems
  VIEWS.problems = function (main) {
    var head = pageHead(main, tt('المشاكل', 'Problems'),
      seg([[1, tt('24 ساعة', '24 h')], [7, tt('7 أيام', '7 days')], [30, tt('30 يوم', '30 days')]], state.issueDays, function (d) {
        state.issueDays = d; update(); refresh(false, true);
      }));
    main.appendChild(el('p', { class: 'sub2', style: 'margin-top:-6px', text: tt('مشاكل البرنامج لقاها بنفسه وأرسلها، مجمعة حسب النوع. اضغط على وحدة تشوف آخر التفاصيل.', 'Problems the app found and reported by itself, grouped by kind. Open one for the latest details.') }));
    var body = el('div');
    main.appendChild(body);
    var open = {};
    function update() {
      body.textContent = '';
      var list = get('issues');
      if (!list) { body.appendChild(waiting()); return; }
      if (!list.length) { body.appendChild(el('div', { class: 'card' }, emptyRow(tt('ما فيه مشاكل في هالفترة.', 'No problems in this period.')))); return; }
      list.forEach(function (g) {
        var det = el('details', { class: 'card group' + (BAD_ISSUES[g.code] ? ' sev-bad' : ''), open: !!open[g.code] });
        det.addEventListener('toggle', function () { open[g.code] = det.open; });
        det.appendChild(el('summary', null,
          el('div', null, el('div', { class: 'title', text: issueName(g.code) }),
             el('div', { class: 'code' }, el('span', { class: 'mono', text: g.code }), ' · ', tt('آخر مرة ', 'last ') + ago(g.last_at),
                (g.versions && g.versions.length) ? [' · ', el('span', { class: 'num', text: g.versions.slice(0, 3).join(', ') })] : null)),
          el('div', { class: 'nums' }, el('b', { text: times(g.n) }), el('span', { text: players(g.players) }))));
        var b = el('div', { class: 'body' });
        (g.latest || []).forEach(function (r) {
          b.appendChild(el('div', { class: 'rep' },
            el('div', { class: 'meta' }, el('span', { class: 'num', title: fmtDay(r.at), text: fmtStamp(r.at) }),
               el('a', { href: '#/user/' + r.install_id }, r.anonymous ? anonTag() : el('span', { dir: 'auto', text: r.name || '?' })),
               r.version ? el('span', { class: 'num', text: r.version }) : null, r.game ? el('span', { text: r.game }) : null),
            el('div', { class: 'txt', dir: 'auto', text: r.detail || '' }), dataBlock(r.data)));
        });
        det.appendChild(b);
        body.appendChild(det);
      });
    }
    return { update: update, status: head.status };
  };

  // ---------------------------------------------------------------- messages
  var msgUi = { filter: 'all' };
  VIEWS.messages = function (main) {
    var head = pageHead(main, tt('الرسائل', 'Messages'),
      el('button', { class: 'btn small', type: 'button', onclick: function () {
        (state.data.feedback || []).forEach(function (m) { if (!isRead(m.id)) readIds.push(String(m.id)); });
        setRead('__', false);
        update();
      } }, tt('علّم الكل مقروء', 'Mark all read')));
    var counts = {};
    var FILTERS = [['all', tt('الكل', 'All')], ['unread', tt('غير مقروءة', 'Unread')], ['problem', tt('مشاكل', 'Problems')], ['idea', tt('أفكار', 'Ideas')], ['question', tt('أسئلة', 'Questions')]];
    main.appendChild(el('div', { class: 'toolbar' }, seg(FILTERS, msgUi.filter, function (f) { msgUi.filter = f; update(); }),
      el('span', { class: 'sub2', text: tt('علامة المقروء محفوظة في هالمتصفح بس.', 'Read marks are kept in this browser only.') })));
    var body = el('div');
    main.appendChild(body);

    function full(m) {
      return msgType(m.type) + '  ·  ' + fmtDay(m.at) + ' ' + fmtTime(m.at) + '  ·  ' + displayName(m) + '\n' +
        tt('التواصل: ', 'Contact: ') + (m.contact || '-') + '  ·  ' + (m.version || '') + (m.game ? '  ·  ' + m.game : '') + '\n\n' +
        (m.text || '') + (m.diagnostics ? '\n\n' + m.diagnostics : '');
    }
    function update() {
      body.textContent = '';
      var list = state.data.feedback;
      if (!list) { body.appendChild(waiting()); return; }
      var shown = list.filter(function (m) {
        if (msgUi.filter === 'unread') return !isRead(m.id);
        if (msgUi.filter === 'all') return true;
        return (m.type || 'problem') === msgUi.filter;
      });
      if (!shown.length) { body.appendChild(el('div', { class: 'card' }, emptyRow(list.length ? tt('ما فيه رسائل هنا.', 'No messages here.') : tt('ما وصلت رسائل لسا.', 'No messages yet.')))); return; }
      shown.forEach(function (m) {
        var read = isRead(m.id);
        var card = el('article', { class: 'card msgcard' + (read ? ' read' : '') },
          el('div', { class: 'meta' }, read ? null : el('span', { class: 'dot unread', title: tt('جديدة', 'New') }),
             el('span', { class: 'tag ' + (m.type || 'problem'), text: msgType(m.type) }),
             el('a', { href: '#/user/' + m.install_id }, m.anonymous ? anonTag() : el('span', { dir: 'auto', text: m.name || '?' })),
             el('span', { class: 'num', title: fmtDay(m.at) + ' ' + fmtTime(m.at), text: fmtStamp(m.at) }), el('span', { text: ago(m.at) }),
             m.version ? el('span', { class: 'num', text: m.version }) : null, m.game ? el('span', { text: m.game }) : null),
          el('div', { class: 'text', dir: 'auto', text: m.text || '' }),
          el('div', { class: 'contact' }, tt('التواصل: ', 'Contact: '), m.contact ? el('b', { dir: 'auto', class: 'ltr', text: m.contact }) : tt('ما كتب', 'none given')),
          m.diagnostics ? el('details', { class: 'raw' }, el('summary', { text: tt('التشخيص', 'Diagnostics') + ' ▾' }), el('pre', { class: 'json', text: m.diagnostics })) : null,
          el('div', { class: 'actions' },
            el('button', { class: 'btn small', type: 'button', onclick: function () { copyText(full(m)); } }, icon('copy'), tt('انسخ', 'Copy')),
            el('button', { class: 'btn small ghost', type: 'button', onclick: function () { setRead(m.id, !read); update(); } }, read ? tt('علّمها جديدة', 'Mark unread') : tt('علّمها مقروءة', 'Mark read'))));
        body.appendChild(card);
      });
    }
    return { update: update, status: head.status };
  };

  // ---------------------------------------------------------------- versions
  VIEWS.versions = function (main) {
    var head = pageHead(main, tt('النسخ', 'Versions'));
    var summary = el('p', { class: 'dim', style: 'margin-top:-4px' });
    main.appendChild(summary);
    var box = el('div', { class: 'card tablewrap' });
    main.appendChild(box);
    function update() {
      box.textContent = '';
      summary.textContent = '';
      var list = state.data.versions;
      if (!list) { box.appendChild(waiting()); return; }
      list = list.slice().sort(function (a, b) { return verCmp(b.version, a.version); });
      var activeTotal = list.reduce(function (s, v) { return s + (v.active_7d || 0); }, 0);
      var latest = null;
      list.forEach(function (v) { if (v.active_7d > 0 && !latest) latest = v.version; });
      if (!latest && list.length) latest = list[0].version;
      var onLatest = list.filter(function (v) { return v.version === latest; }).reduce(function (s, v) { return s + v.active_7d; }, 0);
      if (latest) {
        var pctTxt = fmtPct(activeTotal ? 100 * onLatest / activeTotal : 0);
        add(summary, [tt(pctTxt + ' من النشطين آخر 7 أيام على آخر نسخة ', pctTxt + ' of the users active in the last 7 days are on the latest version '),
                      el('span', { class: 'num', text: latest }), tt(': ' + fmtN(onLatest) + ' من ' + fmtN(activeTotal) + '.', ': ' + fmtN(onLatest) + ' of ' + fmtN(activeTotal) + '.')]);
      }
      if (!list.length) { box.appendChild(emptyRow(tt('ما فيه بيانات.', 'No data.'))); return; }
      var tb = el('tbody');
      list.forEach(function (v) {
        var share = activeTotal ? 100 * v.active_7d / activeTotal : 0;
        var f = el('div', { class: 'fill' });
        f.style.width = share + '%';
        f.style.background = v.version === latest ? 'var(--aqua)' : 'var(--gold)';
        tb.appendChild(el('tr', null,
          el('td', null, el('span', { class: 'num', style: 'font-weight:600', text: v.version }), ' ',
             v.version === latest ? el('span', { class: 'tag latest', text: tt('الأحدث', 'latest') }) : (verCmp(v.version, latest) < 0 ? el('span', { class: 'tag old', text: tt('لازم يحدث', 'needs update') }) : null)),
          el('td', { 'data-k': tt('نشطين 7 أيام', 'Active 7 days') }, el('div', { class: 'hbar', style: 'grid-template-columns:minmax(0,1fr) auto' },
             el('div', { class: 'track' }, f), el('span', { class: 'val num', text: fmtN(v.active_7d) + ' · ' + fmtPct(share) }))),
          el('td', { class: 'n num', 'data-k': tt('نشطين 30 يوم', 'Active 30 days'), text: fmtN(v.active_30d) }),
          el('td', { class: 'n num', 'data-k': tt('كل الأجهزة', 'All installs'), text: fmtN(v.installs) }),
          el('td', { class: 'n num', 'data-k': tt('متصلين', 'Online'), text: fmtN(v.online) }),
          el('td', { 'data-k': tt('آخر ظهور', 'Last seen'), text: ago(v.last_seen) })));
      });
      box.appendChild(el('table', { class: 'data cards' },
        el('thead', null, el('tr', null, el('th', { text: tt('النسخة', 'Version') }), el('th', { style: 'width:34%', text: tt('نشطين 7 أيام', 'Active 7 days') }),
           el('th', { text: tt('نشطين 30 يوم', 'Active 30 days') }), el('th', { text: tt('كل الأجهزة', 'All installs') }),
           el('th', { text: tt('متصلين', 'Online') }), el('th', { text: tt('آخر ظهور', 'Last seen') }))), tb));
    }
    return { update: update, status: head.status };
  };

  // ---------------------------------------------------------------- log
  var logUi = { q: '', kind: '', errors: false };
  VIEWS.log = function (main) {
    var head = pageHead(main, tt('السجل', 'Log'));
    var search = el('input', { type: 'search', class: 'search', placeholder: tt('دوّر في السجل...', 'Search the log...'), value: logUi.q });
    search.addEventListener('input', function () { logUi.q = search.value; update(); });
    var kindSel = el('select', { class: 'sortsel', 'aria-label': tt('النوع', 'Kind') });
    kindSel.addEventListener('change', function () { logUi.kind = kindSel.value; update(); });
    main.appendChild(el('div', { class: 'toolbar' }, search, kindSel,
      el('div', { class: 'chips' }, chip(tt('أخطاء ومشاكل بس', 'Errors and problems only'), logUi.errors, function (on) { logUi.errors = on; update(); }))));
    main.appendChild(el('div', { class: 'sub2', style: 'margin:-4px 0 8px', text: tt('آخر 300 حدث من الكل (النبضات ما تنحفظ كأحداث).', 'The newest 300 events of everyone (heartbeats are not stored as events).') }));
    var box = el('div', { class: 'card tablewrap' });
    main.appendChild(box);
    function update() {
      box.textContent = '';
      var list = state.data.log;
      if (!list) { box.appendChild(waiting()); return; }
      var kinds = {};
      list.forEach(function (e) { kinds[e.kind] = (kinds[e.kind] || 0) + 1; });
      kindSel.textContent = '';
      kindSel.appendChild(el('option', { value: '' }, tt('كل الأنواع', 'All kinds')));
      Object.keys(kinds).sort().forEach(function (k) { kindSel.appendChild(el('option', { value: k, selected: k === logUi.kind }, kindName(k) + ' (' + kinds[k] + ')')); });
      var q = logUi.q.trim().toLowerCase();
      var rows = list.filter(function (e) {
        if (logUi.kind && e.kind !== logUi.kind) return false;
        if (logUi.errors && e.kind !== 'error' && e.kind !== 'issue') return false;
        if (!q) return true;
        return [e.name, e.kind, e.game, e.server, e.detail, e.version].some(function (x) { return x && String(x).toLowerCase().indexOf(q) >= 0; });
      });
      if (!rows.length) { box.appendChild(emptyRow(tt('ما فيه شي.', 'Nothing.'))); return; }
      var tb = el('tbody');
      rows.forEach(function (e) {
        tb.appendChild(el('tr', null,
          el('td', { class: 'num', title: fmtDay(e.at), style: 'white-space:nowrap', text: fmtStamp(e.at) }),
          el('td', { 'data-k': '' }, el('a', { href: '#/user/' + e.install_id }, e.anonymous ? anonTag() : el('span', { dir: 'auto', text: e.name || '?' }))),
          el('td', null, el('span', { class: 'kind ' + e.kind, text: kindName(e.kind) })),
          el('td', { 'data-k': tt('اللعبة', 'Game'), class: 'hide-sm', text: e.game || '' }),
          el('td', { 'data-k': tt('السيرفر', 'Server') }, e.server || '', e.server && e.ping > 0 ? ' · ' : '', e.ping > 0 ? el('span', { class: 'faint num', text: e.ping + ' ms' }) : null),
          el('td', { 'data-k': '' }, el('div', { class: 'ellip', dir: 'auto', title: e.detail || '', text: e.detail || '' }), dataBlock(e.data))));
      });
      box.appendChild(el('table', { class: 'data cards' },
        el('thead', null, el('tr', null, el('th', { text: tt('الوقت', 'Time') }), el('th', { text: tt('مين', 'Who') }), el('th', { text: tt('النوع', 'Kind') }),
          el('th', { class: 'hide-sm', text: tt('اللعبة', 'Game') }), el('th', { text: tt('السيرفر', 'Server') }), el('th', { text: tt('التفاصيل', 'Detail') }))), tb));
    }
    return { update: update, status: head.status };
  };

  // ================================================================== start

  function loadScript(src, integrity) {
    return new Promise(function (resolve, reject) {
      var s = document.createElement('script');
      s.src = src;
      if (integrity) { s.integrity = integrity; s.crossOrigin = 'anonymous'; }
      s.referrerPolicy = 'no-referrer';
      s.onload = resolve;
      s.onerror = function () { reject(new TypeError('failed to load ' + src)); };
      document.head.appendChild(s);
    });
  }

  function boot() {
    applyLang();
    if (DEMO) {
      loadScript('demo.js').then(startApp, function () { root.textContent = 'demo.js missing'; });
      return;
    }
    showLogin();
    loadScript(LIB.src, LIB.integrity).then(function () {
      sb = window.supabase.createClient(SUPABASE_URL, SUPABASE_KEY, {
        auth: { persistSession: true, autoRefreshToken: true, detectSessionInUrl: false, storageKey: 'txp-dev-auth' }
      });
      sb.auth.onAuthStateChange(function (event) {
        if (event === 'SIGNED_OUT' && screen === 'app') { state.data = {}; showLogin(); }
      });
      return sb.auth.getSession().then(function (r) {
        if (r.data && r.data.session) return afterSignIn();
        showLogin();
      });
    }).catch(function (e) {
      showLogin(e instanceof TypeError ? tt('ما قدرنا نحمّل مكتبة Supabase. تأكد من النت وحدّث الصفحة.', "Couldn't load the Supabase library. Check the internet and reload the page.") : signInError(e));
    });
  }

  boot();
})();
