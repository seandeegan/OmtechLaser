# Homing troubleshooting (read this if the head bangs into the rails)

## What correct homing looks like on an OMTech B10

Press Home: the head moves **toward the corner where the limit switches are** (front-left on a standard B10), touches the X and Y switches, backs off about 2 mm, briefly re-touches them slowly, and stops. It takes up to about 20 seconds at normal speed (the very first homing runs slower). The front-left corner is then the bed's 0,0.

If it reaches a corner and *keeps pushing* (grinding or knocking), press ⛔ STOP: either there are no switches in that corner (the homing direction doesn't match where your switches are) or a switch isn't being seen. Open the Setup Wizard, step 3: choose the corner your switches are really in, then run the switch test.

## Decision table

| Symptom | Cause | Fix |
|---|---|---|
| Head drives to a corner and grinds there, then `ALARM:9` | Homing toward a corner without switches (`$23` doesn't match where they are; GRBL's default `$23=0` homes back-right) | Look where your switches are and pick that corner in the Setup Wizard, step 3 — it sets `$23` for you. |
| Head reaches the switch corner, keeps grinding, then `ALARM:9` | Limit switch not detected: unplugged, broken, or wrong wiring polarity | Run the wizard's **limit switch test** (it watches the pins while you press each switch by hand). If a switch reads always-on, set `$5=1`. Check plugs on the control board. |
| 🏠 Home asks you to test the switches first | The switch test hasn't passed on this computer yet | Do the one-minute test (Setup Wizard, step 3). Nothing moves during it. |
| `error:5` when sending `$H` | Homing disabled | `$22=1` — but only after the switch test passes. |
| `ALARM:8` right after the switches click | Pull-off too small or sticky switch | `$27=2.5` or `3`. |
| `ALARM:9` immediately, almost no movement | `$130`/`$131` far too small (search distance = 1.5× travel) | Set `$130=400`, `$131=400`. |
| Homing works but jobs then hit soft limits (`ALARM:2`) or land in the wrong place | The controller's work origin doesn't match the bed corner (stock GRBL reads about −398, −398 after homing) | Home with LaserPilot's 🏠 button — it sets the work origin (`G54`) to the front-left corner automatically. Another program may have changed it; homing again fixes it. |
| Pressing a switch during the switch test raises `ALARM:1` and the test freezes | Hard limits (`$21=1`) treat the press as a crash | The Setup Wizard now pauses hard limits during the test and switches them back on afterwards. Update LaserPilot if you see this. |
| Homing works but jogs later slam the rails | Soft limits off | `$20=1` (requires homing). Then GRBL rejects any motion outside the work area with `error:15` instead of crashing. |
| Random `ALARM:1` (hard limit) during normal jobs | Electrical noise on the switch wires | Route switch wires away from motor wires; if it persists, `$21=0` (keep `$20=1`). |
| One axis homes, the other stalls | That axis's switch or direction bit | Test that switch by hand; check the matching bit of `$23` (bit0 = X, bit1 = Y). |

## How LaserPilot protects you

- **Switches first**: 🏠 Home (and `$H` typed in the console) stays locked until both switches have passed the hand-press test once. A switch that reads "pressed" all the time doesn't pass.
- **Pre-flight checks** before every homing run: `$22/$23/$130/$131/$25` are checked against the machine profile and the current switch states; homing is blocked with an explanation if they look wrong.
- **Gentle first homing**: until a homing has worked once (or after you change the switch corner), homing runs at 600 mm/min, so a missed switch bumps instead of slamming. Your `$25` is put back afterwards.
- **Supervised cycle**: the longest a correct homing can take is worked out from your own `$24–$27/$130/$131` values; if exceeded, LaserPilot soft-resets the controller (instant stop). GRBL sends nothing while it homes, so this can only be a little quicker than GRBL's own give-up (`ALARM:9`) — watch the head and use ⛔ STOP.
- **Bed origin**: after every successful homing, the work origin is checked and set to the bed's front-left corner, so designs land where they are on screen.
- **⛔ STOP** (toolbar and wizard) sends a GRBL soft reset — motion halts within milliseconds and the laser turns off. **⏹ Stop** (job panel) pauses first and then resets, which keeps the machine's position.

## Useful console commands

```
$$        show all settings
$#        show the work offsets (G54 should match the bed corner)
$23=3     home toward front-left (1 rear-left, 2 front-right, 0 rear-right)
$X        unlock after an alarm
$H        run homing (LaserPilot runs it through the supervised 🏠 Home)
?         status report (shows Pn: pin states while a switch is pressed)
```

After any alarm, remember: the controller may have lost its position. Unlock (`$X`), then re-home before trusting coordinates.
