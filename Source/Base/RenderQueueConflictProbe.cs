using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Verse;
using Waylith.Rendering;
using Material = UnityEngine.Material;

namespace VisualTweaks
{
    /// <summary>
    /// Reports other mods claiming the three render queues this mod reserves for its
    /// overlay stack.
    ///
    /// A queue number is a shared namespace: two materials at the same value are sorted
    /// by camera depth, so a foreign overlay can flip in front of ours depending on where
    /// the camera sits. A shader that declares a scene grab at the same queue is worse:
    /// Waylith takes another full-frame copy there and splits the transparent pass, and
    /// <c>FinalDrawItem</c> raises the draw's queue to the grab queue, so the foreign
    /// effect lands on our slot even when its material says otherwise.
    ///
    /// There is no engine API for "who uses queue N", so the probe reads the three
    /// sources that together cover every way a mod can claim one:
    ///
    /// 1. ShaderDefs — declared default queue and scene grab, with the owning mod.
    /// 2. MaterialDefs / ThingDefs / FleckDefs — explicit GraphicData renderQueue values,
    ///    which are applied before any graphic is instantiated.
    /// 3. Live materials — the only source that sees a renderQueue override applied in
    ///    code, which is exactly how this mod claims its own queues.
    ///
    /// Defs never change at runtime, so that half runs once per enable. Shaders can be
    /// registered late and materials are created lazily, so the live half repeats on a
    /// slow timer. Findings are deduplicated by shader/queue, so a conflict is reported
    /// once per enable rather than once per scan. The probe only reads: it never changes
    /// a queue or a draw.
    ///
    /// Gated by <see cref="VisualTweaksSettings.renderQueueProbe"/>, off by default.
    ///
    /// The static half cannot see a material that a mod creates only when a rare event
    /// fires; the live scan is what eventually catches those.
    /// </summary>
    internal static class RenderQueueConflictProbe
    {
        /// <summary>
        /// The three custom slots this mod owns. 2991 is the gas / wind band below the
        /// transparent overlays, 3174 is the cold mist / heat haze band just below fog of
        /// war, and 3179 is the vacuum leak / shockwave band just below darkness. The two
        /// vanilla-shared queues (3000 for dust, 3151 for the shield) are deliberately not
        /// probed: a hit there is expected.
        /// </summary>
        private const int BelowFogAirQueue = 2991;
        private const int BelowFogOverlayQueue = 3174;
        private const int AboveFogQueue = 3179;

        private const float ScanInterval = 30f;
        private const int MaxFindingsPerReport = 12;

        // Our own scene grab names. A foreign shader reusing one of them makes
        // SceneGrabRegistry keep whichever definition it saw first, which silently moves
        // one of the two effects to the wrong queue.
        private const string BelowFogGrabName = "VisualTweaksHeatHaze";
        private const string AboveFogGrabName = "VisualTweaksRefraction";

        private static readonly HashSet<string> ReportedIds = new HashSet<string>(StringComparer.Ordinal);

        private static readonly int ErrorKey =
            "VisualTweaks.RenderQueueConflictProbe".GetHashCode();

        private static float nextScanRealtime = float.NegativeInfinity;
        private static bool wasEnabled;
        private static bool defsScanned;
        private static bool clearReported;
        private static bool conflictReported;

        /// <summary>
        /// Called once per drawn frame from the map component. When the setting is off
        /// this is one bool read; when it is on, the timer gate is two float compares and
        /// everything else only runs on a scan tick.
        /// </summary>
        internal static void Tick()
        {
            if (!VisualTweaksSettings.renderQueueProbe)
            {
                wasEnabled = false;
                return;
            }

            // A fresh enable is a fresh report: clear the dedupe set and the one-time def
            // flag so the user gets the full inventory immediately instead of waiting for
            // the next 30 s tick and seeing only findings that were not logged before.
            if (!wasEnabled)
            {
                wasEnabled = true;
                defsScanned = false;
                clearReported = false;
                conflictReported = false;
                ReportedIds.Clear();
                nextScanRealtime = float.NegativeInfinity;
            }

            float now = Time.realtimeSinceStartup;
            if (now < nextScanRealtime)
            {
                return;
            }

            nextScanRealtime = now + ScanInterval;

            try
            {
                Scan();
            }
            catch (Exception ex)
            {
                Log.ErrorOnce(
                    "[Visual Tweaks] Render queue probe failed: " + ex,
                    ErrorKey);
            }
        }

        private static void Scan()
        {
            List<string> findings = new List<string>();

            // Def data is fixed after startup; scanning it every 30 s would only repeat
            // the same dictionary walks over every ThingDef in a large mod list. The flag
            // is set after the scan so a throw retries on the next tick instead of
            // permanently skipping half the inventory.
            if (!defsScanned)
            {
                ScanDefs(findings);
                defsScanned = true;
            }

            ScanRegisteredShaders(findings);
            ScanLiveMaterials(findings);

            if (findings.Count > 0)
            {
                conflictReported = true;
                Log.Warning(BuildReport(findings));
                return;
            }

            // Only say "clear" before the first conflict is ever seen. A later scan with
            // no *new* findings is not clear, it just has nothing new to report.
            if (!conflictReported && !clearReported)
            {
                clearReported = true;
                VisualTweaksLog.Info(
                    "render queue probe: " + BelowFogAirQueue + ", "
                    + BelowFogOverlayQueue + " and " + AboveFogQueue + " are clear.");
            }
        }

        // ------------------------------------------------------------------
        // Static inventory.
        // ------------------------------------------------------------------

        private static void ScanDefs(List<string> findings)
        {
            foreach (ShaderDef def in DefDatabase<ShaderDef>.AllDefs)
            {
                if (IsOurs(def.defName))
                {
                    continue;
                }

                ShaderProgramDefinition program = def.shaderProgram;
                if (program == null)
                {
                    continue;
                }

                ReportShaderQueue(
                    findings,
                    program.renderQueue,
                    def.defName,
                    OwnerOf(def),
                    "defaults to this queue");

                SceneGrabDeclaration? grab = program.sceneGrab;
                if (grab != null)
                {
                    ReportGrab(
                        findings,
                        grab.queue,
                        grab.name,
                        def.defName,
                        OwnerOf(def));
                }
            }

            foreach (MaterialDef def in DefDatabase<MaterialDef>.AllDefs)
            {
                // -1 means "inherit the shader default", which the ShaderDef pass above
                // has already inspected.
                if (def.renderQueue < 0)
                {
                    continue;
                }

                ShaderProgramDefinition? program = def.shader?.shaderProgram;
                int effective = EffectiveQueue(def.renderQueue, program);

                // When the shader's own default already is this queue, the ShaderDef pass
                // reported the shader; the material adds no new information.
                if (!IsProbeQueue(effective) || (program != null && program.renderQueue == effective))
                {
                    continue;
                }

                ReportMaterial(
                    findings,
                    effective,
                    def.defName,
                    def.shader?.defName,
                    def.modContentPack?.Name ?? "unknown source");
            }

            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                ScanGraphicData(findings, def, def.graphicData);
            }

            foreach (FleckDef def in DefDatabase<FleckDef>.AllDefs)
            {
                ScanGraphicData(findings, def, def.graphicData);

                if (def.randomGraphics == null)
                {
                    continue;
                }

                for (int i = 0; i < def.randomGraphics.Count; i++)
                {
                    ScanGraphicData(findings, def, def.randomGraphics[i]);
                }
            }
        }

        private static void ScanGraphicData(List<string> findings, Def def, GraphicData? graphicData)
        {
            if (graphicData == null || !IsProbeQueue(graphicData.renderQueue))
            {
                return;
            }

            string id = "q" + graphicData.renderQueue + ":graphic:" + def.defName;
            if (!ReportedIds.Add(id))
            {
                return;
            }

            findings.Add(
                "  " + QueueLabel(graphicData.renderQueue) + "  graphic of def '" + def.defName
                + "' (" + (def.modContentPack?.Name ?? "unknown source")
                + ", shader " + (graphicData.shaderType?.defName ?? "<none>")
                + ") is built at this queue; its draws are depth-sorted against ours.");
        }

        // ------------------------------------------------------------------
        // Runtime inventory.
        // ------------------------------------------------------------------

        private static void ScanRegisteredShaders(List<string> findings)
        {
            IReadOnlyList<Waylith.Rendering.Shader> shaders = ShaderRegistry.AllShaders;
            for (int i = 0; i < shaders.Count; i++)
            {
                Waylith.Rendering.Shader shader = shaders[i];
                if (shader == null || IsOurs(shader.Key))
                {
                    continue;
                }

                // Shaders backed by a ShaderDef were covered by the one-time def scan.
                // This catches programs registered outside the def pipeline.
                if (DefDatabase<ShaderDef>.GetNamedSilentFail(shader.Key) != null)
                {
                    continue;
                }

                ShaderProgramDefinition program = shader.Program;
                if (program == null)
                {
                    continue;
                }

                ReportShaderQueue(
                    findings,
                    shader.DefaultRenderQueue,
                    shader.Key,
                    "runtime-registered",
                    "defaults to this queue");

                SceneGrabDeclaration? grab = program.sceneGrab;
                if (grab != null)
                {
                    ReportGrab(findings, grab.queue, grab.name, shader.Key, "runtime-registered");
                }
            }
        }

        private static void ScanLiveMaterials(List<string> findings)
        {
            Material[] materials = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null)
                {
                    continue;
                }

                // Reading renderQueue/shader would force a deferred material to resolve
                // early. Its shader's default queue is already covered by the def scan.
                if (material.IsDeferredMaterial)
                {
                    continue;
                }

                string materialName = material.name;
                if (materialName != null
                    && materialName.StartsWith("VisualTweaks", StringComparison.Ordinal))
                {
                    continue;
                }

                UnityEngine.Shader unityShader = material.shader;
                string shaderName = unityShader != null ? unityShader.name : "<no shader>";

                Waylith.Rendering.Shader? engineShader = null;
                ShaderRegistry.TryResolve(shaderName, out engineShader);

                int effective = EffectiveQueue(material.renderQueue, engineShader?.Program);

                // The def scan already reports a shader whose default queue is ours; a
                // material on that default is not a second finding.
                if (!IsProbeQueue(effective)
                    || (engineShader != null && engineShader.DefaultRenderQueue == effective))
                {
                    continue;
                }

                ReportMaterial(
                    findings,
                    effective,
                    materialName ?? "<unnamed material>",
                    shaderName,
                    OwnerOf(shaderName));
            }
        }

        // ------------------------------------------------------------------
        // Reporting.
        // ------------------------------------------------------------------

        private static void ReportShaderQueue(
            List<string> findings,
            int queue,
            string shaderName,
            string owner,
            string suffix)
        {
            if (!IsProbeQueue(queue))
            {
                return;
            }

            string id = "q" + queue + ":shader:" + shaderName;
            if (!ReportedIds.Add(id))
            {
                return;
            }

            findings.Add(
                "  " + QueueLabel(queue) + "  shader '" + shaderName + "' (" + owner + ") "
                + suffix + "; any draw using it is depth-sorted against ours.");
        }

        private static void ReportGrab(
            List<string> findings,
            int queue,
            string? grabName,
            string shaderName,
            string owner)
        {
            // Same name, different queue: SceneGrabRegistry keeps the first definition
            // and only logs an engine warning, so one of the two effects is silently
            // running at the wrong queue. This check must run before the queue filter —
            // the whole point is that the other mod declared a different queue.
            bool isOurBelowName = grabName == BelowFogGrabName && queue != BelowFogOverlayQueue;
            bool isOurAboveName = grabName == AboveFogGrabName && queue != AboveFogQueue;
            if (isOurBelowName || isOurAboveName)
            {
                string nameId = "grabname:" + grabName;
                if (ReportedIds.Add(nameId))
                {
                    findings.Add(
                        "  " + QueueLabel(queue) + "  scene grab name '" + grabName
                        + "' of shader '" + shaderName + "' (" + owner
                        + ") is also used by this mod at "
                        + (isOurBelowName ? BelowFogOverlayQueue : AboveFogQueue)
                        + "; SceneGrabRegistry keeps whichever definition loaded first, so one effect runs at the wrong queue.");
                }
            }

            if (!IsProbeQueue(queue))
            {
                return;
            }

            string id = "q" + queue + ":grab:" + (grabName ?? shaderName);
            if (!ReportedIds.Add(id))
            {
                return;
            }

            findings.Add(
                "  " + QueueLabel(queue) + "  scene grab '" + (grabName ?? "<unnamed>")
                + "' of shader '" + shaderName + "' (" + owner
                + "): while it draws, Waylith takes a second full-frame copy and splits the transparent pass at this queue.");
        }

        private static void ReportMaterial(
            List<string> findings,
            int queue,
            string materialName,
            string? shaderName,
            string owner)
        {
            string id = "q" + queue + ":material:" + (shaderName ?? materialName);
            if (!ReportedIds.Add(id))
            {
                return;
            }

            findings.Add(
                "  " + QueueLabel(queue) + "  material '" + materialName + "' (shader '"
                + (shaderName ?? "<none>") + "', " + owner
                + ") is created at this queue; when it draws here it is depth-sorted against ours.");
        }

        private static string BuildReport(List<string> findings)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("[Visual Tweaks] Render queue probe: another content source declares or creates draws on a queue reserved by this mod ("
                + BelowFogAirQueue + " gas/wind; "
                + BelowFogOverlayQueue + " below fog: cold mist / heat haze; "
                + AboveFogQueue + " above fog: vacuum leak / shockwave). This is a warning, not an error: nothing is broken unless the effects overlap, but when they do the order follows camera depth and can flip. A foreign grab also costs an extra full-frame copy per frame while it draws. Check whether the effects can be visible together; if so, move one of the two assignments.");
            builder.Append('\n');

            int shown = Math.Min(findings.Count, MaxFindingsPerReport);
            for (int i = 0; i < shown; i++)
            {
                builder.Append(findings[i]);
                builder.Append('\n');
            }

            if (findings.Count > shown)
            {
                builder.Append("  ... and ");
                builder.Append(findings.Count - shown);
                builder.Append(" more.\n");
            }

            return builder.ToString();
        }

        // ------------------------------------------------------------------
        // Helpers.
        // ------------------------------------------------------------------

        private static bool IsProbeQueue(int queue)
        {
            return queue == BelowFogAirQueue
                || queue == BelowFogOverlayQueue
                || queue == AboveFogQueue;
        }

        private static string QueueLabel(int queue)
        {
            switch (queue)
            {
                case BelowFogAirQueue:
                    return BelowFogAirQueue + " (below fog, gas/wind)";
                case BelowFogOverlayQueue:
                    return BelowFogOverlayQueue + " (below fog, mist/haze)";
                case AboveFogQueue:
                    return AboveFogQueue + " (above fog, leak/shockwave)";
                default:
                    // Reached only by the grab-name clash, which can be at any queue.
                    return queue.ToString();
            }
        }

        /// <summary>
        /// The queue a draw actually lands on. FinalDrawItem raises a material's queue to
        /// its shader's grab queue when the grab is later, so a grab collision can hit a
        /// draw whose material never mentions the queue at all.
        /// </summary>
        private static int EffectiveQueue(int materialQueue, ShaderProgramDefinition? program)
        {
            if (program?.sceneGrab != null)
            {
                int grabQueue = program.sceneGrab.queue;
                if (materialQueue < grabQueue)
                {
                    return grabQueue;
                }
            }

            return materialQueue;
        }

        private static string OwnerOf(Def def)
        {
            return def.modContentPack?.Name ?? "unknown source";
        }

        private static string OwnerOf(string? shaderName)
        {
            if (string.IsNullOrEmpty(shaderName))
            {
                return "unknown source";
            }

            ShaderDef? def = DefDatabase<ShaderDef>.GetNamedSilentFail(shaderName);
            return def?.modContentPack?.Name ?? "unknown source";
        }

        private static bool IsOurs(string? shaderKey)
        {
            switch (shaderKey)
            {
                case "VisualTweaksGasField":
                case "VisualTweaksAOWindGpu":
                case "VisualTweaksColdMist":
                case "VisualTweaksHeatHaze":
                case "VisualTweaksVacuumLeak":
                case "VisualTweaksExplosionRipple":
                case "VisualTweaksShieldDome":
                    return true;
                default:
                    return false;
            }
        }
    }
}
