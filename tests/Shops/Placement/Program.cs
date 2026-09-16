using System;
using SailwindRadio.Shops;
using SailwindRadio.Physical;
using UnityEngine;
static class Program
{
 static int checks;
 static void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);}
 static int Main(){try{Run();Console.WriteLine(checks+" production stand placement/catalog/layout checks passed with synthetic physics. Live display appearance remains unverified.");return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
 static void Run()
 {
  var root=new Transform();root.Add<IslandSceneryScene>().parentIslandIndex=9;
  var t=new Transform{name="shop area (3)",parent=root,localPosition=new Vector3(-90.43f,4.65f,-544.32f),localScale=new Vector3(6.47693f,4.90124f,4.98642f),lossyScale=new Vector3(6.47693f,4.90124f,4.98642f),position=new Vector3(0,1.04f,0)};
  var area=t.Add<ShopArea>();var box=t.Add<BoxCollider>();box.isTrigger=true;area.Keeper=new Transform().Add<Shopkeeper>();
  Check(RadioShopCatalog.Matches(area),"exact DragonCliffs vendor accepted");
  t.name="shop area (4)";Check(!RadioShopCatalog.Matches(area),"neighbor vendor rejected");t.name="shop area (3)";
  root.GetComponent<IslandSceneryScene>().parentIslandIndex=10;Check(!RadioShopCatalog.Matches(area),"wrong island rejected");root.GetComponent<IslandSceneryScene>().parentIslandIndex=9;
  box.size=new Vector3(2,1,1);Check(!RadioShopCatalog.Matches(area),"changed native shop volume rejected");box.size=Vector3.one;
  Check(RadioShopCatalog.BasePrice(0)==789&&RadioShopCatalog.BasePrice(1)==526&&RadioShopCatalog.BasePrice(2)==1579&&RadioShopCatalog.BasePrice(3)==2632,"approved native base prices");
  foreach(int island in new[]{1,9,15})Check(RadioStandLayout.TryAnchor(island,out _,out _),"authored capital anchor"+island);
  Check(!RadioStandLayout.TryAnchor(10,out _,out _),"noncapital has no anchor");
  Check(RadioShopPlacement.TryStand(area,out var first,out var facing,out _),"complete clear supported display accepted");
  Check(MathF.Abs(first.y-.02f)<.0001,"floor offset");
  Check(MathF.Abs(first.x-.35f)<.0001&&MathF.Abs(first.z-2.35f)<.0001,"authored offsets measured in metres not shop scale");
  Check(RadioShopPlacement.TryStand(area,out var again,out _,out _)&&(again-first).sqrMagnitude<.0001,"deterministic anchor");
  Check((facing*new Vector3(0,0,-1)).z>.99f,"front faces customer side");
  var blocker=new Transform{name="player crate"}.Add<Collider>();Physics.Overlap=_=>new[]{blocker};
  Check(!RadioShopPlacement.TryStand(area,out _,out _,out var why)&&why.Contains("player crate"),"full display collision rejected with useful reason");
  blocker.isTrigger=true;Check(RadioShopPlacement.TryStand(area,out _,out _,out _),"generic region trigger ignored");
  blocker.transform.Add<ShipItem>();Check(!RadioShopPlacement.TryStand(area,out _,out _,out _),"existing stock trigger blocks stand");
  Physics.Overlap=p=>p.z>3?new[]{blocker}:Array.Empty<Collider>();Check(!RadioShopPlacement.TryStand(area,out _,out _,out why)&&why.Contains("approach"),"customer walkway remains clear");
  Physics.Overlap=_=>Array.Empty<Collider>();var original=Physics.Support;
  Physics.Support=(p,d)=>null;Check(!RadioShopPlacement.TryStand(area,out _,out _,out _),"missing ground rejected");Physics.Support=original;
  Physics.Ground.attachedRigidbody=new Rigidbody();Check(!RadioShopPlacement.TryStand(area,out _,out _,out _),"moving support rejected");Physics.Ground.attachedRigidbody=null;
  int samples=0;Physics.Support=(p,d)=>{samples++;if(samples>1)return null;return original(p,d);};
  Check(!RadioShopPlacement.TryStand(area,out _,out _,out why)&&why.Contains("beneath"),"all stand and ground woofer corners require support");
  Physics.Support=(p,d)=>{var h=original(p,d);if(h.HasValue){var x=h.Value;x.normal=new Vector3(0,.5f,.5f);return x;}return h;};Check(!RadioShopPlacement.TryStand(area,out _,out _,out _),"sloped support rejected");Physics.Support=original;
  samples=0;Physics.Support=(p,d)=>{samples++;var h=original(p,d);if(samples>1&&h.HasValue){var x=h.Value;x.point.y=.1f;return x;}return h;};Check(!RadioShopPlacement.TryStand(area,out _,out _,out _),"uneven supports rejected");Physics.Support=original;
  Physics.Rays=0;Check(RadioShopPlacement.TryStand(area,out _,out _,out _)&&Physics.Rays==9,"bounded nine ground checks replace grid scan");
  var standTransform=new Transform{position=first,rotation=facing};var stand=standTransform.Add<RadioShopStand>();var board=new Transform{parent=standTransform}.Add<Collider>();Physics.Overlap=_=>new[]{board};
  Check(RadioShopPlacement.SlotClear(stand,0,3,out _),"restock ignores own bare boards");
  board.transform.Add<ShipItem>();Check(!RadioShopPlacement.SlotClear(stand,0,3,out _),"restock never ignores item even under stand hierarchy");Physics.Overlap=_=>Array.Empty<Collider>();
  for(int i=0;i<7;i++)
  {
   Check(RadioShopPlacement.SlotClear(stand,i,RadioShopCatalog.StockKinds[i],out _),"clear restock slot"+i);
   var size=RadioDevice.Size(RadioShopCatalog.StockKinds[i]);var center=RadioStandLayout.Slots[i]+RadioDevice.Center(RadioShopCatalog.StockKinds[i]);
   var delta=center-RadioStandLayout.EnvelopeCenter;
   Check(MathF.Abs(delta.x)+size.x*.5f<=RadioStandLayout.EnvelopeHalf.x&&MathF.Abs(delta.y)+size.y*.5f<=RadioStandLayout.EnvelopeHalf.y&&MathF.Abs(delta.z)+size.z*.5f<=RadioStandLayout.EnvelopeHalf.z,"slot fully within validated envelope"+i);
   var eye=new Vector3(center.x,1.6f,-1.1f);Check((eye-center).magnitude<1.8f,"slot within standing front reach"+i);
   for(int j=0;j<i;j++)
   {
    var b=RadioDevice.Size(RadioShopCatalog.StockKinds[j]);var other=RadioStandLayout.Slots[j]+RadioDevice.Center(RadioShopCatalog.StockKinds[j]);var d=center-other;
    Check(MathF.Abs(d.x)>=(size.x+b.x)*.5f||MathF.Abs(d.y)>=(size.y+b.y)*.5f||MathF.Abs(d.z)>=(size.z+b.z)*.5f,"device bodies never overlap "+i+"/"+j);
   }
  }
  Check(RadioStandLayout.Slots[0].y==0,"woofer rests on ground beside shelf");
 }
}
