using System;
using System.Numerics;
using System.Runtime.Serialization;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public enum WaypointAnchorKind { System, Body, Grid }

    [DataContract]
    public sealed class PersistentWaypoint
    {
        private PersistentWaypoint(string key, string name, ulong star, WaypointAnchorKind kind)
            => (Key, Name, StarSeed, AnchorKind) = (key, name, star, kind);

        [DataMember(IsRequired = true)] public string Key { get; private set; }
        [DataMember(IsRequired = true)] public string Name { get; private set; }
        [DataMember(IsRequired = true)] public ulong StarSeed { get; private set; }
        [DataMember(IsRequired = true)] public WaypointAnchorKind AnchorKind { get; private set; }
        [DataMember] public ulong BodySeed { get; private set; }
        [DataMember] public string? GridId { get; private set; }
        [DataMember] public long SystemX { get; private set; }
        [DataMember] public long SystemY { get; private set; }
        [DataMember] public long SystemZ { get; private set; }
        [DataMember] private float LocalX { get; set; }
        [DataMember] private float LocalY { get; set; }
        [DataMember] private float LocalZ { get; set; }
        public Vector3 LocalPosition => new Vector3(LocalX, LocalY, LocalZ);

        /// <summary>Creates a system-relative anchor in the game's persistent system coordinate units.</summary>
        public static PersistentWaypoint AtSystem(string key, string name, ulong star, long x, long y, long z)
            => new PersistentWaypoint(key, name, star, WaypointAnchorKind.System) { SystemX = x, SystemY = y, SystemZ = z }.Validate();

        public static PersistentWaypoint AtBody(string key, string name, BodyEnvironmentTarget body, Vector3 localMetres)
            => new PersistentWaypoint(key, name, body.StarSeed, WaypointAnchorKind.Body)
            { BodySeed = body.BodySeed, LocalX = localMetres.X, LocalY = localMetres.Y, LocalZ = localMetres.Z }.Validate();

        public static PersistentWaypoint AtGrid(string key, string name, ulong star, string persistentGridId, Vector3 localMetres)
            => new PersistentWaypoint(key, name, star, WaypointAnchorKind.Grid)
            { GridId = persistentGridId, LocalX = localMetres.X, LocalY = localMetres.Y, LocalZ = localMetres.Z }.Validate();

        internal PersistentWaypoint Validate()
        {
            ContentAllocator.ValidateKey(Key);
            if (string.IsNullOrWhiteSpace(Name) || Name.Length > 96 || StarSeed == 0
                || !Enum.IsDefined(typeof(WaypointAnchorKind), AnchorKind)
                || !float.IsFinite(LocalX) || !float.IsFinite(LocalY) || !float.IsFinite(LocalZ)
                || (AnchorKind == WaypointAnchorKind.Grid && !WreckClaimJournal.IsIdentity(GridId ?? "")))
                throw new ArgumentException("Invalid persistent waypoint.");
            return this;
        }
    }
}
