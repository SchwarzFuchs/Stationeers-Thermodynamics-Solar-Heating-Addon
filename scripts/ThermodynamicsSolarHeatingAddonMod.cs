using Assets.Scripts;
using BepInEx;
using HarmonyLib;
using StationeersMods.Interface;
using ThermodynamicsSolarHeatingAddon.scripts;
using UnityEngine;

namespace ThermodynamicsSolarHeatingAddon.scripts
{
    /// <summary>
    /// Mod entry point (LaunchPad auto-discovers ModBehaviour subclasses — see
    /// StationeersLaunchPad FindAnySM).  OnLoaded: bind config, create the
    /// simulation core, apply the Harmony patches.
    ///
    /// The [StationeersMod] attribute (a BepInPlugin attribute) makes ModBehaviour
    /// auto-create this.Config from the GUID (BepInEx/config/&lt;GUID&gt;.cfg).
    /// StationeersLaunchPad reads exactly that ConfigFile and renders it in the
    /// in-game settings panel — so all settings are adjustable from the game menu.
    /// </summary>
    [StationeersMod("ThermodynamicsSolarHeatingAddon", "Thermodynamics Solar Heating Addon", "1.0")]
    class ThermodynamicsSolarHeatingAddonMod : ModBehaviour
    {
        public override void OnLoaded(ContentHandler contentHandler)
        {
            Harmony harmony = new Harmony("ThermodynamicsSolarHeatingAddon");
            harmony.PatchAll();

            // Bind against the auto-created Config (GUID-named file) so LaunchPad
            // shows the settings in its in-game menu; entries are read live (.Value).
            ConfigManager.Bind(Config);
            SolarHeatingSimulation.Initialize();

            ConfigManager.Log.LogInfo(
                "[TSHA] ThermodynamicsSolarHeatingAddon loaded. "
                + "solarMult=" + ConfigManager.Entry_SolarHeatingMultiplier.Value
                + " glassMult=" + ConfigManager.Entry_GlassTransmissionMultiplier.Value
                + " threadedInterp=" + ConfigManager.Entry_ThreadedInterpolation.Value
                + " debugLog=" + ConfigManager.Entry_DebugLog.Value
                + ". Settings are editable in the LaunchPad in-game menu. "
                + "Press Home to dump the INSUL panel list.");
        }

        /// <summary>
        /// Stop the optional background compute worker when the mod object is
        /// destroyed (mod unload). MonoBehaviour message handler — Unity resolves
        /// it by name; the worker thread is IsBackground so it dies with the
        /// process anyway, this is just clean shutdown.
        /// </summary>
#pragma warning disable CS0108  // hides inherited member — intentional (MonoBehaviour message handler)
        private void OnDestroy()
        {
            if (SolarHeatingSimulation.Instance != null)
                SolarHeatingSimulation.Instance.Shutdown();
        }
#pragma warning restore CS0108
    }

    /// <summary>
    /// Harmony hooks.  <see cref="onGameUpdate"/> runs every frame (game Update)
    /// and drives the solar tick (~2 Hz) + the Home-key insulation dump.
    /// </summary>
    [HarmonyPatch]
    static class Patches
    {
        [HarmonyPatch(typeof(GameManager), "Update")]
        [HarmonyPostfix]
        public static void onGameUpdate()
        {
            if (SolarHeatingSimulation.Instance != null
                && Input.GetKeyDown(KeyCode.Home))
            {
                SolarHeatingSimulation.Instance.DumpInsulation();
            }

            if (SolarHeatingSimulation.Instance == null)
                return;

            try
            {
                SolarHeatingSimulation.Instance.Tick();
            }
            catch (System.Exception e)
            {
                ConfigManager.Log.LogWarning(
                    "[TSHA] update error: " + e.Message);
            }
        }
    }
}
