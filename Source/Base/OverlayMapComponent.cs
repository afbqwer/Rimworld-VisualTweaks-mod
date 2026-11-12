using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// The mod's only map component.
    ///
    /// Every effect used to own a MapComponent of its own. Map.GetComponent does a linear,
    /// type-checked scan over map.components, so five extra entries made every component
    /// lookup on the map — including the ones other mods do every frame — measurably more
    /// expensive, and none of those five had any state worth scribing. The layers are plain
    /// objects now (<see cref="IOverlayLayer"/>) and this component is the single hook the
    /// game has to know about.
    ///
    /// Because vanilla only calls MapComponentDraw for Find.CurrentMap, this is also the one
    /// place that checks "am I the visible map" and resolves the camera and the clock.
    /// </summary>
    public sealed class OverlayMapComponent : MapComponent, IDisposable
    {
        private readonly GasFieldLayer gasField;
        private readonly DeadlifeDustLayer deadlifeDust;
        private readonly ColdMistLayer coldMist;
        private readonly HeatHazeLayer heatHaze;
        private readonly ExplosionRippleLayer explosions;
        private readonly ShieldRippleLayer shieldRipples;
        private readonly VacuumLeakLayer vacuumLeak;

        // Structural events that refresh the port table early. Doors go through the
        // per-cell path, everything else through the throttled full scan. Stored as
        // fields so they can be unsubscribed; subscribed lazily on the first draw
        // because the biome is not reliably set while the component is being
        // constructed during a load.
        private readonly Action structureChangedHandler;
        private readonly Action<Building_Door> doorChangedHandler;
        private readonly Action<IntVec3> terrainOrRoofChangedHandler;
        private bool leakEventsSubscribed;
        private bool cleanedUp;

        /// <summary>
        /// The wind layer lives in the AO compatibility assembly, which the base assembly
        /// cannot reference. Null without AO, so the stack simply has one layer less.
        /// Deliberately not latched: the factory is read until it exists, which costs one
        /// null check per frame and removes any startup-order assumption.
        /// </summary>
        private IOverlayLayer? wind;

        public OverlayMapComponent(Map map) : base(map)
        {
            gasField = new GasFieldLayer();
            deadlifeDust = new DeadlifeDustLayer();
            coldMist = new ColdMistLayer();
            heatHaze = new HeatHazeLayer();
            explosions = new ExplosionRippleLayer();
            shieldRipples = new ShieldRippleLayer();
            vacuumLeak = new VacuumLeakLayer();

            structureChangedHandler = vacuumLeak.MarkStructureDirty;
            doorChangedHandler = OnDoorChanged;
            terrainOrRoofChangedHandler = OnTerrainOrRoofChanged;
        }

        private void OnDoorChanged(Building_Door door)
        {
            // A door toggling changes nothing but that gap's own ExchangeVacuum: the
            // two end rooms keep their vacuum until vanilla's 250-tick exchange. So
            // recompute the gap cells instead of rescanning every room on the map —
            // doors are by far the most frequent structural event, and they were
            // dragging a full scan along with them on every open and close.
            foreach (IntVec3 cell in door.OccupiedRect())
            {
                vacuumLeak.MarkCellDirty(cell);
            }
        }

        private void OnTerrainOrRoofChanged(IntVec3 cell)
        {
            // The cell itself may be a gap cell; more importantly a roof or terrain
            // change can re-partition rooms, which only a full scan can see. Mark both
            // and let the scan's own throttle decide when that runs.
            vacuumLeak.MarkCellDirty(cell);
            vacuumLeak.MarkStructureDirty();
        }

        /// <summary>
        /// Same dirty sources vanilla's VacuumComponent subscribes to, so a door
        /// opening or a wall losing its roof is picked up on the next frame instead of
        /// waiting out the 250-tick interval. Doors resolve to a single gap cell, roof
        /// and terrain changes also mark the whole table. Subscribed once, on the first
        /// draw of a vacuum map, when the biome is guaranteed to exist.
        /// </summary>
        private void EnsureLeakEventSubscriptions()
        {
            if (leakEventsSubscribed || map.Biome == null || !map.Biome.inVacuum)
            {
                return;
            }

            leakEventsSubscribed = true;
            map.events.RegionsRoomsChanged += structureChangedHandler;
            map.events.DoorOpened += doorChangedHandler;
            map.events.DoorClosed += doorChangedHandler;
            map.events.TerrainChanged += terrainOrRoofChangedHandler;
            map.events.RoofChanged += terrainOrRoofChangedHandler;
        }

        /// <summary>
        /// Called by the explosion patch, which reaches the component through
        /// <c>map.GetComponent&lt;OverlayMapComponent&gt;()</c>.
        /// </summary>
        internal void NotifyExplosion(Explosion explosion)
        {
            explosions.Spawn(explosion);
        }

        /// <summary>
        /// Called by the interceptor patch, once per stopped projectile. The shield is
        /// not a map component of its own, so the hit arrives here through the map it
        /// was standing on.
        /// </summary>
        internal void NotifyShieldHit(
            CompProjectileInterceptor interceptor,
            Vector3 hitPos,
            float damage)
        {
            shieldRipples.Spawn(interceptor, hitPos, damage);
        }

        public override void MapComponentDraw()
        {
            // Begin at the very top of the overlay stack: an early return here means no
            // layer draws at all this frame, which is itself a whole-layer flash.
            GasFieldDiagnostics.BeginFrame(Time.realtimeSinceStartup);
            GasFieldDiagnostics.Put("stack.currentMapIsThisMap", Find.CurrentMap == map);
            GasFieldDiagnostics.Put("stack.currentMapUniqueId", Find.CurrentMap?.uniqueID ?? -1);
            GasFieldDiagnostics.Put("stack.thisMapUniqueId", map.uniqueID);

            // Vanilla calls this for the current map only, but MapComponentUtility is
            // public and other mods do call it for arbitrary maps.
            if (Find.CurrentMap != map)
            {
                if (GasFieldDiagnostics.Enabled) GasFieldDiagnostics.Note("overlay stack skipped: not the current map");
                GasFieldDiagnostics.EndFrame(false, "stack-not-current-map");
                return;
            }

            CameraDriver driver = Find.CameraDriver;
            if (driver == null)
            {
                if (GasFieldDiagnostics.Enabled) GasFieldDiagnostics.Note("overlay stack skipped: no CameraDriver");
                GasFieldDiagnostics.EndFrame(false, "stack-no-camera");
                return;
            }

            TickManager tickManager = Find.TickManager;

            OverlayFrame frame = new OverlayFrame
            {
                View = driver.CurrentViewRect,
                CellPixels = driver.CellSizePixels,
                Realtime = Time.realtimeSinceStartup,
                Tick = tickManager != null ? tickManager.TicksGame : 0,
                Paused = tickManager != null && tickManager.Paused,
                Speed = tickManager != null ? tickManager.CurTimeSpeed : TimeSpeed.Normal,
            };

            // Order follows the shader render queues (gas 2990, wind 2991, cold mist and
            // heat haze 3174, vacuum leak 3177, shockwaves 3178), which already decide
            // the blending order. Drawing in that sequence just keeps the code readable
            // against the defs.
            DrawLayer(gasField, in frame);

            // Same queue band (the dust pass uses vanilla's 3000), so this reads in the order
            // the two passes actually blend: field first, sprites on top.
            DrawLayer(deadlifeDust, in frame);

            if (wind == null)
            {
                wind = AOIntegration.CreateWindLayer?.Invoke();
            }

            if (wind != null)
            {
                DrawLayer(wind, in frame);
            }

            DrawLayer(coldMist, in frame);
            DrawLayer(heatHaze, in frame);

            EnsureLeakEventSubscriptions();
            DrawLayer(vacuumLeak, in frame);

            // Shield ripples share the shockwave render queue (3178). Submitting them
            // first lets a blast that lands in the same frame draw over them, which is
            // the correct read: the shield ripple is a surface event, the shockwave is
            // in front of everything.
            DrawLayer(shieldRipples, in frame);
            DrawLayer(explosions, in frame);
        }

        /// <summary>
        /// One failed effect must not cost the frame its other effects. Layers already catch
        /// failures of their own renderer and switch themselves off; this outer guard only
        /// picks up the unexpected, and ErrorOnce keeps a persistent one from flooding the log.
        /// </summary>
        private void DrawLayer(IOverlayLayer layer, in OverlayFrame frame)
        {
            if (layer == null)
            {
                return;
            }

            try
            {
                layer.Draw(map, in frame);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce(
                    "[Visual Tweaks] Overlay layer " + layer.GetType().Name + " failed: " + ex,
                    layer.GetType().GetHashCode());
            }
        }

        /// <summary>
        /// The one teardown, reachable from both paths the game offers.
        ///
        /// MapRemoved fires when a map is abandoned or a pocket map is destroyed.
        /// Dispose fires when the Map itself is disposed — which is what happens to
        /// every map on save load, on quit-to-main-menu and on a new game. Those three
        /// never call MapRemoved: GameDataSaveLoader.LoadGame and GenScene.GoToMainMenu
        /// both call Current.Game.Dispose() and then drop the whole Game, so without
        /// this every renderer's GPU buffers from the previous game would be left to
        /// the finalizer, and the static breach table would keep rows keyed to a map
        /// that no longer exists.
        /// </summary>
        private void Cleanup()
        {
            if (cleanedUp)
            {
                return;
            }

            cleanedUp = true;

            if (leakEventsSubscribed)
            {
                map.events.RegionsRoomsChanged -= structureChangedHandler;
                map.events.DoorOpened -= doorChangedHandler;
                map.events.DoorClosed -= doorChangedHandler;
                map.events.TerrainChanged -= terrainOrRoofChangedHandler;
                map.events.RoofChanged -= terrainOrRoofChangedHandler;
                leakEventsSubscribed = false;
            }

            // Per map, so disposing one map cannot delete another map's live visuals.
            VacuumBreaches.ClearFor(map.uniqueID);

            gasField.Dispose();
            deadlifeDust.Dispose();
            coldMist.Dispose();
            heatHaze.Dispose();
            shieldRipples.Dispose();
            explosions.Dispose();
            vacuumLeak.Dispose();
            wind?.Dispose();
            wind = null;
        }

        public override void MapRemoved()
        {
            Cleanup();
        }

        public void Dispose()
        {
            Cleanup();
        }
    }
}
