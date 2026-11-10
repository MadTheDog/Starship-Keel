using Sevo.Framework.Runtime;

internal static class StockWreckClaimJournalTests
{
    internal static int Run()
    {
        var root = Path.Combine(".cache", "stock-wreck-claim-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int checks = 0;
        try
        {
            var profile = Guid.NewGuid();
            const string character = "WreckFixture";
            const ulong seed = 100;
            const string system = "sector 200";
            const string identity = "2-301-w";
            var oldBody = new byte[] { 11, 12 };
            var oldMetadata = new byte[] { 13 };
            var replacement = (Body: new byte[] { 21, 22, 23 }, Metadata: new byte[] { 24 });
            var stock = new WreckClaimStockSource(300, 301, WreckClaimFiles.Digest(oldBody), WreckClaimFiles.Digest(oldMetadata));
            var pending = Path.Combine(root, "pending");
            Directory.CreateDirectory(pending);
            var manifest = Path.Combine(pending, WreckClaimJournal.Manifest);
            NewJournal(stock).Publish(manifest);
            var markerName = stock.MarkerName(seed, system);
            var pendingMarker = Path.Combine(pending, markerName);
            var permanentMarker = Path.Combine(root, markerName);
            WreckClaimFiles.Publish(pendingMarker, WreckClaimStockSource.MarkerBytes(identity));
            Check(WreckClaimStockSource.ReadMarker(pendingMarker) == identity);

            var resumed = WreckClaimJournal.Open(manifest, profile, character, seed).StockSource!;
            Check(resumed.MarkerName(seed, system) == markerName);
            Check(resumed.MarkerName(seed + 1, system) != markerName && resumed.MarkerName(seed, "sector 201") != markerName);
            Check(new WreckClaimStockSource(302, 301, null, null).MarkerName(seed, system) != markerName);
            Check(new WreckClaimStockSource(300, 302, null, null).MarkerName(seed, system) != markerName);
            Throws<InvalidOperationException>(() => WreckClaimJournal.Open(manifest, Guid.NewGuid(), character, seed));
            Throws<InvalidOperationException>(() => WreckClaimJournal.Open(manifest, profile, "Other", seed));
            Throws<InvalidOperationException>(() => WreckClaimJournal.Open(manifest, profile, character, seed + 1));

            WreckClaimFiles.Publish(permanentMarker, WreckClaimStockSource.MarkerBytes(identity));
            WreckClaimFiles.Publish(permanentMarker, WreckClaimStockSource.MarkerBytes(identity));
            Throws<IOException>(() => WreckClaimFiles.Publish(permanentMarker, WreckClaimStockSource.MarkerBytes("9-301-w")));
            var body = Path.Combine(root, "stock.sevo");
            var metadata = Path.Combine(root, "stock.xml");

            File.WriteAllBytes(body, oldBody);
            File.WriteAllBytes(metadata, new byte[] { 99 });
            Throws<IOException>(() => resumed.RetireOldPayload(body, metadata, replacement));
            Check(File.ReadAllBytes(body).SequenceEqual(oldBody));
            File.WriteAllBytes(metadata, oldMetadata);

            // Interrupt after one old file has been retired.
            File.Delete(body);
            resumed.RetireOldPayload(body, metadata, replacement);
            Check(!File.Exists(body) && !File.Exists(metadata));
            // Interrupt after one replacement file has been published.
            WreckClaimFiles.Publish(body, replacement.Body);
            resumed.RetireOldPayload(body, metadata, replacement);
            Check(File.ReadAllBytes(body).SequenceEqual(replacement.Body) && !File.Exists(metadata));
            WreckClaimFiles.Publish(metadata, replacement.Metadata);
            resumed.RetireOldPayload(body, metadata, replacement);
            Check(File.ReadAllBytes(metadata).SequenceEqual(replacement.Metadata));
            Directory.Delete(pending, recursive: true);
            Check(WreckClaimStockSource.ReadMarker(permanentMarker) == identity);

            var transient = Path.Combine(root, "transient.bin");
            NewJournal(null).Publish(transient);
            Check(BitConverter.ToInt32(File.ReadAllBytes(transient)) == 1);
            Check(WreckClaimJournal.Open(transient, profile, character, seed).StockSource == null);
            return checks;

            WreckClaimJournal NewJournal(WreckClaimStockSource? source)
                => new(profile, character, seed, system, identity, (0, 1, 2), (0, 0, 0, 1),
                    WreckClaimFiles.Digest(replacement.Body), WreckClaimFiles.Digest(replacement.Metadata), source);
        }
        finally { Directory.Delete(root, recursive: true); }

        void Check(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Stock wreck journal recovery check failed.");
            checks++;
        }

        void Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { checks++; return; }
            throw new InvalidOperationException("Stock wreck journal accepted an incompatible transition.");
        }
    }
}
