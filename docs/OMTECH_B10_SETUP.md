# OMTech Light B10 — setup guide

This is the full, annotated version of what the in-app Setup Wizard does, plus manual steps if you prefer doing it yourself.

## 1. Physical setup

- Assemble per OMTech's manual; make sure both belts are tensioned and the gantry moves freely by hand when powered off.
- The **limit switches** live at the **front-left**: one on the X carriage arm, one on the frame's Y rail. Check both plugs are seated on the control board.
- Focus: use the fixed-focus spacer (the metal bar) between the laser shroud and the material.

## 2. Connect

- USB in, power on. Windows should show a **USB-SERIAL CH340** COM port.
- No port? Install the CH340 driver: <https://www.wch-ic.com/downloads/CH341SER_EXE.html>
- Only one program can use the port. Close LightBurn / LaserGRBL / Arduino IDE first.
- In LaserPilot: pick the COM port → **Connect**. The status bar should show GRBL's hello string (`Grbl 1.1…`).

## 3. GRBL configuration that matters

The wizard applies these; here's what they mean so you can sanity-check:

| Setting | Value | Why |
|---|---|---|
| `$130` / `$131` | 400 / 400 | Work area (mm). Homing search distance is 1.5× this — wrong values make homing search too far or give up early. |
| `$23` | **3** | Homing direction: X− Y− = front-left, where the B10's switches are. **The #1 cause of rail-crashing on this machine is $23=0 (GRBL default), which homes toward the BACK-RIGHT — away from the switches.** |
| `$22` | 1 | Enable homing (only after switches verified!) |
| `$24` / `$25` | 100 / 1500 | Homing locate/seek speeds — deliberately conservative. |
| `$27` | 2.0 | Pull-off after touching a switch. |
| `$20` / `$21` | 1 / 1 | Soft + hard limits — enabled by the wizard **after** homing is proven. Soft limits are what permanently protect the rails. |
| `$32` | 1 | Laser mode: no dwell between moves, power scales with speed, laser off during rapids. |
| `$30` | 1000 | S value for 100% power (matches LaserPilot's G-code). |
| `$100` / `$101` | 80 / 80 | Steps/mm (B10 factory value). If a 100 mm square comes out a different size, calibrate these. |

## 4. The safe order of operations

Never enable homing before its prerequisites are proven. The wizard enforces this order:

1. **Limit switch test (no motion).** Press each switch by hand while LaserPilot watches the controller's live pin report (`Pn:` field). Both must register.
   - Switch shows as *always* pressed? Wiring is normally-closed → set `$5=1` and re-test.
2. **Direction test (10 mm moves).** X+ must go right, Y+ must go toward the rear. A backwards motor is fixed by flipping the corresponding bit of `$3`.
3. **Supervised homing.** LaserPilot computes the maximum legitimate cycle time from your own settings and soft-resets the controller if it's exceeded — a wrong direction can't grind for more than a moment.
4. **Enable `$20`/`$21`.** From now on the firmware itself refuses motion outside 400×400 mm.

## 5. Daily use

1. Power on → Connect → **🏠 Home (safe)**.
2. Import your design (SVG/DXF for cutting, PNG/JPG for engraving), place and size it on the canvas.
3. Put material on the bed, focus with the spacer.
4. **⬚ Frame** to confirm placement (head traces the outline, laser off).
5. Pick a material preset, adjust if needed.
6. Goggles on → **▶ START**. Stay with the machine.

## 6. Notes specific to 10W diode lasers

- 450 nm blue light passes through **clear/blue acrylic and bare glass** — they won't cut/engrave. Dark opaque acrylic works; coat glass first.
- Air assist dramatically improves cut quality if you have it.
- Multiple faster passes usually beat one slow charring pass on wood.
