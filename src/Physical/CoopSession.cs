using System;
using System.Reflection;

namespace SailwindRadio.Physical
{
    // Reads Sailwind Co-op's public CoopStatus by reflection, so Radio needs nothing from Co-op to run without it.
    internal static class CoopSession
    {
        private static readonly MethodInfo inSession, isCaptain;

        static CoopSession()
        {
            // BepInEx loads plugins from their files, so the type is found among the loaded assemblies by name.
            Type status = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == "SailwindCoop") { status = assembly.GetType("SailwindCoop.CoopStatus"); break; }
            inSession = status?.GetProperty("InSession", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
            isCaptain = status?.GetProperty("IsCaptain", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
        }

        // True while this player is a guest in someone else's co-op session. A guest plays from Co-op's own hidden
        // save, never their solo slots, and sees devices the host bought that the guest's save has no record of.
        internal static bool Guest
        {
            get
            {
                if (inSession == null || isCaptain == null) return false;
                try { return (bool)inSession.Invoke(null, null) && !(bool)isCaptain.Invoke(null, null); }
                catch (TargetInvocationException) { return false; }
            }
        }
    }
}
