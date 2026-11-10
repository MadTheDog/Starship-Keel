using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Game;
using HarmonyLib;
using Sevo.Framework.Sdk;
using VoxelEngine;
using World;

namespace Sevo.Framework.Runtime
{
    /// <summary>Supplies prices that native stations pay players for eligible inventory items.</summary>
    public static class StationBuyOffers
    {
        private static readonly SortedDictionary<string, Func<ulong, string, StationType, ItemInstanceData, int?>> Offers =
            new SortedDictionary<string, Func<ulong, string, StationType, ItemInstanceData, int?>>(StringComparer.Ordinal);
        private static Action<string>? warning;
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);
        private static bool sealedRegistration;
        private static bool evaluating;

        public static bool IsAvailable { get; private set; }

        /// <summary>The callback receives system seed, station SGUID, station type and item.
        /// Return a positive native trade-unit price, or null to decline.</summary>
        public static void Register(string ownerId, string localName,
            Func<ulong, string, StationType, ItemInstanceData, int?> quote)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (quote == null) throw new ArgumentNullException(nameof(quote));
            if (sealedRegistration) throw new InvalidOperationException("Station buy offer registration is closed.");
            Offers.Add(key, quote);
        }

        internal static void Install(Harmony harmony, Action<string> reportWarning)
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(UI_StationLandingPage), "TryAddTradeableItem"),
                transpiler: new HarmonyMethod(typeof(StationBuyOffers), nameof(ReplacePriceCall)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(UI_StationLandingPage), "OnTradeMade"),
                transpiler: new HarmonyMethod(typeof(StationBuyOffers), nameof(ReplaceResalePriceCall)));
            warning = reportWarning;
            IsAvailable = true;
        }

        internal static void Disable() => IsAvailable = false;

        private static IEnumerable<CodeInstruction> ReplacePriceCall(IEnumerable<CodeInstruction> instructions)
            => ReplaceCall(instructions, nameof(GetBid), false);

        private static IEnumerable<CodeInstruction> ReplaceResalePriceCall(IEnumerable<CodeInstruction> instructions)
            => ReplaceCall(instructions, nameof(GetResalePrice), true);

        private static IEnumerable<CodeInstruction> ReplaceCall(IEnumerable<CodeInstruction> instructions, string replacement, bool resale)
        {
            var nativePrice = AccessTools.DeclaredMethod(typeof(ItemEconomy), nameof(ItemEconomy.GetItemPrice),
                new[] { typeof(StarData), typeof(ItemInstanceData), typeof(bool), typeof(ParentEntity) });
            var code = instructions.ToList();
            var call = code.Single(instruction => instruction.Calls(nativePrice));
            var bridge = typeof(StationBuyOffers).GetMethod(replacement, BindingFlags.Static | BindingFlags.NonPublic);
            if (resale)
            {
                call.opcode = OpCodes.Ldarg_2;
                call.operand = null;
                code.Insert(code.IndexOf(call) + 1, new CodeInstruction(OpCodes.Call, bridge));
            }
            else call.operand = bridge;
            return code;
        }

        private static float GetResalePrice(StarData star, ItemInstanceData item, bool isForBuy, ParentEntity station, float paidPrice)
        {
            float stock = ItemEconomy.GetItemPrice(star, item, isForBuy, station);
            if (!IsAvailable || PlayerState.sessionMode != PlayerState.SessionMode.solo || Offers.Count == 0)
                return stock;
            float nativeBid = ItemEconomy.GetItemPrice(star, item, false, station);
            return paidPrice != nativeBid ? Math.Max(stock, paidPrice) : stock;
        }

        private static float GetBid(StarData star, ItemInstanceData item, bool isForBuy, ParentEntity station)
        {
            float stock = ItemEconomy.GetItemPrice(star, item, isForBuy, station);
            if (!IsAvailable || isForBuy || evaluating || Offers.Count == 0
                || PlayerState.sessionMode != PlayerState.SessionMode.solo || station == null
                || item.itemType == WorldItemType.none || string.IsNullOrEmpty(station.SGUID)
                || station.EntityStation == null)
                return stock;

            sealedRegistration = true;
            evaluating = true;
            try
            {
                int? price = null;
                foreach (var offer in Offers)
                {
                    int? quote;
                    try
                    {
                        quote = offer.Value(star.seed, station.SGUID, station.EntityStation.StationType, item);
                        if (quote is <= 0 or > 1_000_000)
                            throw new InvalidOperationException("Quote must be between 1 and 1,000,000.");
                    }
                    catch (Exception exception)
                    {
                        if (Reported.Add(offer.Key)) warning?.Invoke($"Station buy offer {offer.Key} skipped: {exception.Message}");
                        continue;
                    }
                    if (!quote.HasValue) continue;
                    if (price.HasValue)
                    {
                        if (Reported.Add(offer.Key + ":conflict"))
                            warning?.Invoke($"Station buy offer {offer.Key} overlaps another quote. Using the stock price.");
                        return stock;
                    }
                    price = quote;
                }
                return price.HasValue ? price.Value : stock;
            }
            finally
            {
                evaluating = false;
            }
        }
    }
}
