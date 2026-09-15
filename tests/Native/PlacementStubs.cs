// Explicit synthetic API doubles for the production placement guard. No Unity physics is executed.
using System;
using System.Collections.Generic;
using System.Linq;
using NQuaternion = System.Numerics.Quaternion;
using NVector = System.Numerics.Vector3;

namespace UnityEngine
{
    public class Object { public static implicit operator bool(Object value) => value != null; }
    public class Component : Object
    {
        public Transform transform;
        public T GetComponent<T>() where T : Component => transform.GetComponent<T>();
    }
    public class Transform : Object
    {
        public Transform parent;
        public Vector3 position;
        public Vector3 lossyScale = Vector3.one;
        public Quaternion rotation = Quaternion.identity;
        public readonly Dictionary<Type, Component> Components = new Dictionary<Type, Component>();
        public readonly List<Component> Children = new List<Component>();
        public T GetComponent<T>() where T : Component => Components.TryGetValue(typeof(T), out var value) ? (T)value : null;
        public T[] GetComponentsInChildren<T>() where T : Component => Children.OfType<T>().ToArray();
        public T Add<T>() where T : Component, new() { var value = new T { transform = this }; Components[typeof(T)] = value; return value; }
        public Vector3 TransformPoint(Vector3 point) => position + rotation * point;
        public Vector3 InverseTransformPoint(Vector3 point) => Quaternion.Inverse(rotation) * (point - position);
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 one => new Vector3(1, 1, 1);
        public static Vector3 up => new Vector3(0, 1, 0);
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);
        public Vector3 normalized => this * (1 / magnitude);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float b) => new Vector3(a.x * b, a.y * b, a.z * b);
    }
    public struct Quaternion
    {
        private NQuaternion value;
        private Quaternion(NQuaternion value) { this.value = value; }
        public static Quaternion identity => new Quaternion(NQuaternion.Identity);
        public static Quaternion Inverse(Quaternion q) => new Quaternion(NQuaternion.Inverse(q.value));
        public static Quaternion operator *(Quaternion a, Quaternion b) => new Quaternion(a.value * b.value);
        public static Vector3 operator *(Quaternion q, Vector3 v)
        {
            NVector value = NVector.Transform(new NVector(v.x, v.y, v.z), q.value);
            return new Vector3(value.X, value.Y, value.Z);
        }
    }
    public class Collider : Component { }
    public class CapsuleCollider : Collider { }
    public class CharacterController : Collider { }
    public struct RaycastHit { public Collider collider; }
    public enum QueryTriggerInteraction { Ignore }
    public static class Physics
    {
        public static Func<Vector3, Collider[]> Overlap = _ => Array.Empty<Collider>();
        public static Func<Vector3, RaycastHit[]> Cast = _ => Array.Empty<RaycastHit>();
        public static readonly List<Vector3> VolumeQueries = new List<Vector3>();
        public static Collider[] OverlapBox(Vector3 center, Vector3 half, Quaternion rotation, int mask, QueryTriggerInteraction trigger)
        { VolumeQueries.Add(center); return Overlap(center); }
        public static RaycastHit[] RaycastAll(Vector3 start, Vector3 direction, float distance, int mask, QueryTriggerInteraction trigger) => Cast(start);
    }
}
public class BoatDamage : UnityEngine.Component { }
public class PlayerControllerMirror : UnityEngine.Component { }
public class BoatEmbarkCollider : UnityEngine.Component
{
    public UnityEngine.Transform walkCollider;
    public UnityEngine.Transform root;
    public UnityEngine.Transform model;
    public UnityEngine.Transform GetMeshParent() => model;
    public UnityEngine.Transform GetTopmostBoatParent() => root;
}
public static class Refs
{
    public static UnityEngine.CharacterController charController;
    public static PlayerControllerMirror observerMirror;
}
public static class GameState { public static UnityEngine.Transform currentBoat; }
