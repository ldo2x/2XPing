-- 2X Ping developer dashboard numbers (Supabase / Postgres).
-- Run in Supabase: SQL Editor > New query > paste > Run. Safe to run again (it only replaces the two functions below).
-- Needs tools/supabase-setup.sql first (tables installs and events, and their indexes).
--
-- One call returns everything the developer panel's «لوحة الأرقام» / Dashboard draws:
--   POST /rest/v1/rpc/dev_stats  {"p_days": 30, "p_tz_minutes": 180}
-- Only the SECRET key (service_role) can call it: the public key in every copy of the app cannot.
-- p_days: the period of the daily charts and the breakdowns (1..180). p_tz_minutes: the developer's UTC offset in
-- minutes, so "today" and the days of the charts are the developer's own days (180 = Riyadh).
--
-- The JSON shape (every count is a whole number; ms and seconds may have decimals; null when nothing to measure):
-- {
--   "days": 30, "generated_at": "2026-10-14T09:00:00Z",
--   "counters": { "installs_total", "online_now", "active_today", "active_7d", "active_30d", "active_period",
--                 "new_today", "new_7d", "matches_today", "locks_today", "issues_24h", "errors_24h",
--                 "matches_period", "locks_period" },
--   "deltas":   { same keys where a fair comparison exists: today vs yesterday up to the same hour, 7 d vs the 7 d
--                 before, 24 h vs the 24 h before (active_* deltas are counted from events on both sides) },
--   "series":   [ {"day": "2026-10-01", "active", "new", "matches", "locks", "errors", "issues"}, ... one per day ],
--   "breakdowns": { "version" | "country" | "lang" | "mode" | "os" | "game" | "lock_region":
--                   {"total": N, "multi": false, "items": [{"k": "0.12.0", "n": 10}, ...top 15]} },
--                 (multi = true: one install can be in several items, so the shares can add up past 100 %)
--   "match": { "total", "verdict": [{"k","n"}] (inside | outside | outside_gateway | unknown | nolock), "source": [{"k","n"}],
--              "by_region": [{"region", "n", "inside", "outside", "gateway", "unknown", "nolock", "queue_median_s", "avg_diff_ms"}],
--              (region keys are Deadlock's: Europe, SEAsia, Row = North America, SAmerica, Oceania; "(none)" = no lock)
--              "ping": {"n", "avg_expected", "avg_actual", "avg_diff", "median_diff", "pairs": [[expected, actual], ...]},
--              "dcs": [{"region", "dc", "n", "known"}], "unknown_dc": N },
--   "issues": [ {"code": "crash", "n": 5, "players": 2}, ... top 15 of the last 7 days ]
-- }

-- a number from JSON text, or null when it isn't one (a broken client never breaks the whole dashboard)
create or replace function public.dev_num(t text)
returns numeric
language sql
immutable
set search_path = public, pg_temp
as $$
  select case when t ~ '^\s*-?[0-9]{1,9}(\.[0-9]+)?\s*$' then t::numeric end
$$;

create or replace function public.dev_stats(p_days int default 30, p_tz_minutes int default 180)
returns jsonb
language plpgsql
stable
security definer
set search_path = public, pg_temp
as $$
declare
  nd     int      := greatest(1, least(coalesce(p_days, 30), 180));
  tz_off interval := make_interval(mins => greatest(-840, least(coalesce(p_tz_minutes, 0), 840)));
  today  date     := ((now() at time zone 'utc') + tz_off)::date;
  t0     timestamptz;   -- the start of the developer's today
  p0     timestamptz;   -- the start of the period
  c      jsonb;
  dl     jsonb;
  ser    jsonb;
  brk    jsonb;
  mt     jsonb;
  iss    jsonb;
begin
  t0 := (today::timestamp - tz_off) at time zone 'utc';
  p0 := t0 - (nd - 1) * interval '1 day';

  -- counters: "active" = seen by the heartbeat (every 2 minutes while the app is open)
  select jsonb_build_object(
    'installs_total', (select count(*) from installs),
    'online_now',     (select count(*) from installs where last_seen > now() - interval '5 minutes'),
    'active_today',   (select count(*) from installs where last_seen >= t0),
    'active_7d',      (select count(*) from installs where last_seen > now() - interval '7 days'),
    'active_30d',     (select count(*) from installs where last_seen > now() - interval '30 days'),
    'active_period',  (select count(*) from installs where last_seen >= p0),
    'new_today',      (select count(*) from installs where first_seen >= t0),
    'new_7d',         (select count(*) from installs where first_seen > now() - interval '7 days'),
    'matches_today',  (select count(*) from events where kind = 'match' and at >= t0),
    'locks_today',    (select count(*) from events where kind = 'lock' and at >= t0),
    'issues_24h',     (select count(*) from events where kind = 'issue' and at > now() - interval '24 hours'),
    'errors_24h',     (select count(*) from events where kind = 'error' and at > now() - interval '24 hours'),
    'matches_period', (select count(*) from events where kind = 'match' and at >= p0),
    'locks_period',   (select count(*) from events where kind = 'lock' and at >= p0)
  ) into c;

  -- changes against the period before, compared fairly (today so far vs yesterday up to the same hour)
  select jsonb_build_object(
    'active_today',  (select count(distinct install_id) from events where at >= t0)
                   - (select count(distinct install_id) from events where at >= t0 - interval '1 day' and at < now() - interval '1 day'),
    'active_7d',     (select count(distinct install_id) from events where at > now() - interval '7 days')
                   - (select count(distinct install_id) from events where at > now() - interval '14 days' and at <= now() - interval '7 days'),
    'new_today',     (select count(*) from installs where first_seen >= t0)
                   - (select count(*) from installs where first_seen >= t0 - interval '1 day' and first_seen < now() - interval '1 day'),
    'new_7d',        (select count(*) from installs where first_seen > now() - interval '7 days')
                   - (select count(*) from installs where first_seen > now() - interval '14 days' and first_seen <= now() - interval '7 days'),
    'matches_today', (select count(*) from events where kind = 'match' and at >= t0)
                   - (select count(*) from events where kind = 'match' and at >= t0 - interval '1 day' and at < now() - interval '1 day'),
    'locks_today',   (select count(*) from events where kind = 'lock' and at >= t0)
                   - (select count(*) from events where kind = 'lock' and at >= t0 - interval '1 day' and at < now() - interval '1 day'),
    'issues_24h',    (select count(*) from events where kind = 'issue' and at > now() - interval '24 hours')
                   - (select count(*) from events where kind = 'issue' and at > now() - interval '48 hours' and at <= now() - interval '24 hours'),
    'errors_24h',    (select count(*) from events where kind = 'error' and at > now() - interval '24 hours')
                   - (select count(*) from events where kind = 'error' and at > now() - interval '48 hours' and at <= now() - interval '24 hours')
  ) into dl;

  -- one row per day of the period, days without anything included (zeros)
  with days as (
    select today - g as d from generate_series(0, nd - 1) g
  ), ev as (
    select ((e.at at time zone 'utc') + tz_off)::date as d,
           count(distinct e.install_id)               as active,
           count(*) filter (where e.kind = 'match')   as matches,
           count(*) filter (where e.kind = 'lock')    as locks,
           count(*) filter (where e.kind = 'error')   as errors,
           count(*) filter (where e.kind = 'issue')   as issues
    from events e where e.at >= p0
    group by 1
  ), nw as (
    select ((i.first_seen at time zone 'utc') + tz_off)::date as d, count(*) as n
    from installs i where i.first_seen >= p0
    group by 1
  )
  select coalesce(jsonb_agg(jsonb_build_object(
           'day', to_char(days.d, 'YYYY-MM-DD'), 'active', coalesce(ev.active, 0), 'new', coalesce(nw.n, 0),
           'matches', coalesce(ev.matches, 0), 'locks', coalesce(ev.locks, 0),
           'errors', coalesce(ev.errors, 0), 'issues', coalesce(ev.issues, 0)) order by days.d), '[]'::jsonb)
    into ser
  from days left join ev on ev.d = days.d left join nw on nw.d = days.d;

  -- who the users are (installs seen in the period) and what they do (events of the period)
  with a as (
    select * from installs where last_seen >= p0
  ), single as (
    select 'version' as b, coalesce(nullif(app_version, ''), '?') as k, install_id from a
    union all select 'country', coalesce(nullif(country, ''), '?'), install_id from a
    union all select 'lang', coalesce(nullif(lang, ''), '?'), install_id from a
    union all select 'mode', coalesce(nullif(mode, ''), '?'), install_id from a
    union all select 'os', case when dev_num(split_part(os, '.', 3)) >= 22000 then 'Windows 11'
                                when split_part(os, '.', 1) = '10' then 'Windows 10'
                                else coalesce(nullif(os, ''), '?') end, install_id from a
  ), multi as (
    -- the games each install used in the period
    select 'game' as b, e.game as k, e.install_id from events e
    where e.at >= p0 and coalesce(e.game, '') <> ''
    -- the regions each install picked: the simple layout's "play on regions" and the match reports' picked regions
    union all
    select 'lock_region', trim(r), e.install_id from events e, unnest(string_to_array(e.server, ',')) r
    where e.kind = 'use' and e.detail = 'play on regions' and e.at >= p0 and coalesce(e.server, '') <> ''
    union all
    select 'lock_region', r, e.install_id from events e,
           jsonb_array_elements_text(case when jsonb_typeof(e.data->'picked_regions') = 'array' then e.data->'picked_regions' else '[]'::jsonb end) r
    where e.kind = 'match' and e.at >= p0
  ), all_rows as (
    select b, k, install_id, false as m from single
    union all select b, k, install_id, true from multi where coalesce(k, '') <> ''
  ), totals as (
    select b, bool_or(m) as m, count(distinct install_id) as total from all_rows group by b
  ), cnt as (
    select b, k, count(distinct install_id) as n,
           row_number() over (partition by b order by count(distinct install_id) desc, k) as rn
    from all_rows group by b, k
  )
  select coalesce(jsonb_object_agg(t.b, jsonb_build_object(
           'total', case when t.m then t.total else (select count(*) from a) end, 'multi', t.m,
           'items', (select coalesce(jsonb_agg(jsonb_build_object('k', x.k, 'n', x.n) order by x.n desc, x.k), '[]'::jsonb)
                     from cnt x where x.b = t.b and x.rn <= 15))), '{}'::jsonb)
    into brk
  from totals t;

  -- match reports (kind = 'match'): was the lock kept, how good the guess of the ping was, where matches really were
  with m as (
    select e.at, e.data as d,
           -- 'outside' on a gateway the lock kept open on purpose (Dubai with the dubai_way_in setting) is allowed by the
           -- player's setting, not a lock failure: its own verdict
           case when e.data->>'verdict' = 'outside' and jsonb_typeof(e.data->'gateways_open') = 'array'
                     and e.data->'gateways_open' ? (e.data->>'dc') then 'outside_gateway'
                else coalesce(nullif(e.data->>'verdict', ''), 'unknown') end as verdict,
           dev_num(e.data->>'expected_ms') as exp_ms, dev_num(e.data->>'actual_ms') as act_ms,
           dev_num(e.data->>'queue_s') as queue_s,
           nullif(e.data->>'dc', '') as dc, nullif(e.data->>'region', '') as region
    from events e
    where e.kind = 'match' and e.at >= p0 and jsonb_typeof(e.data) = 'object'
  ), mr as (
    -- one row per picked region (a match with two picked regions counts under both); no pick = "(none)"
    select m.*, coalesce(r, '(none)') as picked
    from m left join lateral jsonb_array_elements_text(
           case when jsonb_typeof(m.d->'picked_regions') = 'array' and jsonb_array_length(m.d->'picked_regions') > 0
                then m.d->'picked_regions' end) r on true
  ), dcs as (
    select picked, dc, count(*) as n, bool_or(region is not null) as known,
           row_number() over (partition by picked order by count(*) desc, dc) as rn
    from mr where dc is not null group by picked, dc
  ), pairs as (
    select exp_ms, act_ms from m
    where exp_ms > 0 and act_ms > 0 and exp_ms < 2000 and act_ms < 2000
    order by at desc limit 400
  )
  select jsonb_build_object(
    'total', (select count(*) from m),
    'verdict', (select coalesce(jsonb_agg(jsonb_build_object('k', k, 'n', n) order by n desc, k), '[]'::jsonb)
                from (select verdict as k, count(*) as n from m group by 1) v),
    'source', (select coalesce(jsonb_agg(jsonb_build_object('k', k, 'n', n) order by n desc, k), '[]'::jsonb)
               from (select coalesce(nullif(d->>'source', ''), 'none') as k, count(*) as n from m group by 1) s),
    'by_region', (select coalesce(jsonb_agg(jsonb_build_object(
                     'region', picked, 'n', n, 'inside', inside, 'outside', outside, 'gateway', gateway, 'unknown', unk, 'nolock', nolock,
                     'queue_median_s', qmed, 'avg_diff_ms', adiff) order by n desc, picked), '[]'::jsonb)
                  from (select picked, count(*) as n,
                               count(*) filter (where verdict = 'inside') as inside,
                               count(*) filter (where verdict = 'outside') as outside,
                               count(*) filter (where verdict = 'outside_gateway') as gateway,
                               count(*) filter (where verdict = 'unknown') as unk,
                               count(*) filter (where verdict = 'nolock') as nolock,
                               round((percentile_cont(0.5) within group (order by queue_s))::numeric, 1) as qmed,
                               round(avg(act_ms - exp_ms) filter (where exp_ms > 0 and act_ms > 0), 1) as adiff
                        from mr group by picked) g),
    'ping', (select jsonb_build_object(
               'n', count(*),
               'avg_expected', round(avg(exp_ms), 1), 'avg_actual', round(avg(act_ms), 1),
               'avg_diff', round(avg(act_ms - exp_ms), 1),
               'median_diff', round((percentile_cont(0.5) within group (order by act_ms - exp_ms))::numeric, 1),
               'pairs', coalesce(jsonb_agg(jsonb_build_array(exp_ms, act_ms)), '[]'::jsonb))
             from pairs),
    'dcs', (select coalesce(jsonb_agg(jsonb_build_object('region', picked, 'dc', dc, 'n', n, 'known', known)
                                      order by picked, n desc), '[]'::jsonb)
            from dcs where rn <= 6),
    'unknown_dc', (select count(*) from m where dc is not null and region is null)
  ) into mt;

  -- the problems the apps reported by themselves in the last 7 days
  select coalesce(jsonb_agg(jsonb_build_object('code', code, 'n', n, 'players', players) order by n desc, code), '[]'::jsonb)
    into iss
  from (
    select coalesce(nullif(data->>'code', ''), nullif(split_part(detail, ':', 1), ''), '?') as code,
           count(*) as n, count(distinct install_id) as players
    from events where kind = 'issue' and at > now() - interval '7 days'
    group by 1 order by 2 desc, 1 limit 15
  ) x;

  return jsonb_build_object(
    'days', nd, 'generated_at', to_char(now() at time zone 'utc', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
    'counters', c, 'deltas', dl, 'series', ser, 'breakdowns', brk, 'match', mt, 'issues', iss);
end;
$$;

-- only the secret key (service_role) may read the numbers; Supabase grants new functions to anon/authenticated by
-- default, so they are taken back explicitly
revoke all on function public.dev_stats(int, int) from public, anon, authenticated;
grant execute on function public.dev_stats(int, int) to service_role;
revoke all on function public.dev_num(text) from public, anon, authenticated;
grant execute on function public.dev_num(text) to service_role;

-- No new index: every query above reads events through events_kind_at_idx (kind, at), events_at_idx (at) or
-- events_install_at_idx, and installs through installs_last_seen_idx / installs_first_seen_idx (supabase-setup.sql).
