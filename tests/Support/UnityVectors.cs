// Standalone core-check shim only. Outside Assets; never imported by Unity.
using System;
namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x-b.x, a.y-b.y);
        public static float Dot(Vector2 a, Vector2 b) => a.x*b.x+a.y*b.y;
    }
    public struct Vector2Int : IEquatable<Vector2Int>
    {
        public int x, y;
        public Vector2Int(int x, int y) { this.x=x; this.y=y; }
        public bool Equals(Vector2Int b) => x==b.x && y==b.y;
        public override bool Equals(object b) => b is Vector2Int && Equals((Vector2Int)b);
        public override int GetHashCode() => x*397^y;
        public static bool operator ==(Vector2Int a, Vector2Int b) => a.Equals(b);
        public static bool operator !=(Vector2Int a, Vector2Int b) => !a.Equals(b);
    }
    public static class Debug { public static void Log(object message) { Console.WriteLine(message); } }
}
namespace UnityEditor
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MenuItem : Attribute { public MenuItem(string name) { } }
}
internal static class CoreCheckMain { public static void Main() { UavProjectionChecks.RunCore(); } }
