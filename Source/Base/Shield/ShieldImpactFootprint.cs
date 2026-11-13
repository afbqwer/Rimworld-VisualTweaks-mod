using UnityEngine;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Impact sizes in cells, for the two things that draw one.
    ///
    /// These are two *features*, not one: <see cref="ShieldRippleLayer"/> is the optional
    /// refraction ring and lives in its own settings section, while the interception
    /// flare belongs to the shield redraw and must keep working with ripples switched
    /// off. So the tuning is separate — the ripple has its own damage scale slider, the
    /// flare has its own size slider, and neither reads the other's.
    ///
    /// What *is* shared is the shape of the curve, via <see cref="Curve"/>: sub-linear
    /// damage response, an absolute ceiling, and a share-of-shield ceiling. That is the
    /// part that has to stay identical, because it is what makes a hit look like one
    /// event at one scale instead of two unrelated effects firing at the same time.
    /// Copying this math into each renderer is how they would drift apart.
    ///
    /// The two tunings differ because they answer different questions. A ring is a
    /// surface disturbance and reads fine at ~1 cell across. A wedge has to *point*, and
    /// a directional cue needs angular extent to be legible — the first attempt at
    /// unifying them gave the flare the ring's 1.1 cells, which is a 12-degree sliver
    /// that disappears into the dome's own rim.
    /// </summary>
    internal static class ShieldImpactFootprint
    {
        /// <summary>Damage that maps to a ratio of exactly 1.0.</summary>
        internal const float ReferenceDamage = 12f;

        internal const float SizeExponent = 0.6f;

        // Ripple tuning. 12 damage lands at 1.1 cells; a blast tops out at 2.2 and at
        // 35% of the shield radius, whichever comes first.
        internal const float RippleBaseCells = 1.1f;
        internal const float RippleMinCells = 0.35f;
        internal const float RippleMaxCells = 2.2f;
        internal const float RippleShare = 0.35f;

        // Flare tuning. Twice the ring, because a wedge needs to be read at a glance
        // from across the screen. 2.2 cells is 24 degrees on a 4.9-cell shield — the
        // same arc the earlier fixed-angle version drew — and 5 degrees on the 24.9-cell
        // gravship generator, which is the case the fixed angle got wrong.
        internal const float FlareBaseCells = 2.2f;
        internal const float FlareMinCells = 1.5f;
        internal const float FlareMaxCells = 3.5f;
        internal const float FlareShare = 0.45f;

        /// <summary>
        /// Ripple ring radius. Carries the ripple's own damage scale, because that
        /// slider is a ripple setting and this is the ripple's number.
        /// </summary>
        internal static float RippleCells(float damage, float shieldRadius)
        {
            return Curve(
                damage,
                shieldRadius,
                VisualTweaksSettings.shieldRippleDamageScale,
                RippleBaseCells,
                RippleMinCells,
                RippleMaxCells,
                RippleShare);
        }

        /// <summary>
        /// Damage as a multiple of the reference, already carrying the ripple's damage
        /// scale. Exposed so the ripple's strength and duration responses cannot end up
        /// measuring a different quantity than its size.
        /// </summary>
        internal static float RippleDamageRatio(float damage)
        {
            return Mathf.Max(damage, 2f)
                / ReferenceDamage
                * VisualTweaksSettings.shieldRippleDamageScale;
        }

        /// <summary>
        /// Flare half-width. Scale 1.0 regardless of the ripple slider: the flare is part
        /// of the shield redraw, so its size must not change when someone tunes ripples.
        /// The player's own size slider is applied afterwards, then clamped again so no
        /// setting can produce a wedge that swallows the dome.
        ///
        /// Damage widens the flare only up to the clamp: on a 4.9-cell shield the 0.45
        /// share ceiling lands at 2.2 cells, which is already the reference width, so a
        /// charge lance and a pistol draw the same wedge there. That is deliberate — a
        /// wider wedge on a small dome stops pointing and starts covering. On a 25-cell
        /// shield the ceiling is loose enough that damage does show, 5.0 to 8.0 degrees.
        /// </summary>
        internal static float FlareCells(float damage, float shieldRadius)
        {
            float cells = Curve(
                damage,
                shieldRadius,
                1f,
                FlareBaseCells,
                FlareMinCells,
                FlareMaxCells,
                FlareShare);

            float scale = Mathf.Clamp(VisualTweaksSettings.shieldFlareSizeScale, 0.4f, 2.5f);

            return Mathf.Clamp(
                cells * scale,
                FlareMinCells * 0.5f,
                shieldRadius * 0.45f);
        }

        private static float Curve(
            float damage,
            float shieldRadius,
            float damageScale,
            float baseCells,
            float minCells,
            float maxCells,
            float share)
        {
            // Floor of 2 damage keeps a zero-damage projectile (EMP carries none of its
            // own) from collapsing the response to nothing.
            float ratio = Mathf.Max(damage, 2f) / ReferenceDamage * damageScale;

            // The share cap is floored at minCells because Mathf.Clamp with min > max
            // returns min, which would silently un-cap a shield smaller than the minimum.
            float cap = Mathf.Max(
                minCells,
                Mathf.Min(maxCells, shieldRadius * share));

            return Mathf.Clamp(
                baseCells * Mathf.Pow(ratio, SizeExponent),
                minCells,
                cap);
        }
    }
}
