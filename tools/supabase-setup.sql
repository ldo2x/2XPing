-- 2X Ping usage statistics (Supabase / Postgres).
-- Run once in Supabase: SQL Editor > New query > paste > Run.
--
-- The app can ONLY call the checkin() function (write-only). It can never read anything.
-- Reading is done from the developer dashboard with the secret key, which never goes into the app.

create table if not exists public.installs (
  install_id     uuid primary key,
  nickname       text,
  device_name    text,
  app_version    text,
  os             text,
  lang           text,
  mode           text,
  country        text,
  city           text,
  current_game   text,
  current_server text,
  current_ping   int,
  first_seen     timestamptz not null default now(),
  last_seen      timestamptz not null default now()
);

create table if not exists public.events (
  id          bigint generated always as identity primary key,
  install_id  uuid not null,
  at          timestamptz not null default now(),
  kind        text not null,   -- start, test, lock, unlock, detect, boost, reset, error
  game        text,
  server      text,
  ping        int,
  detail      text,
  app_version text
);

-- 0.10: more details for development (safe to run again on an existing project)
alter table public.installs add column if not exists isp  text;
alter table public.installs add column if not exists asn  text;
alter table public.installs add column if not exists info jsonb;  -- hardware/system/setup, sent once per launch
alter table public.events   add column if not exists data jsonb;  -- structured event details (e.g. every ping result)
alter table public.events   add column if not exists session text; -- random id per launch

create index if not exists events_at_idx on public.events (at desc);
create index if not exists events_install_idx on public.events (install_id);
create index if not exists installs_last_seen_idx on public.installs (last_seen desc);

-- 0.11: problems reported by the app by themselves (kind = 'issue', data->>'code' = the problem)
-- the developer tab's Problems list reads the newest issues; this keeps it fast as the table grows
create index if not exists events_kind_at_idx on public.events (kind, at desc);
-- per-player flood guard (see checkin below)
create index if not exists events_install_at_idx on public.events (install_id, at desc);
-- 0.12: global guards in checkin (new installs per minute)
create index if not exists installs_first_seen_idx on public.installs (first_seen desc);
-- the one-time setup check report (install id of zeros), if it is still there
delete from public.events where install_id = '00000000-0000-0000-0000-000000000000';
delete from public.installs where install_id = '00000000-0000-0000-0000-000000000000';

-- No direct access for the public key: tables are locked, only the function below can write.
alter table public.installs enable row level security;
alter table public.events enable row level security;
revoke all on public.installs from anon, authenticated;
revoke all on public.events from anon, authenticated;

-- One call per heartbeat / event. Updates the user's row and logs non-heartbeat events.
-- The public key is in every copy of the app, so anyone can call this: everything here is treated as untrusted.
create or replace function public.checkin(p jsonb)
returns void
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
  iid  uuid;
  k    text  := coalesce(left(p->>'kind', 20), 'heartbeat');
  -- size caps, so a broken client can't fill the database
  inf  jsonb := case when pg_column_size(p->'info') < 8000 then p->'info' end;
  dat  jsonb := case when pg_column_size(p->'data') < 32000 then p->'data' end;
  -- a ping that isn't a small whole number is dropped instead of failing the whole call
  png  int   := case when p->>'ping' ~ '^-?[0-9]{1,6}$' then (p->>'ping')::int end;
begin
  -- a damaged install id: ignored (an error here would only make the client retry)
  begin
    iid := (p->>'install_id')::uuid;
  exception when others then
    return;
  end;
  if iid is null then return; end if;

  -- only the event kinds the app sends
  if k not in ('heartbeat', 'start', 'exit', 'test', 'lock', 'unlock', 'detect', 'boost', 'reset', 'error', 'issue',
               'use', 'settings', 'traffic', 'update', 'locktest') then
    return;
  end if;

  -- global guard against random new install ids (the per-install guard below can't see those):
  -- at most 60 new installs a minute for everyone together
  if not exists (select 1 from installs where install_id = iid)
     and (select count(*) from installs where first_seen > now() - interval '1 minute') >= 60 then
    return;
  end if;

  insert into installs (install_id, nickname, device_name, app_version, os, lang, mode, country, city, isp, asn, info,
                        current_game, current_server, current_ping, last_seen)
  values (iid, left(p->>'nickname', 40), left(p->>'device_name', 80), left(p->>'app_version', 20),
          left(p->>'os', 60), left(p->>'lang', 5), left(p->>'mode', 10), left(p->>'country', 60), left(p->>'city', 80),
          left(p->>'isp', 100), left(p->>'asn', 100), inf,
          left(p->>'game', 60), left(p->>'server', 80), png, now())
  on conflict (install_id) do update set
    nickname       = excluded.nickname,
    device_name    = excluded.device_name,
    app_version    = excluded.app_version,
    os             = excluded.os,
    lang           = excluded.lang,
    mode           = excluded.mode,
    country        = coalesce(excluded.country, installs.country),
    city           = coalesce(excluded.city, installs.city),
    isp            = coalesce(excluded.isp, installs.isp),
    asn            = coalesce(excluded.asn, installs.asn),
    info           = coalesce(excluded.info, installs.info),
    current_game   = excluded.current_game,
    current_server = excluded.current_server,
    current_ping   = excluded.current_ping,
    last_seen      = now();

  -- a broken client stuck in a loop can't flood the table: at most 60 events a minute per install,
  -- and at most 1200 a minute for everyone together (far above real use)
  if k <> 'heartbeat'
     and (select count(*) from events where install_id = iid and at > now() - interval '1 minute') < 60
     and (select count(*) from events where at > now() - interval '1 minute') < 1200 then
    insert into events (install_id, kind, game, server, ping, detail, app_version, data, session)
    values (iid, k, left(p->>'game', 60), left(p->>'server', 80), png,
            left(p->>'detail', 4000), left(p->>'app_version', 20), dat, left(p->>'session', 20));
  end if;
end;
$$;

revoke all on function public.checkin(jsonb) from public;
grant execute on function public.checkin(jsonb) to anon, authenticated;

-- ---------------------------------------------------------------------------------------------------------------
-- 0.11.1: shared game numbers. Supabase fetches Deadlock's numbers from deadlock-api.com (the cron job runs once a
-- minute) and every copy of the app reads them from here, instead of each PC asking deadlock-api.
-- Needs the pg_cron and pg_net extensions (Database > Extensions, or the two lines below).

create extension if not exists pg_net;
create extension if not exists pg_cron;

-- the latest numbers: public, the app reads them with the public key (no personal data in here)
create table if not exists public.game_stats (
  game            text primary key,       -- 'deadlock'
  regions         jsonb,                  -- {"Europe": 70, "Russia": 27, ...}: matches started in the last hour (top 200 live matches)
  matches_per_day int,                    -- every match deadlock-api fetched in the last day
  updated_at      timestamptz             -- when "regions" was last refreshed
);
-- busy-ness right now (1 = a usual hour). No longer computed since 0.13 (always null); kept so older apps still read the row
alter table public.game_stats add column if not exists now_factor real;
alter table public.game_stats enable row level security;
drop policy if exists game_stats_read on public.game_stats;
create policy game_stats_read on public.game_stats for select to anon, authenticated using (true);
grant select on public.game_stats to anon, authenticated;

-- every 10 minutes a copy, for busy hours per region and real queue times later (developer only)
create table if not exists public.game_stats_history (
  id              bigint generated always as identity primary key,
  at              timestamptz not null default now(),
  game            text not null,
  regions         jsonb,
  matches_per_day int
);
alter table public.game_stats_history add column if not exists now_factor real;
create index if not exists game_stats_history_at_idx on public.game_stats_history (game, at desc);
alter table public.game_stats_history enable row level security;
revoke all on public.game_stats_history from anon, authenticated;

-- pg_net answers after the job ends, so each run reads the answers of the previous one, then asks again
create table if not exists public.stats_requests (
  name       text primary key,
  request_id bigint,
  at         timestamptz
);
alter table public.stats_requests enable row level security;
revoke all on public.stats_requests from anon, authenticated;

-- 0.13: the regions come from /v1/matches/active (allowed 100 requests a second, cached by deadlock-api for 2 minutes)
-- instead of /v1/sql (20 requests an hour per address, deprecated). The hourly player_match_history query that gave
-- now_factor is gone with it: now_factor stays null and the app uses the day's average (Stats.DeadlockOnline).
delete from public.stats_requests where name = 'hourly';

create or replace function public.refresh_stats()
returns void
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
  resp    record;
  rid     bigint;
  got     boolean;
  reg     jsonb;
  per_day int;
begin
  -- 1) last run's answers. Each one is read on its own: an answer that isn't the JSON expected (an HTML error page,
  -- a changed format) is skipped, so step 2 still asks again and the job never gets stuck failing every minute.
  -- The live-match list: matches started in the last hour per region_mode (ECitadelRegionMode: 0 Row, 1 Europe,
  -- 2 SEAsia, 3 SAmerica, 4 Russia, 5 Oceania), each match once. The same counting as Stats.DeadlockActivity.
  rid := (select request_id from stats_requests where name = 'regions');
  select r.status_code, r.content into resp from net._http_response r where r.id = rid;
  got := found;
  if got and resp.status_code = 200 then
    begin
      select coalesce(jsonb_object_agg(t.k, t.n), '{}'::jsonb) into reg from (
        select case coalesce(e->>'region_mode', '')
                 when '0' then 'Row' when '1' then 'Europe' when '2' then 'SEAsia'
                 when '3' then 'SAmerica' when '4' then 'Russia' when '5' then 'Oceania'
                 else case e->>'region_mode_parsed'
                        when 'row' then 'Row' when 'europe' then 'Europe' when 'se_asia' then 'SEAsia'
                        when 's_america' then 'SAmerica' when 'russia' then 'Russia' when 'oceania' then 'Oceania'
                      end
               end as k,
               count(distinct coalesce(e->>'match_id', md5(e::text))) as n
        from jsonb_array_elements(resp.content::jsonb) e
        where (e->>'start_time')::bigint > extract(epoch from now()) - 3600
        group by 1
      ) t
      where t.k is not null;
    exception when others then
      reg := null;
    end;
  end if;
  -- the list is about 200 KB: once read, its answer is dropped instead of waiting hours for pg_net's own cleanup
  if got then
    begin
      delete from net._http_response where id = rid;
    exception when others then
      null;
    end;
  end if;
  select r.status_code, r.content into resp from net._http_response r
    where r.id = (select request_id from stats_requests where name = 'info');
  if found and resp.status_code = 200 then
    begin
      per_day := (resp.content::jsonb->>'fetched_matches_per_day')::int;
    exception when others then
      per_day := null;
    end;
  end if;

  if reg is not null or per_day is not null then
    insert into game_stats (game, regions, matches_per_day, now_factor, updated_at)
    values ('deadlock', reg, per_day, null, case when reg is not null then now() end)
    on conflict (game) do update set
      regions         = coalesce(excluded.regions, game_stats.regions),
      matches_per_day = coalesce(excluded.matches_per_day, game_stats.matches_per_day),
      now_factor      = null, -- no longer computed (see 0.13 above): a stale factor must not stay forever
      updated_at      = coalesce(excluded.updated_at, game_stats.updated_at);
    if not exists (select 1 from game_stats_history where game = 'deadlock' and at > now() - interval '10 minutes') then
      insert into game_stats_history (game, regions, matches_per_day, now_factor)
        select game, regions, matches_per_day, now_factor from game_stats where game = 'deadlock';
      delete from game_stats_history where at < now() - interval '180 days';
    end if;
  end if;

  -- 2) ask again: the live matches every 2 minutes (deadlock-api caches them for 120 s, asking more often gets the
  -- same answer), the daily total every 10 minutes (it changes slowly)
  if not exists (select 1 from stats_requests where name = 'regions' and at > now() - interval '110 seconds') then
    insert into stats_requests (name, request_id, at)
    values ('regions', net.http_get('https://api.deadlock-api.com/v1/matches/active'), now())
    on conflict (name) do update set request_id = excluded.request_id, at = excluded.at;
  end if;
  if not exists (select 1 from stats_requests where name = 'info' and at > now() - interval '10 minutes') then
    insert into stats_requests (name, request_id, at)
    values ('info', net.http_get('https://api.deadlock-api.com/v1/info'), now())
    on conflict (name) do update set request_id = excluded.request_id, at = excluded.at;
  end if;
end;
$$;

revoke all on function public.refresh_stats() from public, anon, authenticated;

-- once a minute (running this file again just updates the job)
select cron.schedule('2xping-stats', '* * * * *', 'select public.refresh_stats()');

-- ---------------------------------------------------------------------------------------------------------------
-- Optional retention (NOT enabled: it permanently deletes data, so the developer decides). Uncomment to keep events
-- for 90 days and installs not seen for a year:
-- select cron.schedule('2xping-retention', '17 3 * * *', $r$
--   delete from public.events where at < now() - interval '90 days';
--   delete from public.installs where last_seen < now() - interval '1 year';
-- $r$);
