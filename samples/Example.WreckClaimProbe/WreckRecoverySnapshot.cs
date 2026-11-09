using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace Sevo.Example.WreckClaimProbe;

[DataContract]
internal sealed class WreckRecoverySnapshot
{
    [DataMember(IsRequired = true)] private int schema = 1;
    [DataMember(IsRequired = true)] private Guid profile;
    [DataMember(IsRequired = true)] private Guid session;
    [DataMember(IsRequired = true)] private string character;
    [DataMember(IsRequired = true)] private string mode;
    [DataMember(IsRequired = true)] internal ulong Star { get; private set; }
    [DataMember(IsRequired = true)] internal string Identity { get; private set; }
    [DataMember(IsRequired = true)] private long x, y, z;
    [DataMember(IsRequired = true)] private float rx, ry, rz, rw;
    [DataMember(IsRequired = true)] private string bodyHash = "";
    [DataMember(IsRequired = true)] private string metadataHash = "";

    internal (long X, long Y, long Z) Position => (x, y, z);
    internal (float X, float Y, float Z, float W) Rotation => (rx, ry, rz, rw);

    internal WreckRecoverySnapshot(Guid profile, Guid session, string character, string mode, ulong star, string identity,
        (long X, long Y, long Z) position, (float X, float Y, float Z, float W) rotation)
    {
        this.profile = profile;
        this.session = session;
        this.character = character;
        this.mode = mode;
        Star = star;
        Identity = identity;
        (x, y, z) = position;
        (rx, ry, rz, rw) = rotation;
        Validate(profile, session, character, mode);
    }

    internal void Publish(string path, string body, string metadata)
    {
        bodyHash = Fingerprint(body, 16 * 1024 * 1024, flush: true);
        metadataHash = Fingerprint(metadata, 1024 * 1024, flush: true);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                new DataContractJsonSerializer(typeof(WreckRecoverySnapshot)).WriteObject(output, this);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static WreckRecoverySnapshot Open(string path, Guid profile, Guid session, string character, string mode,
        string body, string metadata)
    {
        WreckRecoverySnapshot snapshot;
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length is <= 0 or > 4096) throw new InvalidOperationException("Invalid wreck snapshot length.");
            snapshot = (WreckRecoverySnapshot?)new DataContractJsonSerializer(typeof(WreckRecoverySnapshot)).ReadObject(input)
                ?? throw new InvalidOperationException("Missing wreck snapshot.");
        }
        snapshot.Validate(profile, session, character, mode);
        snapshot.VerifyFiles(body, metadata);
        return snapshot;
    }

    internal void VerifyFiles(string body, string metadata)
    {
        if (Fingerprint(body, 16 * 1024 * 1024) != bodyHash || Fingerprint(metadata, 1024 * 1024) != metadataHash)
            throw new InvalidOperationException("The staged wreck body or metadata changed.");
    }

    internal static bool IsIdentity(string? value)
        => value is { Length: > 0 and < 100 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z'
            or >= 'a' and <= 'z' or '-' or '_');

    private void Validate(Guid expectedProfile, Guid expectedSession, string expectedCharacter, string expectedMode)
    {
        double norm = (double)rx * rx + (double)ry * ry + (double)rz * rz + (double)rw * rw;
        if (schema != 1 || profile == Guid.Empty || profile != expectedProfile || session == Guid.Empty || session != expectedSession
            || character != expectedCharacter || character != "WreckProbe-" + session.ToString("N").Substring(0, 8)
            || mode != expectedMode || mode is not ("creative" or "survival") || Star == 0 || !IsIdentity(Identity)
            || Position == (0L, 0L, 0L) || !(norm >= 0.99 && norm <= 1.01))
            throw new InvalidOperationException("The wreck snapshot has an invalid fixture identity, mode or pose.");
    }

    private static string Fingerprint(string path, long maxBytes, bool flush = false)
    {
        using var input = new FileStream(path, FileMode.Open, flush ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read);
        if (input.Length <= 0 || input.Length > maxBytes) throw new InvalidOperationException("Invalid staged wreck file length.");
        if (flush) input.Flush(flushToDisk: true);
        using var hash = SHA256.Create();
        return Convert.ToBase64String(hash.ComputeHash(input));
    }
}
