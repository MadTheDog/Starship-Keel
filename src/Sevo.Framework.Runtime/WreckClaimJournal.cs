using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Sevo.Framework.Runtime
{
    internal static class WreckClaimFiles
    {
        internal static byte[] Read(string path, int maximum)
        {
            using var file = File.OpenRead(path);
            if (file.Length <= 0 || file.Length > maximum)
                throw new IOException("Invalid wreck claim file length.");
            using var reader = new BinaryReader(file);
            var bytes = reader.ReadBytes((int)file.Length);
            if (bytes.Length != file.Length) throw new IOException("Incomplete wreck claim file.");
            return bytes;
        }

        internal static void Publish(string path, byte[] bytes)
        {
            if (MatchesExisting(path, bytes)) return;

            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        internal static bool MatchesExisting(string path, byte[] bytes)
        {
            if (!File.Exists(path)) return false;
            if (!Read(path, bytes.Length).SequenceEqual(bytes))
                throw new IOException("A conflicting wreck save file already exists.");
            return true;
        }

        internal static byte[] Digest(byte[] bytes)
        {
            using var hash = SHA256.Create();
            return hash.ComputeHash(bytes);
        }
    }

    internal sealed class WreckClaimJournal
    {
        internal const int MaxBody = 16 * 1024 * 1024;
        internal const int MaxMetadata = 1024 * 1024;
        internal const string Body = "wreck.sevo";
        internal const string Metadata = "wreck.xml";
        internal const string Manifest = "claim.bin";

        internal readonly string Identity;
        internal readonly string System;
        internal readonly (long X, long Y, long Z) Position;
        internal readonly (float X, float Y, float Z, float W) Rotation;
        private readonly Guid profile;
        private readonly string character;
        private readonly ulong seed;
        private readonly byte[] bodyHash;
        private readonly byte[] metadataHash;

        internal WreckClaimJournal(Guid profile, string character, ulong seed, string system, string identity,
            (long, long, long) position, (float, float, float, float) rotation, byte[] bodyHash, byte[] metadataHash)
        {
            this.profile = profile;
            this.character = character;
            this.seed = seed;
            System = system;
            Identity = identity;
            Position = position;
            Rotation = rotation;
            this.bodyHash = bodyHash;
            this.metadataHash = metadataHash;
            double norm = (double)Rotation.X * Rotation.X + (double)Rotation.Y * Rotation.Y
                + (double)Rotation.Z * Rotation.Z + (double)Rotation.W * Rotation.W;
            if (profile == Guid.Empty || string.IsNullOrWhiteSpace(character) || character.Length > 200
                || seed == 0 || string.IsNullOrWhiteSpace(system) || system.Length > 200
                || !IsIdentity(identity) || !(norm >= 0.99 && norm <= 1.01)
                || bodyHash.Length != 32 || metadataHash.Length != 32)
                throw new InvalidOperationException("Invalid wreck claim scope, identity or pose.");
        }

        internal static bool IsIdentity(string? value)
            => value is { Length: > 0 and < 100 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z'
                or >= 'a' and <= 'z' or '-' or '_') && value.ToUpperInvariant() is not
                ("CON" or "PRN" or "AUX" or "NUL" or "COM0" or "COM1" or "COM2" or "COM3" or "COM4"
                 or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or "LPT0" or "LPT1" or "LPT2"
                 or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9");

        internal void Publish(string path)
        {
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer);
            writer.Write(1);
            writer.Write(profile.ToByteArray());
            writer.Write(character);
            writer.Write(seed);
            writer.Write(System);
            writer.Write(Identity);
            writer.Write(Position.X);
            writer.Write(Position.Y);
            writer.Write(Position.Z);
            writer.Write(Rotation.X);
            writer.Write(Rotation.Y);
            writer.Write(Rotation.Z);
            writer.Write(Rotation.W);
            writer.Write(bodyHash);
            writer.Write(metadataHash);
            writer.Flush();
            WreckClaimFiles.Publish(path, buffer.ToArray());
        }

        internal static WreckClaimJournal Open(string path, Guid profile, string character, ulong seed)
        {
            using var buffer = new MemoryStream(WreckClaimFiles.Read(path, 4096));
            using var reader = new BinaryReader(buffer);
            if (reader.ReadInt32() != 1 || new Guid(reader.ReadBytes(16)) != profile
                || reader.ReadString() != character || reader.ReadUInt64() != seed)
                throw new InvalidOperationException("Wreck claim journal belongs to another save or profile.");
            var journal = new WreckClaimJournal(profile, character, seed, reader.ReadString(), reader.ReadString(),
                (reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64()),
                (reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                reader.ReadBytes(32), reader.ReadBytes(32));
            if (buffer.Position != buffer.Length) throw new IOException("Unexpected wreck claim journal data.");
            return journal;
        }

        internal (byte[] Body, byte[] Metadata) ReadPayload(string body, string metadata)
        {
            var bodyBytes = WreckClaimFiles.Read(body, MaxBody);
            var metadataBytes = WreckClaimFiles.Read(metadata, MaxMetadata);
            if (!WreckClaimFiles.Digest(bodyBytes).SequenceEqual(bodyHash)
                || !WreckClaimFiles.Digest(metadataBytes).SequenceEqual(metadataHash))
                throw new IOException("The staged wreck body or metadata changed.");
            return (bodyBytes, metadataBytes);
        }
    }
}
