using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    internal readonly struct TerrainAnchorRecord
    {
        internal TerrainAnchorRecord(string grid, BodyEnvironmentTarget body, Vector3 position, Vector3 normal)
        {
            if (!WreckClaimJournal.IsIdentity(grid) || body.StarSeed == 0 || !Finite(position)
                || !Finite(normal) || Math.Abs(normal.LengthSquared() - 1f) > 0.01f)
                throw new ArgumentException("Invalid saved terrain anchor.");
            (Grid, Body, Position, Normal) = (grid, body, position, normal);
        }

        internal string Grid { get; }
        internal BodyEnvironmentTarget Body { get; }
        internal Vector3 Position { get; }
        internal Vector3 Normal { get; }
        private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }

    internal sealed class TerrainAnchorState
    {
        internal const int MaximumBytes = 1024 * 1024;
        private const int MaximumRecords = 1024;
        private readonly Guid profile;
        private readonly Dictionary<(string Character, ulong World, string Key), TerrainAnchorRecord> records = new();

        internal TerrainAnchorState(Guid profile) => this.profile = profile;

        internal bool TryGet(string character, ulong world, string key, out TerrainAnchorRecord record)
            => records.TryGetValue((character, world, key), out record);

        internal bool TryAdd(string character, ulong world, string key, TerrainAnchorRecord record)
        {
            ContentAllocator.ValidateKey(key);
            if (world == 0 || string.IsNullOrWhiteSpace(character) || character.Length > 128)
                throw new ArgumentException("Invalid terrain anchor world.");
            if (records.Count >= MaximumRecords) return false;
            return records.TryAdd((character, world, key), record);
        }

        internal bool Delete(string character, ulong world, string key, string grid)
            => TryGet(character, world, key, out var record) && StringComparer.OrdinalIgnoreCase.Equals(record.Grid, grid)
                && records.Remove((character, world, key));

        internal byte[] Serialize()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(1);
            writer.Write(profile.ToByteArray());
            writer.Write(records.Count);
            foreach (var item in records)
            {
                writer.Write(item.Key.Character);
                writer.Write(item.Key.World);
                writer.Write(item.Key.Key);
                writer.Write(item.Value.Grid);
                writer.Write(item.Value.Body.StarSeed);
                writer.Write(item.Value.Body.BodySeed);
                WriteVector(item.Value.Position);
                WriteVector(item.Value.Normal);
            }
            writer.Flush();
            if (stream.Length > MaximumBytes) throw new IOException("Oversized terrain anchor state.");
            return stream.ToArray();

            void WriteVector(Vector3 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }
        }

        internal static TerrainAnchorState Read(byte[] bytes, Guid profile)
        {
            if (bytes.Length > MaximumBytes) throw new IOException("Oversized terrain anchor state.");
            using var stream = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != 1 || new Guid(reader.ReadBytes(16)) != profile)
                throw new IOException("Terrain anchor state belongs to another profile or schema.");
            int count = reader.ReadInt32();
            if (count < 0 || count > MaximumRecords) throw new IOException("Invalid terrain anchor count.");
            var state = new TerrainAnchorState(profile);
            for (int i = 0; i < count; i++)
            {
                string character = reader.ReadString();
                ulong world = reader.ReadUInt64();
                string key = reader.ReadString();
                string grid = reader.ReadString();
                var target = new BodyEnvironmentTarget(reader.ReadUInt64(), reader.ReadUInt64());
                if (!state.TryAdd(character, world, key, new TerrainAnchorRecord(grid, target, ReadVector(), ReadVector())))
                    throw new IOException("Duplicate terrain anchor.");
            }
            if (stream.Position != stream.Length) throw new IOException("Unexpected terrain anchor state data.");
            return state;

            Vector3 ReadVector() => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
    }
}
