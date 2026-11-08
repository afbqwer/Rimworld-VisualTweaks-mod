using System;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// The per-frame inputs every overlay layer needs. Captured once by
    /// <see cref="OverlayMapComponent"/> and handed to all layers, so the camera and the
    /// tick manager are resolved once per frame instead of once per effect.
    /// </summary>
    public struct OverlayFrame
    {
        /// <summary>
        /// Cells currently on screen. Vanilla caches this per frame, so reading it is
        /// close to free, but it is still resolved once and shared.
        /// </summary>
        public CellRect View;

        /// <summary>Pixels per cell at the current zoom. The renderers size quads with it.</summary>
        public float CellPixels;

        /// <summary>
        /// Unscaled seconds since startup. Animation phase uses this, never game time,
        /// so an effect can either freeze or keep moving while the game is paused.
        /// </summary>
        public float Realtime;

        /// <summary>Current game tick, 0 while no tick manager exists.</summary>
        public int Tick;

        /// <summary>True while the game is paused.</summary>
        public bool Paused;

        /// <summary>
        /// Current game speed tier. An effect that follows the colony should scale its
        /// animation by this rather than by real time, otherwise it keeps churning at 1x
        /// while the world runs at 15x. The tier, not
        /// <c>TickManager.TickRateMultiplier</c>, is what gets handed out: the multiplier
        /// jumps by 3x/6x/15x steps and varies with "nothing happening" and the ultra
        /// speed boost, which is far too coarse for a visual to key off directly.
        /// <see cref="Verse.TimeSpeed.Normal"/> when there is no tick manager.
        /// </summary>
        public TimeSpeed Speed;
    }

    /// <summary>
    /// One draw pass of the overlay stack. A layer owns its renderer, its own on/off gate
    /// and its own failure handling, so a broken shader disables that effect alone instead
    /// of taking the others down with it.
    ///
    /// Layers are plain objects, not <see cref="MapComponent"/>s: the mod registers exactly
    /// one component per map and every layer is created by it. Instances are per map.
    /// </summary>
    public interface IOverlayLayer : IDisposable
    {
        void Draw(Map map, in OverlayFrame frame);
    }
}
