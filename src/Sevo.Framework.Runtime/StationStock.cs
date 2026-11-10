using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Game;
using HarmonyLib;
using Sevo.Framework.Sdk;
using VoxelEngine;
using World;

namespace Sevo.Framework.Runtime
{
    /// <summary>Adds initial offers to existing native fuel and trading markets.</summary>
    public static class StationStock
    {
        private static readonly SortedDictionary<string, Offer> Offers = new SortedDictionary<string, Offer>(StringComparer.Ordinal);
        private static readonly ConditionalWeakTable<EntityStation, object> Populated = new ConditionalWeakTable<EntityStation, object>();
        private static readonly object Marker = new object();
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);
        private static Action<string>? warning;
        private static int gameThread;
        private static bool sealedRegistration;

        public static bool IsAvailable { get; private set; }

        public static void RegisterItem(string ownerId, string localName, string itemKey, int quantity, int price,
            Func<ulong, string, StationType, bool> matchesStation)
        {
            ContentAllocator.ValidateKey(itemKey);
            Register(ownerId, localName, new Offer(itemKey, -1, quantity, price, matchesStation));
        }

        public static void RegisterBrick(string ownerId, string localName, int brickId, int quantity, int price,
            Func<ulong, string, StationType, bool> matchesStation)
        {
            if (brickId < 0) throw new ArgumentOutOfRangeException(nameof(brickId));
            Register(ownerId, localName, new Offer(null, brickId, quantity, price, matchesStation));
        }

        private static void Register(string ownerId, string localName, Offer offer)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (sealedRegistration) throw new InvalidOperationException("Station stock registration is closed.");
            Offers.Add(key, offer);
        }

        /// <summary>Opens an existing native market from a mod's local interaction.</summary>
        public static bool TryOpenKiosk(ParentEntity station)
        {
            if (!CanUseMarket(station, out _)) return false;
            UI_StationLandingPage.OpenHubUI(station, null, true);
            return true;
        }

        internal static void Install(Harmony harmony, Action<string> reportWarning)
        {
            var build = AccessTools.DeclaredMethod(typeof(UI_StationLandingPage), "BuildStationItemsForSaleList");
            harmony.Patch(build, prefix: new HarmonyMethod(typeof(StationStock), nameof(BeforeBuild)));
            warning = reportWarning;
            gameThread = Environment.CurrentManagedThreadId;
            IsAvailable = true;
        }

        internal static void Disable() => IsAvailable = false;

        private static bool CanUseMarket(ParentEntity station, out EntityStation market)
        {
            market = null!;
            if (!IsAvailable || Environment.CurrentManagedThreadId != gameThread
                || PlayerState.sessionMode != PlayerState.SessionMode.solo || station == null || station.WillBeUnspawned)
                return false;
            var component = station.EntityStation;
            if (component == null || component.stationUltimateParent != component || station.Identity == null
                || string.IsNullOrEmpty(station.SGUID))
                return false;
            var role = station.Identity.consortiumType;
            if (role != ConsortiumType.fuel && role != ConsortiumType.trader) return false;
            market = component;
            return true;
        }

        private static void BeforeBuild(ParentEntity ___station)
        {
            if (Offers.Count == 0 || !CanUseMarket(___station, out var market)
                || Populated.TryGetValue(market, out _))
                return;
            if (market.itemToSell.Count != market.itemToSellPrices.Count)
            {
                warning?.Invoke("Station stock skipped because the native item and price counts differ.");
                return;
            }

            sealedRegistration = true;
            Populated.Add(market, Marker);
            ulong seed = PlanetarySystem.CurrentStarData.seed;
            foreach (var pair in Offers)
            {
                var offer = pair.Value;
                try
                {
                    if (!offer.MatchesStation(seed, ___station.SGUID, market.StationType)) continue;
                    if (!offer.TryCreateItem(out var item))
                        throw new InvalidOperationException("The item identity is unavailable.");
                    item.Register();
                    market.itemToSell.Add(item);
                    market.itemToSellPrices.Add(offer.Price);
                }
                catch (Exception exception)
                {
                    if (Reported.Add(pair.Key))
                        warning?.Invoke($"Station stock offer {pair.Key} skipped: {exception.Message}");
                }
            }
        }

        private sealed class Offer
        {
            private readonly string? itemKey;
            private readonly int brickId;
            private readonly int quantity;
            internal readonly int Price;
            internal readonly Func<ulong, string, StationType, bool> MatchesStation;

            internal Offer(string? itemKey, int brickId, int quantity, int price,
                Func<ulong, string, StationType, bool> matchesStation)
            {
                if (quantity <= 0 || !ItemInstanceCommon.IsValidStackQuantity(quantity))
                    throw new ArgumentOutOfRangeException(nameof(quantity));
                if (price is <= 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(price));
                this.itemKey = itemKey;
                this.brickId = brickId;
                this.quantity = quantity;
                Price = price;
                MatchesStation = matchesStation ?? throw new ArgumentNullException(nameof(matchesStation));
            }

            internal bool TryCreateItem(out ItemInstanceData item)
            {
                if (itemKey != null) return ItemRegistration.TryCreateItemData(itemKey, quantity, out item);
                item = default;
                var bricks = Brick.BRICK_INDEX;
                if (bricks == null || (uint)brickId >= (uint)bricks.Length || bricks[brickId] == null) return false;
                item = new CraftingElement { id = brickId, itemType = WorldItemType.brick, quantity = quantity }.ToItemData;
                return true;
            }
        }
    }
}
