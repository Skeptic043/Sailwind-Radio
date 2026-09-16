namespace SailwindRadio.Shops
{
    // Simulation time, matching the native vendor's two-minute replenishment delay.
    internal sealed class ShopRestock
    {
        internal const float Delay = 120f;
        private float remaining;
        internal bool Occupied { get; private set; }
        internal bool Ready => !Occupied && remaining <= 0;
        internal void Spawned() { Occupied = true; }
        internal void Removed() { if (Occupied) { Occupied = false; remaining = Delay; } }
        internal void Tick(float seconds) { if (seconds > 0 && !float.IsInfinity(seconds) && !float.IsNaN(seconds)) remaining = System.Math.Max(0, remaining-seconds); }
    }
}
