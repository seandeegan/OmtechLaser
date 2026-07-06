# LaserPilot

**Modern, safety-first laser control software for GRBL machines — with first-class support for the OMTech Light B10 (10W diode).**

LaserPilot exists because getting a B10 running with existing software too often ends with the head grinding into the rails during homing. LaserPilot treats safe machine setup as the product, not an afterthought: it verifies your limit switches and homing direction **before** the machine ever makes a big move, and it supervises every homing cycle with an automatic abort.

## Features

- 🧙 **Guided Setup Wizard** for the OMTech B10 — connect, fix GRBL settings, hand-test the limit switches (no motion), verify motor directions with tiny 10 mm moves, then run the first homing cycle under supervision
- 🏠 **Safe homing** — pre-flight configuration checks + a time-budgeted homing cycle that soft-resets the controller the moment it runs longer than physically possible
- ⛔ **Emergency stop** always one click away (GRBL soft reset: instant halt, laser off)
- 🖼 **Import** SVG, DXF, PNG, JPG, BMP — position, resize, rotate on a visual workspace canvas
- ✏ **Design in-app** — rectangle/ellipse/line/star shapes, vector **text** in any installed font, and alignment tools
- 📷 **Photo engraving** with Floyd–Steinberg / Jarvis dithering (plus grayscale-depth and threshold modes, invert option)
- ✒ **Image trace** — convert logos/line art bitmaps into cuttable vector outlines with a live-preview threshold dialog
- ⚡ **G-code generation** — vector cut/engrave (M4 dynamic power, multi-pass) and raster image engraving with overscan and bidirectional scanning
- 🧪 **Material presets** tuned for 10W diode lasers (plywood, MDF, acrylic, leather, slate…)
- ⬚ **Frame** button traces the job outline with the laser off so you can check placement
- 🎛 **Jog controls**, low-power pulse for focusing, work-origin tools
- ⚙ **GRBL settings editor** with plain-language descriptions and risk warnings on every setting
- 🖥 **Console** with full command/response log for debugging
- 📦 **Portable single .exe** — no installer, no dependencies

## Download & install

1. Grab `LaserPilot-win-x64.zip` from the [Releases page](../../releases).
2. Unzip anywhere and run `LaserPilot.exe`. That's it — the .NET runtime is bundled.
3. If Windows SmartScreen complains (unsigned binary), click *More info → Run anyway*.
4. If no COM port appears when you plug in the laser, install the CH340 USB driver:
   <https://www.wch-ic.com/downloads/CH341SER_EXE.html>

## First run with an OMTech B10

1. Plug in USB, power on the laser.
2. Start LaserPilot → it offers the **Setup Wizard**. Run it. The wizard:
   - connects and reads your controller's GRBL settings,
   - applies the correct B10 configuration (400×400 mm work area, laser mode, homing direction **$23=3** toward the front-left switches),
   - has you press each limit switch by hand and confirms the controller sees them,
   - makes single 10 mm test moves to verify motor direction (and fixes `$3` if reversed),
   - runs the first homing cycle supervised, then enables soft + hard limits.
3. After that: **Home → Import → position → Frame → Start.**

See [docs/OMTECH_B10_SETUP.md](docs/OMTECH_B10_SETUP.md) for the full walkthrough and
[docs/HOMING_TROUBLESHOOTING.md](docs/HOMING_TROUBLESHOOTING.md) if homing misbehaves.

## Why not just fork LaserGRBL?

We analysed [LaserGRBL](https://github.com/arkypita/LaserGRBL) before starting. Its GRBL protocol handling (character-counting streaming, realtime commands) is excellent and battle-tested — LaserPilot uses the same protocol approach. But the codebase is a large, tightly-coupled WinForms application where machine configuration is raw `$$` dialogs and homing is "send `$H` and hope". Retrofitting a safety-first setup flow and a modern UI would have meant rewriting most of what we'd inherit. A fresh, modular WPF codebase (~5k lines) with the proven protocol re-implemented cleanly was the better trade.

## Project structure

```
src/
  LaserPilot.Core/          # no UI dependencies — fully reusable
    Comm/                   # GrblController: serial, status polling, streaming
    Machines/               # machine profiles (OMTech B10), GRBL settings catalog
    Safety/                 # homing diagnostics + supervised homing
    Geometry/               # polylines, transforms
    Importers/              # SVG, DXF parsers
    Gcode/                  # vector/raster/frame G-code generation
    Materials/              # 10W diode presets
  LaserPilot.App/           # WPF UI
    MainWindow.*            # workspace canvas, jog, job control, console
    SetupWizardWindow.*     # guided first-time setup
    SettingsWindow.*        # GRBL settings editor
.github/workflows/build.yml # CI + release automation
docs/
```

## Building from source

```powershell
# Requires .NET 10 SDK
dotnet build
dotnet run --project src/LaserPilot.App

# Portable release exe:
dotnet publish src/LaserPilot.App -c Release -r win-x64 `
  -p:PublishSingleFile=true -p:SelfContained=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## Releasing

Push a tag like `v0.1.0` and GitHub Actions builds the portable exe and attaches it to a GitHub Release automatically:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

## Safety

A laser engraver is a fire and eye hazard. Always wear wavelength-rated goggles, ventilate, keep a fire extinguisher nearby, and never leave a running job unattended. LaserPilot's software safeguards reduce risk; they do not replace supervision.

## License

MIT
