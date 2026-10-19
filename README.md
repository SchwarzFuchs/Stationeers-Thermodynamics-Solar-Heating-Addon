# Thermodynamics Solar Heating Addon

Sun-facing structural panels heat the air of the room they border — computed **per wall** from real physics. A companion addon for **[Structure Thermodynamics](https://github.com/trainchoo/StructureThermodynamics)** mod.

| | |
|---|---|
| **Author** | SchwarzFuchs |
| **Version** | 1.0 |
| **Requires** | BepInEx + [StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad) |
| **Game** | Stationeers (Build 24703804 tested) |

## What it does

In vanilla Stationeers a sealed room neither gains nor loses heat to the sun or the outside. This addon adds the missing **solar input**:

- Each sun-facing structural panel heats the room air behind it.
- Heating is computed **per wall panel**: `E = irradiance × FaceArea × buildState × factor × solarHeatingMultiplier × cosAngle`.
- A panel only heats when the sun actually reaches it (**occlusion-aware ray** to the sun).

## Companion mod

This addon is designed to work alongside **Structure Thermodynamics**:

| Mod | Contribution | Sign |
|---|---|---|
| Structure Thermodynamics | Heat **loss** through walls (conduction / convection / radiation) | − |
| **This addon** | Solar **heating** of the room air | + |

Used together they form a realistic thermal balance — a room settles at an equilibrium temperature where solar gain is offset by the heat lost through the structure. This addon **also works standalone** (solar gain only, no heat loss).

## Material thermal properties

Transmissivity **R** / conductivity **C** / absorptivity **A**:

| Material | R (transmissivity) | C (conductivity) | A (absorptivity) | Notes |
|---|---|---|---|---|
| Iron | 0 | 1.0 | 0.30 | high conductivity, poor insulation |
| Steel | 0 | 0.8 | 0.15 | reflective |
| Composite | 0 | 0.4 | 0.25 | moderate |
| **Glass** | **0.85** | **0.15** | **0.05** | transmissive (greenhouse); R scaled by `glassTransmissionMultiplier` (default 1.0) |
| Insulation | 0 | 0.05 | 0.50 | excellent insulation (low C) |
| ForceField | 0.01 | 0.001 | 0.001 | nearly sealed |
| Default | 0 | 1.0 | 0.30 | fallback (same as Iron) |

Example of how much heat (joules) added per tick to cell with 1.0 multiplier with 1250 w/m^2 sun flow at 0 degrees angle.
| | Inner layer |  | | | |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Outer layer** | **Material** | **Iron** | **Steel** | **Glass** | **Insulation** |
| | **Iron** | 1250 | 1000 | 187.5 | 62.5 |
| | **Steel** | 600 | 500 | 93.75 | 31.25 |
| | **Glass** | 1350 | 585 | 3738.75 | 127.0833 |
| | **Insulation** | 125 | 125 | 125 | 104.1667 |
| | **Empty** | 1500 | 600 | 4325 | 125 |

## Configuration

All settings are exposed in the StationeersLaunchPad in-game menu and adjustable at runtime (no restart). File: `BepInEx/config/ThermodynamicsSolarHeatingAddon.cfg`.

| Section | Key | Default | Description |
|---|---|---|---|
| General | `solarHeatingMultiplier` | `0.25` | Global scale on the heating energy. `1` = realistic (pure-physics) level |
| General | `glassTransmissionMultiplier` | `1.0` | Multiplier for glass transmissivity (R). `1` = vanilla glass (R=0.85); 0.5 = half, 2 = double |
| Performance | `threadedInterpolation` | `off` | Experimental function. Heavy compute every 10 ticks on a background thread + interpolation; lowers main-thread load |
| Debug | `debugLog` | `off` | Verbose per-tick `[TSHA]` diagnostics in the BepInEx log |
| Debug | `debugLogInChat` | `off` | Mirror `[TSHA]` diagnostics into the in-game console |
| Debug | `debugLogInterval` | `10` | Debug-line cadence in ticks (1 = every tick, range 1–100) |
| Debug | `performanceTrace` | `off` | Per-stage pipeline timing in the BepInEx log |


## Diagnostics

Diagnostics are tagged `[TSHA]` in `BepInEx/LogOutput.log`:

