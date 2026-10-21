# LaserPilot

**Modern, safety-first laser control software for GRBL machines — built around the OMTech Light B10 (10W diode).**

LaserPilot exists because getting a B10 running with existing software too often ends with the head grinding into the rails during homing. LaserPilot treats safe machine setup as the product, not an afterthought: it verifies your limit switches and homing direction **before** the machine ever makes a big move, supervises every homing cycle with an automatic abort, and keeps the laser inside the bed.

## Features

**Safety & machine**
- 🧙 **Guided Setup Wizard** for the OMTech B10 — connect, check GRBL settings, hand-test the limit switches (no motion), verify motor directions with tiny 10 mm moves, then run the first homing cycle under supervision
- 🏠 **Safe homing** — pre-flight checks + a time-budgeted homing cycle that stops the controller if it runs longer than physically possible; afterwards the work origin is set to the bed's front-left corner automatically (works with both GRBL coordinate conventions)
- ⛔ **Emergency stop** always one click away; ⏹ **Stop** halts smoothly *and keeps the machine's position*
- 🔥 **Flame-sensor / door stop** shown as a big warning banner with a Resume button
- 🔌 **USB unplug detection**, friendly COM-port names with the B10's CH340 port auto-selected
- 🔦 Low-power aiming dot that **switches itself off after 30 s**
- ⚙ **GRBL settings editor** with plain-language descriptions, risk warnings, and automatic + manual **backup/restore**

**Design**
- 🖼 **Import** SVG, DXF, PNG, JPG, BMP, GIF, TIFF (or drag files onto the bed) — transparent PNG backgrounds are left unburned; phone photos are auto-rotated
- ✏ Shapes, vector **text** in any installed font, image **trace** to outlines
- **Line / Fill / Fill+Line** modes — engrave solid text and logos, or cut and score outlines
- Move, **resize with corner handles**, rotate, multi-select (Ctrl+click, drag a box), align, nudge with the arrow keys
- **Undo / redo**, **project files** (`.lpproj`), recent files
- 📷 Photo engraving: Floyd–Steinberg, Jarvis, Stucki, Atkinson, ordered dither, grayscale (min/max power), threshold; brightness / contrast / gamma; **live dithered preview** on the bed

**Running jobs**
- 👁 **Preview** with a simulated burn on a wood-coloured bed and an **accurate time estimate** (models GRBL's own motion planner)
- ⬚ **Frame** (laser off, or a faint 1% dot)
- **Start from:** the bed position, or the laser's **current position** with a 9-point anchor
- Holes are cut before the outlines around them; engraving runs before cutting; travel is minimised
- 🎚 **Live speed and power overrides** while a job runs, time-remaining display, pause/resume
- 💨 Air assist (M8) option, 🧪 **Material test** generator, 💾 export G-code for the B10's **MicroSD card**, run any G-code file

**Help**
- 🎓 **Guided tutorial** — a step-by-step spotlight tour written for people who aren't comfortable with computers. Skip it any time; restart it with **F1**, the ❓ Tutorial button, or jump to a chapter from Help
- 📖 The B10 setup guide and homing troubleshooting are built into the Help menu
- 🔄 **Automatic updates** — when a new version is published, a window pops up offering to install it

## Download & install

1. Download `LaserPilot-win-x64.zip` from the newest release on the [Releases page](../../releases) (sign in to GitHub — the repository is private).
2. Unzip it into a folder you own (for example *Desktop* or *Documents*) and run `LaserPilot.exe`. The .NET runtime is included — nothing else to install.
3. If Windows SmartScreen complains (the program isn't code-signed), click *More info → Run anyway*.
4. If no COM port appears when you plug in the laser, install the CH340 USB driver (Help → Install the USB driver).

## Automatic updates

Every push to `main` makes GitHub Actions build, test and publish a new release (version `0.2.<build number>`). Installed copies check for new releases at start-up and every few hours, show what's new, and install the update with one click (LaserPilot restarts itself; designs and settings are kept). Updates are never installed while a job is running.

Because the repository is **private**, GitHub only shares releases with a read-only access token. Set it up one of two ways — see [docs/UPDATES.md](docs/UPDATES.md):

- **Per computer:** Help → Update settings → paste a token (stored encrypted for that Windows account).
- **Built in:** save the token as the repository secret `LASERPILOT_UPDATE_TOKEN`; CI embeds it so every copy updates with no setup.

## First run with an OMTech B10

1. Plug in USB, switch the laser on, start LaserPilot.
2. The welcome screen offers the **guided tour** and the **Setup Wizard** — do both if you're new. The wizard:
   - connects and compares your controller's GRBL settings with safe B10 values (400×400 mm work area, laser mode, homing toward the **front-left** switches with `$23=3`),
   - asks which corner your limit switches are in, has you press each one by hand and confirms the controller sees them (🏠 Home stays locked until this passes),
   - makes single 10 mm test moves to verify motor direction (and fixes `$3` if reversed),
   - runs the first homing cycle supervised and at a gentle speed, then enables soft + hard limits.
3. After that: **Home → Import → position → Frame → Start.**

See [docs/OMTECH_B10_SETUP.md](docs/OMTECH_B10_SETUP.md) for the full walkthrough and
[docs/HOMING_TROUBLESHOOTING.md](docs/HOMING_TROUBLESHOOTING.md) if homing misbehaves.

## Why not just fork LaserGRBL?

We analysed [LaserGRBL](https://github.com/arkypita/LaserGRBL) before starting. Its GRBL protocol handling (character-counting streaming, realtime commands) is excellent and battle-tested — LaserPilot uses the same protocol approach. But the codebase is a large, tightly-coupled WinForms application where machine configuration is raw `$$` dialogs and homing is "send `$H` and hope". A fresh, modular WPF codebase with the proven protocol re-implemented cleanly was the better trade.

## Project structure

```
src/
  LaserPilot.Core/          # no UI dependencies — fully reusable and unit-tested
    Comm/                   # GrblController (serial, status, streaming, overrides), status parsing
    Machines/               # machine profiles (OMTech B10), GRBL settings catalog
    Safety/                 # homing diagnostics, supervised homing, work-origin setup
    Geometry/               # polylines, transforms
    Importers/              # SVG, DXF, image tracing
    Gcode/                  # vector/fill/raster G-code, path ordering, parser, time estimator
    Materials/              # 10W diode presets
    Updates/                # GitHub Releases client
  LaserPilot.App/           # WPF UI
    MainWindow.*.cs         # machine, design, canvas, job, console, tutorial, updates
    Models/                 # design items, job compiler, project files, undo, material test
    Services/               # settings, ports, updates, backups, crash log, dark title bars
    Dialogs/, Tutorial/, Themes/Dark.xaml
tests/
  LaserPilot.Core.Tests/    # includes a simulated GRBL controller for streaming tests
  LaserPilot.App.Tests/     # job compiler, projects, image import, update install
.github/workflows/build.yml # CI: build, test, release on every push to main
docs/                       # also shown in the app's Help menu
```

## Building from source

```powershell
# Requires the .NET 10 SDK
dotnet build
dotnet test
dotnet run --project src/LaserPilot.App

# Portable release exe (what CI publishes):
dotnet publish src/LaserPilot.App -c Release -r win-x64 `
  -p:PublishSingleFile=true -p:SelfContained=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:BuildNumber=1 -o publish
```

Local builds are "developer builds" (build number 0): they can check for updates but never replace themselves. Set `LASERPILOT_DATA_DIR` to keep test settings out of `%AppData%\LaserPilot`.

## Releasing

Just push to `main` — CI publishes the release and installed copies pick it up. To start a new version series, change `VersionBase` in `src/LaserPilot.App/LaserPilot.App.csproj` (for example `0.3`).

Don't create `v…` tags or releases by hand: installed copies compare the release tag with the version built into the program, so a hand-made tag that doesn't match would be offered as an update over and over.

## Safety

A laser engraver is a fire and eye hazard. Always wear wavelength-rated goggles, ventilate, keep a fire extinguisher nearby, and never leave a running job unattended. LaserPilot's software safeguards reduce risk; they do not replace supervision.

## License

MIT
