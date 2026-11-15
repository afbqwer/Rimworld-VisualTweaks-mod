using System;
using System.Collections.Generic;
using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// Moves shield type styles out of a save and into the mod settings, once.
    ///
    /// This is not a version upgrade: the mod is still 1.7. An earlier build of this
    /// same version scribed the per-type list inside <see cref="ShieldOverrideGameComponent"/>,
    /// and those saves exist in the wild. Type styles are a preference, not save state,
    /// so they live in <see cref="VisualTweaksSettings"/> and apply to every colony;
    /// this reads the old node while a save is being deserialized, hands its records to
    /// the settings, and never writes the node back -- the component keeps no type data
    /// at all, so the next save drops it.
    ///
    /// Conflict rule: the settings win. A type the settings already style is left
    /// alone, because a player who has edited it since the storage moved must not have
    /// that silently rewritten by an old colony they happen to open next.
    ///
    /// Two phases, because they cannot run at the same moment. Deserialization reads
    /// and imports; writing the settings file there would switch Scribe's mode and
    /// saver out from under the save being read. <c>LoadedGame</c> then writes the
    /// file, which is the first point where Scribe is idle again.
    ///
    /// TODO 1.8: delete this whole file and its two call sites. Only saves written
    /// before the storage moved carry the node, and the pending flag exists for that one
    /// write.
    /// </summary>
    internal static class ShieldTypeMigration
    {
        /// <summary>The node the earlier build scribed inside the game component.</summary>
        private const string LegacyNodeName = "shieldTypeStyleOverrides";

        /// <summary>
        /// Set when deserializing a save imported records, i.e. when the settings file
        /// still has to be written for them to survive a restart. The records
        /// themselves are live in memory by then; this only remembers that disk has not
        /// caught up. Static on purpose: it has to survive from ExposeData to
        /// LoadedGame, and the component instance is not the same thing across a load.
        /// </summary>
        private static bool pendingSettingsWrite;

        /// <summary>
        /// Reads the old node, if this save has one, and moves its records into the
        /// settings. Called from the component's ExposeData; every mode other than
        /// <see cref="LoadSaveMode.LoadingVars"/> is a no-op, so the PostLoadInit pass
        /// cannot import a second time.
        /// </summary>
        internal static void ReadFromSave()
        {
            if (Scribe.mode != LoadSaveMode.LoadingVars)
            {
                return;
            }

            try
            {
                List<ShieldTypeStyleOverride>? legacy = null;
                Scribe_Collections.Look(ref legacy, LegacyNodeName, LookMode.Deep);

                // A save written by this build carries no such node, and Scribe leaves
                // the local untouched when the node is missing, so this is the common
                // path: nothing allocated, nothing logged.
                if (legacy == null || legacy.Count == 0)
                {
                    return;
                }

                // An <li> that failed to resolve comes back as a null element
                // (ScribeExtractor logs and yields default), so count what is really
                // there instead of the raw list length.
                int present = 0;
                for (int i = 0; i < legacy.Count; i++)
                {
                    if (legacy[i] != null)
                    {
                        present++;
                    }
                }

                int imported = Import(legacy);
                if (imported > 0)
                {
                    pendingSettingsWrite = true;
                    VisualTweaksLog.Info(
                        "Moved " + imported + " shield type style(s) out of the save and"
                            + " into the mod settings.");
                }
                else
                {
                    VisualTweaksLog.Info(
                        "Dropped " + present + " shield type style record(s) found in a"
                            + " save; the mod settings already style them.");
                }
            }
            catch (Exception ex)
            {
                // A corrupt node must not stop a colony from loading. Nothing was taken
                // from it, and this build never writes the node back, so the styles it
                // held are gone from the next save -- say so instead of only logging
                // the stack trace.
                Log.ErrorOnce(
                    "[Visual Tweaks] Could not move shield type styles out of a save: "
                        + ex
                        + "\nThose shield type styles cannot be recovered by this build;"
                        + " set them again from the shield style window.",
                    typeof(ShieldTypeMigration).GetHashCode() ^ 0x5B);
            }
        }

        /// <summary>
        /// Writes the settings file after a migration. Called from LoadedGame, which
        /// runs once the loader has finished and reset Scribe to Inactive.
        ///
        /// A failed write is not retried here but the flag stays set, so the next load
        /// tries again; in the meantime any other settings write (closing the mod
        /// settings dialog or the style window) also persists them. A busy Scribe is not
        /// a failure and is deferred silently -- see
        /// <see cref="VisualTweaksSettings.PersistShieldTypeOverrides"/> for what writing
        /// at the wrong moment would do to an in-progress save.
        /// </summary>
        internal static void FlushPendingWrite()
        {
            if (!pendingSettingsWrite)
            {
                return;
            }

            if (Scribe.mode != LoadSaveMode.Inactive)
            {
                return;
            }

            if (VisualTweaksSettings.PersistShieldTypeOverrides())
            {
                pendingSettingsWrite = false;
                return;
            }

            Log.Warning(
                "[Visual Tweaks] Could not write the settings file after moving shield"
                    + " type styles out of a save; retrying on the next load.");
        }

        /// <summary>
        /// Adds every record the settings do not already hold, and reports how many.
        ///
        /// Records that override nothing are skipped: they are what opening the type
        /// editor and closing it again leaves behind, and the player asked for no style.
        /// The duplicate test runs against the live index rather than a snapshot, so two
        /// records for one key inside the same legacy list collapse to the first --
        /// a duplicate would otherwise be permanent, since pruning removes one entry and
        /// the other stays.
        /// </summary>
        private static int Import(IReadOnlyList<ShieldTypeStyleOverride> records)
        {
            int imported = 0;
            for (int i = 0; i < records.Count; i++)
            {
                ShieldTypeStyleOverride record = records[i];
                if (record == null || record.DefName.NullOrEmpty() || record.IsEmpty)
                {
                    continue;
                }

                if (VisualTweaksSettings.FindShieldTypeOverrideRecord(
                        record.Target,
                        record.DefName) != null)
                {
                    continue;
                }

                VisualTweaksSettings.AddShieldTypeOverride(record);
                imported++;
            }

            return imported;
        }
    }
}
