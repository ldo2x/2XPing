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

create index if not exists events_at_idx on public.events (at desc);
create index if not exists events_install_idx on public.events (install_id);
create index if not exists installs_last_seen_idx on public.installs (last_seen desc);

-- No direct access for the public key: tables are locked, only the function below can write.
alter table public.installs enable row level security;
alter table public.events enable row level security;
revoke all on public.installs from anon, authenticated;
revoke all on public.events from anon, authenticated;

-- One call per heartbeat / event. Updates the user's row and logs non-heartbeat events.
create or replace function public.checkin(p jsonb)
returns void
language plpgsql
security definer
set search_path = public
as $$
declare
  iid uuid := (p->>'install_id')::uuid;
  k   text := coalesce(left(p->>'kind', 20), 'heartbeat');
begin
  insert into installs (install_id, nickname, device_name, app_version, os, lang, mode, country, city,
                        current_game, current_server, current_ping, last_seen)
  values (iid, left(p->>'nickname', 40), left(p->>'device_name', 80), left(p->>'app_version', 20),
          left(p->>'os', 60), left(p->>'lang', 5), left(p->>'mode', 10), left(p->>'country', 60), left(p->>'city', 80),
          left(p->>'game', 60), left(p->>'server', 80), nullif(p->>'ping', '')::int, now())
  on conflict (install_id) do update set
    nickname       = excluded.nickname,
    device_name    = excluded.device_name,
    app_version    = excluded.app_version,
    os             = excluded.os,
    lang           = excluded.lang,
    mode           = excluded.mode,
    country        = coalesce(excluded.country, installs.country),
    city           = coalesce(excluded.city, installs.city),
    current_game   = excluded.current_game,
    current_server = excluded.current_server,
    current_ping   = excluded.current_ping,
    last_seen      = now();

  if k <> 'heartbeat' then
    insert into events (install_id, kind, game, server, ping, detail, app_version)
    values (iid, k, left(p->>'game', 60), left(p->>'server', 80), nullif(p->>'ping', '')::int,
            left(p->>'detail', 4000), left(p->>'app_version', 20));
  end if;
end;
$$;

revoke all on function public.checkin(jsonb) from public;
grant execute on function public.checkin(jsonb) to anon, authenticated;
