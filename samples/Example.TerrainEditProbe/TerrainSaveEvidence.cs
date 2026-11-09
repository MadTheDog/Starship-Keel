#nullable enable
using System;
using System.IO;
using BepInEx;
using Manager;
using MessagePack;
using Mirror;
using Sevo.Framework.Runtime;
using Unity.Collections;
using UnityEngine;
using World.Terrain;

namespace Sevo.Example.TerrainEditProbe
{
    public sealed partial class Probe
    {
        private const int MaximumCompressedSaveBytes = 16 * 1024 * 1024;
        private string? saveEvidencePath;
        private string saveExpectedHash = "";
        private ulong saveTree;
        private int saveLevel;
        private bool saveFlushStarted;
        private float saveDeadline;
        private float nextSavePoll;
        private string savePendingReason = "waiting_for_native_flush";

        private bool BeginSaveEvidence(OctreeNodeData node, string expectedHash)
        {
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_EDIT_FIXTURE_VERIFY_SAVE") != "1") return false;
            try
            {
                RequireSaveCharacter();
                string root = Path.GetFullPath(Application.persistentDataPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string path = Path.GetFullPath(OctreeNodeResources.GetNodeFileName(node));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Native terrain save path is outside the isolated Unity profile.");
                saveEvidencePath = path;
                saveExpectedHash = expectedHash;
                saveTree = node.treeGuid;
                saveLevel = node.level;
                saveDeadline = Time.realtimeSinceStartup + 20f;
                // The native flush runs from Update, outside the post-transform callback.
                GameloopManager.OnTransformJobFinished -= AdvanceAutomatedFixture;
                Logger.LogInfo($"KEEL_TERRAIN_FIXTURE request={autoId:N} native_save_pending={Path.GetFileName(path)}");
            }
            catch (Exception exception) { FinishAutomation(false, "native_save_begin_failed: " + exception.Message); }
            return true;
        }

        private bool AdvanceSaveEvidence()
        {
            string? path = saveEvidencePath;
            if (!autoArmed || path == null) return false;
            float now = Time.realtimeSinceStartup;
            if (now >= saveDeadline)
            {
                EndSaveEvidence(false, "native_save_timeout: " + savePendingReason);
                return true;
            }
            if (now < nextSavePoll || !GameloopManager.IsMainThread || GameloopManager.IsNativeContainerAccessUnsafe
                || GameloopManager.jobStarted || PlayerState.InLoadingScreen || PlayerState.IsQuittingToDesktopOrTitle
                || PlayerState.sessionMode != PlayerState.SessionMode.solo || PlayerState.gameMode != PlayerState.GameMode.creative
                || !NetworkServer.active || !NetworkServer.dontListen)
                return true;

            nextSavePoll = now + 0.25f;
            try
            {
                string character = RequireSaveCharacter();
                if (!saveFlushStarted)
                {
                    ProfileProtocol.Open(stateDirectory, Paths.GameRootPath, session);
                    saveFlushStarted = true;
                    OctreeNodeResources.FlushPendingNodeSaves(character);
                    savePendingReason = "waiting_for_complete_payload";
                    Logger.LogInfo($"KEEL_TERRAIN_FIXTURE request={autoId:N} native_save_flush_queued character={character}");
                    return true;
                }

                if (!TryReadNativeSave(path, out var compressed)) return true;
                using var stream = new MemoryStream(compressed, writable: false);
                var payload = MessagePackSerializer.Deserialize<NodeSavePayload>(stream, SaveTools.LZ4Compress, default);
                if (payload == null || payload.treeGuid != saveTree || payload.nodeLevel != saveLevel
                    || payload.voxelDatas == null || payload.voxelDatas.Length != OctreeNodeData.DENSITY_COUNT)
                {
                    savePendingReason = "native_payload_identity_or_layout_mismatch";
                    return true;
                }

                using var samples = new NativeArray<VoxelNodeData>(payload.voxelDatas, Allocator.Temp);
                var snapshot = new Snapshot(samples);
                if (snapshot.Hash != saveExpectedHash)
                {
                    savePendingReason = "native_payload_density_hash_mismatch";
                    return true;
                }

                string directory = Path.Combine(stateDirectory, "terrain-probe");
                Directory.CreateDirectory(directory);
                string file = $"{autoId:N}-auto-native-save-{saveTree:x16}.dat";
                using (var output = new FileStream(Path.Combine(directory, file), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    output.Write(compressed, 0, compressed.Length);
                Logger.LogInfo($"KEEL_TERRAIN_FIXTURE request={autoId:N} phase=auto-native-save tree={saveTree:x16}"
                    + $" sha256={snapshot.Hash} file={file} bytes={compressed.Length}");
                EndSaveEvidence(true, "cut_fill_and_native_disk_payload_verified");
            }
            catch (MessagePackSerializationException) { savePendingReason = "native_payload_decode_failed"; }
            catch (Exception exception) { EndSaveEvidence(false, "native_save_failed: " + exception.Message); }
            return true;
        }

        private bool TryReadNativeSave(string path, out byte[] compressed)
        {
            compressed = Array.Empty<byte>();
            try
            {
                // The stock writer creates the file with exclusive sharing. Read only after it closes.
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length <= 0 || input.Length > MaximumCompressedSaveBytes)
                {
                    savePendingReason = "native_payload_size_invalid";
                    return false;
                }

                using var bytes = new MemoryStream((int)input.Length);
                input.CopyTo(bytes);
                compressed = bytes.ToArray();
                return compressed.Length == input.Length;
            }
            catch (IOException) { return false; }
        }

        private string RequireSaveCharacter()
        {
            string? expected = Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_EDIT_FIXTURE_CHARACTER");
            string character = SaveManager.CharacterName;
            if (!autoArmed || !Guid.TryParseExact(session, "N", out _) || string.IsNullOrEmpty(expected)
                || !string.Equals(character, expected, StringComparison.Ordinal)
                || !expected.EndsWith("-" + session.Substring(0, 8), StringComparison.Ordinal))
                throw new InvalidOperationException("Native save verification requires the explicit session-owned disposable character.");
            return character;
        }

        private void EndSaveEvidence(bool accepted, string detail)
        {
            saveEvidencePath = null;
            FinishAutomation(accepted, detail);
        }
    }
}
