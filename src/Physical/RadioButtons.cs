using UnityEngine;

namespace SailwindRadio.Physical
{
    internal static class RadioControls
    {
        internal static bool IsOwnControl(GoPointerButton button) =>
            button is RadioPowerButton || button is RadioActionButton || button is RadioVolumeKnob;
        internal static RadioItemController RadioOf(GoPointerButton button) =>
            button is RadioPowerButton power ? power.Radio : button is RadioActionButton action ? action.Radio :
            button is RadioVolumeKnob knob ? knob.Radio : null;
    }

    public sealed class RadioPowerButton : GoPointerButton
    {
        public RadioItemController Radio;

        public override void OnActivate(GoPointer pointer)
        {
            if (Radio && pointer && !pointer.GetHeldItem())
                Radio.RequestAction(RadioAction.Power);
        }

        public override void ExtraLateUpdate()
        {
            lookText = "";
        }
    }

    public sealed class RadioActionButton : GoPointerButton
    {
        public RadioItemController Radio;
        public RadioAction Action;

        public override void OnActivate(GoPointer pointer)
        {
            if (!Radio || !pointer || pointer.GetHeldItem())
                return;
            Radio.RememberControl(pointer, GetComponent<Collider>());
            Radio.RequestAction(Action);
        }

        public override void ExtraLateUpdate()
        {
            lookText = "";
        }
    }
}
