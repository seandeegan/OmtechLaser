# Homing troubleshooting (read this if the head bangs into the rails)

## What correct homing looks like on an OMTech B10

Press Home: the head moves **toward the front-left corner** at moderate speed, touches the X and Y limit switches, backs off about 2 mm, briefly re-touches them slowly, and stops. Takes roughly 10–40 seconds depending on where it started. That corner is now machine zero.

If yours instead drives to the **back or right** and grinds — the homing *direction* is wrong. If it reaches the front-left but *keeps pushing* — a switch isn't being seen.

## Decision table

| Symptom | Cause | Fix |
|---|---|---|
| Head drives to the **back-right** and grinds | `$23=0` (GRBL default) — homing away from the switches | Set `$23=3`. This is the single most common issue. |
| Head goes front-left, hits the corner, keeps grinding, then `ALARM:9` | Limit switch not detected: unplugged, broken, or wrong wiring polarity | Run the wizard's **limit switch test** (it watches the pins while you press each switch by hand). If a switch reads always-on, set `$5=1`. Check plugs on the control board. |
| `error:5` when sending `$H` | Homing disabled | `$22=1` — but only after the switch test passes. |
| `ALARM:8` right after the switches click | Pull-off too small or sticky switch | `$27=2.5` or `3`. |
| `ALARM:9` immediately, almost no movement | `$130`/`$131` far too small (search distance = 1.5× travel) | Set `$130=400`, `$131=400`. |
| Homing works but jogs later slam the rails | Soft limits off | `$20=1` (requires homing). Then GRBL rejects any motion outside the work area with `error:15` instead of crashing. |
| Random `ALARM:1` (hard limit) during normal jobs | Electrical noise on switch wires | Route switch wires away from motor wires; if it persists, `$21=0` (keep `$20=1`). |
| One axis homes, the other stalls | That axis's switch or direction bit | Test that switch by hand; check the matching bit of `$23` (bit0=X, bit1=Y). |

## How LaserPilot protects you

- **Pre-flight checks** before every homing run: `$22/$23/$130/$131/$25` are validated against the machine profile and current pin states; homing is blocked with an explanation if they look wrong.
- **Supervised cycle**: the max legitimate homing time is computed from your own `$25/$130` values; if exceeded, LaserPilot soft-resets the controller (instant stop) instead of letting it grind.
- **No blind enables**: the Setup Wizard will not turn on `$22` until the switches have been verified by hand, with no motion involved.
- **⛔ STOP** (toolbar and wizard) sends a GRBL soft reset — motion halts within milliseconds and the laser turns off.

## Useful console commands

```
$$        show all settings
$23=3     home toward front-left (B10)
$X        unlock after an alarm
$H        run homing
?         status report (shows Pn: pin states while a switch is pressed)
```

After any alarm, remember: the controller may have lost its position. Unlock (`$X`), then re-home before trusting coordinates.
