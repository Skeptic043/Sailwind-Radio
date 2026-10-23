namespace SailwindRadio
{
    public enum DeviceRole
    {
        Radio, SmallSpeaker, Speaker, Woofer
    }

    // Kind = family * 4 + role. Family 0 is the walnut set, 1 Al'Ankh, 2 Aestrin, 3 Emerald.
    // Behaviour follows the role, while the exact kind remains the model, item and save identity.
    public static class DeviceKind
    {
        public const int Count = 16;
        public const int Walnut = 0, AlAnkh = 1, Aestrin = 2, Emerald = 3;
        public const int WalnutRadio = 0, WalnutSmallSpeaker = 1, WalnutSpeaker = 2, WalnutWoofer = 3;
        public static bool IsValid(int kind) => kind >= 0 && kind < Count;
        public static DeviceRole Role(int kind) => (DeviceRole)(kind & 3);
        public static int Family(int kind) => kind >> 2;
        public static bool IsWalnut(int kind) => Family(kind) == Walnut;
        public static int Of(int family, DeviceRole role) => family * 4 + (int)role;
        // Within one family, Speakers and Woofers stack in any order, and the Radio sits on either. Nothing stacks on a
        // Radio. A stack holds at most MaximumStack devices, and its bottom stays the native item.
        public const int MaximumStack = 6;
        public static bool CanStack(int bottom, int top) => IsValid(bottom) && IsValid(top) && Family(bottom) == Family(top) &&
            IsCabinet(bottom) && (IsCabinet(top) || Role(top) == DeviceRole.Radio);
        private static bool IsCabinet(int kind) => Role(kind) == DeviceRole.Speaker || Role(kind) == DeviceRole.Woofer;
    }
}
