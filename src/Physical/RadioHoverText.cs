using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SailwindRadio.Physical
{
    internal static class RadioHoverText
    {
        private static readonly FieldInfo Controls = AccessTools.Field(typeof(LookUI), "controlsText");
        private static readonly FieldInfo Hint = AccessTools.Field(typeof(LookUI), "hintText");
        private static readonly FieldInfo Extra = AccessTools.Field(typeof(LookUI), "extraText");
        private static readonly FieldInfo Pointer = AccessTools.Field(typeof(LookUI), "pointer");
        private static readonly MethodInfo HideIcons = AccessTools.Method(typeof(LookUI), "HideIcons");
        internal static bool Compatible => Controls?.FieldType == typeof(TextMesh) && Hint?.FieldType == typeof(TextMesh) &&
            Extra?.FieldType == typeof(TextMesh) && HideIcons != null && HideIcons.ReturnType == typeof(void) &&
            HideIcons.GetParameters().Length == 0;

        internal static void Show(LookUI ui, GoPointerButton button)
        {
            RadioItemController radio = null;
            if (RadioControls.IsOwnControl(button))
            {
                ClearPickupHint(ui);
                radio = RadioControls.RadioOf(button);
            }
            else if (button)
                radio = button.GetComponent<RadioItemController>();
            // A stack's body names the song of a Radio stacked on it.
            if (radio && radio.StackTop)
                radio = radio.StackTop;
            // Native ShowLookText writes the button's own text here first, so the song follows it.
            string song = radio && radio.State != null && radio.State.Role == DeviceRole.Radio ? radio.HoverTrack : "";
            if (song != "" && Extra?.GetValue(ui) is TextMesh extra)
                extra.text = string.IsNullOrEmpty(extra.text) ? song : extra.text + "\n" + song;
            // The stack prompt alone needs the pointer. Without it, the rest of the hover text still works.
            AddControls(ui, Pointer?.FieldType == typeof(GoPointer) ? RadioWorldService.StackHint(button, Pointer.GetValue(ui) as GoPointer) : "");
        }

        // Adds a prompt on its own line after the native control prompts.
        internal static void AddControls(LookUI ui, string prompt)
        {
            if (prompt != "" && Controls?.GetValue(ui) is TextMesh controls)
                controls.text = string.IsNullOrEmpty(controls.text) ? prompt : controls.text + "\n" + prompt;
        }

        private static void ClearPickupHint(LookUI ui)
        {
            // Native LookUI's generic branch labels an unfamiliar child control "pick up".
            // Only our three exact control types are affected. The native item body keeps normal pickup UI.
            if (Controls?.GetValue(ui) is TextMesh controls)
                controls.text = "";
            if (Hint?.GetValue(ui) is TextMesh hint)
                hint.text = "";
            HideIcons?.Invoke(ui, null);
        }
    }
}
