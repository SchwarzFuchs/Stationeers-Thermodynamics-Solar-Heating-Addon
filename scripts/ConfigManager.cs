using BepInEx.Configuration;
using BepInEx.Logging;

namespace ThermodynamicsSolarHeatingAddon.scripts
{
    /// <summary>
    /// [TSHA] settings. Bound against the Config that ModBehaviour auto-creates from the
    /// [StationeersMod] GUID (BepInEx/config/ThermodynamicsSolarHeatingAddon.cfg).
    /// StationeersLaunchPad reads exactly that ConfigFile and renders it in its in-game
    /// settings panel, so every entry is adjustable from the LaunchPad menu at runtime.
    /// All multipliers default to 0.25; 1 = realistic (pure physics) level.
    /// Smaller multiplier == better insulation (less heat through).
    /// </summary>
    public static class ConfigManager
    {
        /// <summary>BepInEx log source (logs to BepInEx chain + BepInEx.log).</summary>
        public static readonly ManualLogSource Log = Logger.CreateLogSource("TSHA");

        // Live config entries. Bound in Bind(Config); always read .Value at the use site
                // so changes made from the in-game menu apply immediately (no restart).
                public static ConfigEntry<float> Entry_SolarHeatingMultiplier;
                public static ConfigEntry<float> Entry_GlassTransmissionMultiplier;
                public static ConfigEntry<bool> Entry_DebugLogInChat;
                public static ConfigEntry<bool> Entry_DebugLog;
                public static ConfigEntry<float> Entry_DebugLogInterval;   // debug cadence in ticks (1 = every tick)
                public static ConfigEntry<bool> Entry_ThreadedInterpolation;  // NEW: async compute every 10 ticks + interpolate
                public static ConfigEntry<bool> Entry_PerfTrace;              // per-stage pipeline timing in the BepInEx log

        public static void Bind(ConfigFile config)
        {
            Entry_SolarHeatingMultiplier = config.Bind(
                                        "General", "solarHeatingMultiplier", 0.25f,
                                        "Overall multiplier for solar heating of room air. "
                                        + "1 = realistic (pure physics) level; 0.25 = quarter strength (default). "
                                        + "0 disables it; >1 heats faster.");

                        Entry_GlassTransmissionMultiplier = config.Bind(
                            "General", "glassTransmissionMultiplier", 1.0f,
                            "Multiplier for glass transmissivity (R). "
                            + "1 = vanilla glass R=0.85 (realistic); "
                            + "0.5 = half transmissivity; 2 = double. "
                            + "Affects greenhouse effect: higher = more solar energy passes through glass.");

                        Entry_DebugLogInChat = config.Bind(
                "Debug", "debugLogInChat", false,
                "Echo the per-tick solar summary into the in-game console (F3). "
                + "Full detail goes to the BepInEx log when Debug/debugLog is enabled.");

            Entry_DebugLog = config.Bind(
                "Debug", "debugLog", false,
                "Verbose BepInEx log output: per-panel (PANEL/NOTINSUN/NOROOM/VACUUM) "
                + "lines + the ATMO summary + the DIAG/SPECS atmosphere state. "
                + "Cadence set by Debug/debugLogInterval. OFF by default.");

            Entry_DebugLogInterval = config.Bind(
                "Debug", "debugLogInterval", 10.0f,
                new ConfigDescription(
                    "How often the debug lines are written, in ticks. 10 = every 10 ticks "
                    + "(default); 1 = every tick. Only applies when Debug/debugLog is ON.",
                    new AcceptableValueRange<float>(1.0f, 100.0f)));

            Entry_ThreadedInterpolation = config.Bind(
                "Performance", "threadedInterpolation", false,
                "Run heavy solar calculations every 10 ticks on a background thread, "
                + "interpolate results for intermediate ticks using last 3 computed frames. "
                + "Reduces main-thread load; may add 1-frame latency. OFF by default.");

            Entry_PerfTrace = config.Bind(
                "Debug", "performanceTrace", false,
                "Log per-stage timing of the compute pipeline (sync tick, worker round, "
                + "interpolation apply) into the BepInEx log. OFF by default.");
        }
    }
}
