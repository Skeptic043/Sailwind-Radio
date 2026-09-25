using System;
using SailwindRadio.Persistence;
using SailwindRadio.Playback;

namespace SailwindRadio.Shops
{
    internal static class ShopPurchaseReservation
    {
        internal static int Reserve(RadioSaveStore store, GlobalRadioArbiter arbiter, RadioState state,
            Func<int> nextId, Func<int, bool> nativeIdExists)
        {
            int id = 0;
            for (int attempt=0;attempt<64;attempt++)
            {
                int candidate=nextId();
                if(candidate>0 && !nativeIdExists(candidate) && !store.TryGetAny(candidate,out _) &&
                    !arbiter.TryGet(candidate,out _)) { id=candidate; break; }
            }
            if(id==0) throw new InvalidOperationException("No unused native item identity is available");
            try
            {
                arbiter.WriteTo(store);
                store.Put(RadioRecord.Capture(id,RadioSaveStore.ItemIndex(state.Kind),state));
                store.Save();
                arbiter.Register(id,state);
                return id;
            }
            catch
            {
                store.Remove(id);
                arbiter.Remove(id);
                throw;
            }
        }

        internal static void Cancel(RadioSaveStore store, GlobalRadioArbiter arbiter, int id)
        {
            store.Remove(id);
            arbiter.Remove(id);
        }
    }
}
