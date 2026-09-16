using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SailwindRadio.Physical;
using UnityEngine;

namespace SailwindRadio.Shops
{
    internal sealed class RadioShopService : IDisposable
    {
        private sealed class PurchaseAborted : InvalidOperationException
        {
            internal PurchaseAborted() : base("Native radio registration did not complete") { }
        }
        private sealed class Slot
        {
            internal int Kind;
            internal int Index;
            internal ShopRestock Clock = new ShopRestock();
            internal GameObject Anchor;
            internal RadioShopStock Stock;
        }
        private sealed class Vendor
        {
            internal ShopArea Area;
            internal Slot[] Slots;
            internal int LastAvailable = -1;
            internal RadioShopStand Stand;
            internal string LastReason;
        }
        private static RadioShopService active;
        private readonly RadioWorldService world;
        private readonly Action<string> warn;
        private readonly Harmony harmony = new Harmony("Skeptic043.SailwindRadio.Shops");
        private readonly List<Vendor> vendors = new List<Vendor>();
        private SaveLoadManager session;
        private float nextScan;
        private bool disposed;
        private bool supported;

        internal RadioShopService(RadioWorldService world, Action<string> warn)
        {
            if (active != null) throw new InvalidOperationException("Radio shops already active");
            this.world = world;
            this.warn = warn ?? (_ => { });
            active = this;
            try
            {
                var sale = typeof(Shopkeeper).GetMethod("SellItem", BindingFlags.Instance|BindingFlags.NonPublic,
                    null, new[] { typeof(ShipItem),typeof(int),typeof(int) }, null);
                var sell = typeof(ShipItem).GetMethod("Sell", Type.EmptyTypes);
                var enterBoat = typeof(ShipItem).GetMethod("EnterBoat", BindingFlags.Instance|BindingFlags.NonPublic, null, new[]{typeof(Collider)}, null);
                var price = typeof(Shopkeeper).GetMethod("GetPrice", BindingFlags.Instance|BindingFlags.NonPublic, null, new[]{typeof(ShipItem)}, null);
                if (sale == null || sell == null || enterBoat == null || price == null || enterBoat.ReturnType != typeof(void) || price.ReturnType != typeof(int) ||
                    sale.ReturnType != typeof(void) || sell.ReturnType != typeof(void) || !NativeShopContract.Supports(sale,sell))
                    throw new MissingMethodException("Native shop purchase methods changed");
                harmony.Patch(sale, prefix: new HarmonyMethod(typeof(RadioShopService),nameof(BeforePurchase)),
                    finalizer: new HarmonyMethod(typeof(RadioShopService),nameof(AfterPurchase)));
                harmony.Patch(sell, postfix: new HarmonyMethod(typeof(RadioShopService),nameof(AfterNativeSell)));
                harmony.Patch(enterBoat, prefix: new HarmonyMethod(typeof(RadioShopService),nameof(BeforeEnterBoat)));
                harmony.Patch(price, prefix: new HarmonyMethod(typeof(RadioShopService),nameof(BeforePrice)));
                supported = true;
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                this.warn("Radio shop stock unavailable: " + error.Message);
            }
        }

        internal void Tick()
        {
            if (disposed || !supported) return;
            if (session != SaveLoadManager.instance || GameState.currentlyLoading)
            {
                Clear();
                session = SaveLoadManager.instance;
            }
            if (!world.ReadyForShop) return;
            for(int i=vendors.Count-1;i>=0;i--)
            {
                if (!vendors[i].Area) { Clear(vendors[i]); vendors.RemoveAt(i); }
                else Update(vendors[i]);
            }
            if(Time.unscaledTime < nextScan) return;
            nextScan=Time.unscaledTime+5;
            foreach(var area in UnityEngine.Object.FindObjectsOfType<ShopArea>())
            {
                if (!RadioShopCatalog.Matches(area) || !NativeShopContract.KeeperReady(area) || !area.GetShopkeeper().gameObject.activeInHierarchy ||
                    !Camera.main || Vector3.Distance(Camera.main.transform.position,area.transform.position)>100f || vendors.Exists(v=>v.Area==area)) continue;
                var vendor = new Vendor { Area=area, Slots=new Slot[RadioShopCatalog.StockKinds.Length] };
                for(int i=0;i<vendor.Slots.Length;i++) vendor.Slots[i]=new Slot{Kind=RadioShopCatalog.StockKinds[i],Index=i};
                vendors.Add(vendor);
                Update(vendor);
            }
        }

        private void Update(Vendor vendor)
        {
            bool open = NativeShopContract.KeeperReady(vendor.Area) && vendor.Area.GetShopkeeper().gameObject.activeInHierarchy &&
                (vendor.Area.openAtNight || (Sun.sun && Sun.sun.localTime >= 7 && Sun.sun.localTime <= 18));
            if(!vendor.Stand)
            {
                if(open && Time.unscaledTime>=nextScan && Camera.main && Vector3.Distance(Camera.main.transform.position,vendor.Area.transform.position)<=100f)
                    TryDisplay(vendor);
                return;
            }
            foreach(var slot in vendor.Slots)
            {
                slot.Clock.Tick(Time.deltaTime);
                if(slot.Clock.Occupied && (!slot.Stock || slot.Stock.Purchased || slot.Stock.GetComponent<ShipItem>().sold))
                {
                    slot.Clock.Removed();
                    slot.Stock=null;
                    if(slot.Anchor) UnityEngine.Object.Destroy(slot.Anchor);
                    slot.Anchor=null;
                }
                if(slot.Stock) { ProtectStock(slot.Stock); slot.Stock.gameObject.SetActive(open); continue; }
                if(!open || !slot.Clock.Ready || !Camera.main || Vector3.Distance(Camera.main.transform.position,vendor.Area.transform.position)>100f) continue;
                // Expensive discovery is throttled, independently of simulation-time restocking.
                if(Time.unscaledTime < nextScan) continue;
                TryStock(vendor,slot);
            }
            if(open && Time.unscaledTime>=nextScan)
            {
                int available=0;
                foreach(var slot in vendor.Slots) if(slot.Stock) available++;
                if(available!=vendor.LastAvailable)
                {
                    vendor.LastAvailable=available;
                    if(available<vendor.Slots.Length) warn("Radio shop at "+vendor.Area.transform.parent.name+" has "+available+" of "+vendor.Slots.Length+" devices. Remaining slots are waiting for safe space or restocking");
                }
            }
        }

        private void TryDisplay(Vendor vendor)
        {
            if(!RadioShopPlacement.TryStand(vendor.Area,out var position,out var rotation,out string reason))
            { ReportPlacement(vendor,reason); return; }
            try
            {
                vendor.Stand=RadioShopStand.Create(vendor.Area.transform.parent,position,rotation);
                Physics.SyncTransforms();
                foreach(var slot in vendor.Slots)
                    if(!TryStock(vendor,slot))
                    {
                        Clear(vendor);
                        for(int i=0;i<vendor.Slots.Length;i++) vendor.Slots[i]=new Slot{Kind=RadioShopCatalog.StockKinds[i],Index=i};
                        return; // TryStock already reported the specific blocker. Preserve its deduplication key.
                    }
                vendor.LastReason=null;
            }
            catch(Exception error)
            {
                Clear(vendor);
                for(int i=0;i<vendor.Slots.Length;i++) vendor.Slots[i]=new Slot{Kind=RadioShopCatalog.StockKinds[i],Index=i};
                ReportPlacement(vendor,error.Message);
            }
        }

        private void ReportPlacement(Vendor vendor,string reason)
        {
            if(vendor.LastReason==reason) return;
            vendor.LastReason=reason;
            warn("Radio display at "+vendor.Area.transform.parent.name+" is waiting: "+reason);
        }

        private bool TryStock(Vendor vendor, Slot slot)
        {
            if(!RadioShopPlacement.SlotClear(vendor.Stand,slot.Index,slot.Kind,out string reason))
            { ReportPlacement(vendor,"stock slot blocked by "+reason); return false; }
            var position=vendor.Stand.transform.TransformPoint(RadioStandLayout.Slots[slot.Index]);
            var rotation=vendor.Stand.transform.rotation;
            GameObject anchor=null;
            RadioItemController controller=null;
            try
            {
                anchor=new GameObject("Radio shop stock anchor");
                anchor.SetActive(false);
                anchor.transform.SetParent(vendor.Stand.transform,false);
                anchor.transform.position=position;
                anchor.transform.rotation=rotation;
                Vector3 s=anchor.transform.parent.lossyScale;
                anchor.transform.localScale=new Vector3(1/s.x,1/s.y,1/s.z);
                var native=anchor.AddComponent<ShopItemSpawner>();
                native.enabled=false;
                native.priceMult=1;
                var anchorRenderer=anchor.GetComponent<MeshRenderer>();
                if(!anchorRenderer) throw new MissingComponentException("Native shop spawner renderer was not created");
                anchorRenderer.enabled=false;
                anchor.SetActive(true);
                controller=world.CreateShopStock(anchor.transform,position,rotation,slot.Kind);
                if(!controller) { UnityEngine.Object.Destroy(anchor); return false; }
                var stock=controller.gameObject.AddComponent<RadioShopStock>();
                stock.Service=this;
                stock.Shop=vendor.Area;
                stock.Controller=controller;
                stock.Anchor=anchor.transform;
                controller.GetComponent<ShipItem>().AddToShop(vendor.Area);
                controller.SetShopStock(true);
                slot.Anchor=anchor;
                slot.Stock=stock;
                slot.Clock.Spawned();
                Physics.SyncTransforms();
                return true;
            }
            catch(Exception error)
            {
                if(controller) { vendor.Area.itemsForSale.Remove(controller.GetComponent<ShipItem>()); UnityEngine.Object.Destroy(controller.gameObject); }
                if(anchor) UnityEngine.Object.Destroy(anchor);
                warn("Radio shop stock could not be placed: "+error.Message);
                return false;
            }
        }

        private static void ProtectStock(RadioShopStock stock)
        {
            var item=stock.GetComponent<ShipItem>();
            if(!item || item.sold || !stock.Anchor) return;
            // Native pricing requires the direct ShopItemSpawner parent, including hover pricing
            // before the purchase guard runs. Preserve world position while repairing that contract.
            if(item.transform.parent!=stock.Anchor) item.transform.SetParent(stock.Anchor,true);
            if(item.held && Vector3.Distance(item.transform.position,stock.Anchor.position)>1.8f) item.ReturnToShopPos();
        }

        private static void BeforePrice(ShipItem item)
        {
            var stock=item ? item.GetComponent<RadioShopStock>() : null;
            if(stock) ProtectStock(stock);
        }

        private static bool BeforeEnterBoat(ShipItem __instance)
        {
            // Unsold display stock remains tied to its merchant instead of adopting a boat parent.
            return __instance.sold || !__instance.GetComponent<RadioShopStock>();
        }

        private static bool BeforePurchase(Shopkeeper __instance, ShipItem item)
        {
            var stock=item ? item.GetComponent<RadioShopStock>() : null;
            if(!stock) return true;
            if(active==null || stock.Service!=active || !stock.Shop || stock.Shop.GetShopkeeper()!=__instance || stock.Reserved || stock.Purchased ||
                !active.world.ReservePurchase(stock.Controller))
            {
                if(NotificationUi.instance) NotificationUi.instance.ShowNotification("Radio purchase unavailable. Try again after loading or saving");
                return false;
            }
            stock.Reserved=true;
            return true;
        }

        private static void AfterNativeSell(ShipItem __instance)
        {
            var stock=__instance.GetComponent<RadioShopStock>();
            if(stock && stock.Reserved && stock.Service==active && active!=null)
            {
                stock.Purchased=active.world.FinishPurchase(stock.Controller);
                if(!stock.Purchased) throw new PurchaseAborted();
                stock.Controller.SetShopStock(false);
            }
        }

        private static Exception AfterPurchase(ShipItem item, Exception __exception)
        {
            var stock=item ? item.GetComponent<RadioShopStock>() : null;
            if(stock && stock.Reserved && stock.Service==active && active!=null)
            {
                stock.Purchased=active.world.FinishPurchase(stock.Controller);
                stock.Reserved=false;
                if(stock.Purchased) stock.Controller.SetShopStock(false);
            }
            if(__exception is PurchaseAborted)
            {
                if(NotificationUi.instance) NotificationUi.instance.ShowNotification("Radio purchase cancelled. No money was charged");
                return null;
            }
            return __exception;
        }

        private static void Clear(Vendor vendor)
        {
            foreach(var slot in vendor.Slots)
            {
                if(slot.Stock && !slot.Stock.GetComponent<ShipItem>().sold)
                {
                    if(vendor.Area) vendor.Area.itemsForSale.Remove(slot.Stock.GetComponent<ShipItem>());
                    UnityEngine.Object.Destroy(slot.Stock.gameObject);
                }
                if(slot.Anchor) UnityEngine.Object.Destroy(slot.Anchor);
            }
            if(vendor.Stand) UnityEngine.Object.Destroy(vendor.Stand.gameObject);
            vendor.Stand=null;
        }
        private void Clear() { foreach(var vendor in vendors) Clear(vendor); vendors.Clear(); nextScan=0; }
        public void Dispose()
        {
            if(disposed) return;
            disposed=true;
            Clear();
            harmony.UnpatchSelf();
            if(active==this) active=null;
        }
    }
}
