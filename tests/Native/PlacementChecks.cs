using System;
using SailwindRadio.Physical;
using UnityEngine;

internal static class PlacementChecks
{
    public static void Run()
    {
        int count = 0;
        void Check(bool pass, string description) { count++; if (!pass) throw new Exception(description); }
        var root = new Transform();
        root.Add<BoatDamage>();
        var hull = root.Add<CapsuleCollider>();
        var model = new Transform { parent = root };
        var walk = new Transform { position = new Vector3(1000, 0, 0) };
        var embark = new BoatEmbarkCollider { model = model, root = root, walkCollider = walk };
        model.Children.Add(embark);
        Refs.charController = new Transform().Add<CharacterController>();
        Refs.observerMirror = new Transform().Add<PlayerControllerMirror>();
        GameState.currentBoat = model;
        var position = new Vector3(0, 1, 1);
        var otherHull = new Transform().Add<CapsuleCollider>();
        var wall = new Transform().Add<Collider>();

        Physics.Overlap = p => p.x > 500 ? Array.Empty<Collider>() : new Collider[] { hull };
        Check(RadioPlacement.IsClear(position, Quaternion.identity), "own coarse hull allows clear verified deck placement");
        Check(Physics.VolumeQueries.Count == 2 && Physics.VolumeQueries[0].x > 500 && Physics.VolumeQueries[1].x < 500,
            "full walk-space query must precede visual coarse-hull exemption");
        Physics.Overlap = p => p.x > 500 ? new[] { wall } : new Collider[] { hull };
        Check(!RadioPlacement.IsClear(position, Quaternion.identity), "walk-space wall rejects placement even when visual space is clear");
        Physics.Overlap = p => p.x > 500 ? Array.Empty<Collider>() : new Collider[] { hull, otherHull };
        Check(!RadioPlacement.IsClear(position, Quaternion.identity), "another vessel hull remains an obstacle");
        Physics.Overlap = _ => Array.Empty<Collider>();
        Physics.Cast = p => p.x > 500 ? new[] { new RaycastHit { collider = wall } } : Array.Empty<RaycastHit>();
        Check(!RadioPlacement.IsClear(position, Quaternion.identity), "walk-space path through a wall is rejected");
        Physics.Cast = _ => Array.Empty<RaycastHit>();
        embark.walkCollider = null;
        Check(!RadioPlacement.IsClear(position, Quaternion.identity), "missing native walk mapping fails closed");
        embark.walkCollider = walk;
        embark.root = new Transform();
        Check(!RadioPlacement.IsClear(position, Quaternion.identity), "foreign boat sharing walk collider is not a valid mapping");
        embark.root = root;
        model.lossyScale = new Vector3(2, 2, 2);
        Check(!RadioPlacement.IsClear(position, Quaternion.identity), "unsupported scale does not silently shrink obstruction checks");
        model.lossyScale = Vector3.one;
        Physics.Overlap = _ => new Collider[] { Refs.charController };
        Check(RadioPlacement.IsClear(position, Quaternion.identity), "actual player body is ignored without ignoring all layer peers");
        GameState.currentBoat = null;
        Physics.Overlap = _ => new Collider[] { hull };
        Check(!RadioPlacement.IsClear(position, Quaternion.identity), "land placement does not exempt a nearby boat hull");
        Physics.Overlap = _ => Array.Empty<Collider>();
        Check(RadioPlacement.IsClear(position, Quaternion.identity), "clear land needs no vessel mapping");
        Console.WriteLine($"{count} production placement guard checks passed using synthetic physics responses. Live collision and visual-space acceptance remain open.");
    }
}
