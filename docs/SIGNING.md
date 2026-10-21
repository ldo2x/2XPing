# Code signing with SignPath Foundation

How to get `2XPing.exe` signed for free by [SignPath Foundation](https://signpath.org/), and what to do on every release.
The public side is already in place: the [Code signing policy](https://ldo2x.github.io/2XPing/code-signing.html) page
(EN + AR, linked from both home pages), the "Code signing policy" sections in both READMEs and the release notes,
the privacy policy in [SECURITY.md](../SECURITY.md#usage-statistics-beta), uninstall steps, and the CI steps in
[.github/workflows/build.yml](../.github/workflows/build.yml).

## Why bother (in plain words)

- **SmartScreen ("Windows protected your PC").** Windows warns about downloaded programs it doesn't know. An unsigned exe
  starts from zero every release, because each new build is a new file. A signed exe carries a certificate, and the
  reputation Windows builds up belongs to that certificate, so it carries over from one release to the next. The warning
  usually fades after enough people have downloaded signed releases; signing alone doesn't make it disappear on day one.
- **Smart App Control (Windows 11).** On PCs where it is on, Smart App Control blocks unsigned programs it can't vouch
  for, with no "Run anyway". A program signed with a trusted certificate is allowed to run (it can still be blocked if it
  gets a bad reputation). So for those players, signing is the difference between "works" and "can't be opened".
- **Trust.** The signature proves the exe came from this repository's CI and wasn't changed afterwards. The in-app updater
  still checks the SHA256 file, which the workflow now makes from the signed zip.

## One-time setup

1. **Turn on two-factor authentication on GitHub** (Settings > Password and authentication). SignPath requires MFA for
   everyone on the project, on GitHub and on SignPath.
2. **Check the public pages are live.** Push `main` so GitHub Pages serves `docs/` (the site at
   https://ldo2x.github.io/2XPing/ with `code-signing.html` and `ar/code-signing.html`), and the README shows the
   "Code signing policy" section. SignPath reviews these.
3. **Apply** at https://signpath.org/apply. Give the repository URL (https://github.com/ldo2x/2XPing), the website,
   the licence (MIT), and point to the Code signing policy page. Say releases are built by GitHub Actions from tags and
   approved by hand. Wait for the approval email; SignPath Foundation then creates an organization for you on
   https://app.signpath.io.
4. **Sign in to SignPath** (turn on MFA there too) and note the **Organization ID** (in the organization's settings).
5. **Connect GitHub.** In SignPath, under Trusted Build Systems, add the predefined **GitHub.com** build system to the
   organization. Installing the SignPath GitHub App on the repository is optional for a public repository.
6. **Create the project** with slug **`2XPing`**, linked to the repository and to the GitHub.com trusted build system.
7. **Artifact configuration.** In the project, add an artifact configuration with slug **`initial`** and paste the
   contents of [.signpath/artifact-configuration.xml](../.signpath/artifact-configuration.xml). It signs only
   `2XPing.exe` inside the package, and only if its product name is "2X Ping" and its product version is the one the
   workflow passes (the exe's own version, e.g. `0.12.0-beta`).
8. **Signing policy.** Create a signing policy with slug **`release-signing`** using the certificate SignPath Foundation
   gave you. Set **lDo2x** as submitter and as approver, and require **manual approval** for every request.
9. **API token.** Create a CI user (or use your own) with permission to submit to the `release-signing` policy, and make
   an API token for it.
10. **GitHub secret and variable.** In the repository: Settings > Secrets and variables > Actions:
    - **Secret** `SIGNPATH_API_TOKEN` = the API token.
    - **Variable** `SIGNPATH_ORGANIZATION_ID` = the Organization ID.

Until the secret exists, the workflow publishes releases unsigned, exactly as before.

## Every release

Follow [RELEASING.md](RELEASING.md) as usual. After pushing the tag:

1. The **Build** workflow builds the release, uploads the unsigned package (artifact `2XPing-unsigned`) and sends it to
   SignPath. The job then waits (up to 5 hours).
2. **Approve it in SignPath**: open https://app.signpath.io > Signing requests, check it is the tag you just pushed
   (the request links to the GitHub run), and press **Approve**. Reject anything you didn't start.
3. The workflow downloads the signed files, checks that `2XPing.exe` has a valid signature, zips them again under the
   same name, writes a new `.sha256` from that zip, and publishes the release with them.
4. Check: download the zip from the release, right-click `2XPing.exe` > Properties > Digital Signatures: the signer is
   SignPath Foundation. The in-app update from the previous version should still work (it checks the new SHA256).

If you miss the 5-hour window, the job fails and nothing is published: re-run the workflow and approve the new request.

## If something fails

- **"product name / version does not match"**: the exe's version resource must say `2X Ping` and the version in
  `src/Brand.cs` (`AssemblyProduct`, `AssemblyInformationalVersion`). Check with
  `(Get-Item dist\2XPing.exe).VersionInfo`.
- **The signing step is skipped**: the secret `SIGNPATH_API_TOKEN` is missing or empty.
- **"artifact not found"**: SignPath reads the run's artifacts with the job's token; the workflow already asks for
  `actions: read`.
