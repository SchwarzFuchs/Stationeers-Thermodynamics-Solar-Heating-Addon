using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;

namespace ThermodynamicsSolarHeatingAddon.scripts
{
    /// <summary>
    /// Solar-heating + insulation simulation core (TZ §2–§5).
    ///
    /// For every sun-facing structural panel, energy is delivered to the room
    /// air it bounds:
    ///
    ///     E = s * cos(a) * f * factor
    ///
    ///   s = FaceArea (4 m²)
    ///   a = sun angle (radians), cos(a) = dot(outwardNormal, sunDir)
    ///   f = OrbitalSimulation.SolarIrradiance (W/m², time-varying)
    ///   factor = material factor from ThermalConstants:
    ///     - Single wall:  R + C * A
    ///     - Double wall:  R_in * (R_out + A_out) + A_in * Min(C_in, C_out / k)
    ///     - Triple+:      recursive fold
    ///
    /// Where R=transmissivity, C=conductivity, A=absorptivity, k=1.2 (conductor length penalty).
    /// All layers (including insulation) participate — no special cases.
    ///
    /// Optimisations (per-tick caches):
    ///   - structures list reused
    ///   - LayerStack per panel cached
    ///   - FaceHasSunlight per sunCell cached
    ///   - sunDir, cosAngle precomputed
    ///   - config values read once
    ///   - LogAtmoSummary merged into main loop
    /// </summary>
    public class SolarHeatingSimulation
    {
        public static SolarHeatingSimulation Instance { get; private set; }

        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly float _tickInterval = 0.5f;   // s between solar ticks
        private int _lastStructureCount = -1;
        private int _tick;

        // Grid3 is decimetric (10x world units) -> axial neighbours are +/-20.
        public static readonly Grid3[] Axes =
        {
            new Grid3( 20, 0, 0), new Grid3(-20, 0, 0),
            new Grid3(0,  20, 0), new Grid3(0, -20, 0),
            new Grid3(0, 0,  20), new Grid3(0, 0, -20),
        };

        // Per-tick caches
        private List<Structure> _cachedStructures;
        private Dictionary<Structure, LayerStack> _layerStackCache;
        private Dictionary<Grid3, bool> _sunlightCache;
        private Vector3 _sunDirNormalized;
        private float _irr;
        private float _solarMult;
        private float _glassMult;
        private bool _logNow;
        private int _heated, _notInSun, _noRoom, _ok, _vacuum;
        private double _totalEnergy;

        // ----------------------------------------------------------------
        // Threaded-interpolation mode (optional, config: Performance/threadedInterpolation)
        //
        //   - the heavy compute round (structure scan, layer stacks, DDA sunlight,
        //     factor/cos/energy math) runs every 10 ticks on a background thread;
        //   - between rounds the main thread applies per-panel energy interpolated
        //     from the last 3 completed rounds (linear least-squares fit), so the
        //     heating rate stays smooth and the main thread does only cheap
        //     dictionary lookups + AddEnergy;
        //   - a failed round (scene change, race, exception) is simply skipped —
        //     interpolation continues from the last good history.
        //
        // Thread safety: the worker only READS game state (managed C# objects) and
        // never touches Atmosphere/ConsoleWindow — all game writes (AddEnergy)
        // happen on the main thread. Game reads are wrapped in try/catch; a bad
        // read just discards that round.
        // ----------------------------------------------------------------
        private Thread _worker;
        private readonly object _gate = new object();
        private readonly ManualResetEventSlim _jobSignal = new ManualResetEventSlim(false);
        private volatile bool _workerExit;
        private int _jobTick;                       // tick number of the latest enqueued round
        private long _jobEnqueuedAt;                // Stopwatch timestamp of the latest EnqueueRound (guarded by _gate)
        private ComputeResult _finishedResult;      // guarded by _gate

        private readonly ComputeResult[] _history = new ComputeResult[3];
        private int _historyCount;                  // valid entries, max 3
        private int _historyHead;                   // next slot to overwrite

        public static SolarHeatingSimulation Initialize()
        {
            if (Instance == null)
                Instance = new SolarHeatingSimulation();
            return Instance;
        }

        // ----------------------------------------------------------------
        // Per-frame driver (called from the GameManager.Update postfix).
        // ----------------------------------------------------------------
        public void Tick()
        {
            if (GridController.World == null)
                return;

            int count = GridController.AllStructuresPool.ActiveCount;
            if (count != _lastStructureCount)
            {
                _lastStructureCount = count;
                // Scene changed -> previous compute results are stale.
                lock (_gate) { _finishedResult = null; }
                ClearHistory();
                _diagRoomCells.Clear();
            }

            float elapsed = (float)_watch.ElapsedMilliseconds / 1000f;
            if (elapsed < _tickInterval)
                return;
            _watch.Restart();
            if (elapsed > 1.0f)
                elapsed = 1.0f; // cap after a pause / long frame

            bool threaded = ConfigManager.Entry_ThreadedInterpolation.Value;
            if (threaded)
            {
                // Worker failed to start (OOM, …) -> fall back to synchronous mode
                // for this tick instead of skipping heating entirely.
                if (!EnsureWorkerRunning())
                    threaded = false;
            }
            else
            {
                StopWorkerIfAny();
            }

            try
            {
                _tick++;
                string summary = threaded
                    ? RunThreadedTick()
                    : RunSimulation();
                if (LogCadenceDue())
                    LogDiagnostics();
                if (ConfigManager.Entry_DebugLogInChat.Value)
                    ConsoleWindow.Print("[TSHA]: " + summary);
            }
            catch (Exception e)
            {
                ConfigManager.Log.LogInfo("[TSHA] tick error: " + e.Message);
            }
        }

        // ----------------------------------------------------------------
        // Threaded mode: one main-thread tick.
        //   - collect a finished round into the history (if any);
        //   - apply per-panel energy interpolated from the last 3 rounds;
        //   - every 10 ticks enqueue a heavy compute round on the worker.
        // ----------------------------------------------------------------
        private string RunThreadedTick()
        {
            CollectFinishedResult();

            string summary = InterpolateAndApply();

            // First round starts immediately (no 10-tick warmup); then every 10 ticks.
            // Retries after a failed first round are throttled to every 3rd tick.
            if (_historyCount == 0 ? _tick % 3 == 0 : _tick % 10 == 0)
                EnqueueRound(_tick);

            return summary;
        }

        // ----------------------------------------------------------------
        // Synchronous mode tick (threadedInterpolation = false) — unchanged path.
        // ----------------------------------------------------------------
        public string RunSimulation()
        {
            if (GridController.World == null) return "";

            bool perf = ConfigManager.Entry_PerfTrace.Value;
            var perfT = new PipelineTimer(perf, StageCount);

            // Clear per-tick caches
            _layerStackCache = new Dictionary<Structure, LayerStack>();
            _sunlightCache = new Dictionary<Grid3, bool>();

            // Read config once per tick
            _irr = OrbitalSimulation.SolarIrradiance;
            _solarMult = ConfigManager.Entry_SolarHeatingMultiplier.Value;
            _glassMult = ConfigManager.Entry_GlassTransmissionMultiplier.Value;
            _logNow = LogCadenceDue();

            // Precompute sun direction once
            _sunDirNormalized = OrbitalSimulation.WorldSunVector;
            if (_sunDirNormalized.sqrMagnitude < 1e-6f) _sunDirNormalized = Vector3.up;
            _sunDirNormalized.Normalize();

            // Reset counters
            _heated = _notInSun = _noRoom = _ok = _vacuum = 0;
            _totalEnergy = 0.0;

            perfT.BeginStage(StageScan);
            // Get structures list once (reuse if count unchanged)
            if (_cachedStructures == null || _cachedStructures.Count != GridController.AllStructuresPool.ActiveCount)
            {
                _cachedStructures = GridController.AllStructuresPool.ToList();
            }
            var structures = _cachedStructures;

            for (int i = 0; i < structures.Count; i++)
            {
                ProcessPanel(structures[i], perfT);
            }

            perfT.BeginStage(StageTail);
            // LogAtmoSummary data now comes from counters accumulated during the loop
            if (_logNow)
            {
                int total = 0, withRoom = 0, withAtmo = 0;
                foreach (var kvp in _layerStackCache)
                {
                    if (kvp.Value == null) continue;
                    total++;
                    withRoom++;
                    if (GetLocalAtmosphere(kvp.Value.RoomAirCell) != null)
                        withAtmo++;
                }
                int rooms = RoomController.World != null ? RoomController.World.Rooms.Count : 0;
                ConfigManager.Log.LogInfo(
                    "[TSHA] ATMO: total=" + total
                    + " local=" + withAtmo
                    + " withRoom=" + withRoom
                    + " roomMap=" + rooms);
            }

            if (_logNow)
            {
                ConfigManager.Log.LogInfo(
                    "[TSHA] tick#" + _tick + ": irr=" + _irr.ToString("F1")
                    + "W/m2 heated=" + _heated + " J=" + _totalEnergy.ToString("F0")
                    + " | notInSun=" + _notInSun + " noRoom=" + _noRoom
                    + " OK=" + _ok + " vacuum=" + _vacuum);
            }

            long totalNs = perfT.Finish();
            if (perf)
                LogPerfLine("sync tick#" + _tick, perfT, totalNs, 0L, _heated);

            return "tick#" + _tick + " irr=" + _irr.ToString("F1")
                + "W/m2 heated=" + _heated + " J=" + _totalEnergy.ToString("F0");
        }

        private void ProcessPanel(Structure s, PipelineTimer t)
        {
            if (s == null) return;
            if (!IsSolarPanel(s)) return;

            Grid3 g = s.GridPosition;

            t.BeginStage(StageLayers);
            // Get or compute LayerStack for this panel
            LayerStack stack;
            if (!_layerStackCache.TryGetValue(s, out stack))
            {
                string bindWhy;
                stack = ResolveLayerStack(s, _sunDirNormalized, out bindWhy);
                _layerStackCache[s] = stack;
            }

            if (stack == null || stack.LayersArray.Length == 0)
            {
                _noRoom++;
                if (_logNow)
                    ConfigManager.Log.LogInfo("[TSHA] DBG: (" + g.x + "," + g.y + "," + g.z
                        + ") " + s.PrefabName + " NOROOM: no room air on any side");
                return;
            }

            NoteRoomCell(stack.RoomAirCell);

            t.BeginStage(StageSunlight);
            // Determine sun test cell
            Grid3 sunCell = ChooseSunTestCell(g, stack.RoomDir);

            // Cached sunlight test
            bool hasSun;
            if (!_sunlightCache.TryGetValue(sunCell, out hasSun))
            {
                string sunWhy;
                hasSun = FaceHasSunlight(sunCell, out sunWhy);
                _sunlightCache[sunCell] = hasSun;
                if (!hasSun && _logNow)
                    ConfigManager.Log.LogInfo("[TSHA] DBG: (" + g.x + "," + g.y + "," + g.z
                        + ") " + s.PrefabName + " NOTINSUN test=(" + sunCell.x + "," + sunCell.y + "," + sunCell.z
                        + "): " + sunWhy);
            }

            if (!hasSun)
            {
                _notInSun++;
                return;
            }

            t.BeginStage(StageAtmo);
            Atmosphere atmo = GetLocalAtmosphere(stack.RoomAirCell);
            if (atmo == null)
            {
                _vacuum++;
                if (_logNow)
                    ConfigManager.Log.LogInfo("[TSHA] DBG: (" + g.x + "," + g.y + "," + g.z
                        + ") " + s.PrefabName + " VACUUM room=(" + stack.RoomAirCell.x + "," + stack.RoomAirCell.y + "," + stack.RoomAirCell.z + ")");
                return;
            }

            t.BeginStage(StageMath);
            // Compute material factor
            float factor = ThermalConstants.MultiLayerFactor(stack.LayersArray);
            float buildState = GetBuildState(s);

            // Cosine law using precomputed sunDir and stack.RoomDir
            Vector3 outNormal = stack.RoomDir.ToVector3() * -1f;
            if (outNormal.sqrMagnitude < 1e-6f) outNormal = Vector3.up;
            outNormal.Normalize();
            float cosAngle = Mathf.Max(0f, Vector3.Dot(outNormal, _sunDirNormalized));

            double energy = (double)_irr
                * ThermalConstants.FaceArea
                * buildState
                * factor
                * _solarMult
                * cosAngle;

            t.BeginStage(StageApply);
            if (energy > 0.0)
            {
                ApplySolarEnergy(atmo, energy);
                _totalEnergy += energy;
                _ok++;
            }
            else
            {
                _vacuum++;
            }
            _heated++;

            if (_logNow)
            {
                ConfigManager.Log.LogInfo(
                    "[TSHA] PANEL: (" + g.x + "," + g.y + "," + g.z + ") "
                    + s.PrefabName + " stack=" + stack.LayersDesc + " cos=" + cosAngle.ToString("F3")
                    + " factor=" + factor.ToString("F4")
                    + " atmoID=" + AtmoId(atmo)
                    + " E=" + energy.ToString("F0") + "J");
            }
        }

        // ----------------------------------------------------------------
        // LayerStack: which side faces the room + all layers in that direction.
        // ----------------------------------------------------------------
        private sealed class LayerStack
        {
            public Grid3 RoomAirCell;
            public Grid3 RoomDir;              // direction from panel to room (inward normal)
            public ThermalConstants.Material[] LayersArray; // [outer, inner1, inner2...] pre-allocated array
            public string LayersDesc;          // cached description for logging
        }

        private LayerStack ResolveLayerStack(Structure panel, Vector3 sunDir, out string why)
        {
            Grid3 pos = panel.GridPosition;

            string sides = "";
            LayerStack best = null;
            foreach (Grid3 d in Axes)
            {
                // Which cell on this side holds the room air?
                Grid3 roomCell;
                if (IsRoomCell(pos + d))
                {
                    roomCell = pos + d;            // single wall / inRoom
                }
                else if (IsRoomCell(pos + d + d))
                {
                    roomCell = pos + d + d;        // classic double wall
                }
                else
                {
                    continue; // no room on this side
                }
                sides += "[" + d.x + "," + d.y + "," + d.z + "]";

                // Collect all structural layers between panel and room air (inclusive of panel)
                var layersList = new List<ThermalConstants.Material>(4); // small capacity, typical 1-3 layers
                layersList.Add(ThermalConstants.GetMaterial(panel.PrefabName));

                // Check cell at pos+d for inner layer(s)
                Grid3 innerCell = pos + d;
                Cell c = GridController.World.GetCell(innerCell);
                if (c != null && c.AllStructures.Count > 0)
                {
                    Vector3 dNorm = d.ToVector3();
                    dNorm.Normalize();
                    foreach (Structure cand in c.AllStructures)
                    {
                        if (cand == null || !IsSolarPanel(cand))
                            continue;
                        Vector3 n = cand.Forward;
                        if (n.sqrMagnitude < 1e-6f)
                            continue;
                        n.Normalize();
                        if (Mathf.Abs(Vector3.Dot(n, dNorm)) < 0.9f)
                            continue; // attached to a different wall -> not counted here
                        layersList.Add(ThermalConstants.GetMaterial(cand.PrefabName));
                    }
                }

                // Convert to array once, cache description
                var layersArray = layersList.ToArray();
                string desc = "[" + string.Join("|", Array.ConvertAll(layersArray, m => m.ToString())) + "]";

                bool awayFromSun = Vector3.Dot(d.ToVector3(), sunDir) < 0f;
                var stack = new LayerStack { RoomAirCell = roomCell, RoomDir = d, LayersArray = layersArray, LayersDesc = desc };

                if (best == null) { best = stack; continue; }
                bool bestAway = Vector3.Dot((best.RoomAirCell - pos).ToVector3(), sunDir) < 0f;
                if (awayFromSun != bestAway)
                    best = awayFromSun ? best : stack;
                else
                {
                    float distNew = (stack.RoomAirCell - pos).SqrMagnitude();
                    float distBest = (best.RoomAirCell - pos).SqrMagnitude();
                    if (distNew < distBest) best = stack;
                }
            }

            if (best == null)
            {
                why = "no room air on any side"
                    + (sides.Length > 0 ? " (rooms found at sides " + sides + ")" : "");
                return null;
            }
            why = "room=(" + best.RoomAirCell.x + "," + best.RoomAirCell.y + "," + best.RoomAirCell.z
                + ") side=(" + best.RoomDir.x + "," + best.RoomDir.y + "," + best.RoomDir.z + ")"
                + (sides.Length > 1 ? " sides=" + sides : "");
            return best;
        }

        // ----------------------------------------------------------------
        // Room / atmosphere lookups
        // ----------------------------------------------------------------
        private static bool IsRoomCell(Grid3 cell)
        {
            return RoomController.World != null
                && RoomController.World.RoomLookup.ContainsKey(cell);
        }

        private static Atmosphere GetLocalAtmosphere(Grid3 cell)
        {
            var ac = GridController.World.AtmosphericsController;
            if (ac == null)
                return null;
            Atmosphere a = ac.GetAtmosphereLocal(new WorldGrid(cell));
            if (a == null)
                return null;
            if (a.TotalMoles.ToFloat() < 0.5f)
                return null; // vacuum / evacuated room
            return a;
        }

        // Adds solar energy to an atmosphere's gas mix in a thread-safe way.
        //
        // WHY THIS WRAPPER EXISTS (root cause of the "moles flow, energy doesn't"
        // airlock bug found in LogOutput-16/17):
        //
        // The game runs its atmosphere simulation (MixInWorld, gas/energy transfer
        // between cells) on a dedicated worker thread (AtmosphericsManager inherits
        // ThreadedManager and starts WorkingThread unconditionally). To let the main
        // thread read state without a lock, Mole caches its Energy/Quantity:
        //
        //     Mole.Energy.get  => if (IsCachable && !AtmosphereHelper.CanWriteAccess)
        //                            return _energyCached;   // main thread: STALE cache
        //     Mole.Energy.set  => _energy = value;           // ALWAYS the real field
        //
        // (CanWriteAccess == "current thread != main thread"; IsCachable is set true
        // by the worker's per-tick CacheDataTask.) So on the MAIN thread a
        // read-modify-write like GasMixture.AddEnergy(e) — which is
        // "TotalEnergy += e", i.e. getter(stale cache sum) then setter(assign real
        // fields proportional to heat capacity) — silently RESETS the real gas
        // energy to (stale cache + solar), destroying the energy transfer the
        // worker thread just performed. Gas QUANTITY is never touched by this mod,
        // so moles kept moving room→airlock while energy did not follow: the
        // airlock filled with cold gas (→ −160 °C) and the room's gas heated up
        // (→ +120 °C) while total energy stayed flat.
        //
        // Setting IsCachable=false first forces the Mole getters to return the REAL
        // fields, so "TotalEnergy += e" is a true addition. The worker thread is
        // unaffected (its CanWriteAccess is true and short-circuits the cache
        // branch), and the game re-enables caching on its next tick.
        private static void ApplySolarEnergy(Atmosphere atmo, double joules)
        {
            atmo.GasMixture.IsCachable = false;
            atmo.GasMixture.AddEnergy(new MoleEnergy(joules));
        }

        // ----------------------------------------------------------------
        // Airlock diagnostics (bug hunt: airlock cools while filling)
        //
        // Logs, for every room cell + its 6 neighbours (deduped), the full
        // atmosphere state each tick: object identity (atmoID), gas/liquid
        // moles, TOTAL gas energy, temperature, pressure, volume, and a
        // per-species moles+energy breakdown. atmoID tells us whether the
        // airlock and the room are ONE Atmosphere or two; EGas vs nGas over
        // time tells us whether energy moves with the moles.
        // ----------------------------------------------------------------
        private readonly Dictionary<string, Grid3> _diagRoomCells
            = new Dictionary<string, Grid3>();

        private static string CellKey(Grid3 c)
        {
            return c.x + "," + c.y + "," + c.z;
        }

        private void NoteRoomCell(Grid3 c)
        {
            _diagRoomCells[CellKey(c)] = c;
        }

        // Object identity of an Atmosphere (stable per object, distinguishes
        // the airlock's atmosphere from the room's if they are two objects).
        private static int AtmoId(Atmosphere a)
        {
            if (a == null) return -1;
            return RuntimeHelpers.GetHashCode(a);
        }

        private static string MolePart(string name, Mole m)
        {
            return name + ":n=" + m.Quantity.ToDouble().ToString("F2")
                 + ",E=" + m.Energy.ToDouble().ToString("F0");
        }

        // Enumerate every Mole field on GasMixture; return "Name:n=..,E=.."
        // for each species holding a non-zero amount.
        private static string SpeciesBreakdown(GasMixture mix)
        {
            var parts = new List<string>();
            foreach (var f in typeof(GasMixture).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.FieldType != typeof(Mole)) continue;
                Mole m = (Mole)f.GetValue(mix);
                if (Math.Abs(m.Quantity.ToDouble()) < 1e-9) continue;
                parts.Add(MolePart(f.Name, m));
            }
            return parts.Count == 0 ? "<empty>" : string.Join(" | ", parts);
        }

        private void LogCellDiagnostics(Grid3 cell)
        {
            var ac = GridController.World.AtmosphericsController;
            if (ac == null) return;
            Atmosphere a = ac.GetAtmosphereLocal(new WorldGrid(cell));
            string c = "(" + cell.x + "," + cell.y + "," + cell.z + ")";
            if (a == null)
            {
                ConfigManager.Log.LogInfo("[TSHA] DIAG t#" + _tick + " cell=" + c + " NO_ATMOSPHERE");
                return;
            }
            double TC = a.Temperature.ToDouble() - 273.15;
            double p = a.PressureGasses.ToDouble();
            double nGas = a.TotalMolesGases.ToDouble();
            double nLiq = a.TotalMolesLiquids.ToDouble();
            double EGas = a.GasMixture.TotalEnergy.ToDouble();
            double vol = a.Volume.ToFloat();
            ConfigManager.Log.LogInfo("[TSHA] DIAG t#" + _tick + " cell=" + c
                + " atmoID=" + AtmoId(a)
                + " T=" + TC.ToString("F1") + "C"
                + " p=" + p.ToString("F2") + "kPa"
                + " nGas=" + nGas.ToString("F1")
                + " EGas=" + EGas.ToString("F0") + "J"
                + " nLiq=" + nLiq.ToString("F3")
                + " vol=" + vol.ToString("F0") + "L");
            ConfigManager.Log.LogInfo("[TSHA] SPECS t#" + _tick + " cell=" + c + " " + SpeciesBreakdown(a.GasMixture));
            // Open/closed faces, room/structure membership, proximity to the
            // global (space) atmosphere — shows which faces leak to space and
            // which mixing sets a cell participates in.
            string openList = "", closedList = "";
            if (a.OpenNeighbors != null)
            {
                for (int i = 0; i < a.OpenNeighbors.Count; i++)
                {
                    Grid3 g = a.OpenNeighbors[i];
                    openList += (i > 0 ? "," : "") + "(" + g.x + "," + g.y + "," + g.z + ")";
                }
            }
            if (a.ClosedNeighbors != null)
            {
                for (int i = 0; i < a.ClosedNeighbors.Count; i++)
                {
                    Grid3 g = a.ClosedNeighbors[i];
                    closedList += (i > 0 ? "," : "") + "(" + g.x + "," + g.y + "," + g.z + ")";
                }
            }
            bool closeToGlobal = false;
            try
            {
                closeToGlobal = a.IsCloseToGlobal(new PressurekPa(AtmosphereHelper.GlobalAtmosphereNeighbourThreshold * AtmosphereHelper.NewAtmosSupressionMultiplier()));
            }
            catch { }
            ConfigManager.Log.LogInfo("[TSHA] OPEN t#" + _tick + " cell=" + c
                + " room=" + (a.Room != null)
                + " cellStruct=" + (a.Cell != null)
                + " flags=" + (int)a.FaceFlags
                + " closeToGlobal=" + closeToGlobal
                + " open=[" + openList + "]"
                + " closed=[" + closedList + "]");
        }

        // Single cadence gate for ALL debug lines (legacy _logNow + DIAG/SPECS).
        // Interval comes from Debug/debugLogInterval (1 = every tick, 10 = default).
        private bool LogCadenceDue()
        {
            if (!ConfigManager.Entry_DebugLog.Value) return false;
            int interval = Math.Max(1, (int)Math.Round(ConfigManager.Entry_DebugLogInterval.Value));
            return (_tick % interval) == 0;
        }

        private void LogDiagnostics()
        {
            if (_diagRoomCells.Count == 0) return;
            var cells = new List<Grid3>();
            var seen = new HashSet<string>();
            void Add(Grid3 cc)
            {
                string k = CellKey(cc);
                if (seen.Add(k)) cells.Add(cc);
            }
            foreach (var kv in _diagRoomCells)
            {
                Grid3 rc = kv.Value;
                Add(rc);
                for (int i = 0; i < Axes.Length; i++)
                    Add(rc + Axes[i]);
            }
            ConfigManager.Log.LogInfo("[TSHA] ==== DIAG t#" + _tick + " (" + cells.Count + " cells) ==== ");
            foreach (var c in cells)
                LogCellDiagnostics(c);
        }

        // ----------------------------------------------------------------
        // Sunlight test with per-tick cache.
        // ----------------------------------------------------------------
        /// <summary>
        /// Cell for the sunlight test. The panel's own structure must not be
        /// its own occluder:
        ///   transparent panel (frame/glass) -> test at the panel cell itself;
        ///   the game's strict ray from that cell also catches lateral
        ///   overhangs ("зонтик" из стен), which the outer-cell ray can miss;
        ///   light-blocking panel (solid wall) -> test from the sun-side neighbor.
        /// </summary>
        private static Grid3 ChooseSunTestCell(Grid3 g, Grid3 roomDir)
        {
            Cell panel = GridController.World.GetCell(g);
            if (panel == null || !panel.IsBlockedLight)
                return g;
            return new Grid3(g.x - roomDir.x, g.y - roomDir.y, g.z - roomDir.z);
        }

        private bool FaceHasSunlight(Grid3 cellPos, out string why)
        {
            why = "";
            if (OrbitalSimulation.IsEclipse)
            {
                why = "eclipse";
                return false;
            }

            Cell c = GridController.World.GetCell(cellPos);
            if (c != null)
            {
                if (c.IsBlockedLight)
                {
                    why = "cell contains a light-blocking structure";
                    return false;
                }

                Vector3 p = c.Position;
                Vector3 compare = p + LightManager.LightRay;
                Vector3 face = RocketGrid.GetClosestFace(p, compare);
                if (!c.IsOpenLight(face))
                {
                    why = "sun-side face closed by a neighbouring structure (face="
                        + face.x.ToString("F1") + "," + face.y.ToString("F1") + "," + face.z.ToString("F1") + ")";
                    return false;
                }
            }

            bool r = LightManager.HasSunlight(cellPos.ToVector3());
            why = r ? "clear" : "ray from cell to sun is blocked (terrain/structure in the path)";
            return r;
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------
        private static bool IsSolarPanel(Structure s)
        {
            return s != null
                && s.PrefabName != null
                && ThermalConstants.IsSolarPanel(s.PrefabName);
        }

        private static float GetBuildState(Structure s)
        {
            int total = s.BuildStates != null ? s.BuildStates.Count : 1;
            if (total <= 0)
                return 1.0f;
            float built = s.CurrentBuildStateIndex + 1f;
            if (built < 0f) built = 0f;
            return Mathf.Clamp01(built / total);
        }

        // ----------------------------------------------------------------
        // Pipeline timing (Debug/performanceTrace)
        // ----------------------------------------------------------------
        // Stage layout for the 8-stage timer (sync tick + worker round):
        private const int StageCount = 8;
        private const int StageSetup = 0;     // config reads, sunDir, cache reset
        private const int StageScan = 1;      // structure list
        private const int StageLayers = 2;    // ResolveLayerStack (geometry, room search)
        private const int StageSunlight = 3;  // FaceHasSunlight (DDA raycast — heaviest)
        private const int StageAtmo = 4;      // GetLocalAtmosphere lookup
        private const int StageMath = 5;      // factor, cos, energy math
        private const int StageApply = 6;     // AddEnergy
        private const int StageTail = 7;      // logging, the rest

        /// <summary>
        /// Stage timer over Stopwatch timestamps. When disabled every call is a
        /// single branch+return (~1 ns), so hot-path cost is negligible.
        /// </summary>
        private sealed class PipelineTimer
        {
            public readonly long[] StageNs;  // StageNs[i] = ns spent in stage i
            private readonly bool _enabled;
            private long _t0;                // start timestamp
            private long _tPrev;             // timestamp of the last stage mark
            private int _current;            // current stage index

            public PipelineTimer(bool enabled, int stageCount)
            {
                _enabled = enabled;
                StageNs = new long[stageCount];
                _current = 0;
                if (enabled)
                {
                    _t0 = Stopwatch.GetTimestamp();
                    _tPrev = _t0;
                }
            }

            /// <summary>Start of stage idx: elapsed time is folded into the previous stage.</summary>
            public void BeginStage(int idx)
            {
                if (!_enabled)
                    return;
                long now = Stopwatch.GetTimestamp();
                StageNs[_current] += now - _tPrev;
                _tPrev = now;
                if (idx > _current)
                    _current = idx;
            }

            /// <summary>Close the timer; returns total ns (0 when disabled).</summary>
            public long Finish()
            {
                if (!_enabled)
                    return 0L;
                long now = Stopwatch.GetTimestamp();
                StageNs[_current] += now - _tPrev;
                return now - _t0;
            }
        }

        private static string Ms(long ns)
        {
            return (ns / (double)Stopwatch.Frequency).ToString("F3") + "ms";
        }

        /// <summary>PERF log line for an 8-stage pipeline sample.</summary>
        private static void LogPerfLine(string what, PipelineTimer t, long totalNs, long waitNs, int panels)
        {
            ConfigManager.Log.LogInfo(
                "[TSHA] PERF " + what
                + ": total=" + Ms(totalNs)
                + (waitNs > 0 ? " wait=" + Ms(waitNs) : "")
                + " | setup=" + Ms(t.StageNs[StageSetup])
                + " scan=" + Ms(t.StageNs[StageScan])
                + " layers=" + Ms(t.StageNs[StageLayers])
                + " sun=" + Ms(t.StageNs[StageSunlight])
                + " atmo=" + Ms(t.StageNs[StageAtmo])
                + " math=" + Ms(t.StageNs[StageMath])
                + " apply=" + Ms(t.StageNs[StageApply])
                + " tail=" + Ms(t.StageNs[StageTail])
                + " | panels=" + panels);
        }

        // ----------------------------------------------------------------
        // Diagnostics (TZ §7) — INSUL dump
        // ----------------------------------------------------------------
        public void DumpInsulation()
        {
            if (GridController.World == null) return;
            var sb = new System.Text.StringBuilder();
            int n = 0;
            var structures = _cachedStructures ?? GridController.AllStructuresPool.ToList();
            for (int i = 0; i < structures.Count; i++)
            {
                Structure s = structures[i];
                if (s == null || !IsSolarPanel(s)) continue;
                if (ThermalConstants.GetMaterial(s.PrefabName) != ThermalConstants.Material.Insulation)
                    continue;
                n++;
                Grid3 g = s.GridPosition;
                sb.AppendLine("[TSHA] INSUL: (" + g.x + "," + g.y + "," + g.z
                    + ") fwd=(" + s.Forward.x.ToString("F2")
                    + "," + s.Forward.y.ToString("F2")
                    + "," + s.Forward.z.ToString("F2") + ")");
            }
            ConfigManager.Log.LogInfo("[TSHA] INSUL dump: " + n + " insulation panels");
            if (n > 0)
                ConfigManager.Log.LogInfo(sb.ToString());
            if (ConfigManager.Entry_DebugLogInChat.Value)
                ConsoleWindow.Print("[TSHA] INSUL dump: " + n + " insulation panels (see BepInEx log)");
        }

        // ====================================================================
        // Threaded-interpolation mode
        // ====================================================================

        private sealed class ComputeResult
        {
            public readonly int Tick;
            public readonly PanelEnergy[] Panels;   // panels with a room (energy may be 0)
            public readonly double TotalEnergy;
            public readonly int Heated, NotInSun, NoRoom;

            public ComputeResult(int tick, PanelEnergy[] panels, double totalEnergy, int heated, int notInSun, int noRoom)
            {
                Tick = tick;
                Panels = panels;
                TotalEnergy = totalEnergy;
                Heated = heated;
                NotInSun = notInSun;
                NoRoom = noRoom;
            }

            public sealed class PanelEnergy
            {
                public readonly Structure Structure;
                public readonly Grid3 RoomCell;     // where the main thread applies the energy
                public readonly double EnergyPerTick;

                public PanelEnergy(Structure structure, Grid3 roomCell, double energyPerTick)
                {
                    Structure = structure;
                    RoomCell = roomCell;
                    EnergyPerTick = energyPerTick;
                }
            }
        }

        // ------------------------------------------------------------------
        // Worker lifecycle
        // ------------------------------------------------------------------
        private bool EnsureWorkerRunning()
        {
            if (_worker != null && _worker.IsAlive)
                return true;
            try
            {
                lock (_gate)
                {
                    _workerExit = false;
                    _finishedResult = null; // drop any stale result from a previous worker
                }
                _worker = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "TSHA-solar-worker"
                };
                _worker.Start();
                ConfigManager.Log.LogInfo("[TSHA] threaded mode: worker thread started");
                return true;
            }
            catch (Exception e)
            {
                ConfigManager.Log.LogInfo("[TSHA] threaded mode: worker start failed: " + e.Message
                    + " — falling back to synchronous mode");
                _worker = null;
                return false;
            }
        }

        public void StopWorkerIfAny()
        {
            if (_worker == null)
                return;
            lock (_gate)
            {
                _workerExit = true;
                _finishedResult = null;
            }
            _jobSignal.Set();
            try { _worker.Join(2000); }
            catch { /* thread already gone */ }
            _worker = null;
            ClearHistory();
            ConfigManager.Log.LogInfo("[TSHA] threaded mode: worker thread stopped");
        }

        /// <summary>Called when the mod is unloaded / disabled.</summary>
        public void Shutdown()
        {
            // NOTE: _jobSignal is intentionally NOT disposed — if the plugin is
            // re-enabled within the same process the worker must be able to restart.
            StopWorkerIfAny();
        }

        private void WorkerLoop()
        {
            while (true)
            {
                _jobSignal.Wait();

                bool exit;
                int roundTick;
                long enqueuedAt;
                lock (_gate)
                {
                    exit = _workerExit;
                    _workerExit = false;
                    _jobSignal.Reset();
                    roundTick = _jobTick;
                    enqueuedAt = _jobEnqueuedAt;
                }
                if (exit)
                    return;

                ComputeResult r = null;
                try
                {
                    long waitNs = enqueuedAt > 0 ? Stopwatch.GetTimestamp() - enqueuedAt : 0L;
                    r = ComputeRound(roundTick, waitNs);
                }
                catch (Exception e)
                {
                    // A bad round (scene unload, structure race, …) is NOT fatal:
                    // the main thread keeps interpolating from the last good history.
                    ConfigManager.Log.LogInfo("[TSHA] worker round failed: " + e.Message);
                }
                if (r != null)
                {
                    // Re-check the exit flag: StopWorkerIfAny may have run while
                    // this round was still computing — don't leave a stale result
                    // behind for the next worker generation.
                    lock (_gate)
                    {
                        if (!_workerExit)
                            _finishedResult = r;
                    }
                }
            }
        }

        private void EnqueueRound(int tick)
        {
            lock (_gate)
            {
                _jobTick = tick;
                _jobEnqueuedAt = Stopwatch.GetTimestamp();
                if (_finishedResult != null)
                    _finishedResult = null; // superseded before the main thread saw it
            }
            _jobSignal.Set();
        }

        // ------------------------------------------------------------------
        // History (ring of the last 3 completed rounds)
        // ------------------------------------------------------------------
        private void CollectFinishedResult()
        {
            ComputeResult fresh;
            lock (_gate)
            {
                fresh = _finishedResult;
                _finishedResult = null;
            }
            if (fresh != null)
            {
                _history[_historyHead] = fresh;
                _historyHead = (_historyHead + 1) % 3;
                if (_historyCount < 3)
                    _historyCount++;

                if (LogCadenceDue())
                {
                    ConfigManager.Log.LogInfo(
                        "[TSHA] ROUND#" + fresh.Tick + ": heated=" + fresh.Heated
                        + " J/tick=" + fresh.TotalEnergy.ToString("F0")
                        + " | notInSun=" + fresh.NotInSun + " noRoom=" + fresh.NoRoom
                        + " (computed on worker thread)");
                }
            }
        }

        private void ClearHistory()
        {
            for (int i = 0; i < 3; i++)
                _history[i] = null;
            _historyCount = 0;
            _historyHead = 0;
        }

        /// <summary>Returns the history entries oldest→newest, max 3.</summary>
        private int GetHistory(ComputeResult[] outEntries)
        {
            int n = Math.Min(_historyCount, 3);
            for (int i = 0; i < n; i++)
            {
                int idx = (_historyHead - n + i) % 3;
                if (idx < 0) idx += 3;
                outEntries[i] = _history[idx];
            }
            return n;
        }

        // ------------------------------------------------------------------
        // Heavy round (worker thread): full scan + compute, NO game writes.
        // ------------------------------------------------------------------
        private ComputeResult ComputeRound(int tick, long waitNs)
        {
            if (GridController.World == null)
                return null;

            bool perf = ConfigManager.Entry_PerfTrace.Value;
            var perfT = new PipelineTimer(perf, StageCount);

            // Read config values once for this round
            float irr = OrbitalSimulation.SolarIrradiance;
            float solarMult = ConfigManager.Entry_SolarHeatingMultiplier.Value;

            // Compute sun direction
            Vector3 sunDir = OrbitalSimulation.WorldSunVector;
            if (sunDir.sqrMagnitude < 1e-6f)
                sunDir = Vector3.up;
            sunDir.Normalize();

            perfT.BeginStage(StageScan);
            // Get structures list
            var structures = GridController.AllStructuresPool.ToList();

            int heated = 0, notInSun = 0, noRoom = 0;
            double totalEnergy = 0.0;
            var panels = new List<ComputeResult.PanelEnergy>();

            for (int i = 0; i < structures.Count; i++)
            {
                var s = structures[i];
                if (!IsSolarPanel(s)) continue;

                Grid3 g = s.GridPosition;

                perfT.BeginStage(StageLayers);
                LayerStack stack = null;
                string why = "";
                stack = ResolveLayerStack(s, sunDir, out why);

                if (stack == null || stack.LayersArray.Length == 0)
                {
                    noRoom++;
                    continue;
                }

                Grid3 sunCell = ChooseSunTestCell(g, stack.RoomDir);

                perfT.BeginStage(StageSunlight);
                string sunWhy;
                bool hasSun = FaceHasSunlight(sunCell, out sunWhy);
                if (!hasSun)
                {
                    notInSun++;
                    continue;
                }

                perfT.BeginStage(StageMath);
                float factor = ThermalConstants.MultiLayerFactor(stack.LayersArray);
                float buildState = GetBuildState(s);

                Vector3 outNormal = stack.RoomDir.ToVector3() * -1f;
                if (outNormal.sqrMagnitude < 1e-6f)
                    outNormal = Vector3.up;
                outNormal.Normalize();
                float cosAngle = Mathf.Max(0f, Vector3.Dot(outNormal, sunDir));

                double energy = (double)irr
                    * ThermalConstants.FaceArea
                    * buildState
                    * factor
                    * solarMult
                    * cosAngle;

                if (energy > 0.0)
                {
                    totalEnergy += energy;
                    panels.Add(new ComputeResult.PanelEnergy(s, stack.RoomAirCell, energy));
                    heated++;
                }
                else
                {
                    noRoom++;
                }
            }

            long totalNs = perfT.Finish();
            if (perf)
                LogPerfLine("round#" + tick + " (worker)", perfT, totalNs, waitNs, heated);

            return new ComputeResult(tick, panels.ToArray(), totalEnergy, heated, notInSun, noRoom);
        }

        // ------------------------------------------------------------------
        // Main thread: apply interpolated energy (the only game writes).
        // ------------------------------------------------------------------
        private string InterpolateAndApply()
        {
            var perfT = new PipelineTimer(ConfigManager.Entry_PerfTrace.Value, 2);

            var entries = new ComputeResult[3];
            int n = GetHistory(entries);
            if (n == 0)
                return "tick#" + _tick + " threaded: warming up (no rounds yet)";

            // Per-round lookups: round index -> (Structure -> energy). O(N) total.
            var perRound = new Dictionary<Structure, double>[n];
            for (int i = 0; i < n; i++)
            {
                perRound[i] = new Dictionary<Structure, double>(entries[i].Panels.Length * 2 + 1);
                foreach (var p in entries[i].Panels)
                    perRound[i][p.Structure] = p.EnergyPerTick;
            }

            var latest = entries[n - 1];
            double appliedTotal = 0.0;
            int applied = 0, vacuum = 0;

            perfT.BeginStage(1);
            foreach (var p in latest.Panels)
            {
                NoteRoomCell(p.RoomCell);
                double e = InterpolatePanelEnergy(p.Structure, n, entries, perRound);
                if (e <= 0.0)
                    continue;

                Atmosphere atmo = GetLocalAtmosphere(p.RoomCell);
                if (atmo == null)
                {
                    vacuum++;
                    continue;
                }

                ApplySolarEnergy(atmo, e);
                appliedTotal += e;
                applied++;
            }

            if (LogCadenceDue())
            {
                ConfigManager.Log.LogInfo(
                    "[TSHA] tick#" + _tick + " INTERP: applied=" + applied
                    + " J=" + appliedTotal.ToString("F0")
                    + " vacuum=" + vacuum
                    + " (from " + n + " round(s))");
            }

            long totalNs = perfT.Finish();
            if (totalNs > 0)
                ConfigManager.Log.LogInfo(
                    "[TSHA] PERF interp tick#" + _tick
                    + ": total=" + Ms(totalNs)
                    + " | interp=" + Ms(perfT.StageNs[0])
                    + " apply=" + Ms(perfT.StageNs[1])
                    + " | applied=" + applied);

            return "tick#" + _tick + " threaded applied=" + applied
                + " J=" + appliedTotal.ToString("F0");
        }

        /// <summary>
        /// Linear least-squares fit through the per-panel energies of the last up to
        /// 3 rounds, evaluated at the current tick. Clamped to [0, 2×max] so a
        /// noisy slope can never spike the heating.
        /// </summary>
        private double InterpolatePanelEnergy(Structure s, int n, ComputeResult[] entries, Dictionary<Structure, double>[] perRound)
        {
            int pts = 0;
            var t = new int[3];
            var e = new double[3];
            for (int i = 0; i < n && pts < 3; i++)
            {
                double v;
                if (perRound[i].TryGetValue(s, out v))
                {
                    t[pts] = entries[i].Tick;
                    e[pts] = v;
                    pts++;
                }
            }
            if (pts == 0)
                return 0.0;
            if (pts == 1)
                return Math.Max(0.0, e[0]);

            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int i = 0; i < pts; i++)
            {
                sx += t[i];
                sy += e[i];
                sxx += (double)t[i] * t[i];
                sxy += (double)t[i] * e[i];
            }
            double denom = pts * sxx - sx * sx;
            double a = Math.Abs(denom) < 1e-9 ? 0.0 : (pts * sxy - sx * sy) / denom;
            double b = (sy - a * sx) / pts;
            double val = a * _tick + b;

            double maxE = 0.0;
            for (int i = 0; i < pts; i++)
                if (e[i] > maxE) maxE = e[i];

            val = Math.Max(0.0, Math.Min(val, Math.Max(maxE * 2.0, 1.0)));
            return val;
        }
    }

    internal static class Grid3Extensions
    {
        public static float SqrMagnitude(this Grid3 g)
        {
            return g.x * g.x + g.y * g.y + g.z * g.z;
        }
    }
}
