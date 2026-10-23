-- 2XPing web dashboard (docs/dev/, https://ldo2x.github.io/2XPing/dev/): what the page may read, and who may read it.
-- Run in Supabase: SQL Editor > New query > paste > Run. Safe to run again (tables "if not exists", functions replaced).
-- Needs tools/supabase-setup.sql and tools/supabase-dev-stats.sql first. Owner steps: docs/WEB-DASHBOARD.md.
--
-- How it is protected:
--   * The page only has the PUBLIC (publishable) key that is already in every copy of the app, plus a Supabase Auth
--     session (email + password, optionally MFA). No secret key is involved.
--   * The tables stay locked (supabase-setup.sql: RLS on, no grants to anon / authenticated).
--   * Every web_* function below runs as its owner (security definer) and first checks is_dev_owner(): the signed-in
--     user must be listed in dev_owners. Anyone else, signed in or not, gets 'not allowed' (SQLSTATE 42501).
--   * Only the role "authenticated" may call them; "anon" (no session) and "public" may not.
--   * With MFA set up for the owner, the session must also have passed it (aal2): a stolen password alone is not enough.

-- the guard below needs dev_stats() (web_stats calls it); stop early with a clear message when it is missing
do $$
begin
  if to_regprocedure('public.dev_stats(integer, integer)') is null then
    raise exception 'dev_stats() is missing: run tools/supabase-dev-stats.sql first, then this file again';
  end if;
end;
$$;

-- ---------------------------------------------------------------------------------------------------------------
-- Who may read: one row per owner account (Authentication > Users). No policies: nobody reads or writes this table
-- through the API; only the SQL editor (and is_dev_owner() below) can see it.
create table if not exists public.dev_owners (
  user_id uuid primary key references auth.users (id) on delete cascade
);
alter table public.dev_owners enable row level security;
revoke all on public.dev_owners from public, anon, authenticated;

-- true when the caller's session belongs to an owner. When that account has a verified MFA factor, the session must
-- have passed it too (aal2); without a factor, a password session is enough.
create or replace function public.is_dev_owner()
returns boolean
language sql
stable
security definer
set search_path = public, pg_temp
as $$
  select exists (select 1 from public.dev_owners o where o.user_id = auth.uid())
     and (coalesce(auth.jwt() ->> 'aal', '') = 'aal2'
          or not exists (select 1 from auth.mfa_factors f where f.user_id = auth.uid() and f.status = 'verified'))
$$;

revoke all on function public.is_dev_owner() from public, anon;
grant execute on function public.is_dev_owner() to authenticated;

-- a jsonb value cut down when it is big (ping tests carry every region's results): the page never needs those in full
create or replace function public.web_small(j jsonb, max_bytes int default 6000)
returns jsonb
language sql
immutable
set search_path = public, pg_temp
as $$
  select case when j is null then null
              when pg_column_size(j) <= max_bytes then j
              else jsonb_build_object('_trimmed', true, '_bytes', pg_column_size(j)) end
$$;

revoke all on function public.web_small(jsonb, int) from public, anon, authenticated;

-- an install that sends without personal details (Settings: «شارك إحصائيات البيتا» off), as Telemetry.IsAnonymous
create or replace function public.web_is_anon(nick text, info jsonb)
returns boolean
language sql
immutable
set search_path = public, pg_temp
as $$
  select coalesce(nick = '(anonymous)', false) or coalesce(info ->> 'anonymous' = 'true', false)
$$;

revoke all on function public.web_is_anon(text, jsonb) from public, anon, authenticated;

-- ---------------------------------------------------------------------------------------------------------------
-- Overview: everything dev_stats() returns (supabase-dev-stats.sql), plus what the web tiles add:
--   counters.online_now counted with the app's OnlineWindow (4.5 minutes, Telemetry.OnlineWindow; dev_stats uses 5),
--   "period": new installs, matches, locks, feedback and active installs in the chosen period, each with the same
--   count for the period before it, up to the same point in time ("prev"),
--   "now_ping": the ping each online install reports right now (for the ping distribution).
create or replace function public.web_stats(p_days int default 30, p_tz_minutes int default 180)
returns jsonb
language plpgsql
stable
security definer
set search_path = public, pg_temp
as $$
declare
  nd     int      := greatest(1, least(coalesce(p_days, 30), 180));
  tz_off interval := make_interval(mins => greatest(-840, least(coalesce(p_tz_minutes, 0), 840)));
  today  date;
  t0     timestamptz;
  p0     timestamptz;   -- start of the period (the same day boundary dev_stats uses)
  q0     timestamptz;   -- start of the period before
  q1     timestamptz;   -- "now" one period ago: the comparison stops here
  base   jsonb;
  per    jsonb;
  pings  jsonb;
begin
  if not public.is_dev_owner() then
    raise exception 'not allowed' using errcode = '42501';
  end if;

  base := public.dev_stats(nd, p_tz_minutes);

  today := ((now() at time zone 'utc') + tz_off)::date;
  t0 := (today::timestamp - tz_off) at time zone 'utc';
  p0 := t0 - (nd - 1) * interval '1 day';
  q0 := p0 - nd * interval '1 day';
  q1 := now() - nd * interval '1 day';

  select jsonb_build_object(
    'new',      jsonb_build_object('now',  (select count(*) from public.installs where first_seen >= p0),
                                   'prev', (select count(*) from public.installs where first_seen >= q0 and first_seen < q1)),
    'active',   jsonb_build_object('now',  (select count(distinct install_id) from public.events where at >= p0),
                                   'prev', (select count(distinct install_id) from public.events where at >= q0 and at < q1)),
    'matches',  jsonb_build_object('now',  (select count(*) from public.events where kind = 'match' and at >= p0
                                              and coalesce(data ->> 'same_server_as_prev', 'false') <> 'true'),
                                   'prev', (select count(*) from public.events where kind = 'match' and at >= q0 and at < q1
                                              and coalesce(data ->> 'same_server_as_prev', 'false') <> 'true')),
    'locks',    jsonb_build_object('now',  (select count(*) from public.events where kind = 'lock' and at >= p0),
                                   'prev', (select count(*) from public.events where kind = 'lock' and at >= q0 and at < q1)),
    'feedback', jsonb_build_object('now',  (select count(*) from public.events where kind = 'feedback' and at >= p0),
                                   'prev', (select count(*) from public.events where kind = 'feedback' and at >= q0 and at < q1)),
    'feedback_7d_prev', (select count(*) from public.events where kind = 'feedback'
                           and at > now() - interval '14 days' and at <= now() - interval '7 days')
  ) into per;

  select coalesce(jsonb_agg(current_ping), '[]'::jsonb) into pings
  from public.installs
  where last_seen > now() - interval '270 seconds' and current_ping > 0 and current_ping < 2000;

  return jsonb_set(base, '{counters,online_now}',
                   to_jsonb((select count(*) from public.installs where last_seen > now() - interval '270 seconds')))
         || jsonb_build_object('period', per, 'now_ping', pings, 'online_window_s', 270);
end;
$$;

-- ---------------------------------------------------------------------------------------------------------------
-- Users: every install with its state now (newest first, at most 5000, like the in-app panel).
-- "lock": the latest of lock / unlock / start / exit for the install; locked = that is a lock and the install is
-- online (closing the app or a crash always removes the lock, so an offline install is never locked).
create or replace function public.web_installs()
returns jsonb
language plpgsql
stable
security definer
set search_path = public, pg_temp
as $$
begin
  if not public.is_dev_owner() then
    raise exception 'not allowed' using errcode = '42501';
  end if;

  return jsonb_build_object(
    'now', to_char(now() at time zone 'utc', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
    'online_window_s', 270,
    'installs', (
      select coalesce(jsonb_agg(jsonb_build_object(
               'id', i.install_id,
               'anonymous', public.web_is_anon(i.nickname, i.info),
               'name', case when public.web_is_anon(i.nickname, i.info) then null else i.nickname end,
               'device', nullif(i.device_name, ''),
               'country', nullif(i.country, ''), 'city', nullif(i.city, ''),
               'isp', nullif(i.isp, ''), 'asn', nullif(i.asn, ''),
               'version', i.app_version, 'os', i.os, 'lang', i.lang, 'mode', i.mode,
               'game', i.current_game, 'server', i.current_server, 'ping', i.current_ping,
               'net', i.info ->> 'net', 'windows', i.info ->> 'windows',
               'first_seen', i.first_seen, 'last_seen', i.last_seen,
               'online', i.last_seen > now() - interval '270 seconds',
               'locked', i.last_seen > now() - interval '270 seconds' and l.kind = 'lock',
               'lock_server', case when l.kind = 'lock' then l.server end,
               'lock_at', case when l.kind = 'lock' then l.at end
             ) order by i.last_seen desc), '[]'::jsonb)
      from (select * from public.installs order by last_seen desc limit 5000) i
      left join lateral (
        select e.kind, e.server, e.at from public.events e
        where e.install_id = i.install_id and e.kind in ('lock', 'unlock', 'start', 'exit')
        order by e.at desc limit 1
      ) l on true
    ));
end;
$$;

-- ---------------------------------------------------------------------------------------------------------------
-- One user: the whole installs row (with the launch details in "info"), counts per event kind, and the latest 300
-- events (heartbeats are never stored as events). Big event data (ping tests) comes back trimmed.
create or replace function public.web_install(p_id uuid)
returns jsonb
language plpgsql
stable
security definer
set search_path = public, pg_temp
as $$
declare
  r public.installs%rowtype;
begin
  if not public.is_dev_owner() then
    raise exception 'not allowed' using errcode = '42501';
  end if;

  select * into r from public.installs where install_id = p_id;
  if not found then
    return null;
  end if;

  return jsonb_build_object(
    'now', to_char(now() at time zone 'utc', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
    'install', jsonb_build_object(
      'id', r.install_id,
      'anonymous', public.web_is_anon(r.nickname, r.info),
      'name', case when public.web_is_anon(r.nickname, r.info) then null else r.nickname end,
      'device', nullif(r.device_name, ''), 'country', nullif(r.country, ''), 'city', nullif(r.city, ''),
      'isp', nullif(r.isp, ''), 'asn', nullif(r.asn, ''),
      'version', r.app_version, 'os', r.os, 'lang', r.lang, 'mode', r.mode,
      'game', r.current_game, 'server', r.current_server, 'ping', r.current_ping,
      'first_seen', r.first_seen, 'last_seen', r.last_seen,
      'online', r.last_seen > now() - interval '270 seconds',
      'info', public.web_small(r.info, 8000)),
    'lock', (select jsonb_build_object('kind', e.kind, 'server', e.server, 'at', e.at, 'detail', e.detail)
             from public.events e
             where e.install_id = p_id and e.kind in ('lock', 'unlock', 'start', 'exit')
             order by e.at desc limit 1),
    'counts', (select coalesce(jsonb_object_agg(kind, n), '{}'::jsonb)
               from (select kind, count(*) as n from public.events where install_id = p_id group by kind) c),
    'events', (select coalesce(jsonb_agg(jsonb_build_object(
                 'id', e.id, 'at', e.at, 'kind', e.kind, 'game', e.game, 'server', e.server, 'ping', e.ping,
                 'detail', left(e.detail, 2000), 'version', e.app_version, 'session', e.session,
                 'data', public.web_small(e.data)) order by e.at desc), '[]'::jsonb)
               from (select * from public.events where install_id = p_id order by at desc limit 300) e)
  );
end;
$$;

-- ---------------------------------------------------------------------------------------------------------------
-- Problems the apps reported by themselves (kind 'issue'), grouped by code: how often, how many players, which
-- versions, and the latest 5 reports of each with who sent them.
create or replace function public.web_issues(p_days int default 7)
returns jsonb
language plpgsql
stable
security definer
set search_path = public, pg_temp
as $$
declare
  nd int := greatest(1, least(coalesce(p_days, 7), 180));
begin
  if not public.is_dev_owner() then
    raise exception 'not allowed' using errcode = '42501';
  end if;

  return (
    with iss as (
      select e.*, coalesce(nullif(e.data ->> 'code', ''), nullif(split_part(e.detail, ':', 1), ''), '?') as code
      from public.events e
      where e.kind = 'issue' and e.at > now() - nd * interval '1 day'
    ), ranked as (
      select iss.*, row_number() over (partition by code order by at desc) as rn from iss
    )
    select coalesce(jsonb_agg(g order by (g ->> 'n')::int desc, g ->> 'code'), '[]'::jsonb)
    from (
      select jsonb_build_object(
               'code', code, 'n', count(*), 'players', count(distinct install_id),
               'versions', (select coalesce(jsonb_agg(v order by v desc), '[]'::jsonb)
                            from (select distinct app_version as v from iss x where x.code = r.code and app_version is not null) vv),
               'first_at', min(at), 'last_at', max(at),
               'latest', (select coalesce(jsonb_agg(jsonb_build_object(
                             'at', y.at, 'install_id', y.install_id,
                             'name', case when public.web_is_anon(i.nickname, i.info) then null else i.nickname end,
                             'anonymous', coalesce(public.web_is_anon(i.nickname, i.info), false),
                             'version', y.app_version, 'game', y.game, 'detail', left(y.detail, 2000),
                             'data', public.web_small(y.data, 4000)) order by y.at desc), '[]'::jsonb)
                          from ranked y left join public.installs i on i.install_id = y.install_id
                          where y.code = r.code and y.rn <= 5)
             ) as g
      from ranked r
      group by code
    ) groups
  );
end;
$$;

-- ---------------------------------------------------------------------------------------------------------------
-- Messages from «تواصل معنا» / Contact us (kind 'feedback'), newest first, at most 500.
create or replace function public.web_feedback(p_days int default 90)
returns jsonb
language plpgsql
stable
security definer
set search_path = public, pg_temp
as $$
declare
  nd int := greatest(1, least(coalesce(p_days, 90), 365));
begin
  if not public.is_dev_owner() then
    raise exception 'not allowed' using errcode = '42501';
  end if;

  return (
    select coalesce(jsonb_agg(jsonb_build_object(
             'id', e.id, 'at', e.at, 'install_id', e.install_id,
             'name', case when public.web_is_anon(i.nickname, i.info) then null else i.nickname end,
             'anonymous', coalesce(public.web_is_anon(i.nickname, i.info), false),
             'type', coalesce(nullif(e.data ->> 'type', ''), 'problem'),
             'text', e.detail, 'contact', nullif(e.data ->> 'contact', ''),
             'diagnostics', e.data ->> 'diagnostics', 'lang', e.data ->> 'lang',
             'version', e.app_version, 'game', e.game) order by e.at desc), '[]'::jsonb)
    from (select * from public.events
          where kind = 'feedback' and at > now() - nd * interval '1 day'
          order by at desc limit 500) e
    left join public.installs i on i.install_id = e.install_id
  );
end;
$$;

-- ---------------------------------------------------------------------------------------------------------------
-- Installs per app version: all of them, active in 7 and 30 days, online now (the page sorts and finds the latest).
create or replace function public.web_versions()
returns jsonb
language plpgsql
stable
security definer
set search_path = public, pg_temp
as $$
begin
  if not public.is_dev_owner() then
    raise exception 'not allowed' using errcode = '42501';
  end if;

  return (
    select coalesce(jsonb_agg(jsonb_build_object(
             'version', v, 'installs', n, 'active_7d', a7, 'active_30d', a30, 'online', onl, 'last_seen', ls)
             order by a7 desc, n desc), '[]'::jsonb)
    from (
      select coalesce(nullif(app_version, ''), '?') as v, count(*) as n,
             count(*) filter (where last_seen > now() - interval '7 days') as a7,
             count(*) filter (where last_seen > now() - interval '30 days') as a30,
             count(*) filter (where last_seen > now() - interval '270 seconds') as onl,
             max(last_seen) as ls
      from public.installs
      group by 1
    ) x
  );
end;
$$;

-- ---------------------------------------------------------------------------------------------------------------
-- The event log: the newest p_limit events (1..1000) of everyone, with who sent them.
create or replace function public.web_log(p_limit int default 300)
returns jsonb
language plpgsql
stable
security definer
set search_path = public, pg_temp
as $$
declare
  lim int := greatest(1, least(coalesce(p_limit, 300), 1000));
begin
  if not public.is_dev_owner() then
    raise exception 'not allowed' using errcode = '42501';
  end if;

  return (
    select coalesce(jsonb_agg(jsonb_build_object(
             'id', e.id, 'at', e.at, 'install_id', e.install_id,
             'name', case when public.web_is_anon(i.nickname, i.info) then null else i.nickname end,
             'anonymous', coalesce(public.web_is_anon(i.nickname, i.info), false),
             'kind', e.kind, 'game', e.game, 'server', e.server, 'ping', e.ping,
             'detail', left(e.detail, 1000), 'version', e.app_version,
             'data', public.web_small(e.data, 3000)) order by e.at desc), '[]'::jsonb)
    from (select * from public.events order by at desc limit lim) e
    left join public.installs i on i.install_id = e.install_id
  );
end;
$$;

-- ---------------------------------------------------------------------------------------------------------------
-- Who may call them: signed-in users only (each one still checks is_dev_owner()). Supabase grants new functions to
-- anon and authenticated by default, so everything is taken back first.
revoke all on function public.web_stats(int, int) from public, anon;
revoke all on function public.web_installs()      from public, anon;
revoke all on function public.web_install(uuid)   from public, anon;
revoke all on function public.web_issues(int)     from public, anon;
revoke all on function public.web_feedback(int)   from public, anon;
revoke all on function public.web_versions()      from public, anon;
revoke all on function public.web_log(int)        from public, anon;
grant execute on function public.web_stats(int, int) to authenticated;
grant execute on function public.web_installs()      to authenticated;
grant execute on function public.web_install(uuid)   to authenticated;
grant execute on function public.web_issues(int)     to authenticated;
grant execute on function public.web_feedback(int)   to authenticated;
grant execute on function public.web_versions()      to authenticated;
grant execute on function public.web_log(int)        to authenticated;

-- the API sees the new functions right away
notify pgrst, 'reload schema';

-- ===============================================================================================================
-- OWNER: run this ONE line once, by itself, after replacing OWNER_EMAIL_HERE with the e-mail of the user you added
-- in Authentication > Users (docs/WEB-DASHBOARD.md, step 3). Never commit your real e-mail into this file.
--
-- insert into public.dev_owners select id from auth.users where email = 'OWNER_EMAIL_HERE' on conflict do nothing;
--
-- Check it worked (one row):   select o.user_id, u.email from public.dev_owners o join auth.users u on u.id = o.user_id;
-- Take access away:            delete from public.dev_owners where user_id = (select id from auth.users where email = '...');
-- ===============================================================================================================
