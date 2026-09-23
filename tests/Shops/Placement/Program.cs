using System;
using SailwindRadio.Shops;
using SailwindRadio.Physical;
using UnityEngine;

static class Program
{
    private static int checks;
    private static void Check(bool ok, string name) { checks++; if (!ok) throw new Exception(name); }
    private static int Main()
    {
        try { Run(); Console.WriteLine(checks + " authored preview placement checks passed with synthetic physics. Live coordinates remain unverified."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Run()
    {
        CheckMarker();
        var sceneTransform = new Transform { position = new Vector3(0, 0, 0) };
        var scene = sceneTransform.Add<IslandSceneryScene>();
        foreach (int island in new[] { 1, 9, 15 })
        {
            scene.parentIslandIndex = island;
            Check(RadioStandLayout.TryAnchor(island, out var local, out _), "capital anchor " + island);
            Check(RadioShopPlacement.TryStand(scene, out var position, out _, out _), "capital preview visible " + island);
            Check(MathF.Abs(position.x - local.x) < .001f && MathF.Abs(position.z - local.z) < .001f, "independent scenery-local anchor " + island);
        }
        scene.parentIslandIndex = 10;
        Check(!RadioShopPlacement.TryStand(scene, out _, out _, out _), "no noncapital preview");
        // Current serialized landmarks share each IslandSceneryScene root. Gold Rock has
        // half scale, while Dragon Cliffs and Fort Aestrin use unit scale.
        RadioStandLayout.TryAnchor(1, out var gold, out _);
        scene.parentIslandIndex = 1;sceneTransform.lossyScale = new Vector3(.5f,.5f,.5f);
        Check(RadioShopPlacement.TryStand(scene, out var goldWorld, out _, out _) &&
            MathF.Abs(goldWorld.x-gold.x*.5f)<.001f && MathF.Abs(goldWorld.z-gold.z*.5f)<.001f,
            "Gold Rock scenery scale applied exactly once");
        Check(gold.x>1593f && gold.x<1597f && gold.z< -443f && gold.z> -448f && MathF.Abs(gold.y-3.1f)<.2f,
            "Gold Rock candidate is beside the empty waterfront stall rather than down the market path");
        RadioStandLayout.TryAnchor(15, out var fort, out _);
        Check(fort.x<-117f && fort.x>-121f && MathF.Abs(fort.z-37f)<.2f,
            "Fort candidate moves toward foreground while leaving the inn entrance clear");
        sceneTransform.lossyScale=Vector3.one;
        scene.parentIslandIndex = 9;
        RadioStandLayout.TryAnchor(9, out var dragon, out _);
        Check(dragon.x>-106.8f && dragon.x<-102.4f && MathF.Abs(dragon.z+530f)<1f,
            "Dragon candidate between serialized tree landmarks");
        RadioStandLayout.TryAnchor(9, out _, out var dragonYaw);
        Check(MathF.Abs(dragonYaw-45f)<.01f,"approved Dragon Cliffs orientation preserved");
        var blocker = new Transform { name = "player crate" }.Add<Collider>();
        Physics.Overlap = _ => new[] { blocker };
        Check(RadioShopPlacement.TryStand(scene, out var first, out var facing, out var reason) && reason.Contains("player crate") && reason.Contains("footprint"), "overlap diagnosed without suppressing preview");
        Physics.Overlap = _ => Array.Empty<Collider>();
        var old = Physics.Support;
        Physics.Support = (_, _) => null;
        Check(RadioShopPlacement.TryStand(scene, out _, out _, out reason) && reason.Contains("ground"), "missing ground uses authored preview height and reports gap");
        Physics.Support = (_, _) => new RaycastHit { collider=Physics.Ground, point=new Vector3(dragon.x,dragon.y+.6f,dragon.z), normal=Vector3.up };
        Check(RadioShopPlacement.TryStand(scene, out var unlifted, out _, out reason) &&
            MathF.Abs(unlifted.y-(dragon.y+.02f))<.001f && reason.Contains("differs"),
            "nearby static prop cannot lift authored preview");
        Physics.Support = old;
        var stand = new Transform { position = first, rotation = facing }.Add<RadioShopStand>();
        var board = new Transform { parent = stand.transform }.Add<Collider>();
        Physics.Overlap = _ => new[] { board };
        Check(RadioShopPlacement.SlotClear(stand, 0, 3, out reason) && reason == null, "own structure ignored");
        board.transform.Add<ShipItem>();
        Check(RadioShopPlacement.SlotClear(stand, 0, 3, out reason) && reason != null, "item overlap visible in slot diagnostic");
        Physics.Overlap = _ => Array.Empty<Collider>();
        for (int i = 0; i < RadioStandLayout.Slots.Length; i++)
        {
            int kind = RadioShopCatalog.StockKinds[i];
            var size = RadioDevice.Size(kind);
            if (i > 0)
            {
                // Every device base now rests just above one flat table top.
                float tableTop = .78f + .075f * .5f;
                float clearance = RadioStandLayout.Slots[i].y - tableTop;
                Check(clearance >= 0f && clearance <= .02f,
                    "device bottom rests just above flat top " + i);
            }
            var center = RadioStandLayout.Slots[i] + RadioStandLayout.SlotRotation(kind) * RadioDevice.Center(kind);
            var delta = center - RadioStandLayout.EnvelopeCenter;
            Check(MathF.Abs(delta.x) + size.x * .5f <= RadioStandLayout.EnvelopeHalf.x &&
                MathF.Abs(delta.y) + size.y * .5f <= RadioStandLayout.EnvelopeHalf.y &&
                MathF.Abs(delta.z) + size.z * .5f <= RadioStandLayout.EnvelopeHalf.z, "device inside preview envelope " + i);
            for (int j = 0; j < i; j++)
            {
                var otherSize = RadioDevice.Size(RadioShopCatalog.StockKinds[j]);
                var other = RadioStandLayout.Slots[j] + RadioStandLayout.SlotRotation(RadioShopCatalog.StockKinds[j]) * RadioDevice.Center(RadioShopCatalog.StockKinds[j]);
                var d = center - other;
                Check(MathF.Abs(d.x) >= (size.x + otherSize.x) * .5f ||
                    MathF.Abs(d.y) >= (size.y + otherSize.y) * .5f ||
                    MathF.Abs(d.z) >= (size.z + otherSize.z) * .5f, "device pair separate " + i + "/" + j);
            }
        }
        Check(RadioStandLayout.Slots[0].y == 0 && RadioStandLayout.Slots[1].x < 0 && RadioStandLayout.Slots[2].x > 0,
            "woofer on ground with large speakers at table ends");
        Check(RadioStandLayout.Slots[3].x<0 && RadioStandLayout.Slots[4].x>0 &&
            MathF.Abs(RadioStandLayout.Slots[3].x)<MathF.Abs(RadioStandLayout.Slots[1].x) &&
            RadioStandLayout.Slots[5].x==RadioStandLayout.Slots[3].x &&
            RadioStandLayout.Slots[6].x==RadioStandLayout.Slots[4].x &&
            RadioStandLayout.Slots[5].z<RadioStandLayout.Slots[3].z &&
            RadioStandLayout.Slots[5].y==RadioStandLayout.Slots[3].y,
            "upright radios at back with shorter small speakers in front on one flat surface");
        var face=RadioStandLayout.SlotRotation(0) * new Vector3(0,0,-1);
        Check(MathF.Abs(face.y)<.01f && face.z<-.99f,"radio front remains upright toward customer");
    }

    private static void CheckMarker()
    {
        Check(!RadioShopPositionMarker.TryDescribe(null, Array.Empty<IslandSceneryScene>(), out var message) &&
            message.Contains("player"), "marker explains missing observer");
        var observer = new Transform();
        Check(!RadioShopPositionMarker.TryDescribe(observer, Array.Empty<IslandSceneryScene>(), out message) &&
            message.Contains("capital"), "marker explains missing capital scenery");
        var noncapital = new Transform().Add<IslandSceneryScene>();
        noncapital.parentIslandIndex = 10;
        Check(!RadioShopPositionMarker.TryDescribe(observer, new[] { noncapital }, out _),
            "marker excludes noncapital scenery");

        var goldRoot = new Transform {
            position = new Vector3(50, 2, 100), rotation = Quaternion.Euler(0, 90, 0),
            lossyScale = new Vector3(.5f, .5f, .5f)
        };
        var goldScene = goldRoot.Add<IslandSceneryScene>();
        goldScene.parentIslandIndex = 1;
        RadioStandLayout.TryAnchor(1, out var anchor, out _);
        Vector3 intendedLocal = anchor + new Vector3(1.25f, .50f, -.75f);
        observer.position = goldRoot.TransformPoint(intendedLocal);
        observer.rotation = goldRoot.rotation * Quaternion.Euler(0, -45, 0);
        var distantScene = new Transform { position = new Vector3(5000, 0, 0) }.Add<IslandSceneryScene>();
        distantScene.parentIslandIndex = 15;
        Check(RadioShopPositionMarker.TryDescribe(observer, new[] { noncapital, distantScene, goldScene }, out message) &&
            message.Contains("Gold Rock City (1)") && message.Contains("(1596.25, 3.60, -446.25)") &&
            message.Contains("facing yaw -45.0, stand yaw 135.0"),
            "marker handles scaled and rotated scenery and converts customer facing to stand yaw");
        observer.rotation = goldRoot.rotation * Quaternion.Euler(0, 135, 0);
        Check(RadioShopPositionMarker.TryDescribe(observer, new[] { goldScene }, out message) &&
            message.Contains("facing yaw 135.0, stand yaw -45.0"),
            "suggested stand yaw wraps into signed range");
        goldRoot.gameObject.activeInHierarchy = false;
        Check(!RadioShopPositionMarker.TryDescribe(observer, new[] { goldScene }, out message),
            "marker ignores inactive scenery");
        goldRoot.gameObject.activeInHierarchy = true;
        observer.position = goldRoot.TransformPoint(intendedLocal) + new Vector3(120, 0, 0);
        Check(!RadioShopPositionMarker.TryDescribe(observer, new[] { goldScene }, out message) &&
            message.Contains("capital"), "marker rejects distant loaded capital");
    }
}
