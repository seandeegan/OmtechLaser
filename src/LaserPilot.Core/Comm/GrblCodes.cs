namespace LaserPilot.Core.Comm;

/// <summary>Human-readable descriptions for GRBL 1.1 error and alarm codes.</summary>
public static class GrblCodes
{
    public static string ErrorText(int code) => Errors.TryGetValue(code, out var t) ? t : $"Unknown error {code}";
    public static string AlarmText(int code) => Alarms.TryGetValue(code, out var t) ? t : $"Unknown alarm {code}";

    public static readonly Dictionary<int, string> Errors = new()
    {
        [1] = "G-code words consist of a letter and a value. Letter was not found.",
        [2] = "Numeric value format is not valid or missing an expected value.",
        [3] = "GRBL '$' system command was not recognized or supported.",
        [4] = "Negative value received for an expected positive value.",
        [5] = "Homing cycle is not enabled via settings ($22=1 required).",
        [6] = "Minimum step pulse time must be greater than 3 microseconds.",
        [7] = "EEPROM read failed. Reset and restored to default values.",
        [8] = "GRBL '$' command cannot be used unless GRBL is IDLE.",
        [9] = "G-code locked out during alarm or jog state. Unlock with $X or home with $H.",
        [10] = "Soft limits cannot be enabled without homing also enabled.",
        [11] = "Max characters per line exceeded. Line was not processed.",
        [12] = "GRBL '$' setting value exceeds the maximum step rate supported.",
        [13] = "Safety door detected as opened and door state initiated.",
        [14] = "Build info or startup line exceeded EEPROM line length limit.",
        [15] = "Jog target exceeds machine travel. Command ignored. (This protects your rails — the requested jog would go outside the work area.)",
        [16] = "Jog command with no '=' or contains prohibited g-code.",
        [17] = "Laser mode ($32) requires PWM output.",
        [20] = "Unsupported or invalid g-code command found in block.",
        [21] = "More than one g-code command from same modal group found in block.",
        [22] = "Feed rate has not yet been set or is undefined.",
        [23] = "G-code command in block requires an integer value.",
        [24] = "Two G-code commands that both require the use of the XYZ axis words were detected in the block.",
        [25] = "A G-code word was repeated in the block.",
        [26] = "A G-code command implicitly or explicitly requires XYZ axis words in the block, but none were detected.",
        [27] = "N line number value is not within the valid range of 1 - 9,999,999.",
        [28] = "A G-code command was sent, but is missing some required P or L value words in the line.",
        [29] = "GRBL supports six work coordinate systems G54-G59. G59.1/.2/.3 are not supported.",
        [30] = "The G53 G-code command requires either a G0 seek or G1 feed motion mode to be active.",
        [31] = "There are unused axis words in the block and G80 motion mode cancel is active.",
        [32] = "A G2 or G3 arc was commanded but there are no XYZ axis words in the selected plane.",
        [33] = "The motion command has an invalid target.",
        [34] = "Arc radius value is invalid.",
        [35] = "A G2 or G3 arc, traced with the offset definition, is missing the IJK offset word.",
        [36] = "There are unused, leftover G-code words that aren't used by any command in the block.",
        [37] = "The G43.1 dynamic tool length offset command cannot apply an offset to an axis other than its configured axis.",
        [38] = "Tool number greater than max supported value.",
    };

    public static readonly Dictionary<int, string> Alarms = new()
    {
        [1] = "Hard limit triggered — the machine hit a limit switch during motion. Machine position is likely lost. Re-home or verify position before continuing.",
        [2] = "Soft limit alarm — a G-code motion target exceeds the machine work area. Motion was NOT executed. Position is safe. Unlock with $X.",
        [3] = "Reset while in motion. Machine position is lost — re-home before continuing.",
        [4] = "Probe fail. The probe is not in the expected initial state before starting probe cycle.",
        [5] = "Probe fail. Probe did not contact the workpiece within the programmed travel.",
        [6] = "Homing fail. The homing cycle was reset (cancelled) before it could complete.",
        [7] = "Homing fail. Safety door was opened during the homing cycle.",
        [8] = "Homing fail — pull-off did not clear the limit switch. Check wiring and $27 pull-off distance.",
        [9] = "Homing fail — could not find the limit switch within the search distance. This usually means the homing DIRECTION is wrong ($23), the switch is not wired/working, or the search distance is too short. The head likely travelled away from the switches or stalled against the rail.",
        [10] = "Homing fail. On dual-axis machines, could not find the second limit switch for self-squaring.",
    };
}
