using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Sevo.Framework.Runtime
{
    /// <summary>Reads the object table of the uncompressed Unity player asset schema used by the catalog audit.</summary>
    internal sealed class GalaxyCatalogSerializedAsset : IDisposable
    {
        internal const int MonoBehaviour = 114;
        internal const int MonoScript = 115;
        internal const int PrefabInstance = 1001;
        private const int HeaderSize = 48;
        private const int MaxMetadataBytes = 16 * 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        private readonly FileStream input;
        private readonly BinaryReader reader;
        private readonly long metadataEnd;
        private readonly long dataOffset;
        private readonly List<string> sources = new List<string>();
        internal string Name { get; }
        internal IReadOnlyList<string> ExternalSources => sources;
        internal Dictionary<long, (long Offset, uint Size, int Class)> Objects { get; }
            = new Dictionary<long, (long, uint, int)>();

        internal GalaxyCatalogSerializedAsset(string file)
        {
            Name = Path.GetFileName(file);
            ProfileProtocol.NoReparse(file);
            input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            reader = new BinaryReader(input, Utf8);
            try
            {
                byte[] header = reader.ReadBytes(HeaderSize);
                if (header.Length != HeaderSize || header[16] != 0)
                    throw new InvalidDataException("The asset header is missing or uses unsupported byte order.");
                uint version = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
                uint metadataSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
                long fileSize = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(24, 8));
                dataOffset = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(32, 8));
                metadataEnd = HeaderSize + (long)metadataSize;
                if ((version != 22 && version != 23) || metadataSize == 0 || metadataSize > MaxMetadataBytes
                    || fileSize != input.Length || dataOffset < metadataEnd || dataOffset >= fileSize)
                    throw new InvalidDataException("The serialized asset file layout is outside the audited schema.");
                ReadMetadata();
            }
            catch (Exception exception)
            {
                reader.Dispose();
                throw new InvalidOperationException("Cannot audit installed asset " + Name + ". " + exception.Message, exception);
            }
        }

        private void ReadMetadata()
        {
            ReadCString();
            reader.ReadInt32(); // target platform
            if (reader.ReadBoolean())
                throw new InvalidDataException("Catalog admission currently requires stripped asset type trees.");
            var classes = new int[Count(65_536, "types")];
            for (int index = 0; index < classes.Length; index++)
            {
                classes[index] = reader.ReadInt32();
                reader.ReadBoolean();
                reader.ReadInt16();
                Skip(classes[index] == MonoBehaviour ? 32 : 16); // script/type hashes
            }

            int count = Count(262_144, "objects");
            for (int index = 0; index < count; index++)
            {
                AlignMetadata();
                long id = reader.ReadInt64();
                long offset = reader.ReadInt64();
                uint size = reader.ReadUInt32();
                int type = reader.ReadInt32();
                if (id == 0 || (uint)type >= classes.Length || offset < 0 || offset > input.Length - dataOffset - size)
                    throw new InvalidDataException("A serialized object has an invalid ID, type, or data extent.");
                Objects.Add(id, (dataOffset + offset, size, classes[type]));
            }

            count = Count(65_536, "script references");
            for (int index = 0; index < count; index++)
            {
                Skip(4);
                AlignMetadata();
                Skip(8);
            }
            sources.Add(Name);
            count = Count(256, "external sources");
            for (int index = 0; index < count; index++)
            {
                if (ReadCString().Length != 0)
                    throw new InvalidDataException("An external source uses an unsupported remapping name.");
                Skip(20); // guid and source type
                sources.Add(ReadCString());
            }
            if (reader.ReadInt32() != 0 || ReadCString().Length != 0 || input.Position != metadataEnd)
                throw new InvalidDataException("The asset has unresolved managed references or metadata.");
        }

        internal BinaryReader OpenObject(long id, uint maximum = 4096)
        {
            var item = Objects[id];
            input.Position = item.Offset;
            int length = (int)Math.Min(item.Size, maximum);
            byte[] payload = reader.ReadBytes(length);
            if (payload.Length != length)
                throw new EndOfStreamException("A serialized object is truncated.");
            return new BinaryReader(new MemoryStream(payload, writable: false), Utf8);
        }

        internal GalaxyCatalogAssetReference ReadReference(BinaryReader from, bool allowNull = false)
        {
            int file = from.ReadInt32();
            long id = from.ReadInt64();
            if ((uint)file >= sources.Count || (id == 0 && (!allowNull || file != 0)))
                throw new InvalidDataException("A catalog asset has an unresolved object reference.");
            return new GalaxyCatalogAssetReference(sources[file], id);
        }

        internal static string ReadAlignedString(BinaryReader from)
        {
            int length = from.ReadInt32();
            if (length < 0 || length > 1024)
                throw new InvalidDataException("A serialized asset name is invalid.");
            byte[] text = from.ReadBytes(length);
            if (text.Length != length)
                throw new EndOfStreamException("A serialized asset name is truncated.");
            var stream = from.BaseStream;
            long next = (stream.Position + 3) & ~3L;
            if (next > stream.Length) throw new EndOfStreamException("A serialized asset name has missing alignment.");
            stream.Position = next;
            return Utf8.GetString(text);
        }

        private string ReadCString()
        {
            var bytes = new List<byte>();
            while (input.Position < metadataEnd && bytes.Count < 1024)
            {
                byte value = reader.ReadByte();
                if (value == 0) return Utf8.GetString(bytes.ToArray());
                bytes.Add(value);
            }
            throw new InvalidDataException("An asset metadata name is invalid.");
        }

        private int Count(int maximum, string label)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > maximum || input.Position > metadataEnd)
                throw new InvalidDataException("Invalid serialized " + label + " count.");
            return count;
        }

        private void AlignMetadata() => Skip((int)((-input.Position) & 3L));

        private void Skip(int length)
        {
            if (input.Position > metadataEnd - length)
                throw new EndOfStreamException("Serialized asset metadata is truncated.");
            input.Position += length;
        }

        public void Dispose() => reader.Dispose();
    }
}
