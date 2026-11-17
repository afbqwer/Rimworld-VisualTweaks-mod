using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;
using Waylith.Rendering;

namespace VisualTweaks
{
    /// <summary>
    /// Surfaces the engine's own "this draw did not happen" messages while
    /// <see cref="VisualTweaksSettings.renderQueueProbe"/> is on.
    ///
    /// Waylith already explains every dropped draw — "pipeline creation failed",
    /// "draw skipped: pipeline is null", "draw skipped: material descriptor set is
    /// null", "pipeline rejected: vertex attributes did not match shader", "draw
    /// command rejected while resolving mesh" — but all of those go through a
    /// <c>LogDiagnostic(string? diagnosticPrefix, string message)</c> that starts with
    /// <c>if (diagnosticPrefix != null)</c>. Every <c>RecordPass</c> caller in RimWorld
    /// (map, water, world) passes null, so the messages are unreachable through the
    /// normal log no matter what the log level is: <c>Program.cs</c> only filters the
    /// "Verbose" category, and these are Warning level on the "Vulkan" category, which
    /// always passes the Debug minimum.
    ///
    /// That matters for support: "the selection brackets and the crafting progress bar
    /// are not drawn" is exactly the symptom of a draw being skipped, and without these
    /// lines the player's log says nothing at all. This hook reads the message the
    /// engine was about to discard and hands it to the probe's report, which is already
    /// the place a player is told to enable.
    ///
    /// The prefix is void, so the original method still runs and the engine's behaviour
    /// is unchanged. Messages are recorded only when the caller passed no prefix: with a
    /// prefix the engine writes the line itself and a second copy would only be noise.
    /// </summary>
    internal static class RenderDrawDiagnostics
    {
        /// <summary>
        /// Distinct messages kept per session. A broken material emits one message per
        /// frame, and a large mod list can break dozens of them; the cap keeps a support
        /// log readable and the memory cost bounded. Everything past it is counted so the
        /// report can say how much was dropped on the floor.
        /// </summary>
        private const int MaxTracked = 64;

        // DrawSortKey contains the submission index, so the tail of every "draw skipped"
        // message is different on every frame. Cutting it there is what makes the dedupe
        // below actually work instead of filling the cap with one draw per frame.
        private const string SortKeyMarker = " sortKey=";

        private static readonly object Gate = new object();

        private static readonly List<string> Pending = new List<string>();

        private static readonly HashSet<string> Seen =
            new HashSet<string>(StringComparer.Ordinal);

        private static int suppressed;
        private static int hooked;

        /// <summary>Messages thrown away after the cap was reached.</summary>
        internal static int Suppressed
        {
            get
            {
                lock (Gate)
                {
                    return suppressed;
                }
            }
        }

        /// <summary>How many of the three engine log paths were hooked.</summary>
        internal static int Hooked => hooked;

        /// <summary>
        /// Installed once at startup next to the other patches, not when the setting is
        /// switched on: the player may turn the probe on in the middle of a session,
        /// and the very first frame after that is the one worth capturing. The cost while
        /// the setting is off is a single bool read inside a method that is only reached
        /// when there is something to report.
        /// </summary>
        internal static void Apply(Harmony harmony)
        {
            MethodInfo prefix = AccessTools.Method(
                typeof(RenderDrawDiagnostics),
                nameof(CaptureMessage));
            if (prefix == null)
            {
                Log.Warning("[Visual Tweaks] Cannot hook draw diagnostics.");
                return;
            }

            int count = 0;
            if (TryHook(harmony, typeof(MeshBatchRenderer), prefix))
            {
                count++;
            }

            if (TryHook(harmony, typeof(MeshPipelineCache), prefix))
            {
                count++;
            }

            if (TryHook(harmony, typeof(DrawItemFactory), prefix))
            {
                count++;
            }

            hooked = count;
            if (count > 0)
            {
                VisualTweaksLog.Info("draw diagnostics hooked " + count
                    + "/3 engine log paths (off until the render queue probe is enabled).");
            }
        }

        private static bool TryHook(Harmony harmony, Type owner, MethodInfo prefix)
        {
            MethodInfo target = AccessTools.Method(
                owner,
                "LogDiagnostic",
                new[] { typeof(string), typeof(string) });
            if (target == null)
            {
                Log.Warning("[Visual Tweaks] Cannot patch " + owner.Name
                    + ".LogDiagnostic; draw diagnostics are incomplete.");
                return false;
            }

            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("[Visual Tweaks] Failed to patch " + owner.Name
                    + ".LogDiagnostic: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Harmony prefix on all three <c>LogDiagnostic</c> overloads. The parameters are
        /// positional on purpose: the engine's own parameter names are not part of any
        /// contract we control.
        /// </summary>
        private static void CaptureMessage(string? __0, string? __1)
        {
            if (!VisualTweaksSettings.renderQueueProbe)
            {
                return;
            }

            // A caller that supplies a prefix gets the engine's warning in the log
            // already; capturing it too would print every line twice.
            if (__0 != null)
            {
                return;
            }

            Record(__1);
        }

        private static void Record(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            string key = Normalise(message!);

            lock (Gate)
            {
                if (Seen.Count >= MaxTracked && !Seen.Contains(key))
                {
                    suppressed++;
                    return;
                }

                if (!Seen.Add(key))
                {
                    return;
                }

                Pending.Add(key);
            }
        }

        private static string Normalise(string message)
        {
            int cut = message.IndexOf(SortKeyMarker, StringComparison.Ordinal);
            return cut > 0 ? message.Substring(0, cut) : message;
        }

        /// <summary>
        /// Hands the messages recorded since the last scan to the probe. Called on the
        /// probe's own cadence so both kinds of finding land in the same report.
        /// </summary>
        internal static void Drain(List<string> into)
        {
            lock (Gate)
            {
                if (Pending.Count == 0)
                {
                    return;
                }

                into.AddRange(Pending);
                Pending.Clear();
            }
        }
    }
}
