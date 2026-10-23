/* 2XPing developer dashboard: made-up data for index.html?demo=1.
 * Every name, place, message and number here is invented. Nothing is fetched; the page never contacts Supabase in
 * demo mode. The shapes match tools/supabase-web-dashboard.sql (web_stats, web_installs, web_install, web_issues,
 * web_feedback, web_versions, web_log). */
(function () {
  'use strict';

  var seed = 20261023;
  function rnd() { // mulberry32: the same "random" data on every load
    seed |= 0; seed = seed + 0x6D2B79F5 | 0;
    var t = Math.imul(seed ^ seed >>> 15, 1 | seed);
    t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t;
    return ((t ^ t >>> 14) >>> 0) / 4294967296;
  }
  function pick(a) { return a[Math.floor(rnd() * a.length)]; }
  function int(a, b) { return a + Math.floor(rnd() * (b - a + 1)); }
  function uuid() {
    var h = '';
    for (var i = 0; i < 32; i++) h += Math.floor(rnd() * 16).toString(16);
    return h.slice(0, 8) + '-' + h.slice(8, 12) + '-4' + h.slice(13, 16) + '-a' + h.slice(17, 20) + '-' + h.slice(20, 32);
  }
  var NOW = Date.now();
  function iso(ms) { return new Date(ms).toISOString(); }
  var MIN = 60000, HOUR = 3600000, DAY = 86400000;

  var LATEST = '0.13.0 Beta';
  var NAMES = ['Abood', 'سعود', 'Faisal_99', 'ريان', 'Nawaf', 'مازن', 'Turki7', 'x7mood', 'خالد', 'Hamad', 'عبدالله', 'Ziyad',
               'يزن', 'Omar.k', 'Bandar', 'فهد', 'Sultan', 'راكان', 'Majed', 'Yousef', 'حسام', 'Ammar', 'Saleh', 'نايف',
               'Rakan_gg', 'تركي', 'Mshari', 'Basel', 'وليد', 'Hazem', 'عمر', 'Fares', 'Anas', 'سلطان', 'Moath', 'Talal',
               'Waleed', 'لؤي', 'Ibrahim'];
  var PLACES = [['Jeddah', 'Saudi Arabia', 'Saudi Telecom Company JSC'], ['Riyadh', 'Saudi Arabia', 'Saudi Telecom Company JSC'],
                ['Riyadh', 'Saudi Arabia', 'Mobily'], ['Mecca', 'Saudi Arabia', 'Zain KSA'], ['Dammam', 'Saudi Arabia', 'Saudi Telecom Company JSC'],
                ['Medina', 'Saudi Arabia', 'Mobily'], ['Dubai', 'United Arab Emirates', 'Emirates Integrated Telecommunications (du)'],
                ['Kuwait City', 'Kuwait', 'Zain Kuwait'], ['Manama', 'Bahrain', 'Batelco'], ['Doha', 'Qatar', 'Ooredoo Q.S.C.'],
                ['Amman', 'Jordan', 'Orange Jordan'], ['Cairo', 'Egypt', 'Telecom Egypt'], ['Abha', 'Saudi Arabia', 'Saudi Telecom Company JSC']];
  var CPUS = ['AMD Ryzen 7 5800X3D', 'Intel Core i5-12400F', 'AMD Ryzen 5 5600', 'Intel Core i7-13700K', 'AMD Ryzen 7 7800X3D', 'Intel Core i5-10400F'];
  var GPUS = ['NVIDIA GeForce RTX 3060', 'NVIDIA GeForce RTX 4070', 'AMD Radeon RX 6700 XT', 'NVIDIA GeForce GTX 1660 SUPER', 'NVIDIA GeForce RTX 4060'];
  var SERVERS = ['Frankfurt', 'Stockholm', 'Vienna', 'Dubai', 'Amsterdam', 'Singapore', 'Warsaw'];
  var DCS = [['fra', 'Europe'], ['sto', 'Europe'], ['vie', 'Europe'], ['ams', 'Europe'], ['waw', 'Europe'], ['dxb', 'SEAsia'], ['sgp', 'SEAsia']];

  // ---------------------------------------------------------------- installs
  var installs = [];
  for (var i = 0; i < 46; i++) {
    var anon = i % 7 === 3;
    var pl = pick(PLACES);
    var r = rnd();
    // a third online now, the rest spread over the last weeks
    var last = i < 15 ? NOW - int(5, 230) * 1000 : NOW - (r < 0.5 ? int(10, 60 * 24) * MIN : int(1, 40) * DAY);
    var first = last - int(0, 50) * DAY - int(1, 600) * MIN;
    var version = i < 15 ? (i % 5 === 4 ? '0.12.2 Beta' : LATEST) : pick([LATEST, LATEST, '0.12.2 Beta', '0.12.0 Beta', '0.11.1 Beta']);
    var game = pick(['Deadlock', 'Deadlock', 'Deadlock', 'Deadlock', 'Arena Breakout: Infinite', 'Counter-Strike 2']);
    var server = game === 'Deadlock' ? pick(SERVERS) : game === 'Counter-Strike 2' ? pick(['Dubai', 'Frankfurt']) : 'Middle East';
    var name = anon ? '(anonymous)' : NAMES[i % NAMES.length];
    installs.push({
      id: uuid(), anonymous: anon, name: anon ? null : name,
      device: anon ? null : rnd() < 0.55 ? 'DESKTOP-' + (1e7 + Math.floor(rnd() * 9e7)).toString(36).toUpperCase().slice(0, 7)
                                         : (name.replace(/[^A-Za-z]/g, '').toUpperCase() || 'GAMER') + '-PC',
      country: anon ? null : pl[1], city: anon ? null : pl[0], isp: anon ? null : pl[2],
      asn: anon ? null : 'AS' + int(20000, 60000) + ' ' + pl[2],
      version: version, os: pick(['10.0.22631.0', '10.0.26100.0', '10.0.19045.0']), lang: rnd() < 0.78 ? 'ar' : 'en',
      mode: rnd() < 0.7 ? 'simple' : 'advanced', game: game, server: server,
      ping: game === 'Deadlock' ? (server === 'Dubai' ? int(18, 40) : server === 'Singapore' ? int(95, 140) : int(58, 110)) : int(30, 90),
      net: pick(['cable', 'cable', 'wifi']), windows: pick(['Windows 11 Pro 23H2', 'Windows 10 Pro 22H2', 'Windows 11 Home 24H2']),
      first_seen: iso(first), last_seen: iso(last),
      cpu: CPUS[i % CPUS.length], gpu: GPUS[i % GPUS.length], ram: pick([16, 16, 32, 8, 64])
    });
  }
  // the locked ones (online, locked on picked regions)
  installs.forEach(function (u, k) {
    var on = NOW - Date.parse(u.last_seen) < 270000;
    u.online = on;
    u.locked = on && k % 3 !== 2 && u.game === 'Deadlock';
    u.lock_server = u.locked ? pick(['Europe', 'Europe, SE Asia', 'Frankfurt, Vienna, Stockholm']) : null;
    u.lock_at = u.locked ? iso(NOW - int(5, 90) * MIN) : null;
  });
  installs.sort(function (a, b) { return Date.parse(b.last_seen) - Date.parse(a.last_seen); });

  function publicInstall(u) {
    return { id: u.id, anonymous: u.anonymous, name: u.name, device: u.device, country: u.country, city: u.city, isp: u.isp, asn: u.asn,
             version: u.version, os: u.os, lang: u.lang, mode: u.mode, game: u.game, server: u.server, ping: u.ping, net: u.net,
             windows: u.anonymous ? null : u.windows, first_seen: u.first_seen, last_seen: u.last_seen, online: u.online,
             locked: u.locked, lock_server: u.lock_server, lock_at: u.lock_at };
  }

  // ---------------------------------------------------------------- events
  var ISSUE_SAMPLES = [
    ['lock_outside', 'lock_outside: match on dxb (SEAsia) while locked to Europe', { code: 'lock_outside', dc: 'dxb', picked_regions: ['Europe'] }],
    ['connection_loss', 'connection_loss: 14% loss over 60 s to fra', { code: 'connection_loss', loss_pct: 14, dc: 'fra' }],
    ['slow_startup', 'slow_startup: 9.8 s until the window was ready', { code: 'slow_startup', ms: 9800 }],
    ['sdr_new_pop', "sdr_new_pop: Valve lists 'dfra2', not in the Deadlock table", { code: 'sdr_new_pop', pop: 'dfra2' }],
    ['crash', 'crash: NullReferenceException at StateCard.OnPaint', { code: 'crash', type: 'NullReferenceException' }],
    ['firewall_off', 'firewall_off: Windows Firewall is off for the Public profile', { code: 'firewall_off' }],
    ['update_failed', 'update_failed: SHA256 mismatch, download refused', { code: 'update_failed' }],
    ['queue_long', 'queue_long: locked to Europe, no match after 9 min', { code: 'queue_long', minutes: 9 }]
  ];
  var MESSAGES = [
    ['problem', 'التثبيت على أوروبا ما مسك، دخلت سيرفر دبي مرتين ورا بعض. البرنامج كان شغال ومثبّت.', 'abood.x'],
    ['idea', 'Would be nice to see my ping history per region for the last week.', null],
    ['question', 'هل البرنامج يسبب باند في ديدلوك؟ قصدي هل فالف تعتبره غش؟', 'rayan@example.com'],
    ['problem', 'After the update the app takes about 20 seconds to open.', 'nawaf_gg'],
    ['idea', 'ودي لو فيه زر يرجع آخر تثبيت بضغطة وحدة بدل ما أختار المناطق كل مرة.', null],
    ['problem', 'البنق المتوقع 60 بس في المباراة طلع 95، فرانكفورت.', 'turki7'],
    ['question', 'Does it work with Arena Breakout or only Deadlock?', null],
    ['idea', 'الوضع البسيط حلو، بس أبي أشوف كم لاعب في كل منطقة دحين.', 'mazen#1'],
    ['problem', 'جدار الحماية عندي مطفي من برنامج ثاني، والتثبيت يقول شغال بس ما يسوي شي.', null]
  ];

  var events = [];
  var eid = 900000;
  function ev(u, at, kind, o) {
    var e = { id: eid++, at: iso(at), install_id: u.id, kind: kind, game: o.game || null, server: o.server || null, ping: o.ping || null,
              detail: o.detail || null, version: u.version, session: 'a1b2c3', data: o.data || null };
    events.push(e);
    return e;
  }
  installs.forEach(function (u, k) {
    var last = Date.parse(u.last_seen), first = Date.parse(u.first_seen);
    var span = Math.max(HOUR, last - first);
    var n = int(6, 40);
    for (var j = 0; j < n; j++) {
      var at = last - rnd() * Math.min(span, 30 * DAY);
      var r2 = rnd();
      if (r2 < 0.38 && u.game === 'Deadlock') {
        var dc = pick(DCS), exp = int(55, 95), act = exp + Math.round((rnd() - 0.35) * 30);
        var picked = rnd() < 0.8 ? ['Europe'] : [];
        var verdict = !picked.length ? 'nolock' : dc[1] === 'Europe' ? 'inside' : (rnd() < 0.5 ? 'outside' : 'unknown');
        ev(u, at, 'match', { game: 'Deadlock', server: u.server, data: { game: 'Deadlock', locked: !!picked.length, picked_regions: picked,
           expected_ms: picked.length ? exp : null, actual_ms: act, dc: dc[0], region: dc[1], verdict: verdict,
           source: pick(['log_located', 'log_colocated', 'capture']), queue_s: int(40, 420), same_server_as_prev: false,
           gateways_open: [], dubai_way_in: false } });
      } else if (r2 < 0.55) {
        ev(u, at, 'test', { game: u.game, server: pick(SERVERS), ping: int(20, 120), detail: '7 regions, ' + int(1800, 4200) + ' ms', data: { regions: 7 } });
      } else if (r2 < 0.66) {
        ev(u, at, 'lock', { game: u.game, server: 'Europe', detail: int(80, 240) + ' ranges blocked, mode firewall' });
        ev(u, at + int(10, 70) * MIN, 'unlock', { game: u.game });
      } else if (r2 < 0.74) {
        ev(u, at, 'start', { detail: u.os, data: null });
        ev(u, at + int(20, 180) * MIN, 'exit', { data: { minutes: int(20, 180) } });
      } else if (r2 < 0.82) {
        ev(u, at, 'settings', { detail: (u.mode || 'simple') + ', ' + u.lang, data: { share_stats: !u.anonymous, auto_test: rnd() < 0.5, dubai_way_in: false } });
      } else if (r2 < 0.9) {
        ev(u, at, 'use', { detail: pick(['test button', 'other regions', 'pick by hand', 'play on regions', 'settings']) });
      } else if (r2 < 0.95) {
        var s = ISSUE_SAMPLES[(k + j) % ISSUE_SAMPLES.length];
        ev(u, at, 'issue', { game: u.game, detail: s[1], data: s[2] });
      } else {
        ev(u, at, 'detect', { game: u.game, server: pick(SERVERS), detail: 'auto, console log, ok' });
      }
    }
    if (u.locked) ev(u, Date.parse(u.lock_at), 'lock', { game: u.game, server: u.lock_server, detail: '142 ranges blocked, mode firewall' });
  });
  MESSAGES.forEach(function (m, k) {
    var u = installs[(k * 5 + 2) % installs.length];
    var at = NOW - (k === 0 ? 25 * MIN : k * int(5, 40) * HOUR);
    ev(u, at, 'feedback', { game: u.game, detail: m[1], data: { type: m[0], contact: m[2], lang: /[؀-ۿ]/.test(m[1]) ? 'ar' : 'en', id: 'm' + k,
       diagnostics: '2XPing ' + u.version + ' · Windows ' + u.os + ' · .NET 4.8.1 · WireGuard: no\n[21:14:02] Lock on: Europe (142 ranges)\n[21:15:40] Match: fra (Europe), 71 ms' } });
  });
  events.push({ id: eid++, at: iso(NOW - 3 * HOUR), install_id: installs[4].id, kind: 'error', game: 'Deadlock', server: 'Dubai', ping: null,
                detail: 'boost via relay-dxb: WebException: The operation has timed out', version: installs[4].version, session: 'z9', data: null });
  events.sort(function (a, b) { return Date.parse(b.at) - Date.parse(a.at); });
  var byId = {};
  installs.forEach(function (u) { byId[u.id] = u; });

  // ---------------------------------------------------------------- answers
  function later(v) { return new Promise(function (res) { setTimeout(function () { res(JSON.parse(JSON.stringify(v))); }, 120); }); }
  function who(id) { var u = byId[id]; return { name: u && !u.anonymous ? u.name : null, anonymous: !!(u && u.anonymous) }; }
  function since(ms) { return function (e) { return NOW - Date.parse(e.at) < ms; }; }
  function trimData(d) { return d; }
  function isOn(u) { return NOW - Date.parse(u.last_seen) < 270000; }

  var statsCache = {};
  function stats(a) {
    var days = Math.max(1, Math.min(180, a.p_days || 30));
    if (statsCache[days]) return later(statsCache[days]);
    var series = [];
    var today = new Date(NOW); today.setHours(0, 0, 0, 0);
    for (var d = days - 1; d >= 0; d--) {
      var day = new Date(today.getTime() - d * DAY);
      var t = (days - d) / days, wk = day.getDay() === 4 || day.getDay() === 5 ? 1.25 : 1; // Thu/Fri busier
      var active = Math.round((14 + 22 * t + rnd() * 6) * wk);
      series.push({ day: day.getFullYear() + '-' + ('0' + (day.getMonth() + 1)).slice(-2) + '-' + ('0' + day.getDate()).slice(-2),
                    active: active, 'new': int(0, 4) + (rnd() < 0.15 ? 4 : 0), matches: Math.round(active * (1.1 + rnd())),
                    locks: Math.round(active * (0.4 + rnd() * 0.3)), errors: rnd() < 0.7 ? 0 : int(1, 3), issues: int(0, 5) });
    }
    var sum = function (k) { return series.reduce(function (s, x) { return s + x[k]; }, 0); };
    var online = installs.filter(isOn).length;
    var pairs = [];
    for (var p = 0; p < 220; p++) { var e2 = int(45, 105); pairs.push([e2, Math.max(15, e2 + Math.round((rnd() - 0.3) * 34))]); }
    var diffs = pairs.map(function (x) { return x[1] - x[0]; }).sort(function (a2, b2) { return a2 - b2; });
    var avg = function (arr) { return Math.round(10 * arr.reduce(function (s, x) { return s + x; }, 0) / arr.length) / 10; };
    var mt = sum('matches');
    var verCounts = {};
    installs.forEach(function (u) { verCounts[u.version] = (verCounts[u.version] || 0) + 1; });
    return later(statsCache[days] = {
      days: days, generated_at: new Date(NOW).toISOString(), online_window_s: 270,
      counters: { installs_total: installs.length, installs_anonymous: installs.filter(function (u) { return u.anonymous; }).length,
                  online_now: online, active_today: series[series.length - 1].active, active_7d: 38, active_30d: 44, active_period: 44,
                  new_today: 2, new_7d: 9, matches_today: series[series.length - 1].matches, locks_today: series[series.length - 1].locks,
                  issues_24h: 7, feedback_7d: 5, errors_24h: 1, matches_period: mt, locks_period: sum('locks') },
      deltas: { active_today: 4, active_7d: 6, new_today: 1, new_7d: -2, matches_today: 9, locks_today: 3, issues_24h: -3, errors_24h: 0 },
      period: { 'new': { now: sum('new'), prev: Math.round(sum('new') * 0.8) }, active: { now: 44, prev: 37 },
                matches: { now: mt, prev: Math.round(mt * 0.86) }, locks: { now: sum('locks'), prev: Math.round(sum('locks') * 1.05) },
                feedback: { now: Math.min(9, days), prev: Math.max(1, Math.min(9, days) - 3) }, feedback_7d_prev: 3 },
      series: series,
      breakdowns: {
        version: { total: installs.length, multi: false, items: Object.keys(verCounts).map(function (k) { return { k: k, n: verCounts[k] }; }).sort(function (a2, b2) { return b2.n - a2.n; }) },
        country: { total: 40, multi: false, items: [{ k: 'Saudi Arabia', n: 27 }, { k: 'United Arab Emirates', n: 4 }, { k: 'Kuwait', n: 3 }, { k: 'Bahrain', n: 2 }, { k: 'Egypt', n: 2 }, { k: 'Jordan', n: 1 }, { k: '?', n: 1 }] },
        game: { total: 41, multi: true, items: [{ k: 'Deadlock', n: 36 }, { k: 'Arena Breakout: Infinite', n: 7 }, { k: 'Counter-Strike 2', n: 5 }, { k: 'Dead by Daylight', n: 2 }] },
        lock_region: { total: 30, multi: true, items: [{ k: 'Europe', n: 27 }, { k: 'SEAsia', n: 6 }, { k: 'Row', n: 1 }] },
        lang: { total: 46, multi: false, items: [{ k: 'ar', n: 36 }, { k: 'en', n: 10 }] },
        mode: { total: 46, multi: false, items: [{ k: 'simple', n: 32 }, { k: 'advanced', n: 14 }] },
        os: { total: 46, multi: false, items: [{ k: 'Windows 11', n: 29 }, { k: 'Windows 10', n: 17 }] }
      },
      match: {
        total: mt, verdict: [{ k: 'inside', n: Math.round(mt * 0.78) }, { k: 'nolock', n: Math.round(mt * 0.12) }, { k: 'outside', n: Math.round(mt * 0.05) },
                             { k: 'unknown', n: Math.round(mt * 0.04) }, { k: 'outside_gateway', n: Math.round(mt * 0.01) }],
        source: [{ k: 'log_located', n: Math.round(mt * 0.7) }, { k: 'capture', n: Math.round(mt * 0.3) }],
        by_region: [{ region: 'Europe', n: Math.round(mt * 0.7), inside: Math.round(mt * 0.66), outside: Math.round(mt * 0.03), gateway: 2, unknown: 4, nolock: 0, queue_median_s: 118, avg_diff_ms: 6.2 },
                    { region: 'SEAsia', n: Math.round(mt * 0.12), inside: Math.round(mt * 0.1), outside: Math.round(mt * 0.02), gateway: 0, unknown: 1, nolock: 0, queue_median_s: 74, avg_diff_ms: 3.1 },
                    { region: '(none)', n: Math.round(mt * 0.12), inside: 0, outside: 0, gateway: 0, unknown: 0, nolock: Math.round(mt * 0.12), queue_median_s: 65, avg_diff_ms: null }],
        ping: { n: pairs.length, avg_expected: avg(pairs.map(function (x) { return x[0]; })), avg_actual: avg(pairs.map(function (x) { return x[1]; })),
                avg_diff: avg(diffs), median_diff: diffs[Math.floor(diffs.length / 2)], pairs: pairs },
        dcs: [], unknown_dc: 2, dup_suspect: 3
      },
      issues: [{ code: 'connection_loss', n: 9, players: 5 }, { code: 'lock_outside', n: 6, players: 4 }, { code: 'slow_startup', n: 4, players: 3 },
               { code: 'sdr_new_pop', n: 3, players: 3 }, { code: 'crash', n: 2, players: 2 }, { code: 'firewall_off', n: 1, players: 1 }],
      now_ping: installs.filter(function (u) { return isOn(u) && u.ping > 0; }).map(function (u) { return u.ping; })
    });
  }

  window.TXP_DEMO = {
    web_stats: stats,
    web_installs: function () {
      return later({ now: new Date(NOW).toISOString(), online_window_s: 270, installs: installs.map(publicInstall) });
    },
    web_install: function (a) {
      var u = byId[a.p_id];
      if (!u) return later(null);
      var mine = events.filter(function (e) { return e.install_id === u.id; });
      var counts = {};
      mine.forEach(function (e) { counts[e.kind] = (counts[e.kind] || 0) + 1; });
      var lk = mine.filter(function (e) { return ['lock', 'unlock', 'start', 'exit'].indexOf(e.kind) >= 0; })[0];
      if (u.locked) lk = { kind: 'lock', server: u.lock_server, at: u.lock_at };
      var pu = publicInstall(u);
      pu.info = u.anonymous ? { anonymous: true, windows: u.windows, startup_ms: 1800 } :
        { windows: u.windows, cpu: u.cpu, cores: 8, gpu: u.gpu, ram_gb: u.ram, screen: '2560x1440', scale: 100, screens: 2, net: u.net,
          net_mbps: u.net === 'wifi' ? 866 : 1000, games_installed: ['Deadlock', 'Counter-Strike 2'], ui_lang: u.lang, wireguard: false, startup_ms: 1650 };
      return later({ now: new Date(NOW).toISOString(), install: pu, lock: lk ? { kind: lk.kind, server: lk.server, at: lk.at } : null,
                     counts: counts, events: mine.slice(0, 300).map(trimData) });
    },
    web_issues: function (a) {
      var list = events.filter(function (e) { return e.kind === 'issue'; }).filter(since((a.p_days || 7) * DAY));
      var g = {};
      list.forEach(function (e) {
        var c = (e.data && e.data.code) || e.detail.split(':')[0];
        (g[c] = g[c] || []).push(e);
      });
      return later(Object.keys(g).map(function (c) {
        var es = g[c], ids = {}, vers = {};
        es.forEach(function (e) { ids[e.install_id] = 1; vers[e.version] = 1; });
        return { code: c, n: es.length, players: Object.keys(ids).length, versions: Object.keys(vers).sort().reverse(),
                 first_at: es[es.length - 1].at, last_at: es[0].at,
                 latest: es.slice(0, 5).map(function (e) { var w = who(e.install_id); return { at: e.at, install_id: e.install_id, name: w.name, anonymous: w.anonymous, version: e.version, game: e.game, detail: e.detail, data: e.data }; }) };
      }).sort(function (x, y) { return y.n - x.n; }));
    },
    web_feedback: function () {
      return later(events.filter(function (e) { return e.kind === 'feedback'; }).map(function (e) {
        var w = who(e.install_id);
        return { id: e.id, at: e.at, install_id: e.install_id, name: w.name, anonymous: w.anonymous, type: e.data.type, text: e.detail,
                 contact: e.data.contact, diagnostics: e.data.diagnostics, lang: e.data.lang, version: e.version, game: e.game };
      }));
    },
    web_versions: function () {
      var v = {};
      installs.forEach(function (u) {
        var x = v[u.version] = v[u.version] || { version: u.version, installs: 0, active_7d: 0, active_30d: 0, online: 0, last_seen: u.last_seen };
        var age = NOW - Date.parse(u.last_seen);
        x.installs++; if (age < 7 * DAY) x.active_7d++; if (age < 30 * DAY) x.active_30d++; if (age < 270000) x.online++;
        if (Date.parse(u.last_seen) > Date.parse(x.last_seen)) x.last_seen = u.last_seen;
      });
      return later(Object.keys(v).map(function (k) { return v[k]; }));
    },
    web_log: function (a) {
      return later(events.slice(0, a.p_limit || 300).map(function (e) {
        var w = who(e.install_id);
        return { id: e.id, at: e.at, install_id: e.install_id, name: w.name, anonymous: w.anonymous, kind: e.kind, game: e.game, server: e.server,
                 ping: e.ping, detail: e.detail, version: e.version, data: e.data };
      }));
    }
  };
})();
