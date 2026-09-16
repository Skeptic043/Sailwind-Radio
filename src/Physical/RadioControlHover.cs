using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SailwindRadio.Physical
{
    internal static class RadioControlHover
    {
        private static readonly FieldInfo Controls = AccessTools.Field(typeof(LookUI), "controlsText");
        private static readonly FieldInfo Hint = AccessTools.Field(typeof(LookUI), "hintText");
        private static readonly MethodInfo HideIcons = AccessTools.Method(typeof(LookUI), "HideIcons");
        internal static bool Compatible => Controls?.FieldType == typeof(TextMesh) && Hint?.FieldType == typeof(TextMesh) &&
            HideIcons != null && HideIcons.ReturnType == typeof(void) && HideIcons.GetParameters().Length == 0;
        internal static void ClearPickupHint(LookUI ui, GoPointerButton button)
        {
            // Native LookUI's generic branch labels an unfamiliar child control "pick up".
            // Only our three exact control types are affected. The native item body keeps normal pickup UI.
            if (!(button is RadioPowerButton) && !(button is RadioActionButton) && !(button is RadioVolumeKnob))
                return;
            if (Controls?.GetValue(ui) is TextMesh controls)
                controls.text = "";
            if (Hint?.GetValue(ui) is TextMesh hint)
                hint.text = "";
            HideIcons?.Invoke(ui, null);
        }
    }
}
