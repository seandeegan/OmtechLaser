# Automatic updates

LaserPilot keeps itself up to date from this GitHub repository's **Releases**.

## How it works

1. You push a commit to `main`.
2. GitHub Actions builds LaserPilot, runs the tests, and publishes a release named **LaserPilot 0.2.N** (N = the build number) with `LaserPilot-win-x64.zip` attached. The release notes are your commit messages.
3. Every installed copy checks for a newer release when it starts and every 6 hours.
4. When one is found, a window pops up showing what's new, with **Update now**, **Remind me later** and **Skip this version**. If you're busy — a job running, homing, framing, the aiming laser on, the machine moving, or another window open — a blue banner appears instead and the pop-up waits until you're done.
5. **Update now** downloads the new version, swaps the program file, closes LaserPilot and starts the new version. Designs and settings are kept.

Updates are never installed while a job is running, and LaserPilot asks to save an unsaved design first.

Releases are made only by GitHub Actions. Don't create `v…` tags or releases by hand: the version in the tag must match the version built into the program. (If a release's program reports a different version after installing, LaserPilot says so and stops offering that release.)

## The access token (needed because the repository is private)

GitHub only shares a private repository's releases with an access token. Create one **read-only** token:

1. Open <https://github.com/settings/personal-access-tokens/new> (signed in as the repository owner).
2. **Token name:** LaserPilot updates. **Expiration:** the longest option offered.
3. **Repository access:** *Only select repositories* → choose this repository.
4. **Permissions → Repository permissions → Contents:** *Read-only*. Leave everything else at *No access*.
5. Click **Generate token** and copy it.

Then give it to LaserPilot in one of two ways.

### Option A — enter it on each computer (most private)

In LaserPilot: **Help → Update settings**, paste the token, click **Test**, then **Save**. It's stored encrypted for that Windows account and is only ever sent to GitHub.

### Option B — build it into the program (no setup on each computer)

In the repository on GitHub: **Settings → Secrets and variables → Actions → New repository secret**, name it `LASERPILOT_UPDATE_TOKEN`, paste the token. From the next push on, every build carries the token, so a fresh copy on another computer updates itself with no setup.

**Trade-off:** someone who has the program file could dig the token out of it and use it to read this repository. Only choose Option B if you don't share `LaserPilot.exe` outside people you'd trust with the source code. A token typed in on a computer (Option A) always takes priority over the built-in one.

## Installing on another computer for the first time

1. Sign in to GitHub in a web browser, open this repository's **Releases**, and download `LaserPilot-win-x64.zip` from the newest release.
2. Unzip it into a folder you own — *Desktop* or *Documents* are good. Avoid *Program Files*: LaserPilot can't replace itself there without administrator rights.
3. Run `LaserPilot.exe`. With Option B nothing else is needed; with Option A, set up the token in Help → Update settings.

## Version numbers

The version is `VersionBase.BuildNumber`. `VersionBase` (currently `0.2`) lives in `src/LaserPilot.App/LaserPilot.App.csproj` — raise it for a big release. CI supplies the build number, so every push produces a higher version. Builds made on your own PC have build number 0: they're marked as developer builds and never replace themselves.

## Troubleshooting

| Message | What to do |
|---|---|
| "The repository is private, so LaserPilot needs an access token" | Set up a token (Option A or B above). |
| "GitHub rejected the update token" | The token expired or was deleted — create a new one and paste it in Help → Update settings (or update the secret). |
| "The token doesn't have permission to read this repository" | Give the token *Contents: Read-only* on this repository. |
| "LaserPilot doesn't have permission to change files in …" | Move `LaserPilot.exe` to a folder you own, such as Desktop. |
| No update offered | Help → Check for updates. Check the latest Actions run succeeded and its release has `LaserPilot-win-x64.zip` attached. |

Note: Actions minutes on private repositories are limited on GitHub's free plan, and Windows runners count double. Each build takes a few minutes.
