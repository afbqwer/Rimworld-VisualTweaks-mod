using System.Collections.Generic;
using RimWorld;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// A shield dome is drawn from its parent building's <c>Comps_PostDraw()</c>.
    /// Vanilla's <c>DynamicDrawManager.CullJob</c> decides whether that runs at all,
    /// using <c>coarseBounds = thing.OccupiedDrawRect()</c> — the *building's* 1x1
    /// footprint. Move the generator off screen and PostDraw never executes, so the
    /// dome disappears even while most of the area it actually blocks is still on
    /// screen. The larger the radius, the more common this is: on a 25-cell shield
    /// the normal case is a visible rim with an off-screen centre.
    ///
    /// Vanilla already solves this for one specific building: BulletShieldGenerator
    /// (the 24.9-cell gravship shield) carries &lt;drawOffscreen&gt;true&lt;/drawOffscreen&gt;
    /// in its def. Royalty's BroadshieldProjector and mod-added interceptors do not.
    /// This sets the same flag for every def with a projectile interceptor, which is
    /// the engine's own mechanism rather than a new culling rule.
    ///
    /// Deliberately done once at startup from the def database, not lazily on first
    /// draw: a shield that is being culled never reaches a draw method, so a lazy fix
    /// could not fix the very case it exists for. There is no per-frame cost and no
    /// Harmony patch. All the other vanilla gates (fog, spawned, dynamic draw
    /// registration, despawn) stay exactly as they were — drawOffscreen is read in
    /// exactly one place, DynamicDrawManager.ComputeCulledThings.
    ///
    /// The cost: the generator itself is submitted for drawing even when far off
    /// screen, which is one extra quad the GPU clips away. Interceptor buildings are
    /// counted in ones and twos per map, so this is cheaper than a per-frame
    /// OccupiedDrawRect postfix (which would also change CameraDriver.InViewOf and
    /// therefore tick rates, and enlarge section mesh bounds).
    /// </summary>
    internal static class ShieldCullFix
    {
        /// <summary>Returns the number of defs that needed the flag.</summary>
        internal static int Apply()
        {
            int changed = 0;

            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                if (def.drawOffscreen)
                {
                    continue;
                }

                List<CompProperties> comps = def.comps;
                if (comps == null)
                {
                    continue;
                }

                for (int i = 0; i < comps.Count; i++)
                {
                    // `is` so modded subclasses (e.g. a custom gravship shield's
                    // props class) are picked up too. Only the props matter here;
                    // compClass does not change the drawn radius.
                    if (comps[i] is CompProperties_ProjectileInterceptor)
                    {
                        def.drawOffscreen = true;
                        changed++;
                        break;
                    }
                }
            }

            return changed;
        }
    }
}
