# نشر نسخة جديدة | Releasing a version

كل نسخة تنرفع، يتحدّث معها كل شي مربوط فيها، مو بس الكود.
Every release updates everything tied to it, not just the code.

The public version only shows the games marked `Released = true` in `src/GameDb.cs`. Every screenshot, list and text below must match them.

## 1. الكود | Code
- [ ] `src/Brand.cs`: `AssemblyVersion`, `AssemblyFileVersion`, `AssemblyInformationalVersion`
- [ ] `CHANGELOG.md`: rename "Next (in testing)" to `## vX.Y.Z-beta (date)`
- [ ] `.github/release-notes.md`: what's new (Arabic | English), and who updates by hand
- [ ] `powershell -ExecutionPolicy Bypass -File build.ps1 -Release` builds without errors

## 2. صفحة قيتهب | GitHub page
- [ ] `README.md` and `README.en.md`: features table, supported games, how to use, known issues (keep the search phrases: "server selector", "Deadlock server selector", «اختيار سيرفر ديدلوك», and the FAQ)
- [ ] Website (GitHub Pages from `docs/`): `docs/index.html` and `docs/ar/index.html` (text, FAQ and its JSON-LD, supported games), and `<lastmod>` in `docs/sitemap.xml`
- [ ] `docs/screenshot-ar.png` and `docs/screenshot-en.png`: fresh captures of the new version
- [ ] `docs/social/social-preview.html` -> `social-preview.png` (Edge headless, 1280x640), then upload it in the repo's Settings > Social preview (can't be done from the command line)
- [ ] Description and topics: `gh repo edit ldo2x/2XPing --description "..." --add-topic ... --remove-topic ...`
- [ ] `ROADMAP.md`: tick what shipped, update "Now" / "Next"
- [ ] `SECURITY.md`: any new data sent, new services contacted, new system changes

## 3. الشرح | Guide and slides
- [ ] `docs/guide/guide.html` -> `docs/2XPing-Guide-AR.pdf` (Edge `--print-to-pdf`)
- [ ] `docs/presentation/slides.html` -> `img/slideN.png` (`?n=N`, 1920x1080) and `docs/2XPing-Slides-AR.pdf`
- [ ] `docs/presentation/build-pptx.js` speaker notes -> `docs/2XPing-Slides-AR.pptx` (`node build-pptx.js`)
- [ ] Check every slide image: nothing overflowing into the footer

## 4. قاعدة البيانات | Database
- [ ] `tools/supabase-setup.sql`: new columns, indexes or kinds the version sends; tell the developer to run it in the SQL Editor
- [ ] Run the SQL in Supabase **before** publishing a build that depends on it (e.g. `game_stats.regions_source`: until the SQL runs, the new app ignores the shared numbers and every PC asks deadlock-api itself)

## 5. النشر | Publish
- [ ] Commit, push `main`, then `git tag vX.Y.Z-beta` and push the tag: GitHub Actions builds and publishes the release with the zip and SHA256
- [ ] Check the release page has both files and is marked Pre-release
