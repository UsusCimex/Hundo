# AGENTS.md

For agents and people who change the code. What the app does and how to use it is in the [README](README.md).

## Commands

```
dotnet build AchievementCompanion.sln -c Release
dotnet run --project tests/AC.Core.Tests -c Release
tools\screenshots.ps1 -DataDir <data folder> -OutDir <output> -Theme Dark -Set "UiLanguage=ru"
```

The setup: after the publish step of `release.yml`, `ISCC.exe /DAppVersion=X.Y.Z installer\Hundo.iss` (Inno Setup 6.7) writes it to `out`.

Tests are a console runner without packages: OK/FAIL per test and a total, exit code 1 on failure.
Check UI changes with screenshots in both themes: the build passes even when the markup is broken.
The script works on copies of the build and of the data folder, so the real folder is fine as input.

## Safety while checking

- Do not change the user's real data folder (`%LocalAppData%\AchievementCompanion`): run the app with `AC_DATA_DIR` pointing to a copy.
- Do not run the Steam client mode (`Hundo.exe --steam-client ...`): while it runs, friends see the user "in game", and `unlock` writes achievements to their account. Check with `AC_NO_STEAM_CLIENT=1`, as `tools/screenshots.ps1` does.
- Do not open windows over a game: if `HKCU\Software\Valve\Steam\RunningAppID` is not 0, start nothing.

| Variable | Effect |
| --- | --- |
| `AC_DATA_DIR` | data folder |
| `AC_NO_STEAM_CLIENT=1` | the app never connects to the Steam client |
| `AC_SOFTWARE_RENDERING=1` | software rendering: hardware rendering stops while the display is off |
| `AC_UPDATE_URL` | another "latest release" JSON, for testing updates against a local server |
| `AC_RUNNING_APPID` | the app takes this game for the running one: checks of what happens during play without a game |

## Structure

`AC.App` depends on `AC.Infrastructure`, which depends on `AC.Core`.

| Project | Contents |
| --- | --- |
| `src/AC.Core` | models and all logic, tested without Steam: statuses and probably missed missables (`GameLogic`), kind and chapter from the text (`KindClassifier`), time estimates (`TimeEstimator`, `SampleStats`, `Progress`), plan (`Planner`), session summary (`SessionTracker`, `SessionSummary`), unlocks by day (`UnlockHistory`), quotes from guides (`GuideParser`, `GuideMatcher`), filters (`AchievementQuery`), export, tags file (`TagPack`), Steam file formats (`SteamFormats/`), texts (`Loc`, `Lang/`) |
| `src/AC.Infrastructure` | I/O: use cases (`GameService`), Steam Web API, registry and Steam files (`SteamLocal`), Steam client (`SteamClientSession`, `SteamClientCommand`, `BridgeClient`), storage (`JsonStore`), data folder (`AppPaths`), error log (`AppLog`), DPAPI, Steam Hunters (`SteamHunters`), Steam guides (`SteamGuides`), machine translation (`Translator`), store art paths (`StoreAssets`), updates (`UpdateService`) |
| `src/AC.App` | WPF UI on the built-in Fluent theme: windows, view models, cached image loading (`RemoteImage`), unlock chart (`UnlockChart`), theme (`ThemeManager`), hotkeys (`Native`) |
| `tests/AC.Core.Tests` | tests of the logic and storage |
| `installer` | Inno Setup script of the per-user setup and its wizard images |

No NuGet packages: MVVM, tests, DPAPI and parsers are written in the project.

Achievements are read and written through the Steam client: `steamclient64.dll` and its interfaces via their virtual tables.
Steam takes the game from the process's `SteamAppId`: one process, one game. So for every operation the app starts its own
exe with `--steam-client` (`read`, `unlock`, `lock`); it prints JSON to stdout and exits.

### Texts

- Interface texts are in `src/AC.Core/Lang/<lang>.json`: English (`en.json`) is the default and the fallback, the others are translations.
- In XAML `{local:Tr section.key}`, in code `Loc.T("section.key", args)` and `Loc.Plural("section.key", n, args)`.
- A text with a number is an object of plural forms: `one`/`other` in English, `one`/`few`/`many` in Russian.
- Tests check that every language has the same keys and placeholders, and that every key is used in the code.
- A new language: `<code>.json` with `language.name` and `language.steam`; plural rules other than one/other go to `Loc.Category`.

### Data

The folder is `AppPaths.DataRoot`: `AC_DATA_DIR`, else `data` next to the exe (portable mode), else `%LocalAppData%\AchievementCompanion`.
The app was first called Achievement Companion: the data folder, the DPAPI entropy, the single-instance mutex and the setup's AppId keep that name, so data, the saved key and in-place updates survive.

| File | Contents |
| --- | --- |
| `games/<appid>.json` | schema, states, tags (kind, group, auto/manual source), time estimates, write queue, own playtime and playtime history, player sample |
| `summary.json` | game progress for the library, so game files are not read |
| `settings.json` | settings; the API key is encrypted with DPAPI |
| `library.json` | library cache |
| `unlock_log.jsonl` | log of writes to Steam |
| `app.log` | errors and starts, moved to `app.log.1` at 1 MB |
| `cache/guides/<appid>.json` | quotes from Steam guides per achievement, with the guide, author, section and pictures, and their translations; read again after a month or when `GuideTips.CurrentFormat` changes |
| `cache/images/` | images, trimmed to 200 MB at 300 MB |

### Sources

| What | Where from |
| --- | --- |
| Schema | `GetSchemaForGame` (key) and the local `appcache/stats/UserGameStatsSchema_<appid>.bin`: the Web API gives no descriptions of hidden ones, the local schema does; it also has the "protected" flag |
| Global % | `GetGlobalAchievementPercentagesForApp` (no key), else `steamhunters.com/api/apps/<appid>/achievements` |
| Time to 100% | `steamhunters.com/api/apps/<appid>`: `medianCompletionTime`, minutes, asked once a week |
| Status | `GetPlayerAchievements` (key and public "Game details") or the Steam client |
| Library | `GetOwnedGames` (key); fresh playtime of one game - the same call with `appids_filter` |
| Own sample | authors of `store.steampowered.com/appreviews/<appid>` and their `GetPlayerAchievements`; collected on its own only when nobody has percentages, at most once a week |
| Guide quotes | `IPublishedFileService/QueryFiles` (key): guides tagged Achievements and the language, up to 5 by title, until every achievement has words; `GetSubSectionData`: sections in BBCode; pictures from its tags and links, `[previewimg]` ones from the uploads (`return_previews`), without icons by size; authors from `GetPlayerSummaries`. The content language first, then English with English names from the local schema or `GetSchemaForGame` |
| Translation of quotes | on a click: `translate.googleapis.com/translate_a/single?client=gtx`, Google Translate's keyless web endpoint (not an official API); when it does not answer, the translator's page in the browser |
| Store art | `store_item_assets/steam/apps/<appid>/header.jpg`; newer apps keep it in a hashed folder: `IStoreBrowseService/GetItems`, else the store's `appdetails` |
| Running game and new unlocks | registry `RunningAppID`, watching `appcache/stats/UserGameStats_<accountid>_<appid>.bin` and polling on a timer |
| Updates | `api.github.com/repos/UsusCimex/Hundo/releases/latest`: the `*-win-x64.zip` asset, or `*-win-x64-setup.exe` for a copy installed by the setup (`unins000.exe` beside the exe), downloaded by the browser |

### Time

- Per achievement: own estimate, else measured in the sample, else the rarity model: t = T x ln(maxPct / pct) / ln(maxPct / minPct). P75 and P90 are margins of x1.3 and x1.7.
- T (time to 100%): manual, else Steam Hunters, else the sample median, else derived from own pace.
- Stretch (`Progress`): on the "minutes from the start" scale every achievement owns the way from the previous one to it. Stretches of unlocked ones are the progress made, of locked ones what is left, in any order of unlocks.
- Time left (`Planner`): the stretches of locked achievements times the pace; to one achievement, those of the locked ones up to it. Own estimates get the P75 and P90 margins too. Achievement hours show the time from the start or the time left (a setting).
- Countdown (`Plan.CountDown`): the minutes played since the last unlock or mark (`Progress.MinutesSinceProgress`: the play known to come after it by the playtime history, plus the sessions the app saw that Steam's playtime does not show yet, `SessionTracker`) go to the nearest achievement only; the others keep their gap to it, so idle hours do not push them all past their estimates. Past its estimate the wide margin (P90, at least 15 minutes) gives more time; past that there is no estimate, and a missable one is probably missed.
- Pace: Steam minutes between playtime snapshots (`PlaytimeHistory`: when a game loads outside a session, after a session together with its start when the app saw it, and two weeks back from Steam) divided by the progress made between them, as a progress-weighted median. Stretches without progress (AFK, a game left open) do not count. From 1 to 3: faster than the median gives no speed-up.
- Sample (`SampleStats`): up to 300 review authors until 80 public profiles, collected again in the background when over a week old; between "moment - minutes played" points play is taken as even.

## Rules

- Everything is in English: identifiers, comments, logs, commits, docs, issues. Interface texts go through the language files.
- A comment only for the non-obvious: why, not what. No change history, task links or commented-out code.
- Texts (interface, docs, comments) use keyboard characters only: a hyphen instead of dashes, straight quotes, three dots.
- Colors come from the Fluent theme via `DynamicResource`; no own colors except the rarity palette in `ThemeManager` and the hero and overlay (always dark).
- Commits follow [Conventional Commits](https://www.conventionalcommits.org/): `type(scope): what`, imperative, up to 72 characters, the body says why. One finished change per commit, with its tests. Commit only what was run. No Co-Authored-By or session links.
- The backlog is GitHub issues: every request gets an issue, a commit closes it (`Closes #N`).
- No CHANGELOG: the release notes say what changed.

## Release

Every finished change is released. Versions follow [SemVer](https://semver.org/): new features - minor, fixes - patch.

1. A separate `chore(release): X.Y.Z` commit with the version in `Directory.Build.props`.
2. The notes go into an annotated tag: `git tag -a vX.Y.Z --cleanup=whitespace -F notes.md` (without `--cleanup` git drops lines starting with `#`, the headings). The first line is the title (`Hundo X.Y.Z`), then the groups `## New`, `## Changed`, `## Fixed`: short, what a user notices, no implementation details.
3. `git push origin main vX.Y.Z`: `release.yml` runs the tests, builds the zip (a single-file exe) and the setup, and creates the release with the notes from the tag.

## Limits

- The overlay is not visible over exclusive fullscreen: it is a plain window on top, nothing is injected into the game (anti-cheat).
- The sample is noisy when few review authors have public profiles; review authors play more than average.
- api.steampowered.com turns away some networks, often VPNs, with an Akamai "Access Denied" page even for keyless requests. Then the app says so once, skips the other API steps and takes percentages and time from Steam Hunters; the status needs the Steam client.
- Windows only (WPF).
- Updates are seen only from a public repository: for a private one GitHub answers 404 and the check stays silent.
- No in-place update: an unsigned exe that downloads and swaps in its replacement is taken for a dropper by behavior-based antivirus (Kaspersky: PDM:Trojan.Win32.Generic). That needs code signing first.
