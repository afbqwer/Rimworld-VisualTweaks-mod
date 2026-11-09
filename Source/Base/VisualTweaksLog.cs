using Verse;

namespace VisualTweaks
{
    /// <summary>
    /// The single exit for every log line this mod writes.
    ///
    /// Three levels, each with one job:
    ///
    /// - <see cref="TraceOnce"/>: "did this code path actually run" messages (first draw
    ///   of a renderer, patch took over vanilla's layer). Gated on dev mode only. They
    ///   fire once per session, so they are cheap, but they are also noise to anyone who
    ///   enabled dev mode for an unrelated red error — hence the gate.
    ///
    /// - <see cref="GasTrace"/> / <see cref="LeakTrace"/>: high-frequency per-frame or
    ///   per-scan output, so they sit behind their own settings checkbox on top of dev
    ///   mode. Off by default, and the caller must check the matching *Enabled property
    ///   before building the message so an off switch costs nothing.
    ///
    /// - <see cref="Info"/>: support information that must survive into a player's log
    ///   with everything off — which build patched what, which layer was taken over.
    ///   Never gated, because that is exactly the line a bug report needs.
    ///
    /// Warnings and errors deliberately bypass this class: <c>Log.Warning</c> and
    /// <c>Log.Error</c> already carry the stack trace and the log window's colour
    /// coding, and wrapping them would only add a prefix. Their call sites keep the
    /// tag by hand.
    ///
    /// The prefix lives here so call sites cannot drift apart, and so a message that is
    /// moved between levels keeps its tag.
    /// </summary>
    public static class VisualTweaksLog
    {
        private const string Prefix = "[Visual Tweaks] ";

        /// <summary>Gas field window tracing. Read before composing a message.</summary>
        public static bool GasTraceEnabled =>
            Prefs.DevMode && VisualTweaksSettings.gasFieldTraceWindows;

        /// <summary>Vacuum leak tracing. Read before composing a message.</summary>
        public static bool LeakTraceEnabled =>
            Prefs.DevMode && VisualTweaksSettings.vacuumLeakTrace;

        /// <summary>
        /// Once-per-session trace. The flag is only set when the line is actually
        /// written, so turning dev mode on later still shows the message on the next
        /// frame instead of being swallowed by an earlier, silent call.
        /// </summary>
        public static void TraceOnce(ref bool logged, string message)
        {
            if (!Prefs.DevMode || logged)
            {
                return;
            }

            logged = true;
            Log.Message(Prefix + message);
        }

        public static void GasTrace(string message)
        {
            if (!GasTraceEnabled)
            {
                return;
            }

            Log.Message(Prefix + message);
        }

        public static void LeakTrace(string message)
        {
            if (!LeakTraceEnabled)
            {
                return;
            }

            Log.Message(Prefix + message);
        }

        public static void Info(string message)
        {
            Log.Message(Prefix + message);
        }
    }
}
