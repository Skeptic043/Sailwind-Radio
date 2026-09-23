using UnityEngine;

namespace SailwindRadio.Physical
{
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
