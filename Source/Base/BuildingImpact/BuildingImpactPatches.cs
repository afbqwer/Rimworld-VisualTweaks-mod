using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Debris + dust when damage lands on a building (walls, doors, furniture,
    /// production buildings, mineable rock — anything derived from Building).
    ///
    /// Thing.TakeDamage is the single funnel every damage source flows through
    /// for non-pawn things: Bullet.Impact, melee verbs and GenExplosion all end
    /// there. Two hooks on it, plus one on Bullet.Impact:
    ///   Bullet.Impact prefix — captures the projectile's ExactPosition and its
    ///       target. TakeDamage itself only knows the victim, not where the shot
    ///       landed; the exact impact point makes the debris sit on the struck
    ///       face of a multi-cell building instead of its centre. Consumed once
    ///       by the TakeDamage postfix (a later melee hit on the same wall must
    ///       not reuse a stale bullet position).
    ///   TakeDamage prefix — snapshots map / occupied rect / centre / material
    ///       colour while the building is still alive. A killing blow destroys
    ///       the thing inside Apply, after which Map and Spawned are gone, so
    ///       everything positional has to be captured up front.
    ///   TakeDamage postfix — the spawn point. __result.totalDamageDealt is the
    ///       real damage after absorption (PreApplyDamage can zero it), and
    ///       Destroyed tells a finishing blow apart from a chip hit: destruction
    ///       throws a bigger burst with more smoke.
    ///
    /// Direction follows the same contract as the metal sparks: the shader fans
    /// debris toward -travelDir. Ranged hits spray back toward the shooter
    /// (spall reflects off the face), explosions spray outward away from the
    /// blast (dinfo.Angle points centre -> target, so travelDir is its negation),
    /// melee falls back to the attacker -> target vector. Angle is only trusted
    /// for isRanged / isExplosive defs; other damage randomises it.
    ///
    /// The burst is anchored on the struck FACE: the closest footprint point to
    /// the attacker, pushed exactly onto that face plane, plus the face's
    /// outward normal. The shader constrains every chip to the outside of that
    /// plane and clips fragments crossing it, because a wall is solid and any
    /// debris drawn on the far side would read as passing through it.
    ///
    /// The prefix runs for EVERY Thing.TakeDamage in the game, so the non-building
    /// path is a single type check before anything else.
    /// </summary>
    internal static class BuildingImpactPatches
    {
        // Snapshot taken while the building is still alive; handed from the
        // TakeDamage prefix to its postfix through Harmony's ref __state.
        private sealed class ImpactEvent
        {
            public Map Map = null!;
            public CellRect Rect;
            public Vector3 Center;
            public Color Color;
            public Thing? Instigator;
            public DamageDef? Def;
            public float Angle;
        }

        // One-slot bridge from Bullet.Impact into the TakeDamage it calls.
        // Main-thread only; consumed (cleared) by the first matching postfix.
        private static Thing? s_bulletTarget;
        private static Vector3 s_bulletPos;

        // Backward bias per damage source. Ranged spall fans back toward the
        // shooter but keeps a wide lateral spread; an explosion throws debris
        // strongly outward; melee is a loose fan away from the attacker.
        private const float BiasRanged = 0.55f;
        private const float BiasExplosive = 0.75f;
        private const float BiasMelee = 0.35f;

        internal static void Apply(Harmony harmony)
        {
            MethodInfo takeDamage = AccessTools.Method(typeof(Thing), nameof(Thing.TakeDamage));
            MethodInfo takePrefix = AccessTools.Method(
                typeof(BuildingImpactPatches), nameof(TakeDamagePrefix));
            MethodInfo takePostfix = AccessTools.Method(
                typeof(BuildingImpactPatches), nameof(TakeDamagePostfix));

            MethodInfo bulletImpact = AccessTools.Method(typeof(Bullet), "Impact");
            MethodInfo bulletPrefix = AccessTools.Method(
                typeof(BuildingImpactPatches), nameof(BulletImpactPrefix));

            if (takeDamage == null || takePrefix == null || takePostfix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch Thing.TakeDamage for building impact debris.");
                return;
            }

            try
            {
                harmony.Patch(
                    takeDamage,
                    prefix: new HarmonyMethod(takePrefix),
                    postfix: new HarmonyMethod(takePostfix));

                // Optional precision upgrade: without it (e.g. a mod replaces
                // Bullet.Impact entirely) the fallback direction logic still works.
                if (bulletImpact != null && bulletPrefix != null)
                {
                    harmony.Patch(bulletImpact, prefix: new HarmonyMethod(bulletPrefix));
                }

                VisualTweaksLog.Info(
                    "Patched Thing.TakeDamage + Bullet.Impact for building impact debris.");
            }
            catch (Exception ex)
            {
                Log.Error("[Visual Tweaks] Failed to patch building impact debris: " + ex);
            }
        }

        private static void BulletImpactPrefix(Projectile __instance, Thing hitThing)
        {
            if (hitThing is Building && BuildingImpactBridge.Active)
            {
                s_bulletTarget = hitThing;
                s_bulletPos = __instance.ExactPosition;
            }
        }

        private static void TakeDamagePrefix(Thing __instance, DamageInfo dinfo, ref ImpactEvent? __state)
        {
            // Hot path: every item, pawn and filth in the game passes through
            // here. One type check before anything else.
            if (__instance is not Building)
            {
                return;
            }

            if (!BuildingImpactBridge.Active || !__instance.Spawned || __instance.Map == null)
            {
                return;
            }

            __state = new ImpactEvent
            {
                Map = __instance.Map,
                Rect = __instance.OccupiedRect(),
                Center = __instance.TrueCenter(),
                // DrawColor resolves the stuff colour (granite grey, wood brown,
                // steel blue-grey...) and falls back to graphicData.color, so
                // debris and dust are tinted by what the wall is made of.
                Color = __instance.DrawColor,
                Instigator = dinfo.Instigator,
                Def = dinfo.Def,
                Angle = dinfo.Angle,
            };
        }

        private static void TakeDamagePostfix(
            Thing __instance,
            ImpactEvent? __state,
            DamageWorker.DamageResult __result)
        {
            if (__state == null)
            {
                return;
            }

            // Consume the bullet bridge even when the hit is filtered out below,
            // so a later melee hit on the same building cannot reuse it.
            bool hasBulletPos = s_bulletTarget == __instance;
            Vector3 bulletPos = s_bulletPos;
            if (hasBulletPos)
            {
                s_bulletTarget = null;
            }

            bool destroyed = __instance.Destroyed;
            float dealt = __result.totalDamageDealt;
            if (!destroyed && dealt < VisualTweaksSettings.buildingImpactMinDamage)
            {
                return;
            }

            ResolveDirection(__state, hasBulletPos, bulletPos,
                out Vector3 sourcePos, out Vector3 travelDir, out float bias);

            // The quad is centred on the struck FACE, not in the building, and
            // carries that face's outward normal. The shader uses the normal to
            // keep debris on the outside and to clip anything that would sweep
            // across the wall — the wall is solid, so a chip drawn over it would
            // read as passing through.
            Vector3 impactPos = ClosestPointOnRect(__state.Rect, sourcePos, out Vector3 faceNormal);
            impactPos.y = __state.Center.y;

            OverlayMapComponent component = __state.Map.GetComponent<OverlayMapComponent>();
            component?.NotifyBuildingImpact(
                impactPos, faceNormal, travelDir, bias, dealt, __state.Color, destroyed);
        }

        /// <summary>
        /// Resolves the spray direction and a virtual source position (used only
        /// to pick the struck face when no exact bullet position exists).
        /// Same angle conventions as MechSparkPatches.ResolveTravelDir.
        /// </summary>
        private static void ResolveDirection(
            ImpactEvent ev,
            bool hasBulletPos,
            Vector3 bulletPos,
            out Vector3 sourcePos,
            out Vector3 travelDir,
            out float bias)
        {
            DamageDef? def = ev.Def;
            if (def != null && def.isExplosive)
            {
                // dinfo.Angle points blast centre -> target; debris flies outward.
                Vector3 outward = Quaternion.Euler(0f, ev.Angle, 0f) * Vector3.forward;
                travelDir = -outward;
                sourcePos = ev.Center - outward * 4f;
                bias = BiasExplosive;
                return;
            }

            if (def != null && def.isRanged)
            {
                travelDir = Quaternion.Euler(0f, ev.Angle, 0f) * Vector3.forward;
                sourcePos = hasBulletPos
                    ? bulletPos - travelDir * 4f
                    : InstigatorPosOr(ev, ev.Center - travelDir * 4f);
                bias = BiasRanged;
                return;
            }

            // Melee / other: best-effort attacker -> target.
            Thing? instigator = ev.Instigator;
            if (instigator != null && instigator.Spawned && instigator.Map == ev.Map)
            {
                Vector3 flat = (ev.Center - instigator.TrueCenter()).Yto0();
                if (flat.sqrMagnitude > 0.01f)
                {
                    travelDir = flat.normalized;
                    sourcePos = instigator.TrueCenter();
                    bias = BiasMelee;
                    return;
                }
            }

            travelDir = Vector3.zero;
            sourcePos = ev.Center;
            bias = 0f;
        }

        private static Vector3 InstigatorPosOr(ImpactEvent ev, Vector3 fallback)
        {
            Thing? instigator = ev.Instigator;
            return instigator != null && instigator.Spawned && instigator.Map == ev.Map
                ? instigator.TrueCenter()
                : fallback;
        }

        /// <summary>
        /// Closest point of the building's footprint to the source, in world
        /// coordinates, plus the outward normal of the face that point lies on.
        /// For a 1x1 wall this lands on the struck edge; for a big building it
        /// keeps the debris on the face the attack came from. Normals are
        /// snapped to the dominant axis (buildings are axis-aligned), which is
        /// all the shader's half-space clip needs.
        /// </summary>
        private static Vector3 ClosestPointOnRect(
            CellRect rect,
            Vector3 source,
            out Vector3 normal)
        {
            float minX = rect.minX;
            float maxX = rect.maxX + 1f;
            float minZ = rect.minZ;
            float maxZ = rect.maxZ + 1f;

            float ox = source.x < minX ? minX - source.x
                : source.x > maxX ? source.x - maxX
                : 0f;
            float oz = source.z < minZ ? minZ - source.z
                : source.z > maxZ ? source.z - maxZ
                : 0f;

            if (ox > 0f || oz > 0f)
            {
                if (ox >= oz)
                {
                    normal = new Vector3(source.x < minX ? -1f : 1f, 0f, 0f);
                }
                else
                {
                    normal = new Vector3(0f, 0f, source.z < minZ ? -1f : 1f);
                }
            }
            else
            {
                // Source inside the footprint (grazing shot, source on the wall
                // itself): face the dominant axis away from the centre.
                float dx = source.x - (minX + maxX) * 0.5f;
                float dz = source.z - (minZ + maxZ) * 0.5f;
                normal = Mathf.Abs(dx) >= Mathf.Abs(dz)
                    ? new Vector3(dx < 0f ? -1f : 1f, 0f, 0f)
                    : new Vector3(0f, 0f, dz < 0f ? -1f : 1f);
            }

            float x = Mathf.Clamp(source.x, minX, maxX);
            float z = Mathf.Clamp(source.z, minZ, maxZ);

            // Push the point onto the face plane so the quad centre sits exactly
            // on the surface: the shader clips at dot(local, normal) < 0.
            if (normal.x > 0.5f) x = maxX;
            else if (normal.x < -0.5f) x = minX;
            else if (normal.z > 0.5f) z = maxZ;
            else z = minZ;

            return new Vector3(x, source.y, z);
        }
    }
}