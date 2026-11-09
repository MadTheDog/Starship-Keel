#nullable enable
using System;
using System.Collections;
using System.IO;
using System.Text;
using BepInEx;
using Game;
using Manager;
using Mirror;
using Sevo.Framework.Runtime;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Example.PassThroughStorageProbe;

[BepInPlugin(Owner, "Keel Pass-through Storage Probe", "0.1.0")]
[BepInDependency(Plugin.Id)]
public sealed class Probe : BaseUnityPlugin
{
    private const string Owner = "example.keel.passthroughstorage";
    private const string NormalName = "Probe Pass-through Corner";
    private const string FlushName = "Probe Pass-through Flush Fitting";
    private const string SleeveName = "Probe Sensor Sleeve";
    private string session = "";
    private bool pending, done, transferStarted;
    private float readyDeadline;
    private ParentEntity? grid;
    private BrickInstanceData sourceData, sinkData, flushData;
    private bool transferReady;

    private void Awake()
    {
        const string flag = "SEVO_KEEL_PASS_THROUGH_STORAGE_FIXTURE";
        if (Environment.GetEnvironmentVariable(flag) != "1") return;
        try
        {
            session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION") ?? "";
            string? state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
            string? game = Environment.GetEnvironmentVariable(flag + "_GAME");
            Require(Guid.TryParseExact(session, "N", out _) && !string.IsNullOrWhiteSpace(state)
                && !string.IsNullOrWhiteSpace(game) && Path.IsPathFullyQualified(game)
                && Application.companyName == "SEVO Keel Conn Lab 010" && Application.productName == "SEVO CT Test"
                && string.Equals(Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(Paths.GameRootPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
                "Use the exact managed disposable connector lab.");
            ProfileProtocol.Open(state!, Paths.GameRootPath, session);
            Require(Environment.GetEnvironmentVariable("SEVO_PLATFORM_EXPERIMENTAL_CONTENT") == "1"
                && Environment.GetEnvironmentVariable("SEVO_KEEL_EXPERIMENTAL_FLUIDS") == "1"
                && Environment.GetEnvironmentVariable("SEVO_KEEL_EXPERIMENTAL_STORAGE_CONNECTORS") == "1"
                && Environment.GetEnvironmentVariable("SEVO_KEEL_EXPERIMENTAL_SELECTABLE_PASS_THROUGH") == "1"
                && Environment.GetEnvironmentVariable("SEVO_KEEL_EXPERIMENTAL_FLUSH_STORAGE_PLACEMENT") == "1",
                "Enable managed content, the native fluid tick, storage connectors, pass-through and flush placement.");
            StorageConnectorRegistration.RegisterPassThrough(Owner, "corner", 1, NormalName,
                () => TabStorage.PIPE_ANGLED, ItemStorageType.tube);
            StorageConnectorRegistration.RegisterFlushItemFitting(Owner, "flush", 1, FlushName,
                () => TabStorage.TANK_JOIN, passThrough: true);
            SelectablePassThrough.RegisterHull(Owner, "sleeve", 1, SleeveName);
            FluidRegistration.RegisterTick(Owner, "stock-cargo-transfer", TransferTick);
            pending = true;
            Record("armed", "awaiting stock solo creative world");
        }
        catch (Exception exception) { Fail(exception); }
    }

    private void Update()
    {
        if (!pending) return;
        if (readyDeadline == 0f) readyDeadline = Time.realtimeSinceStartup + 100f;
        if (Time.realtimeSinceStartup > readyDeadline)
        {
            Fail(new TimeoutException("solo creative readiness"));
            return;
        }
        if (!Ready() || !FluidRegistration.IsAvailable || !CanBuild()) return;
        pending = false;
        StartCoroutine(Guard(Run()));
    }

    private IEnumerator Guard(IEnumerator run)
    {
        try
        {
            while (!done)
            {
                object? current;
                try
                {
                    if (!run.MoveNext()) break;
                    current = run.Current;
                }
                catch (Exception exception)
                {
                    Fail(exception);
                    break;
                }
                yield return current;
            }
        }
        finally { (run as IDisposable)?.Dispose(); }
    }

    private IEnumerator Run()
    {
        var corner = FindDefinition(NormalName);
        var flush = FindDefinition(FlushName);
        CheckCollider(corner);
        CheckCollider(flush);
        var sleeve = FindDefinition(SleeveName);
        CheckCollider(sleeve);
        Require(sleeve.GetItemStorageType(null!, 0) == ItemStorageType.none,
            "The independent sensor sleeve acquired a storage role.");
        Require(!Brick.BrickProperties[TabStorage.PIPE_ANGLED.brickID].isCollisionOff
            && !Brick.BrickProperties[TabStorage.TANK_JOIN.brickID].isCollisionOff,
            "Owned admission changed a stock template's collision properties.");
        Record("colliders", $"corner={corner.brickID};flush={flush.brickID};sleeve={sleeve.brickID};ownedCollisionOff=True;stockCollisionOff=False");

        flushData = flush.PreviewBrickData;
        flushData.scale = 0;
        flushData.gridPosition = new Vector3i(0, flushData.GridSizeUnit, 0);
        sourceData = TabStorage.STORAGE_UNIT.PreviewBrickData;
        sourceData.gridLevel = flushData.gridLevel;
        sourceData.scale = 0;
        sinkData = sourceData;
        sinkData.gridPosition = flushData.gridPosition;
        var normal = corner.PreviewBrickData;
        normal.gridLevel = sourceData.gridLevel;
        normal.scale = 0;
        normal.gridPosition = new Vector3i(96, 0, 0);
        var solid = TabStorage.STORAGE_UNIT.PreviewBrickData;
        solid.gridLevel = normal.gridLevel;
        solid.scale = 0;
        solid.gridPosition = normal.gridPosition;

        var player = Dependency.PlayerManager;
        var position = new WorldTransform(player.TransformData);
        position.systemPosition += new Vector3l(new Vector3(0f, 25f, 0f));
        grid = player.NetworkSetup.ServerCreateEntity(sourceData, position, EntityType.spaceship);
        Require(grid != null, "Stock builder rejected the fixture grid.");
        float deadline = Time.realtimeSinceStartup + 15f;
        while (Waiting(() => Find(sourceData)?.Command is StorageCommandData, deadline, "stock cargo source"))
            yield return null;

        Require(!Overlaps(normal), "Stock overlap query rejected a clear normal fitting.");
        Edit(normal, VoxelActionType.build);
        deadline = Time.realtimeSinceStartup + 12f;
        while (Waiting(() => Find(normal).HasValue, deadline, "normal pass-through fitting")) yield return null;
        var liveNormal = Find(normal)!.Value;
        BrickCoord exact = default;
        string rayDiagnostic = "no safe native query frame";
        deadline = Time.realtimeSinceStartup + 8f;
        while (!Ready() || !TryRayHit(liveNormal, out exact, out rayDiagnostic))
        {
            if (Time.realtimeSinceStartup > deadline)
                throw new TimeoutException("ordinary stock corner ray. " + rayDiagnostic);
            yield return null;
        }
        Record("corner-ray", $"ordinary stock ray selected exact corner fitting {exact.brickData.instanceId}");
        bool duplicate = Overlaps(normal);
        bool occupied = Overlaps(solid);
        Record("overlap-result", $"duplicate={duplicate};cargoOverFitting={occupied}");
        Require(duplicate && occupied, "Native placement allowed an exact duplicate or cargo over the fitting.");
        Record("duplicate-rejected", "stock overlap query rejected fitting and cargo over live fitting");

        Edit(exact.brickData, VoxelActionType.delete);
        deadline = Time.realtimeSinceStartup + 12f;
        while (Waiting(() => !Find(normal).HasValue, deadline, "exact stock ray-selected fitting removal"))
            yield return null;
        Record("normal-removed", $"native delete used exact ordinary ray hit {exact.brickData.instanceId}");

        Edit(solid, VoxelActionType.build);
        deadline = Time.realtimeSinceStartup + 12f;
        while (Waiting(() => Find(solid).HasValue, deadline, "stock solid")) yield return null;
        Require(Overlaps(normal), "Native placement allowed the fitting through an existing solid.");
        Record("reverse-rejected", "stock overlap query rejected fitting over live solid");

        Edit(flushData, VoxelActionType.build);
        deadline = Time.realtimeSinceStartup + 12f;
        while (Waiting(() => Find(flushData).HasValue, deadline, "pass-through flush fitting")) yield return null;
        var liveFlush = Find(flushData)!.Value;
        rayDiagnostic = "no safe native query frame";
        deadline = Time.realtimeSinceStartup + 8f;
        while (!Ready() || !TryRayHit(liveFlush, out _, out rayDiagnostic))
        {
            if (Time.realtimeSinceStartup > deadline)
                throw new TimeoutException("ordinary stock flush ray. " + rayDiagnostic);
            yield return null;
        }
        Record("flush-ray", $"ordinary stock ray selected exact flush fitting {liveFlush.brickData.instanceId}");
        Edit(sinkData, VoxelActionType.build);
        deadline = Time.realtimeSinceStartup + 12f;
        while (Waiting(() => Find(sinkData)?.Command is StorageCommandData, deadline, "second flush cargo"))
            yield return null;
        transferReady = true;
        deadline = Time.realtimeSinceStartup + 30f;
        while (!done)
        {
            if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("native cargo transfer");
            yield return null;
        }
    }

    private void TransferTick(FluidTickContext tick)
    {
        if (!transferReady || done) return;
        try
        {
            var source = Find(sourceData);
            var sink = Find(sinkData);
            var fitting = Find(flushData);
            if (source?.Command is not StorageCommandData left || sink?.Command is not StorageCommandData right
                || !fitting.HasValue) throw new InvalidOperationException("The fixture cargo or fitting was removed.");
            int a = left.StorageId;
            int b = right.StorageId;
            int pipe = fitting.Value.brickData.Brick.GetItemStorageId(grid!, fitting.Value.brickData.instanceId);
            Require(a > 0 && b > 0 && pipe > 0 && HasNeighbor(pipe, a) && HasNeighbor(a, pipe)
                && HasNeighbor(pipe, b) && HasNeighbor(b, pipe), "The native fitting is missing a bidirectional cargo edge.");
            int material = TabHull.HULL_A.brickID;
            if (!transferStarted)
            {
                Require(Count(a, material) == 0f && Count(b, material) == 0f, "Fixture cargo did not start empty.");
                var context = StorageCommon.CONTEXT;
                Require(StorageCommon.InsertUnregisteredItemOrMerge(a,
                    new ItemInstanceData { itemType = WorldItemType.brick, prefabId = material, quantity = 12f }, context, 0),
                    "Stock source rejected fixture cargo.");
                StorageCommon.AddOrReplaceRequest(b, new ItemRequestData
                {
                    requestType = RequestType.pull, itemType = WorldItemType.brick, prefabId = material,
                    quantity = 4f, targetType = RequestTargetType.onlyStorageUnit, parentMode = RequestParentMode.sameParent
                }, context);
                transferStarted = true;
                Record("linked", $"source={a};fitting={pipe};sink={b};bidirectional=True");
                return;
            }
            float from = Count(a, material);
            float to = Count(b, material);
            Require(from + to == 12f && to <= 4f, "Stock cargo transfer failed conservation.");
            if (to < 4f) return;
            done = true;
            Record("pass", "source=8;sink=4;native ray, removal, placement and transfer verified");
        }
        catch (Exception exception) { Fail(exception); }
    }

    private static Brick FindDefinition(string name)
        => Array.Find(Brick.BRICK_INDEX, definition => definition != null && definition.name == name)
            ?? throw new InvalidOperationException("Missing owned fitting " + name);

    private static void CheckCollider(Brick definition)
    {
        int id = definition.brickID;
        Require(definition.IsCollisionOff && Brick.BrickProperties[id].isCollisionOff, "Missing published collision-off flag.");
        var colliders = BrickCollider.Datas;
        for (int i = 0; id >= 0 && i < 16; i++)
        {
            Require(id < colliders.Length && colliders[id].skipCollision, "A native fitting shape still takes physical collisions.");
            id = colliders[id].nextShapeId;
        }
        Require(id == -1, "Native collider chain did not terminate.");
    }

    private bool TryRayHit(BrickCoord expected, out BrickCoord exact, out string diagnostic)
    {
        exact = default;
        diagnostic = "owned fitting placement unavailable";
        if (grid == null || !ReferenceEquals(expected.entity, grid)) return false;
        var data = expected.brickData;
        var bounds = data.GetAabb(useGeoBounds: true);
        Vector3 extents = ((Vector3)bounds.Max - (Vector3)bounds.Min) * 0.5f;
        Vector3 localCenter = ((Vector3)bounds.Min + (Vector3)bounds.Max) * 0.5f;
        var transform = grid.TransformData;
        var firstHits = new StringBuilder();
        foreach (var axis in new[] { Vector3.up, Vector3.right, Vector3.forward, Vector3.down, Vector3.left, Vector3.back })
        {
            Vector3 side = Vector3.Cross(axis, Mathf.Abs(axis.y) > 0.5f ? Vector3.right : Vector3.up);
            Vector3 across = Vector3.Cross(axis, side);
            var direction = transform.rotation * axis;
            string first = "miss";
            for (int u = -1; u <= 1; u++)
            for (int v = -1; v <= 1; v++)
            {
                var offset = 0.6f * Vector3.Scale(extents, u * side + v * across);
                var center = transform.LocalToWorld(localCenter + offset);
                if (!VoxelPhysics.BuildItem.Raycast(center + 1.75f * direction, -direction, out var hit, 2.5f,
                    null, Dependency.PlayerManager.TransformId)) continue;
                if (hit.isHitBrick && ReferenceEquals(hit.Entity, grid) && hit.brickData.instanceId == data.instanceId
                    && hit.brickData.brickId == data.brickId)
                {
                    exact = hit.brickCoord;
                    diagnostic = "";
                    return true;
                }
                if (first == "miss")
                    first = FormattableString.Invariant(
                        $"{hit.type}/brick={hit.brickData.brickId}/instance={hit.brickData.instanceId}/ownGrid={ReferenceEquals(hit.Entity, grid)}/d={hit.distance:F3}");
            }
            firstHits.Append(FormattableString.Invariant($" axis={axis}:{first}"));
        }
        diagnostic = $"expected brick={data.brickId}/instance={data.instanceId}/coord={data.gridPosition};first hits:{firstHits}";
        return false;
    }

    private BrickCoord? Find(BrickInstanceData expected)
    {
        if (grid == null || !grid.IsInitialized || !ReferenceEquals(grid.ID.Entity, grid)) return null;
        var data = grid.Bricks.GetBrickData(grid.Bricks.GetBrickInstanceId(expected));
        return data.IsValid && data.brickId == expected.brickId && data.gridPosition == expected.gridPosition
            && data.gridLevel == expected.gridLevel && data.rotation == expected.rotation ? new BrickCoord(grid.ID, data) : null;
    }

    private bool Overlaps(BrickInstanceData data)
        => VoxelPhysics.IsOverlapping(new BrickCoord(grid!.ID, data), out _, fastMode: false);

    private void Edit(BrickInstanceData data, VoxelActionType type)
    {
        Require(Ready() && grid != null && grid.IsInitialized && ReferenceEquals(grid.ID.Entity, grid), "Fixture grid unavailable.");
        Dependency.PlayerManager.NetworkedBuilder.LocalVoxelAction(
            new VoxelActionData { entityID = grid!.ID, brickData = data, type = type }, false, 0);
    }

    private static unsafe bool HasNeighbor(int from, int to)
    {
        var data = StorageResources.GetStorageData(from);
        for (int i = 0; i < 6; i++) if (data.neighbors[i] == to) return true;
        return false;
    }

    private static float Count(int id, int material)
    {
        int next = StorageResources.GetStorageData(id).rootItemId;
        float total = 0f;
        for (int i = 0; next != 0 && i < 16; i++)
        {
            var item = ItemInstanceResources.GetItemData(next);
            if (item.itemType == WorldItemType.brick && item.prefabId == material) total += item.quantity;
            next = item.nextElementId;
        }
        Require(next == 0, "Fixture cargo traversal exceeded its bound.");
        return total;
    }

    private static bool Waiting(Func<bool> ready, float deadline, string step)
    {
        if (Ready() && ready()) return false;
        if (Time.realtimeSinceStartup > deadline) throw new TimeoutException(step);
        return true;
    }

    private bool CanBuild()
    {
        var player = Dependency.PlayerManager;
        return SaveManager.CharacterName == "ConnectorProbe-" + session.Substring(0, 8)
            && !PlayerState.InMenu && !PlayerState.SystemEntered && player.Health != null && !player.Health.IsRespawning
            && player.TransformData.systemPosition.Magnitude >= 100f
            && GameIntroductionResources.instance?.Medbay?.inAnimation == false;
    }

    private static bool Ready()
        => GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe && !GameloopManager.jobStarted
            && PlayerState.sessionMode == PlayerState.SessionMode.solo && PlayerState.gameMode == PlayerState.GameMode.creative
            && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle && NetworkServer.active
            && NetworkServer.dontListen && Dependency.PlayerManager != null && Dependency.PlayerManager.IsLocal;

    private void Fail(Exception exception)
    {
        pending = false;
        done = true;
        Record("fail", exception.Message);
    }

    private void Record(string stage, string detail)
        => Logger.LogInfo($"KEEL_PASS_THROUGH_STORAGE|{session}|{stage}|{detail.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ')}");

    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }
}
