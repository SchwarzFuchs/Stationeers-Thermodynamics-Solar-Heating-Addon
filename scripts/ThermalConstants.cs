using System;
using System.Collections.Generic;

namespace ThermodynamicsSolarHeatingAddon.scripts
{
    /// <summary>
    /// Material properties for the new universal solar-heating formula.
    /// 
    /// Single wall:  E = s * cos(a) * f * (R + C * A)
    /// Double wall:  E = s * cos(a) * f * (R_out * (R_in + A_in * C_in) + A_out * Min(C_out, C_in / k))
    /// 
    /// Where:
    ///   s = face area (4 m²), a = sun angle (radians), f = solar flux (W/m²)
    ///   R = transmissivity (τ), C = conductivity (λ), A = absorptivity (α)
    ///   k = conductor length penalty = 1.2
    ///   _out = outer layer (sun side), _in = inner layer (room side)
    /// </summary>
    public static class ThermalConstants
    {
        public enum Material
        {
            Default,
            Iron,
            Steel,
            Composite,
            Insulation,
            Glass,
            ForceField
        }

        // Material properties: (transmissivity R, conductivity C, absorptivity A)
        private static readonly Dictionary<Material, (float R, float C, float A)> materialProps =
            new Dictionary<Material, (float, float, float)>
        {
            {Material.Default,     (0f,    1f,    0.3f)},
            {Material.Iron,        (0f,    1f,    0.3f)},
            {Material.Steel,       (0f,    0.8f,  0.15f)},
            {Material.Composite,   (0f,    0.4f,  0.25f)},
            {Material.Insulation,  (0f,    0.05f, 0.5f)},
            {Material.Glass,       (0.85f, 0.15f, 0.05f)},
            {Material.ForceField,  (0.01f, 0.001f, 0.001f)},
        };

        // Prefab -> material mapping (unchanged)
        private static readonly Dictionary<string, Material> prefabToMaterial =
            new Dictionary<string, Material>
        {
            // Frames
            {"StructureFrameIron", Material.Iron},
            {"StructureFrame", Material.Steel},

            // Composite walls
            {"StructureCompositeWall", Material.Composite},
            {"StructureCompositeWall02", Material.Composite},
            {"StructureCompositeWall03", Material.Composite},
            {"StructureCompositeWall04", Material.Composite},
            {"StructureCompositeWindow", Material.Glass},

            // Window shutters
            {"StructureCompositeWindowShutter", Material.Glass},
            {"StructureCompositeWindowShutterConnector", Material.Composite},
            {"StructureCompositeWindowShutterController", Material.Composite},

            // Arch walls
            {"StructureWallArch", Material.Steel},
            {"StructureWallArchArrow", Material.Steel},
            {"StructureWallArchPlating", Material.Steel},
            {"StructureWallArchTwoTone", Material.Steel},

            // Flat walls
            {"StructureWallFlat", Material.Steel},
            {"StructureWallLargePanel", Material.Steel},
            {"StructureWallLargePanelArrow", Material.Steel},
            {"StructureWallPlating", Material.Steel},
            {"StructureWallSmallPanelsAndHatch", Material.Steel},
            {"StructureWallSmallPanelsArrow", Material.Steel},
            {"StructureWallSmallPanelsMonoChrome", Material.Steel},
            {"StructureWallSmallPanelsTwoTone", Material.Steel},
            {"StructureWallSmallPanelsOpen", Material.Steel},

            // Geometry walls
            {"StructureWallGeometryStreight", Material.Steel},
            {"StructureWallGeometryCorner", Material.Steel},
            {"StructureWallGeometryTMirrored", Material.Steel},
            {"StructureWallGeometryT", Material.Steel},

            // Iron walls
            {"StructureWallIron", Material.Iron},
            {"StructureWallIron02", Material.Iron},
            {"StructureWallIron03", Material.Iron},
            {"StructureWallIron04", Material.Iron},
            {"StructureCompositeWindowIron", Material.Glass},

            // Padded walls (insulation)
            {"StructureWallPadding", Material.Insulation},
            {"StructureWallPaddedArch", Material.Insulation},
            {"StructureWallPaddingLightFitting", Material.Insulation},
            {"StructureWallPaddedArchLightsFittings", Material.Insulation},
            {"StructureWallPaddedArchLightFittingTop", Material.Insulation},
            {"StructureWallPaddingArchVent", Material.Insulation},
            {"StructureWallPaddedNoBorder", Material.Insulation},
            {"StructureWallPaddingThin", Material.Insulation},
            {"StructureWallPaddedThinNoBorder", Material.Insulation},
            {"StructureWallPaddedWindow", Material.Glass},
            {"StructureWallPaddedWindowThin", Material.Glass},

            // Reinforced walls
            {"StructureReinforcedWall", Material.Steel},
            {"StructureReinforcedCompositeWindowSteel", Material.Glass},
            {"StructureReinforcedCompositeWindow", Material.Glass},
            {"StructureReinforcedWallPaddedWindow", Material.Glass},
            {"StructureReinforcedWallPaddedWindowThin", Material.Glass},

            // Doors
            {"StructureBlastDoor", Material.Steel},
            {"StructureGlassDoor", Material.Glass},
            {"StructureCompositeDoor", Material.Composite},
            {"StructureManualHatch", Material.Iron},
            {"StructureInteriorDoorGlass", Material.Glass},
            {"StructureInteriorDoorPadded", Material.Insulation},
            {"StructureInteriorDoorPaddedThin", Material.Insulation},
            {"StructureInteriorDoorTriangle", Material.Composite},
            {"StructureRobotArmDoor", Material.Steel},

            // Force field
            {"StructureForceFieldDoor", Material.ForceField},
            {"StructureForceFieldDoor2x1", Material.ForceField},
            {"StructureForceFieldDoor4x2", Material.ForceField},
        };

        // Conductor length penalty for double walls
        public const float ConductorLengthPenalty = 1.2f;

        // Sun-facing face area of one wall panel, in m².
        // A grid cell is 2x2 (world units == meters), so ONE exposed face is
        // 2 m * 2 m = 4 m².
        public const float FaceArea = 4f;

        public static Material GetMaterial(string prefabName)
        {
            if (prefabName != null && prefabToMaterial.TryGetValue(prefabName, out var m))
                return m;
            return Material.Default;
        }

        public static bool IsSolarPanel(string prefabName)
        {
            return prefabName != null && prefabToMaterial.ContainsKey(prefabName);
        }

        /// <summary>
                        /// Get material properties: (transmissivity R, conductivity C, absorptivity A)
                        /// </summary>
                        public static (float R, float C, float A) GetProps(Material m)
                        {
                            var (R, C, A) = materialProps.TryGetValue(m, out var v) ? v : (0f, 1f, 0f);
                            if (m == Material.Glass)
                            {
                                // Apply glass transmissivity multiplier from config (default 1.0 = vanilla R=0.85)
                                float glassMult = ConfigManager.Entry_GlassTransmissionMultiplier != null 
                                    ? ConfigManager.Entry_GlassTransmissionMultiplier.Value 
                                    : 1.0f;
                                R = 0.85f * glassMult;
                            }
                            return (R, C, A);
                        }

        /// <summary>
        /// Compute solar heating factor for a single layer.
        /// Formula: R + C * A
        /// </summary>
        public static float SingleWallFactor(Material m)
        {
            var (R, C, A) = GetProps(m);
            return R + C * A;
        }

        /// <summary>
        /// Compute solar heating factor for a double wall (outer + inner).
        /// Formula: R_out * (R_in + A_in * C_in) + A_out * Min(C_out, C_in / k)
        /// </summary>
        public static float DoubleWallFactor(Material outer, Material inner)
        {
            var (R_out, C_out, A_out) = GetProps(outer);
            var (R_in,  C_in,  A_in)  = GetProps(inner);
            float k = ConductorLengthPenalty;
            return R_out * (R_in + A_in * C_in) + A_out * Math.Min(C_out, C_in / k);
        }

        /// <summary>
        /// Compute solar heating factor for a triple wall (outer + mid + inner).
        /// Extends the double-wall formula recursively.
        /// </summary>
        public static float TripleWallFactor(Material outer, Material mid, Material inner)
        {
            // Treat (outer+mid) as combined outer for inner layer
            var (R_om, C_om, A_om) = CombineTwoLayers(outer, mid);
            var (R_in, C_in, A_in) = GetProps(inner);
            float k = ConductorLengthPenalty;
            return R_om * (R_in + A_in * C_in) + A_om * Math.Min(C_om, C_in / k);
        }

        /// <summary>
        /// Combine two layers into equivalent single layer for recursive formula.
        /// </summary>
        private static (float R, float C, float A) CombineTwoLayers(Material outer, Material inner)
        {
            var (R_out, C_out, A_out) = GetProps(outer);
            var (R_in,  C_in,  A_in)  = GetProps(inner);
            float k = ConductorLengthPenalty;
            
            // Equivalent properties of the combined layer (using the new formula)
            float R_comb = R_out * (R_in + A_in * C_in) + A_out * Math.Min(C_out, C_in / k);
            float C_comb = Math.Min(C_out, C_in); // bottleneck conductivity
            float A_comb = A_out + R_out * A_in;  // absorptivity cascades through transmissivity
            return (R_comb, C_comb, A_comb);
        }

        /// <summary>
        /// Generic N-layer factor computation.
        /// </summary>
        public static float MultiLayerFactor(params Material[] layers)
        {
            if (layers.Length == 0) return 0f;
            if (layers.Length == 1) return SingleWallFactor(layers[0]);
            if (layers.Length == 2) return DoubleWallFactor(layers[0], layers[1]);
            if (layers.Length == 3) return TripleWallFactor(layers[0], layers[1], layers[2]);
            
            // For 4+ layers, fold from outside in
            var (R_acc, C_acc, A_acc) = GetProps(layers[0]);
            for (int i = 1; i < layers.Length; i++)
            {
                var (R_in, C_in, A_in) = GetProps(layers[i]);
                float k = ConductorLengthPenalty;
                // New formula: R_out * (R_in + A_in * C_in) + A_out * Min(C_out, C_in / k)
                float R_new = R_acc * (R_in + A_in * C_in) + A_acc * Math.Min(C_acc, C_in / k);
                float C_new = Math.Min(C_acc, C_in);
                float A_new = A_acc + R_acc * A_in;
                (R_acc, C_acc, A_acc) = (R_new, C_new, A_new);
            }
            return R_acc; // Final equivalent factor
        }
    }
}
