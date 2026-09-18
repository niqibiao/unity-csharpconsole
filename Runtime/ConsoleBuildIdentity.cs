using UnityEngine;
using UnityEngine.Scripting;

namespace Zh1Zh1.CSharpConsole
{
    /// <summary>
    /// Identity of the build this player came from.
    ///
    /// A submission bound for a player is compiled in an editor, against material --
    /// preprocessor symbols and stripped assemblies -- taken from a build. Nothing else
    /// proves that material came from the build actually running: an editor whose
    /// project has moved on, or a player someone else made, both compile without
    /// complaint and fail at run time. Reporting the build's own GUID lets the editor
    /// find the compile set registered for it, or refuse rather than guess.
    /// </summary>
    [Preserve]
    public static class ConsoleBuildIdentity
    {
        private static string s_BuildGuid = "";

        /// <summary>
        /// The build GUID, or empty in the editor and before <see cref="Capture"/> has
        /// run. Empty means "unknown", which callers treat as nothing to contradict.
        /// </summary>
        public static string BuildGuid
        {
            get { return s_BuildGuid; }
        }

        /// <summary>
        /// Called from <see cref="RuntimeInitializer"/> rather than through
        /// RuntimeInitializeOnLoadMethod: measured in a player, an attributed method in
        /// this assembly never reached RuntimeInitializeOnLoads.json, though the same
        /// declaration in the project's own assembly did, and though the method and its
        /// attribute both survived stripping. Initialising from the call that starts the
        /// service does not depend on that scan, and is on the main thread by
        /// construction -- which the property requires and the HTTP worker serving a
        /// health request is not.
        ///
        /// That reachable call is also what keeps Application.buildGUID in the build. It
        /// is stripped out of a player that never mentions it, and no attribute can be
        /// attached to it directly.
        /// </summary>
        [Preserve]
        internal static void Capture()
        {
            s_BuildGuid = Application.buildGUID ?? "";
        }
    }
}
