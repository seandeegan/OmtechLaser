# OMTech Light B10 — setup guide

This is the full, annotated version of what the in-app Setup Wizard does, plus manual steps if you prefer doing it yourself.

## 1. Physical setup

- Assemble per OMTech's manual; make sure both belts are tensioned and the gantry moves freely by hand when switched off.
- The **limit switches** live at the **front-left**: one on the X carriage arm, one on the frame's Y rail. Check both plugs are seated on the control board.
- Focus: use the focusing tool (the small spacer) between the laser and the material — rest the laser on it, tighten the knob, remove the tool.
- The B10 has a **flame sensor** under the crossbeam and **built-in air assist**. Keep the air running for cutting — it gives cleaner edges and fewer flare-ups.

## 2. Connect

- USB in, switch on. Windows should show a **USB-SERIAL CH340** COM port — LaserPilot marks it with ★ and selects it for you.
- No port? Install the CH340 driver (Help → Install the USB driver): <https://www.wch-ic.com/downloads/CH341SER_EXE.html>
- Only one program can use the port. Close LightBurn / LaserGRBL / Arduino IDE first.
- Click **Connect**. The status bar shows GRBL's version (`Grbl 1.1h…`). LaserPilot backs up the controller's settings automatically the first time it reads them.

## 3. GRBL configuration that matters

The wizard checks these and offers to fix mismatches; here's what they mean:

| Setting | Value | Why |
|---|---|---|
| `$130` / `$131` | 400 / 400 | Work area (mm). Homing search distance is 1.5× this — wrong values make homing search too far or give up early. |
| `$23` | **3** | Homing direction: X− Y− = front-left, where the B10's switches are. **The #1 cause of rail-crashing on this machine is `$23=0` (GRBL default), which homes toward the BACK-RIGHT — away from the switches.** |
| `$22` | 1 | Enable homing (only after the switches are verified!) |
| `$24` / `$25` | 100 / 1500 | Homing locate/seek speeds — deliberately gentle. |
| `$27` | 2.0 | Pull-off after touching a switch. |
| `$20` / `$21` | 1 / 1 | Soft + hard limits — switched on by the wizard **after** homing is proven. Soft limits are what permanently protect the rails. |
| `$32` | 1 | Laser mode: no dwell between moves, power scales with speed, laser off during travel. |
| `$30` | 1000 | S value for 100% power. LaserPilot reads this value, so other values also work. |

**Not changed by the wizard** (shown for comparison only): `$100`/`$101` steps per mm (typically 80) and `$110`/`$111` maximum speed (the B10 is rated for 250 mm/s). These are factory calibration. If a 100 mm square comes out the wrong size, measure it and set `$100` = current value × 100 ÷ measured mm (same for `$101` on Y).

`$10` (status report type) can be 0 or 1 — LaserPilot works with either.

## 4. The safe order of operations

Never enable homing before its prerequisites are proven. The wizard enforces this order:

1. **Where are the switches?** Front-left on the B10 (`$23=3`); rear-left machines use `$23=1`.
2. **Limit switch test (no motion).** Press each switch by hand while LaserPilot watches the controller's live pin report (`Pn:` field). Both must register.
   - If hard limits (`$21=1`) are on, pressing a switch would trigger an alarm that freezes the controller, so the wizard pauses hard limits during the test and switches them back on afterwards.
   - A switch that shows as *always* pressed is wired normally-closed → set `$5=1` and test again.
3. **Direction test (10 mm moves).** X+ must go right, Y+ must go toward the back. A backwards motor is fixed by flipping one bit of `$3`. If soft limits block these moves (the position isn't known before homing), the wizard offers to pause them for the test.
4. **Supervised homing.** LaserPilot works out the longest a correct cycle can take from your own settings and stops the controller if it's exceeded — a wrong direction can't grind for long.
5. **Turn on `$20`/`$21`.** From now on the firmware itself refuses motion outside 400×400 mm.

## 5. Where is (0,0)?

LaserPilot's grid is the bed seen from above, with **0,0 at the front-left corner**. After every successful homing, LaserPilot makes sure the controller's work origin (`G54`) matches that corner:

- Stock GRBL uses a "negative" machine space (after homing the position reads about −398, −398). LaserPilot sets `G54` to −400, −400 so your design lands where it is on screen.
- Controllers built to call the home position 0,0 need no change, and nothing is written.

The offset is only written when it's wrong (it's stored in the controller's memory). "Set origin" changes it temporarily; the next homing puts it back in the corner.

**Using LightBurn on the same laser?** After LaserPilot sets the origin, use LightBurn's *Absolute Coords* without *negative workspace*, or send `G10 L2 P1 X0 Y0` before switching programs.

## 6. Daily use

1. Switch on → **Connect** → **🏠 Home**.
2. Import your design (SVG/DXF for cutting, PNG/JPG for engraving) or add text and shapes; place and size it on the grid.
3. Choose **Line** (cut/score) or **Fill** (engrave solid areas), and pick a material preset.
4. Put the material on the bed and focus.
5. **⬚ Frame** to confirm placement (the head traces the outline with the laser off).
6. **👁 Preview** to see the burn and how long it will take.
7. Goggles on → **▶ START**. Stay with the machine. Use the Speed/Power sliders to adjust a running job.

New material? **Tools → Material test** burns a small grid of speed/power combinations so you can pick the best one.

To place a design on an object without homing, choose **Start from: Current laser position**, move the head over the object, and pick which point of the design goes there. A dashed orange box shows where it will land.

## 7. Notes specific to 10W diode lasers

- 450 nm blue light passes through **clear/blue acrylic and bare glass** — they won't cut or engrave. Dark opaque acrylic works; coat glass first.
- Air assist dramatically improves cut quality.
- Several faster passes usually beat one slow, charring pass on wood.
- If the **flame sensor** stops a job, LaserPilot shows a red "Safety stop" banner. Check for fire first; when it's safe, press Resume.
- The B10 can run jobs from its **MicroSD card**: File → Export G-code saves a file you can copy onto it. Home the laser before starting a card job so the design lands where it was on screen.
