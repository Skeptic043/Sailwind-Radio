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
            internal IslandSceneryScene Scenery;
            internal ShopArea Area;
            internal Slot[] Slots;
            internal int LastAvailable = -1;
            internal RadioShopStand Stand;
            internal GameObject OwnedNpc;
            internal Collider[] MerchantTriggers;
            internal bool DisplayShown;
            internal bool InitialStockAttempted;
            internal float StageReadyAt = -1f;
            internal float NextStockAttempt;
            internal readonly HashSet<string> ReportedReasons=new HashSet<string>();
        }
        private static RadioShopService active;
        private readonly RadioWorldService world;
        private readonly Action<string> warn;
        private readonly Harmony harmony = new Harmony("Skeptic043.SailwindRadio.Shops");
        private readonly List<Vendor> vendors = new List<Vendor>();
        private SaveLoadManager session;
        private float nextScan;
        private float sessionBeganAt;
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
                var areaEnter=typeof(ShopArea).GetMethod("OnTriggerEnter", BindingFlags.Instance|BindingFlags.NonPublic);
                var keeperEnter=typeof(Shopkeeper).GetMethod("OnTriggerEnter", BindingFlags.Instance|BindingFlags.NonPublic);
                if(areaEnter==null || keeperEnter==null) throw new MissingMethodException("Native shop trigger methods changed");
                harmony.Patch(areaEnter, prefix: new HarmonyMethod(typeof(RadioShopService),nameof(BeforeAreaEnter)));
                harmony.Patch(keeperEnter, prefix: new HarmonyMethod(typeof(RadioShopService),nameof(BeforeKeeperEnter)));
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
            if (disposed) return;
            if (session != SaveLoadManager.instance || GameState.currentlyLoading)
            {
                Clear();
                session = SaveLoadManager.instance;
                sessionBeganAt = Time.unscaledTime;
            }
            if (GameState.currentlyLoading || !session || !GameState.playing || GameState.loadingScenes > 0) return;
            // Build the hidden display as soon as the native scenery is present.
            // Save/economy readiness can follow several seconds later, especially
            // when loading a save while looking directly at the stall.
            for(int i=vendors.Count-1;i>=0;i--)
            {
                if (!vendors[i].Scenery) { Clear(vendors[i]); vendors.RemoveAt(i); }
                else
                {
                    if (world.ReadyToStageShop && vendors[i].StageReadyAt < 0f)
                        vendors[i].StageReadyAt = Time.unscaledTime;
                    if (vendors[i].Stand && vendors[i].Area && (world.ReadyToStageShop || !vendors[i].DisplayShown))
                        Update(vendors[i]);
                }
            }
            if(Time.unscaledTime < nextScan) return;
            foreach(var scenery in UnityEngine.Object.FindObjectsOfType<IslandSceneryScene>())
            {
                if (!RadioStandLayout.TryAnchor(scenery.parentIslandIndex, out var local, out _) ||
                    !Camera.main || Vector3.Distance(Camera.main.transform.position,scenery.transform.TransformPoint(local))>100f) continue;
                var existing=vendors.Find(v=>v.Scenery==scenery);
                if(existing!=null)
                {
                    if(!existing.Stand) TryDisplay(existing);
                    else if(!existing.Area) TryMerchant(existing);
                    continue;
                }
                var vendor = new Vendor { Scenery=scenery, Slots=new Slot[RadioShopCatalog.StockKinds.Length] };
                for(int i=0;i<vendor.Slots.Length;i++) vendor.Slots[i]=new Slot{Kind=RadioShopCatalog.StockKinds[i],Index=i};
                vendors.Add(vendor);
                TryDisplay(vendor);
            }
            // Native scenery and merchant templates may stream in after the
            // loading flag clears. Retry briefly during that transition, then
            // return to the inexpensive steady-state scan interval.
            bool incomplete=false;
            foreach(var vendor in vendors)
                if(!vendor.Stand || !vendor.Area) { incomplete=true; break; }
            nextScan=Time.unscaledTime + (incomplete || Time.unscaledTime-sessionBeganAt < 10f ? .5f : 5f);
        }

        private void Update(Vendor vendor)
        {
            // Some native NPC components initialize after activation. Keep the
            // broad child triggers closed if one is re-enabled later.
            if(vendor.MerchantTriggers!=null)
                foreach(var collider in vendor.MerchantTriggers)
                    if(collider && collider.enabled) collider.enabled=false;
            bool merchantReady=world.ReadyToStageShop && ReadyToStock(vendor);
            bool open = vendor.Area.GetShopkeeper().gameObject.activeInHierarchy &&
                (vendor.Area.openAtNight || (Sun.sun && Sun.sun.localTime >= 7 && Sun.sun.localTime <= 18));
            bool saleReady=open && vendor.DisplayShown && world.ReadyForShop && PurchaseEnvironmentReady(vendor);
            bool attemptedStock=false;
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
                if(slot.Stock)
                {
                    slot.Stock.PurchaseEnabled=saleReady;
                    ProtectStock(slot.Stock);
                    slot.Stock.gameObject.SetActive(open && vendor.DisplayShown);
                    continue;
                }
                if(!merchantReady || !slot.Clock.Ready || !Camera.main || Vector3.Distance(Camera.main.transform.position,vendor.Area.transform.position)>100f) continue;
                // Failed stock creation is retried separately from simulation-time restocking.
                if(vendor.InitialStockAttempted && Time.unscaledTime < vendor.NextStockAttempt) continue;
                attemptedStock=true;
                TryStock(vendor,slot);
            }
            if(attemptedStock)
            {
                vendor.InitialStockAttempted=true;
                vendor.NextStockAttempt=Time.unscaledTime+5f;
            }
            if(!vendor.DisplayShown && (merchantReady ||
                (vendor.StageReadyAt >= 0f && Time.unscaledTime-vendor.StageReadyAt>=5f)))
            {
                // Attempt every initial slot in the same update, then reveal the
                // display as one group. A missing economy/slot must not hide the
                // location forever; the visible stall remains diagnostic.
                vendor.DisplayShown=true;
                vendor.Stand.SetVisible(true);
                SetNpcVisible(vendor.OwnedNpc,true);
                var saleTrigger=vendor.OwnedNpc ? vendor.OwnedNpc.GetComponent<SphereCollider>() : null;
                if(saleTrigger) saleTrigger.enabled=true;
                foreach(var slot in vendor.Slots)
                {
                    if(!slot.Stock) continue;
                    slot.Stock.PurchaseEnabled=open && world.ReadyForShop && PurchaseEnvironmentReady(vendor);
                    slot.Stock.gameObject.SetActive(open);
                }
            }
            else if(vendor.DisplayShown && open)
                foreach(var slot in vendor.Slots)
                    if(slot.Stock) slot.Stock.gameObject.SetActive(true);
            if(open && Time.unscaledTime>=nextScan)
            {
                int available=0;
                foreach(var slot in vendor.Slots) if(slot.Stock) available++;
                if(available!=vendor.LastAvailable)
                {
                    vendor.LastAvailable=available;
                    if(available<vendor.Slots.Length) warn("Radio shop at "+vendor.Scenery.name+" has "+available+" of "+vendor.Slots.Length+" devices. Remaining slots are waiting for stock creation or restocking");
                }
            }
        }

        private void TryDisplay(Vendor vendor)
        {
            if(!RadioShopPlacement.TryStand(vendor.Scenery,out var position,out var rotation,out string reason))
            { ReportPlacement(vendor,reason); return; }
            try
            {
                vendor.Stand=RadioShopStand.Create(vendor.Scenery.transform,position,rotation,vendor.Scenery.parentIslandIndex);
                vendor.Stand.SetVisible(false);
                Physics.SyncTransforms();
                if(reason.Length>0) ReportPlacement(vendor,reason);
                TryMerchant(vendor);
            }
            catch(Exception error)
            {
                Clear(vendor);
                for(int i=0;i<vendor.Slots.Length;i++) vendor.Slots[i]=new Slot{Kind=RadioShopCatalog.StockKinds[i],Index=i};
                ReportPlacement(vendor,error.Message);
            }
        }

        private void TryMerchant(Vendor vendor)
        {
            try
            {
                CreateMerchant(vendor);
                warn("Radio merchant at "+vendor.Scenery.name+" position "+vendor.Stand.transform.position+" is ready for native region and economy checks");
            }
            catch(Exception error)
            {
                if(vendor.Area) UnityEngine.Object.Destroy(vendor.Area.gameObject);
                if(vendor.OwnedNpc) UnityEngine.Object.Destroy(vendor.OwnedNpc);
                vendor.Area=null;
                vendor.OwnedNpc=null;
                vendor.MerchantTriggers=null;
                // Streaming can load the native appearance template after the
                // scenery. Show an incomplete diagnostic preview only after the
                // native item world has also had time to become ready.
                if(vendor.Stand && vendor.StageReadyAt >= 0f && Time.unscaledTime-vendor.StageReadyAt>=10f)
                { vendor.DisplayShown=true; vendor.Stand.SetVisible(true); }
                ReportPlacement(vendor,"merchant setup waiting: "+error.Message);
            }
        }

        private static void CreateMerchant(Vendor vendor)
        {
            GameObject areaObject=null;
            GameObject visual=null;
            try
            {
            var template=FindNativeMerchant(vendor.Scenery,vendor.Stand.transform.position);
            if(!template) throw new MissingComponentException("native shopkeeper template is not loaded yet");
            // Cloning gives the Radio merchant the game's NPC mesh, collider and shop
            // behaviour. The source vendor stays in place. Our transform and stock are owned.
            visual=UnityEngine.Object.Instantiate(template.gameObject,vendor.Scenery.transform);
            visual.SetActive(false);
            visual.name="Radio shopkeeper";
            visual.transform.position=vendor.Stand.transform.TransformPoint(RadioStandLayout.MerchantOffset);
            visual.transform.rotation=vendor.Stand.transform.rotation*Quaternion.Euler(0,180,0);
            var keeper=visual.GetComponent<Shopkeeper>();
            if(!keeper) throw new MissingComponentException("native shopkeeper clone lost Shopkeeper");
            // The native keeper's root trigger owns the held-good resale popup.
            // Its much larger Modular NPC child trigger reaches adjacent stalls.
            // Retain a counter-sized root trigger and close the child triggers.
            vendor.MerchantTriggers=ConfigureMerchantTriggers(visual,vendor.Scenery.parentIslandIndex);
            var saleTrigger=visual.GetComponent<SphereCollider>();
            if(saleTrigger) saleTrigger.enabled=vendor.DisplayShown;
            var home=typeof(Shopkeeper).GetField("homePos",BindingFlags.Instance|BindingFlags.NonPublic);
            if(home==null || home.FieldType!=typeof(Transform)) throw new MissingFieldException("Shopkeeper.homePos changed");
            home.SetValue(keeper,visual.transform);
            var regionField=typeof(Shopkeeper).GetField("parentRegion",BindingFlags.Instance|BindingFlags.NonPublic);
            if(regionField==null || regionField.FieldType!=typeof(Region)) throw new MissingFieldException("Shopkeeper.parentRegion changed");
            var region=regionField.GetValue(template) as Region;
            if(!region || !CorrectRegion(region,vendor.Scenery.parentIslandIndex))
                region=FindRegionAt(visual.transform.position,vendor.Scenery.parentIslandIndex);
            if(region) regionField.SetValue(keeper,region);
            areaObject=new GameObject("Radio merchant area");
            // Keep the trigger inactive until its keeper and stock list are established.
            // Native OnTriggerEnter reads itemsForSale without a null check, so the
            // list and keeper must both exist before any collider can enter.
            areaObject.SetActive(false);
            areaObject.transform.SetParent(vendor.Stand.transform,false);
            areaObject.transform.localPosition=Vector3.zero;
            areaObject.transform.position=vendor.Stand.transform.position;
            var areaBody=areaObject.AddComponent<Rigidbody>();
            areaBody.isKinematic=true;
            areaBody.useGravity=false;
            var areaCollider=areaObject.AddComponent<BoxCollider>();
            areaCollider.isTrigger=true;
            areaCollider.center=new Vector3(0,1.15f,0);
            areaCollider.size=new Vector3(2.65f,2.3f,1.5f);
            var area=areaObject.AddComponent<ShopArea>();
            var keeperField=typeof(ShopArea).GetField("keeper",BindingFlags.Instance|BindingFlags.NonPublic);
            if(keeperField==null || keeperField.FieldType!=typeof(Shopkeeper)) throw new MissingFieldException("ShopArea.keeper changed");
            keeperField.SetValue(area,keeper);
            keeper.RegisterShop(area);
            area.itemsForSale=new List<ShipItem>();
            vendor.Area=area;
            vendor.OwnedNpc=visual;
            SetNpcVisible(visual,vendor.DisplayShown);
            visual.SetActive(true);
            areaObject.SetActive(true);
            }
            catch
            {
                if(areaObject) UnityEngine.Object.Destroy(areaObject);
                if(visual) UnityEngine.Object.Destroy(visual);
                throw;
            }
        }

        private static void SetNpcVisible(GameObject npc,bool visible)
        {
            if(!npc) return;
            foreach(var renderer in npc.GetComponentsInChildren<Renderer>(true)) renderer.enabled=visible;
        }

        private static Collider[] ConfigureMerchantTriggers(GameObject npc,int islandIndex)
        {
            var disabled=new List<Collider>();
            bool rootSaleTrigger=false;
            // Gold Rock's wider counter needs the native 2 m reach to accept
            // held goods from its customer edge. Other stalls keep 1.75 m.
            float saleRadius=islandIndex==1 ? 2f : 1.75f;
            foreach(var collider in npc.GetComponentsInChildren<Collider>(true))
            {
                if(!collider.isTrigger) continue;
                if(collider.transform==npc.transform && collider is SphereCollider sphere && !rootSaleTrigger)
                {
                    sphere.radius=Mathf.Min(sphere.radius,saleRadius);
                    rootSaleTrigger=true;
                    continue;
                }
                collider.enabled=false;
                disabled.Add(collider);
            }
            if(!rootSaleTrigger)
                throw new MissingComponentException("native shopkeeper root sale trigger contract changed");
            return disabled.ToArray();
        }

        private static Shopkeeper FindNativeMerchant(IslandSceneryScene scenery,Vector3 standPosition)
        {
            Shopkeeper best=null;
            float bestDistance=30f;
            // Native ShopArea can deactivate its keeper at night or while offscreen.
            // Search this scenery including inactive children, while retaining the
            // direct-child and built-in-name filters below.
            foreach(var keeper in scenery.GetComponentsInChildren<Shopkeeper>(true))
                if(keeper && keeper.transform.parent==scenery.transform &&
                   NativeKeeperName(keeper.name) &&
                   keeper.GetComponent<Renderer>())
                {
                    float distance=Vector3.Distance(standPosition,keeper.transform.position);
                    if(distance<bestDistance) { best=keeper; bestDistance=distance; }
                }
            return best;
        }

        private static bool NativeKeeperName(string name)
        {
            if(string.Equals(name,"shopkeeper",StringComparison.OrdinalIgnoreCase)) return true;
            const string prefix="shopkeeper (";
            return name!=null && name.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(")",StringComparison.Ordinal) &&
                int.TryParse(name.Substring(prefix.Length,name.Length-prefix.Length-1),out _);
        }

        private static bool CorrectRegion(Region region,int island)
        {
            if(!region) return false;
            return island==1 ? region.portRegion==PortRegion.alankh :
                island==9 ? region.portRegion==PortRegion.emerald :
                island==15 && region.portRegion==PortRegion.medi;
        }

        private static Region FindRegionAt(Vector3 position,int island)
        {
            Region found=null;
            foreach(var collider in Physics.OverlapSphere(position,.4f,~0,QueryTriggerInteraction.Collide))
            {
                var region=collider ? collider.GetComponentInParent<Region>() : null;
                if(!CorrectRegion(region,island)) continue;
                if(found && found!=region) return null;
                found=region;
            }
            return found;
        }

        private void ReportPlacement(Vendor vendor,string reason)
        {
            if(!vendor.ReportedReasons.Add(reason)) return;
            warn("Radio display at "+vendor.Scenery.name+" has placement diagnostic: "+reason);
        }

        private bool TryStock(Vendor vendor, Slot slot)
        {
            if (!ReadyToStock(vendor)) return false;
            if(!RadioShopPlacement.SlotClear(vendor.Stand,slot.Index,slot.Kind,out string reason))
            { ReportPlacement(vendor,"stock slot blocked by "+reason); return false; }
            if(reason!=null) ReportPlacement(vendor,"stock slot "+slot.Index+" overlaps "+reason);
            var position=vendor.Stand.transform.TransformPoint(RadioStandLayout.Slot(vendor.Scenery.parentIslandIndex,slot.Index,vendor.Stand.UsesNativeGoldRockCounter));
            var rotation=vendor.Stand.transform.rotation * RadioStandLayout.SlotRotation(slot.Kind);
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
                stock.PurchaseEnabled=vendor.DisplayShown && world.ReadyForShop && PurchaseEnvironmentReady(vendor);
                controller.GetComponent<ShipItem>().AddToShop(vendor.Area);
                controller.SetShopStock(true);
                slot.Anchor=anchor;
                slot.Stock=stock;
                slot.Clock.Spawned();
                stock.gameObject.SetActive(false);
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

        private bool ReadyToStock(Vendor vendor)
        {
            if(!supported || !vendor.Area) return false;
            var keeper=vendor.Area.GetShopkeeper();
            if(!keeper) return false;
            var economy=typeof(Shopkeeper).GetField("economy",BindingFlags.Instance|BindingFlags.NonPublic);
            var parentRegion=typeof(Shopkeeper).GetField("parentRegion",BindingFlags.Instance|BindingFlags.NonPublic);
            var region=parentRegion?.GetValue(keeper) as Region;
            if(!CorrectRegion(region,vendor.Scenery.parentIslandIndex))
            {
                region=FindRegionAt(keeper.transform.position,vendor.Scenery.parentIslandIndex);
                if(region) parentRegion?.SetValue(keeper,region);
            }
            return CorrectRegion(region,vendor.Scenery.parentIslandIndex) &&
                economy?.GetValue(keeper)!=null && NativeShopContract.KeeperReady(vendor.Area);
        }

        private bool PurchaseEnvironmentReady(Vendor vendor)
        {
            if(!ReadyToStock(vendor) || vendor.Area.itemsForSale==null) return false;
            int currency=vendor.Scenery.parentIslandIndex==1 ? 0 : vendor.Scenery.parentIslandIndex==9 ? 1 : 2;
            return currency>=0 && currency<3 && PlayerGold.currency!=null && PlayerGold.currency.Length>currency &&
                PlayerReputation.retailDiscounts!=null && PlayerReputation.retailDiscounts.Length>currency &&
                CurrencyMarket.instance && UISoundPlayer.instance && BuyItemUI.instance &&
                DayLogs.instance && DayLogs.instance.dayLogs!=null && DayLogs.instance.dayLogs.Length>currency &&
                DayLogs.instance.dayLogs[currency]!=null && MoneyNotification.instance;
        }

        private static bool BeforeAreaEnter(ShopArea __instance, Collider other)
        {
            if(active==null) return true;
            var otherKeeper=other ? other.GetComponentInParent<Shopkeeper>() : null;
            var item=other ? other.GetComponentInParent<ShipItem>() : null;
            var stock=item ? item.GetComponent<RadioShopStock>() : null;
            bool unsoldRadioStock=stock && stock.Service==active && !(stock.Purchased && item.sold);
            if(!active.Owns(__instance))
                return !(otherKeeper && active.Owns(otherKeeper)) && !unsoldRadioStock;
            if(otherKeeper) return otherKeeper==__instance.GetShopkeeper();
            return !item || (stock && stock.Purchased && item.sold) || (unsoldRadioStock && stock.Shop==__instance);
        }

        private static bool BeforeKeeperEnter(Shopkeeper __instance, Collider other)
        {
            if(active==null) return true;
            var item=other ? other.GetComponentInParent<ShipItem>() : null;
            var stock=item ? item.GetComponent<RadioShopStock>() : null;
            bool unsoldRadioStock=stock && stock.Service==active && !(stock.Purchased && item.sold);
            if(!active.Owns(__instance)) return !unsoldRadioStock;
            if(!item) return true; // Keep native Region entry for local currency.
            if(unsoldRadioStock) return stock.Shop && stock.Shop.GetShopkeeper()==__instance;
            // Native Shopkeeper.OnTriggerEnter offers resale only for held, sold
            // goods. Keep this scoped to our small counter trigger and ready
            // economy so a neighboring merchant remains the active seller.
            foreach(var vendor in active.vendors)
                if(vendor.OwnedNpc && vendor.OwnedNpc.GetComponent<Shopkeeper>()==__instance)
                    return item.sold && item.held && active.world.ReadyForShop && active.ReadyToStock(vendor) &&
                        Camera.main && Vector3.Distance(Camera.main.transform.position,vendor.Stand.transform.position)<=3.25f;
            return false;
        }

        private bool Owns(ShopArea area)
        {
            foreach(var vendor in vendors) if(vendor.Area==area) return true;
            return false;
        }

        private bool Owns(Shopkeeper keeper)
        {
            foreach(var vendor in vendors) if(vendor.OwnedNpc && vendor.OwnedNpc.GetComponent<Shopkeeper>()==keeper) return true;
            return false;
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
            if(!stock.PurchaseEnabled)
            {
                if(NotificationUi.instance) NotificationUi.instance.ShowNotification("Radio merchant is still getting ready");
                return false;
            }
            if(active==null || !active.CanSell(stock) || stock.Service!=active || !stock.Shop || stock.Shop.GetShopkeeper()!=__instance || stock.Reserved || stock.Purchased ||
                !active.world.ReservePurchase(stock.Controller))
            {
                if(NotificationUi.instance) NotificationUi.instance.ShowNotification("Radio purchase unavailable. Try again after loading or saving");
                return false;
            }
            stock.Reserved=true;
            return true;
        }

        private bool CanSell(RadioShopStock stock)
        {
            foreach(var vendor in vendors)
                if(vendor.Area==stock.Shop)
                    return Camera.main && vendor.Stand &&
                        Vector3.Distance(Camera.main.transform.position,vendor.Stand.transform.position)<=4f &&
                        PurchaseEnvironmentReady(vendor) && stock.Anchor && stock.Anchor.GetComponent<ShopItemSpawner>() &&
                        stock.Controller && stock.Controller.GetComponent<ShipItem>().value>0;
            // Only this service creates marked stock, and teardown disables purchases
            // before destroying it. This also covers a callback already in progress.
            return stock.PurchaseEnabled && stock.Service==this && stock.Shop && stock.Controller;
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
                    slot.Stock.PurchaseEnabled=false;
                    if(vendor.Area) vendor.Area.itemsForSale.Remove(slot.Stock.GetComponent<ShipItem>());
                    UnityEngine.Object.Destroy(slot.Stock.gameObject);
                }
                if(slot.Anchor) UnityEngine.Object.Destroy(slot.Anchor);
            }
            if(vendor.Stand) UnityEngine.Object.Destroy(vendor.Stand.gameObject);
            if(vendor.Area) UnityEngine.Object.Destroy(vendor.Area.gameObject);
            if(vendor.OwnedNpc) UnityEngine.Object.Destroy(vendor.OwnedNpc);
            vendor.Stand=null;
            vendor.Area=null;
            vendor.OwnedNpc=null;
            vendor.MerchantTriggers=null;
            vendor.DisplayShown=false;
            vendor.InitialStockAttempted=false;
            vendor.StageReadyAt=-1f;
            vendor.NextStockAttempt=0f;
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
