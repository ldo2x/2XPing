# Web dashboard (developer only)

A private page with the same numbers as the app's developer mode: overview and charts, users and their state right now, one user's history, problems, messages, versions and the event log. It works on a phone.

- Address: https://ldo2x.github.io/2XPing/dev/ (linked from nowhere, `noindex`)
- Files: `docs/dev/index.html`, `app.js`, `app.css` (and `demo.js` for the demo)
- Database side: `tools/supabase-web-dashboard.sql`

## How it is protected

- The page carries only the **public** (publishable) key that is already in every copy of the app. No secret key is used or needed, and none may ever be put in these files.
- You sign in with e-mail and password through Supabase Auth. Sign-ups are off (step 1), so only the account you add exists.
- The page reads nothing from the tables directly (they stay locked, as in `supabase-setup.sql`). It calls only the `web_*` functions, and each one first checks that the signed-in account is listed in `dev_owners`. Anyone else, signed in or not, gets `not allowed`.
- With two-step sign-in turned on (step 4), the database also refuses any session that has not passed it, so a leaked password alone opens nothing.

## Setup (once)

1. **Turn off sign-ups.** Supabase > Authentication > Sign In / Providers: switch off **Allow new users to sign up**. Leave the Email provider on.
2. **Add your account.** Authentication > Users > **Add user** > Create new user: your e-mail and a strong, unique password (a password manager's generated one). Tick **Auto Confirm User**.
3. **Run the SQL.** SQL Editor > New query: paste all of `tools/supabase-web-dashboard.sql` and Run (it needs `supabase-setup.sql` and `supabase-dev-stats.sql` to have run before; it says so if not). Then, in a new query, run the owner line from the end of that file with your e-mail in place of `OWNER_EMAIL_HERE`:

   ```sql
   insert into dev_owners select id from auth.users where email = 'OWNER_EMAIL_HERE' on conflict do nothing;
   ```

   Check it: `select o.user_id, u.email from dev_owners o join auth.users u on u.id = o.user_id;` shows one row. Don't save your e-mail into the file in the repo.
4. **Two-step sign-in (optional, recommended).** Open the page, sign in, press the shield button at the top, then **Start**: scan the code with an authenticator app and type the 6 digits. From then on every sign-in asks for a code. (TOTP is on by default in Supabase: Authentication > Multi-Factor.)
5. **Open the page:** https://ldo2x.github.io/2XPing/dev/ and sign in.

## Using it

- It refreshes by itself every 30 seconds while the tab is open and in front, and pauses when it isn't. **Refresh** (حدّث) loads now. The time of the last update is at the top.
- **ع / EN** switches the language; the choice is remembered in that browser.
- "Online" means a heartbeat in the last 4.5 minutes, like the app's developer panel.
- Read marks on messages are kept only in the browser you mark them in.
- **Sign out** (اطلع) when you used someone else's device: the session stays in that browser until you do.

## When something is wrong

| The page says | What to do |
|---|---|
| Wrong e-mail or password | Retype it. Forgotten: in Authentication > Users delete the user, add it again (step 2), then run the owner line again (step 3) |
| This account has no access (not allowed) | The account isn't in `dev_owners`: step 3. If you just turned on two-step sign-in, sign out and in again |
| The web_* functions aren't in the database yet | Run `tools/supabase-web-dashboard.sql` (step 3) |
| No connection to Supabase | Check the internet and press Refresh |

To take access away: `delete from dev_owners where user_id = (select id from auth.users where email = '...');` (deleting the user in Authentication > Users does it too).

## Demo

`https://ldo2x.github.io/2XPing/dev/?demo=1` shows the page with made-up data from `demo.js`. It never loads the Supabase library and never contacts Supabase. Add `&lang=en` for English. Screens of it are in `docs/dev/screens/`.

## Changing it

- New numbers belong in a `web_*` function (security definer, `set search_path = public, pg_temp`, the `is_dev_owner()` check first, revoked from `public` and `anon`, granted to `authenticated` only). Never open a table to `authenticated`.
- The Supabase library is pinned to one version with an integrity hash (`LIB` at the top of `app.js`). To update it, change the version and the hash together (jsDelivr lists a file's hash on its package page).
- Put data on the page only as text (the `el()` helper), never as HTML: names, messages and details come from players.
