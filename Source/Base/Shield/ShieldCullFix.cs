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
    /// VEF's <c>CompProperties_ShieldField</c> (the VFES repulsors and any other
    /// building shield built on it) has exactly the same geometry: the billboard is
    /// drawn from the parent's PostDraw and its radius dwarfs the footprint. The VFES
    /// defs happen to set drawOffscreen themselves, but the flag is added for any
    /// building def carrying the props so another mod's shield field is covered too.
    /// Only buildings are marked: an apparel's comps are drawn through the wearer and
    /// a pawn's through the pawn, so a def-level drawOffscreen there would make the
    /// pawn itself draw off screen instead of fixing anything.
    ///
    /// VEF's bubbles (<c>CompShieldBubble</c>) and hediff shields
    /// (<c>HediffComp_Shield</c>) need no entry here. Both are centred on a pawn and
    /// are drawn as part of that pawn's draw, so the pawn's own cull is the correct
    /// bound — exactly as it is for vanilla shield belts.
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
            // Resolved once: VEF may be absent, in which case only the vanilla
            // interceptor defs are touched.
            System.Type? shieldFieldProps = GenTypes.GetTypeInAnyAssembly(
                "VEF.Apparels.CompProperties_ShieldField");

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
                    if (comps[i] is CompProperties_ProjectileInterceptor
                        || (shieldFieldProps != null
                            && def.category == ThingCategory.Building
                            && shieldFieldProps.IsInstanceOfType(comps[i])))
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
