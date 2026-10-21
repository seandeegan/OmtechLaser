using System.Windows;
using System.Windows.Controls;
using LaserPilot.App.Tutorial;

namespace LaserPilot.App;

public partial class MainWindow
{
    private TutorialOverlay? _tutorial;
    private List<TutorialStep>? _tutorialSteps;

    private bool IsTutorialActive => _tutorial is not null;

    private IReadOnlyList<TutorialStep> TutorialSteps => _tutorialSteps ??= BuildTutorialSteps();

    private void Tutorial_Click(object sender, RoutedEventArgs e) => StartTutorial(0);

    /// <summary>Starts (or restarts) the guided tour at the given step.</summary>
    private void StartTutorial(int stepIndex)
    {
        _tutorial?.Close(false);
        var overlay = new TutorialOverlay(RootGrid, TutorialSteps, stepIndex) { KeepClear = () => EStopBtn };
        overlay.Finished += completed =>
        {
            if (_tutorial == overlay) _tutorial = null;
            _settings.TutorialCompleted = _settings.TutorialCompleted || completed;
            _settings.Save();
            if (!completed && !_closingConfirmed)
                ShowAlert("🎓 Tutorial closed. Restart it any time: press F1 or click ❓ Tutorial. Help → Tutorial chapters jumps straight to a topic.",
                    AlertKind.Info);
        };
        _tutorial = overlay;
        RootGrid.Children.Add(overlay);
    }

    private void EndTutorial(bool completed, bool remember = true)
    {
        if (_tutorial is null) return;
        var t = _tutorial;
        _tutorial = null;
        if (remember) t.Close(completed);
        else (t.Parent as Panel)?.Children.Remove(t);
    }

    private void BuildTutorialMenu()
    {
        MenuTutorialChapters.Items.Clear();
        var steps = TutorialSteps;
        foreach (var chapter in steps.Select(s => s.Chapter).Distinct())
        {
            int first = steps.ToList().FindIndex(s => s.Chapter == chapter);
            var mi = new MenuItem { Header = chapter };
            mi.Click += (_, _) => StartTutorial(first);
            MenuTutorialChapters.Items.Add(mi);
        }
    }

    private List<TutorialStep> BuildTutorialSteps()
    {
        const string welcome = "1 · Welcome";
        const string connect = "2 · Connecting the laser";
        const string moving = "3 · Moving the laser";
        const string design = "4 · Making a design";
        const string settings = "5 · Laser settings";
        const string run = "6 · Running a job";
        const string extras = "7 · Saving & extras";

        return new List<TutorialStep>
        {
            // ---------------- 1. Welcome ----------------
            new()
            {
                Chapter = welcome,
                Title = "Welcome to LaserPilot! 👋",
                Body = "This tour shows you around, one step at a time. A glowing box highlights the part of the screen we're talking about. " +
                       "Read the note, then click Next.\n\n" +
                       "You can leave whenever you like with \"Skip tutorial\", and come back any time: press F1 on your keyboard, " +
                       "or click ❓ Tutorial at the top of the window. The whole tour takes about 10 minutes.",
            },
            new()
            {
                Chapter = welcome,
                Title = "Safety first ⚠",
                Body = "A laser can hurt your eyes and start fires. Before every job:\n\n" +
                       "•  Wear the green laser goggles that came with your B10 — everyone in the room.\n" +
                       "•  Never look at the bright dot, even with goggles on.\n" +
                       "•  Keep fresh air flowing — laser smoke isn't healthy — and keep a fire extinguisher close by.\n" +
                       "•  Stay with the machine the whole time it's running. The B10 has a flame sensor, but it's a backup, not a babysitter.",
            },
            new()
            {
                Chapter = welcome,
                Title = "The big red STOP button",
                Body = "If anything looks wrong — a flame, a strange noise, the laser heading somewhere it shouldn't — click STOP. " +
                       "It stops the motors and switches the laser off instantly.\n\nIt's always up here in the top-right corner, even during this tour.",
                Target = () => EStopBtn,
            },
            new()
            {
                Chapter = welcome,
                Title = "Your work area — the bed",
                Body = "This grid is a picture of the laser's bed, seen from above: 400 × 400 mm (about 16 × 16 inches).\n\n" +
                       "The green dot is the front-left corner — the corner nearest you, on the left. Each small square is 1 cm and the brighter lines are every 5 cm. " +
                       "Your designs sit on this grid exactly where they'll be burned. The red circle shows where the laser head is right now.",
                Target = () => CanvasBorder,
            },
            new()
            {
                Chapter = welcome,
                Title = "Machine controls (left side)",
                Body = "The buttons on the left control the laser itself: homing, moving the head around, the air assist, and a faint aiming dot for lining things up.",
                Target = () => MachineGroup,
            },
            new()
            {
                Chapter = welcome,
                Title = "Your design and the job (right side)",
                Body = "Everything about what you're making lives on the right: the list of items on the bed, their size, the laser settings, and the buttons that run the job. " +
                       "If the window is small, scroll down on that side to see it all.",
                Target = () => DesignGroup,
            },

            // ---------------- 2. Connecting ----------------
            new()
            {
                Chapter = connect,
                Title = "Plug in and switch on",
                Body = "Connect the laser's USB cable to this computer and switch the laser on with its power switch. Give Windows a few seconds to recognise it.\n\n" +
                       "Not near your laser right now? That's fine — just click Next and read along.",
            },
            new()
            {
                Chapter = connect,
                Title = "Pick the connection port",
                Body = "This box lists the connection ports on your computer. The B10 usually appears as \"USB-SERIAL CH340\" marked with a ★, and LaserPilot picks it for you.\n\n" +
                       "List empty? Click ⟳ next to it to refresh. Still nothing? Windows needs a small driver: Help → Install the USB driver.",
                Target = () => PortCombo,
            },
            new()
            {
                Chapter = connect,
                Title = "Connect",
                Body = "Click Connect. It takes a couple of seconds.\n\n" +
                       "If it fails, close any other laser program (LightBurn, LaserGRBL…) — only one program can talk to the laser at a time — then try again.",
                TryIt = "Try it: click Connect now (or click Next to skip).",
                IsDone = () => _grbl.IsConnected,
                DoneText = "✓ Connected! Click Next to carry on.",
                Target = () => ConnectBtn,
            },
            new()
            {
                Chapter = connect,
                Title = "The status light",
                Body = "This shows what the laser is doing:\n\n" +
                       "•  Ready (green) — waiting for you.\n" +
                       "•  Running / Moving (blue) — busy working.\n" +
                       "•  Paused (yellow).\n" +
                       "•  Locked (red) — normal right after switching on. Click 🏠 Home.\n" +
                       "•  Safety stop (red) — the flame sensor or lid switch stopped the machine. Check for fire first!",
                Target = () => StateBadge,
            },
            new()
            {
                Chapter = connect,
                Title = "First time? Use the Setup Wizard",
                Body = "The Setup Wizard checks your laser safely, one step at a time: it tests the limit switches while you press them by hand (nothing moves), " +
                       "checks the motors turn the right way with tiny moves, then does the first homing while watching closely.\n\n" +
                       "Do it once when you first set up the laser — and again if the head ever bangs into the frame.",
                Target = () => WizardBtn,
            },

            // ---------------- 3. Moving ----------------
            new()
            {
                Chapter = moving,
                Title = "Home the laser",
                Body = "Homing sends the head to its home corner until it touches two small switches, so it knows exactly where it is. " +
                       "Do this every time you switch the laser on.\n\n" +
                       "The very first time, LaserPilot has you test the two switches by hand (nothing moves), and the first homing runs slowly.\n\n" +
                       "Watch the head: if it reaches the end of a rail and keeps pushing with a grinding noise, press ⛔ STOP.",
                TryIt = "Try it: click 🏠 Home (safe) once the laser is connected.",
                IsDone = () => _homedThisSession,
                DoneText = "✓ Homed — the laser knows where it is. Click Next.",
                Target = () => HomeBtn,
            },
            new()
            {
                Chapter = moving,
                Title = "Move the head by hand",
                Body = "The arrows move the laser head — the laser stays off. ▲ moves away from you, ▼ toward you, ◀ ▶ left and right. ■ stops a move.\n\n" +
                       "\"Step\" is how far one click moves the head; \"Speed\" is how fast.\n\n" +
                       "Tip: right-click anywhere on the bed and choose \"Move the laser here\".",
                Target = () => JogGroup,
            },
            new()
            {
                Chapter = moving,
                Title = "Focus the laser",
                Body = "A small, sharp dot gives crisp results. The B10 has a fixed focus: stand the little focusing tool on top of your material, " +
                       "lower the laser until it rests on the tool, tighten the knob, then take the tool away.\n\n" +
                       "\"Laser on\" makes a faint aiming dot so you can see exactly where the laser points — goggles on! It switches itself off after 30 seconds.",
                Target = () => FocusGroup,
            },

            // ---------------- 4. Design ----------------
            new()
            {
                Chapter = design,
                Title = "Bring in a design",
                Body = "Click Import to open a picture or a drawing:\n\n" +
                       "•  Photos and pictures (JPG, PNG…) are engraved line by line, a bit like a printer.\n" +
                       "•  Drawings (SVG, DXF) can be cut out or traced as lines.\n\n" +
                       "Even easier: drag a file from your desktop and drop it straight onto the bed.",
                Target = () => ImportBtn,
            },
            new()
            {
                Chapter = design,
                Title = "Add text",
                Body = "Type a name or a message, choose a font and how tall the letters should be (in millimetres — 20 mm is about the height of a fingertip). " +
                       "Text is engraved filled-in by default.",
                TryIt = "Try it: click 🅰 Text…, type your name and click Add.",
                IsDone = () => _items.Any(i => i.Name.StartsWith("Text:", StringComparison.Ordinal)),
                DoneText = "✓ There's your text on the bed! Click Next.",
                Target = () => TextBtn,
            },
            new()
            {
                Chapter = design,
                Title = "Add shapes",
                Body = "Rectangles, circles, lines and stars — handy for cutting out coasters, tags and frames, or drawing a border around your text.",
                Target = () => ShapeBtn,
            },
            new()
            {
                Chapter = design,
                Title = "Move and resize on the bed",
                Body = "•  Click an item to select it — it turns blue.\n" +
                       "•  Drag it to move it. Drag a white corner square to resize it.\n" +
                       "•  Hold Ctrl and click to select several, or drag a box around them.\n" +
                       "•  Made a mistake? Press Ctrl+Z to undo.\n" +
                       "•  Zoom with the mouse wheel. Drag with the right mouse button to look around.",
                Target = () => CanvasBorder,
            },
            new()
            {
                Chapter = design,
                Title = "Exact size and position",
                Body = "Prefer typing numbers? Enter the position (X across, Y front-to-back) and the size (W = width, H = height) in millimetres, then press Enter.\n\n" +
                       "\"Lock aspect\" stops the design from stretching when you change just the width or height.",
                Target = () => PositionGroup,
            },
            new()
            {
                Chapter = design,
                Title = "Your design list",
                Body = "Every item on the bed is listed here, with how it will be burned. The tick box means \"burn this\" — untick it to keep an item on screen without burning it.\n\n" +
                       "Copy, 90° and Delete tidy things up, and the align buttons snap the selection to the edges or the middle of the bed.",
                Target = () => DesignGroup,
            },

            // ---------------- 5. Settings ----------------
            new()
            {
                Chapter = settings,
                Title = "Line or Fill?",
                Body = "For drawings, text and shapes, choose how to burn them in Mode:\n\n" +
                       "•  Line — follows the outline. Use it to CUT pieces out, or to draw thin lines.\n" +
                       "•  Fill — engraves the whole inside, like colouring in. Best for text and logos.\n" +
                       "•  Fill + Line — both, for a crisp edge.",
                Target = () => LaserGroup,
            },
            new()
            {
                Chapter = settings,
                Title = "Pick your material",
                Body = "Choose what you're working on — plywood, leather, slate… LaserPilot fills in a good starting speed and power for the 10 W B10.\n\n" +
                       "Every piece of wood is a little different, so always try a scrap first. Found settings you love? Click ★ to save them as your own preset.",
                Target = () => PresetCombo,
            },
            new()
            {
                Chapter = settings,
                Title = "Speed, power and passes",
                Body = "•  Speed — how fast the head moves while burning. Slower = darker and deeper.\n" +
                       "•  Power — how strong the laser is, 0–100 %. Higher = darker and deeper.\n" +
                       "•  Passes — how many times to repeat. Thick wood is cut with several passes.\n" +
                       "•  Line gap — how close together the engraving lines are. 0.08–0.1 mm suits the B10.",
                Target = () => OpSpeed.Parent as FrameworkElement,
            },
            new()
            {
                Chapter = settings,
                Title = "Photos look best as dots",
                Body = "For pictures, pick an Image mode. The \"Photo\" modes turn the picture into tiny dots, like a newspaper photo — that's what looks best on wood.\n\n" +
                       "Switch on 🖼 Engraving preview to see the dots before you burn anything. Image adjustments let you brighten a dark photo or add contrast.",
                Target = () => PreviewToggle,
            },
            new()
            {
                Chapter = settings,
                Title = "Not sure which settings to use?",
                Body = "Tools → Material test burns a grid of small squares, each with a different speed and power. " +
                       "Pick the square you like best and use its numbers. It's the quickest way to dial in a new material — and it only uses a small scrap.",
            },

            // ---------------- 6. Running ----------------
            new()
            {
                Chapter = run,
                Title = "Where will it burn?",
                Body = "•  Bed position — burns exactly where the design sits on the grid. Home the laser first.\n" +
                       "•  Current laser position — burns wherever the head is now. Great for placing a design on an object: " +
                       "move the laser over your piece, choose which point of the design goes there, done. A dashed orange box shows where it will land.",
                Target = () => OriginCombo,
            },
            new()
            {
                Chapter = run,
                Title = "Frame — check before you burn",
                Body = "Frame moves the head around the outside edge of your design with the laser OFF, so you can see whether it fits on your material.\n\n" +
                       "Do it every time — it's the easiest way to avoid wasting material.",
                Target = () => FrameBtn,
            },
            new()
            {
                Chapter = run,
                Title = "Preview",
                Body = "Shows exactly what the laser will do and roughly how long it will take — before anything is burned. " +
                       "You can also save the job from there for the B10's MicroSD card.",
                Target = () => PreviewBtn,
            },
            new()
            {
                Chapter = run,
                Title = "Start",
                Body = "When everything looks right — goggles on, material flat and focused, air assist and ventilation running, extinguisher nearby — click START.\n\n" +
                       "LaserPilot shows a final checklist first. Progress and the time left appear at the bottom of the window.",
                Target = () => StartBtn,
            },
            new()
            {
                Chapter = run,
                Title = "Pause and stop",
                Body = "⏸ Pause stops after the current move, and Resume carries on where it left off. ⏹ Stop ends the job smoothly.\n\n" +
                       "In an emergency, always use the red ⛔ STOP button at the top right.",
                Target = () => PauseBtn.Parent as FrameworkElement,
            },
            new()
            {
                Chapter = run,
                Title = "Too light or too dark?",
                Body = "While a job runs you can adjust it on the fly: slide Speed down or Power up to burn darker, or the other way to burn lighter. " +
                       "Changes take effect within a second.",
                Target = () => OverridePanel,
            },

            // ---------------- 7. Extras ----------------
            new()
            {
                Chapter = extras,
                Title = "Save your work",
                Body = "File → Save project (Ctrl+S) keeps your design and all its settings, so you can make it again later.\n\n" +
                       "File → Export G-code saves a file you can copy onto the B10's MicroSD card to run without a computer.",
            },
            new()
            {
                Chapter = extras,
                Title = "The console (for troubleshooting)",
                Body = "This shows the raw messages between LaserPilot and the laser. You won't need it day to day, but it's useful if something goes wrong — " +
                       "and it's where you can type commands if someone helping you asks you to.",
                Target = () => ConsoleExpander,
                OnEnter = () => ConsoleExpander.IsExpanded = true,
                OnLeave = () => ConsoleExpander.IsExpanded = false,
            },
            new()
            {
                Chapter = extras,
                Title = "Updates and help",
                Body = "When a new version of LaserPilot is ready, a message pops up — click Update and it installs itself and restarts.\n\n" +
                       "Need a refresher? Click ❓ Tutorial or press F1 any time. Help → Tutorial chapters jumps straight to one topic.",
                Target = () => TutorialBtn,
            },
            new()
            {
                Chapter = extras,
                Title = "You're ready! 🎉",
                Body = "That's the tour. A good first project:\n\n" +
                       "1.  Put a scrap of plywood on the bed and focus the laser.\n" +
                       "2.  Connect and 🏠 Home.\n" +
                       "3.  Add your name with 🅰 Text.\n" +
                       "4.  Pick \"Wood engrave (light)\" as the material.\n" +
                       "5.  ⬚ Frame, then ▶ START — and watch it work.\n\n" +
                       "Have fun, and stay safe!",
            },
        };
    }
}
