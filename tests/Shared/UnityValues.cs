// Shared value and static Unity API doubles with plain math semantics and no test hooks.
// Object, Component, GameObject, Transform, Collider and Physics stay per suite because each suite
// models lookup, destruction and queries differently. Audio keeps its own copies because its doubles
// must also compile under Unity's Mono compiler and its Distance accumulates in double precision.
using System;
namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y = 0, float z = 0) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new(0, 0, 0);
        public static Vector3 one => new(1, 1, 1);
        public static Vector3 up => new(0, 1, 0);
        public static Vector3 down => new(0, -1, 0);
        public static Vector3 forward => new(0, 0, 1);
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public Vector3 normalized => new(x / magnitude, y / magnitude, z / magnitude);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float b) => new(a.x * b, a.y * b, a.z * b);
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 Scale(Vector3 a, Vector3 b) => new(a.x * b.x, a.y * b.y, a.z * b.z);
        public static Vector3 ProjectOnPlane(Vector3 vector, Vector3 normal) => vector - normal * Dot(vector, normal);
        public static float Distance(Vector3 a, Vector3 b) => MathF.Sqrt((a - b).sqrMagnitude);
    }
    public static class Mathf
    {
        public const float Rad2Deg = 180f / MathF.PI;
        public static float Abs(float value) => MathF.Abs(value);
        public static float Atan2(float y, float x) => MathF.Atan2(y, x);
        public static float Clamp01(float value) => Math.Clamp(value, 0, 1);
        public static int FloorToInt(float value) => (int)MathF.Floor(value);
        public static int RoundToInt(float value) => (int)Math.Round(value);
        public static int Min(int a, int b) => Math.Min(a, b);
        public static float Min(float a, float b) => Math.Min(a, b);
    }
    public static class Time
    {
        public static float realtimeSinceStartup, unscaledTime, deltaTime;
        public static float timeScale = 1;
    }
}
