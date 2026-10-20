using System;
using System.Linq;
using SailwindRadio.Shops;
using SailwindRadio.Physical;
using UnityEngine;

static class Program
{
    private static int checks;
    private static void Check(bool ok, string name) { checks++; if (!ok) throw new Exception(name); }
    // The approved slot layout was measured with the walnut set. Regional stock sizes await the stand rearrangement.
    private static int WalnutKind(int slot) => SailwindRadio.DeviceKind.Of(0, RadioShopCatalog.StockRoles[slot]);
    private static int Main()
    {
        try { Run(); Console.WriteLine(checks + " authored preview placement checks passed with synthetic physics. Live coordinates remain unverified."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Run()
    {
        var sceneTransform = new Transform { position = new Vector3(0, 0, 0) };
        var scene = sceneTransform.Add<IslandSceneryScene>();
        foreach (int island in new[] { 1, 9, 15, 25 })
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
        RadioStandLayout.TryAnchor(1, out var gold, out var goldYaw);
        scene.parentIslandIndex = 1;sceneTransform.lossyScale = new Vector3(.5f,.5f,.5f);
        Check(RadioShopPlacement.TryStand(scene, out var goldWorld, out var goldRotation, out _) &&
            MathF.Abs(goldWorld.x-gold.x*.5f)<.001f && MathF.Abs(goldWorld.z-gold.z*.5f)<.001f,
            "Gold Rock scenery scale applied exactly once");
        // The owned stand cancels the scenery's half scale, so its 1.04 m
        // keeper offset is 2.08 m in scenery-local Gold Rock coordinates.
        var goldNpcLocal = gold + (Quaternion.Euler(0, goldYaw, 0) * RadioStandLayout.MerchantOffset) * 2f;
        var goldForward = Quaternion.Euler(0, -121.6f, 0) * Vector3.forward;
        var goldRight = Quaternion.Euler(0, -121.6f, 0) * new Vector3(1, 0, 0);
        var priorGoldNpcLocal = new Vector3(1596.7175f, gold.y, -436.2411f) +
            Quaternion.Euler(0, 58.4f, 0) * RadioStandLayout.MerchantOffset * 2f;
        var goldNudgeWorld = (goldNpcLocal - priorGoldNpcLocal) * .5f;
        Check(MathF.Abs(Vector3.Dot(goldNudgeWorld, goldForward) - .0625f) < .001f &&
            MathF.Abs(Vector3.Dot(goldNudgeWorld, goldRight)) < .001f &&
            MathF.Abs(gold.y-3.1f)<.001f && MathF.Abs(goldYaw-58.4f)<.001f,
            "Gold Rock merchant and stand advance another sixteenth metre at their approved yaw");
        var goldNpcWorld = goldWorld + goldRotation * RadioStandLayout.MerchantOffset;
        Check(MathF.Abs(goldNpcWorld.x-goldNpcLocal.x*.5f)<.001f && MathF.Abs(goldNpcWorld.z-goldNpcLocal.z*.5f)<.001f,
            "Gold Rock merchant offset respects half-scale scenery");
        var desiredGoldForward = goldForward;
        var actualGoldForward = Quaternion.Euler(0, goldYaw + 180f, 0) * Vector3.forward;
        Check(Vector3.Dot(desiredGoldForward, actualGoldForward) > .9999f,
            "Gold Rock merchant faces the player's logged direction rather than the wall");
        Check(MathF.Abs(RadioStandLayout.Slot(1, 0, true).x - 1.9f) < .001f &&
            MathF.Abs(RadioStandLayout.Slot(1, 0, true).y + .04f) < .001f &&
            MathF.Abs(RadioStandLayout.Slot(9, 0, false).y) < .001f &&
            MathF.Abs(RadioStandLayout.Slot(1, 3, true).y - .975f) < .001f &&
            MathF.Abs(RadioStandLayout.Slot(1, 3, false).y - .82f) < .001f &&
            MathF.Abs(RadioStandLayout.Slot(9, 0, false).x - 1.7f) < .001f &&
            MathF.Abs(RadioStandLayout.Slot(15, 3, false).y - .82f) < .001f,
            "Gold Rock stock follows native counter height or generic fallback without moving approved stalls");
        Check(MathF.Abs(RadioStandLayout.Slot(15, 3, false).x + .35f) < .0001f && MathF.Abs(RadioStandLayout.Slot(15, 4, false).x - .35f) < .0001f &&
            RadioStandLayout.Slot(15, 5, false).Equals(RadioStandLayout.Slots[5]) && RadioStandLayout.Slot(15, 6, false).Equals(RadioStandLayout.Slots[6]) &&
            RadioStandLayout.Slot(9, 3, false).Equals(RadioStandLayout.Slots[3]) && RadioStandLayout.Slot(9, 4, false).Equals(RadioStandLayout.Slots[4]) &&
            MathF.Abs(RadioStandLayout.Slot(1, 4, false).x - .32f) < .0001f,
            "Fort Aestrin radios stand 3 cm further out each while the other stalls and slots keep their places");
        var originalGoldSupport = Physics.Support;
        Physics.Support = (p, _) => new RaycastHit { collider = Physics.Ground,
            point = new Vector3(p.x, 1.39f, p.z), normal = Vector3.up };
        Check(RadioShopPlacement.TryStand(scene, out var groundedGold, out _, out var goldReason) &&
            MathF.Abs(groundedGold.y - 1.29f) < .001f && !goldReason.Contains("differs"),
            "Gold Rock stand and keeper settle .10 m below the dock support");
        Physics.Support = (p, _) => new RaycastHit { collider = Physics.Ground,
            point = new Vector3(p.x, 1.8f, p.z), normal = Vector3.up };
        Check(RadioShopPlacement.TryStand(scene, out var unraisedGold, out _, out goldReason) &&
            MathF.Abs(unraisedGold.y - 1.47f) < .001f && goldReason.Contains("differs"),
            "higher prop cannot lift Gold Rock stand and keeper");
        Physics.Support = originalGoldSupport;
        RadioStandLayout.TryAnchor(15, out var fort, out var fortYaw);
        var fortNpcLocal = fort + Quaternion.Euler(0, fortYaw, 0) * RadioStandLayout.MerchantOffset;
        Check(MathF.Abs(fortNpcLocal.x+136.97f)<.001f && MathF.Abs(fortNpcLocal.z-42.91f)<.001f &&
            MathF.Abs(fort.y-2.3f)<.001f && MathF.Abs(fortYaw-180f)<.001f,
            "Fort merchant moves .30 m forward with approved facing unchanged");
        sceneTransform.lossyScale=Vector3.one;
        scene.parentIslandIndex = 15;
        var originalSupport = Physics.Support;
        Physics.Support = (p, _) => new RaycastHit { collider = Physics.Ground,
            point = new Vector3(p.x, 2.103f, p.z), normal = Vector3.up };
        Check(RadioShopPlacement.TryStand(scene, out var groundedFort, out _, out var fortReason) &&
            MathF.Abs(groundedFort.y - 2.103f) < .001f && !fortReason.Contains("ground"),
            "Fort stand lowers to measured static support instead of hovering .197 m above it");
        Physics.Support = (p, _) => new RaycastHit { collider = Physics.Ground,
            point = new Vector3(p.x, 2.5f, p.z), normal = Vector3.up };
        Check(RadioShopPlacement.TryStand(scene, out var unraisedFort, out _, out fortReason) &&
            MathF.Abs(unraisedFort.y - 2.32f) < .001f && fortReason.Contains("differs"),
            "higher prop cannot raise Fort stand above authored preview");
        Physics.Support = originalSupport;
        scene.parentIslandIndex = 9;
        RadioStandLayout.TryAnchor(9, out var dragon, out _);
        Check(dragon.x>-106.8f && dragon.x<-102.4f && MathF.Abs(dragon.z+530f)<1f,
            "Dragon candidate between serialized tree landmarks");
        RadioStandLayout.TryAnchor(9, out _, out var dragonYaw);
        Check(MathF.Abs(dragonYaw-45f)<.01f,"approved Dragon Cliffs orientation preserved");
        var blocker = new Transform { name = "player crate" }.Add<Collider>();
        Physics.Overlap = _ => new[] { blocker };
        Check(RadioShopPlacement.TryStand(scene, out _, out _, out var reason) && reason.Contains("player crate") && reason.Contains("footprint"), "overlap diagnosed without suppressing preview");
        Physics.Overlap = _ => Array.Empty<Collider>();
        var old = Physics.Support;
        Physics.Support = (_, _) => null;
        Check(RadioShopPlacement.TryStand(scene, out _, out _, out reason) && reason.Contains("ground"), "missing ground uses authored preview height and reports gap");
        Physics.Support = (_, _) => new RaycastHit { collider=Physics.Ground, point=new Vector3(dragon.x,dragon.y+.6f,dragon.z), normal=Vector3.up };
        Check(RadioShopPlacement.TryStand(scene, out var unlifted, out _, out reason) &&
            MathF.Abs(unlifted.y-(dragon.y+.02f))<.001f && reason.Contains("differs"),
            "nearby static prop cannot lift authored preview");
        Physics.Support = (_, _) => null;
        Physics.Overlap = _ => new[] { blocker };
        Check(RadioShopPlacement.TryStand(scene, out _, out _, out reason) && reason.Contains("anchor, uneven") && reason.IndexOf(';') < 0,
            "several placement reasons are joined without semicolons");
        Physics.Overlap = _ => Array.Empty<Collider>();
        Physics.Support = old;
        for (int i = 0; i < RadioStandLayout.Slots.Length; i++)
        {
            int kind = WalnutKind(i);
            var size = RadioDevice.Size(kind);
            if (i > 0)
            {
                // Every device base now rests just above one flat table top.
                float tableTop = .78f + .075f * .5f;
                float clearance = RadioStandLayout.Slots[i].y - tableTop;
                Check(clearance >= 0f && clearance <= .02f,
                    "device bottom rests just above flat top " + i);
            }
            var center = RadioStandLayout.Slots[i] + RadioDevice.Center(kind);
            var delta = center - RadioStandLayout.EnvelopeCenter;
            Check(MathF.Abs(delta.x) + size.x * .5f <= RadioStandLayout.EnvelopeHalf.x &&
                MathF.Abs(delta.y) + size.y * .5f <= RadioStandLayout.EnvelopeHalf.y &&
                MathF.Abs(delta.z) + size.z * .5f <= RadioStandLayout.EnvelopeHalf.z, "device inside preview envelope " + i);
            for (int j = 0; j < i; j++)
            {
                var otherSize = RadioDevice.Size(WalnutKind(j));
                var other = RadioStandLayout.Slots[j] + RadioDevice.Center(WalnutKind(j));
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
        GoldRockCounter();
        Chronos();
        var face=new Vector3(0,0,-1);
        Check(MathF.Abs(face.y)<.01f && face.z<-.99f,"radio front remains upright toward customer");
    }

    // Model bounds from the shipped devices.json, read without the runtime model loader.
    private static (Vector3 min, Vector3 max) Bounds(int kind)
    {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("devices.json");
        using var document = System.Text.Json.JsonDocument.Parse(stream);
        foreach (var model in document.RootElement.GetProperty("models").EnumerateArray())
            if (model.GetProperty("kind").GetInt32() == kind)
            {
                var bounds = model.GetProperty("bounds");
                Vector3 Corner(string name) { var a = bounds.GetProperty(name); return new Vector3(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle()); }
                return (Corner("min"), Corner("max"));
            }
        throw new Exception("kind " + kind + " has no bounds");
    }

    // The native table's three planks in stand space, decoded from market_stall_001's upward faces through the
    // stand's copy transform: x half-length, z front and back, and the top height at the front and back edges.
    private static readonly (string name, float half, float z0, float z1, float y0, float y1)[] GoldPlanks =
    {
        ("front", 1.26f, -.62f, -.305f, .885f, .89f), ("middle", 1.355f, -.305f, .345f, .905f, .935f), ("rear", 1.31f, .345f, .69f, .955f, .97f)
    };

    // Gold Rock's native table with the Al'Ankh devices it actually stocks: footprints from the model bounds.
    private static void GoldRockCounter()
    {
        var footprints = new (float x0, float x1, float z0, float z1)[7];
        var planks = new int[7];
        for (int i = 1; i < 7; i++)
        {
            int kind = RadioShopCatalog.StockKind(1, i);
            var (min, max) = Bounds(kind);
            var slot = RadioStandLayout.Slot(1, i, true);
            var center = slot + RadioDevice.Center(SailwindRadio.DeviceKind.Role(kind), min, max);
            var size = max - min;
            footprints[i] = (center.x - size.x * .5f, center.x + size.x * .5f, center.z - size.z * .5f, center.z + size.z * .5f);
            // The copy turns one degree, so keep two centimetres from each plank edge.
            planks[i] = Array.FindIndex(GoldPlanks, p => footprints[i].z0 >= p.z0 + .02f && footprints[i].z1 <= p.z1 - .02f &&
                footprints[i].x0 >= -p.half + .02f && footprints[i].x1 <= p.half - .02f);
            Check(planks[i] >= 0, "Gold Rock slot " + i + " sits wholly on one plank without straddling a step");
            var plank = GoldPlanks[Math.Max(0, planks[i])];
            // The plank's top under the footprint's highest edge.
            float top = plank.y0 + (plank.y1 - plank.y0) * (footprints[i].z1 - plank.z0) / (plank.z1 - plank.z0);
            Check(slot.y - top >= .002f && slot.y - top <= .03f, "Gold Rock slot " + i + " rests just above its " + plank.name + " plank");
        }
        for (int i = 1; i < 7; i++)
            for (int j = 1; j < i; j++)
            {
                var a = footprints[i]; var b = footprints[j];
                Check(a.x0 >= b.x1 + .01f || b.x0 >= a.x1 + .01f || a.z0 >= b.z1 + .01f || b.z0 >= a.z1 + .01f,
                    "Gold Rock slots " + j + " and " + i + " keep at least a centimetre apart");
            }
        Check(planks[3] == 2 && planks[4] == 2 && planks[1] == 1 && planks[2] == 1 && planks[5] == 0 && planks[6] == 0,
            "radios stand on the rear plank, large speakers on the middle and small speakers on the front");
        float inner = footprints[2].x0 - footprints[1].x1;
        Check(inner >= .07f && inner <= .11f, "the large speakers sit about nine centimetres apart: " + inner);
    }

    // Chronos uses the empty native tent in the main square, facing the keeper into it like its neighbours.
    private static void Chronos()
    {
        Check(RadioStandLayout.TryAnchor(25, out var anchor, out float yaw) && MathF.Abs(yaw + 139.1f) < .001f,
            "Chronos turns with the empty tent's canopy");
        // Half the 1.13 m counter depth toward the customers, at the stand's -Z, from the tent pivot.
        float toward = 1.13f * .5f, radians = yaw * MathF.PI / 180;
        Check(MathF.Abs(anchor.x - (494.88f - MathF.Sin(radians) * toward)) < .001f && MathF.Abs(anchor.z - (504.69f - MathF.Cos(radians) * toward)) < .001f,
            "the Chronos stall stands half a counter depth forward of the tent pivot, in line with its neighbours");
        float keeperYaw = yaw + 180f;
        Check(MathF.Abs(keeperYaw - 39.1f) < 5f, "the Chronos keeper faces into the square like the neighbouring keepers");
        Check(Enumerable.Range(0, RadioShopCatalog.StockRoles.Length).All(i => SailwindRadio.DeviceKind.Family(RadioShopCatalog.StockKind(25, i)) == 0),
            "Chronos stocks the walnut set");
        // Serialized neighbouring shop items closest to the tent, scenery-local.
        (float x, float z)[] neighbours = { (497.23f, 504.09f), (497.69f, 504.66f), (496.75f, 503.55f), (498.21f, 503.01f), (498.47f, 503.41f),
            (498.44f, 503.08f), (498.67f, 502.59f), (498.44f, 502.79f), (498.88f, 503.07f), (498.69f, 503.25f), (499.07f, 502.89f), (499.47f, 502.59f) };
        float c = MathF.Cos(yaw * MathF.PI / 180), s = MathF.Sin(yaw * MathF.PI / 180);
        foreach (var (x, z) in neighbours)
        {
            float dx = x - anchor.x, dz = z - anchor.z, lx = dx * c - dz * s, lz = dx * s + dz * c;
            Check(MathF.Abs(lx - RadioStandLayout.EnvelopeCenter.x) > RadioStandLayout.EnvelopeHalf.x + .3f ||
                MathF.Abs(lz - RadioStandLayout.EnvelopeCenter.z) > RadioStandLayout.EnvelopeHalf.z + .3f,
                "the Chronos display stays clear of the neighbouring stall's item at " + x + ", " + z);
            Check(MathF.Abs(lx - .55f) > 1.8f + .2f || MathF.Abs(lz + 1.02f) > .38f + .2f,
                "the Chronos customer approach stays clear of the item at " + x + ", " + z);
        }
    }
}
