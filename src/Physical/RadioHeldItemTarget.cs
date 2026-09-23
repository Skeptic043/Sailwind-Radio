namespace SailwindRadio.Physical
{
    internal static class RadioHeldItemTarget
    {
        internal static void Clear(GoPointer pointer, ref GoPointerButton target, ref float lookDistance)
        {
            // Native GoPointer drops a ShipItem on pickup-key release only when its
            // pointedAtButton is null. Clear child controls before native LateUpdate
            // handles the click, including a target left from an earlier FixedUpdate.
            if ((target is RadioPowerButton || target is RadioActionButton || target is RadioVolumeKnob) &&
                pointer && pointer.GetHeldItem())
            {
                target = null;
                lookDistance = 0f;
            }
        }
    }
}
