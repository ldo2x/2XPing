-- 2X Ping: let checkin() accept 'feedback' (messages sent from the app's «تواصل معنا» / Contact us form).
-- Run in Supabase: SQL Editor > New query > paste > Run. Safe to run again: it only re-creates checkin() exactly as in
-- tools/supabase-setup.sql (no table changes). Until it runs, the server drops the app's messages without an error.

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

  -- only the event kinds the app sends ('feedback' = a message from «تواصل معنا», 0.13)
  if k not in ('heartbeat', 'start', 'exit', 'test', 'lock', 'unlock', 'detect', 'boost', 'reset', 'error', 'issue',
               'use', 'settings', 'traffic', 'update', 'locktest', 'match', 'feedback') then
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
