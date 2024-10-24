using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CriticalCommonLib.Models;
using CriticalCommonLib.Services;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.Reflection;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InventoryTools.IPC;
using Lumina.Excel.GeneratedSheets;
using Microsoft.Extensions.Logging;
using static ECommons.GenericHelpers;
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using InventoryItem = FFXIVClientStructs.FFXIV.Client.Game.InventoryItem;
using InventoryType = FFXIVClientStructs.FFXIV.Client.Game.InventoryType;
using MemoryHelper = Dalamud.Memory.MemoryHelper;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace InventoryTools.Services
{
    public class ItemInfo(uint itemId, uint quantity, uint hqQuantity)
    {
        public uint ItemId { get; set; } = itemId;
        public uint Quantity { get; set; } = quantity;
        public uint HQQuantity { get; set; } = hqQuantity;
    }
    public class RestockService : IDisposable
    {
        private readonly IDalamudPluginInterface _pluginInterface;
        private ILogger<RestockService> _logger;
        private IInventoryMonitor _inventoryMonitor;
        private int firstFoundQuantity = 0;
        private static readonly HashSet<int> _inventoryTypes = [10000, 10001, 10002, 10003, 10004, 10005, 10006, 12001];
        private bool _inventoryChanged;
        private TaskManager TM = new();
        internal static bool GenericThrottle => EzThrottler.Throttle("RestockFromRetainerThrottler", 100);
        internal static void RethrottleGeneric(int num) => EzThrottler.Throttle("RestockFromRetainerThrottler", num, true);
        internal static void RethrottleGeneric() => EzThrottler.Throttle("RestockFromRetainerThrottler", 100, true);

        public RestockService(IDalamudPluginInterface pluginInterface, ILogger<RestockService> logger, IInventoryMonitor inventoryMonitor)
        {
            _pluginInterface = pluginInterface;
            _logger = logger;
            _inventoryMonitor = inventoryMonitor;
            _inventoryMonitor.OnInventoryChanged += InventoryMonitorOnOnInventoryChanged;
        }
        public void Dispose()
        {
            _inventoryMonitor.OnInventoryChanged -= InventoryMonitorOnOnInventoryChanged;
        }

        private void InventoryMonitorOnOnInventoryChanged(List<InventoryChange> inventoryChanges, InventoryMonitor.ItemChanges? changedItems)
        {
            if (changedItems != null && (changedItems.NewItems.Any(item => item.ItemId != 1) || changedItems.RemovedItems.Any(item => item.ItemId != 1)))
            {
                _inventoryChanged = true;
            }
        }

        private uint ItemCount(ulong retainerId, uint itemId, bool hqonly = false)
        {
            return (uint)_inventoryMonitor.AllItems
                .Where(c => c.RetainerId == retainerId
                    && c.ItemId == itemId
                    && (!hqonly || c.Flags == InventoryItem.ItemFlags.HighQuality)
                    && _inventoryTypes.Contains((int)c.SortedContainer)
                    )
                .Sum(c => c.Quantity);
        }

        private (uint, uint) GetRetainerItemCount(ulong retainerId, uint itemId)
        {
            if (!Svc.ClientState.IsLoggedIn || Svc.Condition[ConditionFlag.OnFreeTrial]) return (0, 0);
            return (ItemCount(retainerId, itemId), ItemCount(retainerId, itemId, true));
        }
        private unsafe ulong GetRetainerId(int index)
        {
            ulong retainerId = 0;
            var retainer = RetainerManager.Instance()->GetRetainerBySortedIndex((uint)index);
            if (retainer->Available)
                retainerId = retainer->RetainerId;
            return retainerId;
        }

        private bool processStart = false;

        internal void RestockFromRetainers(Dictionary<uint, int> requiredItems)
        {
            processStart = false;
            TM.Enqueue(() => RestockFromRetainer(requiredItems, 0));
        }
        private void RestockFromRetainer(Dictionary<uint, int> requiredItems, int retainerIndex)
        {
            requiredItems = requiredItems.Where(x => x.Value > 0).ToDictionary();
            if (requiredItems.Count == 0 || retainerIndex < 0 || retainerIndex >= 10)
            {
                if (processStart)
                {
                    TM.DelayNext("CloseRetainerList", 200);
                    TM.Enqueue(RetainerListHandlers.CloseRetainerList);
                    TM.Enqueue(YesAlready.Unlock);
                    TM.Enqueue(AutoRetainer.Unsuppress);
                    TM.Enqueue(() => Svc.Framework.Update -= Tick);
                    processStart = false;
                }
                return;
            }

            var retainer = GetRetainerId(retainerIndex);
            if (retainer != 0)
            {
                var retainerSelected = false;
                foreach (var (item, count) in requiredItems)
                {
                    if (count <= 0)
                        continue;
                    var itemCount = GetRetainerItemCount(retainer, item);
                    if (itemCount.Item1 == 0)
                        continue;
                    if (!processStart)
                    {
                        if (PlayerWorldHandlers.GetReachableRetainerBell() == null)
                        {
                            Svc.Chat.Print("[Restock] 没有可交互的传唤铃。");
                            return;
                        }
                        Svc.Chat.Print("[Restock] 开始自动补货..");
                        TM.Enqueue(() => Svc.Framework.Update += Tick);
                        TM.Enqueue(AutoRetainer.Suppress);
                        TM.Enqueue(YesAlready.Lock);
                        TM.Enqueue(PlayerWorldHandlers.SelectNearestBell);
                        TM.Enqueue(PlayerWorldHandlers.InteractWithTargetedBell);
                        TM.DelayNext("BellInteracted", 200);
                        processStart = true;
                    }
                    if (!retainerSelected)
                    {
                        TM.Enqueue(() => RetainerListHandlers.SelectRetainerByID(retainer));
                        TM.DelayNext("WaitToSelectEntrust", 200);
                        TM.Enqueue(RetainerHandlers.SelectEntrustItems);
                        TM.DelayNext("EntrustSelected", 200);
                        retainerSelected = true;
                    }
                    TM.DelayNext("SwitchItems", 200);
                    TM.Enqueue(() => ExtractItem(requiredItems, retainer, item));
                }
                if (retainerSelected)
                {
                    TM.DelayNext("CloseRetainer", 200);
                    TM.Enqueue(RetainerHandlers.CloseAgentRetainer);
                    TM.DelayNext("ClickQuit", 200);
                    TM.Enqueue(RetainerHandlers.SelectQuit);
                    retainerSelected = false;
                }
            }
            TM.Enqueue(() => RestockFromRetainer(requiredItems, retainerIndex + 1));
        }

        private static unsafe void Tick(IFramework framework)
        {
            if (Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            {
                if (TryGetAddonByName<AddonTalk>("Talk", out var addon) && addon->AtkUnitBase.IsVisible)
                {
                    new AddonMaster.Talk((nint)addon).Click();
                }
            }
        }

        private bool ExtractItem(Dictionary<uint, int> requiredItems, ulong retainerId, uint itemId)
        {
            if (requiredItems[itemId] <= 0)
                return true;
            _inventoryChanged = false;
            var itemCount = GetRetainerItemCount(retainerId, itemId);
            if (itemCount.Item1 == 0)
                return true;
            bool lookingForHQ = itemCount.Item2 > 0;
            Svc.Log.Debug($"HQ?: {lookingForHQ}");
            TM.EnqueueImmediate(() => RetainerHandlers.WaitOnRetainerInventory());
            TM.EnqueueImmediate(() => RetainerHandlers.OpenItemContextMenu(itemId, lookingForHQ, out firstFoundQuantity), 300);
            TM.DelayNextImmediate("WaitOnNumericPopup", 200);
            TM.EnqueueImmediate(() =>
            {
                var value = Math.Min(requiredItems[itemId], firstFoundQuantity);
                if (value == 0) return true;
                if (firstFoundQuantity == 1 || RetainerHandlers.InputNumericValue(value))
                {
                    requiredItems[itemId] -= value;
                    Svc.Chat.Print($"[Restock] 取出了 {value}x {Svc.Data.GetExcelSheet<Item>().GetRow(itemId).Name}。");
                    TM.EnqueueImmediate(() => _inventoryChanged);
                    TM.EnqueueImmediate(() =>
                    {
                        ExtractItem(requiredItems, retainerId, itemId);
                    }, "RecursiveExtract");
                    return true;
                }
                else
                {
                    return false;
                }
            }, 1000);
            return true;
        }
    }


    internal unsafe static class PlayerWorldHandlers
    {
        private static string BellName => Svc.Data.GetExcelSheet<EObjName>().GetRow(2000401).Singular.ToString();

        internal static IGameObject? GetReachableRetainerBell()
        {
            foreach (var x in Svc.Objects)
            {
                if ((x.ObjectKind == ObjectKind.Housing || x.ObjectKind == ObjectKind.EventObj) && x.Name.ToString().EqualsIgnoreCaseAny(BellName, "リテイナーベル"))
                {
                    if (Vector3.Distance(x.Position, Svc.ClientState.LocalPlayer.Position) < GetValidInteractionDistance(x) && x.IsTargetable)
                    {
                        return x;
                    }
                }
            }
            return null;
        }

        private static float GetValidInteractionDistance(IGameObject bell)
        {
            if (bell.ObjectKind == ObjectKind.Housing)
            {
                return 6.5f;
            }
            else if (Inns.List.Contains(Svc.ClientState.TerritoryType))
            {
                return 4.75f;
            }
            else
            {
                return 4.6f;
            }
        }


        internal static bool? SelectNearestBell()
        {
            if (Svc.Condition[ConditionFlag.OccupiedSummoningBell]) return true;
            if (!IsOccupied())
            {
                var x = GetReachableRetainerBell();
                if (x != null)
                {
                    if (RestockService.GenericThrottle)
                    {
                        Svc.Targets.Target = x;
                        Svc.Log.Debug($"Set target to {x}");
                        return true;
                    }
                }
            }
            return false;
        }

        internal static bool? InteractWithTargetedBell()
        {
            if (Svc.Condition[ConditionFlag.OccupiedSummoningBell]) return true;
            var x = Svc.Targets.Target;
            if (x != null && (x.ObjectKind == ObjectKind.Housing || x.ObjectKind == ObjectKind.EventObj) && x.Name.ToString().EqualsIgnoreCaseAny(BellName, "リテイナーベル") && !IsOccupied())
            {
                if (Vector3.Distance(x.Position, Svc.ClientState.LocalPlayer.Position) < GetValidInteractionDistance(x) && x.IsTargetable)
                {
                    if (RestockService.GenericThrottle && EzThrottler.Throttle("InteractWithBell", 1000))
                    {
                        //TargetSystem.Instance()->InteractWithObject((GameObject*)x.Address, false);
                        TargetSystem.Instance()->OpenObjectInteraction((GameObject*)x.Address);
                    }
                }
            }
            return false;
        }
    }

    internal unsafe static class RetainerListHandlers
    {
        internal static bool? SelectRetainerByID(ulong id)
        {
            string retainerName = "";
            for (uint i = 0; i < 10; i++)
            {
                var retainer = RetainerManager.Instance()->GetRetainerBySortedIndex(i);
                if (retainer == null) continue;

                if (retainer->RetainerId == id)
                    retainerName = retainer->NameString;
            }

            return SelectRetainerByName(retainerName);
        }

        internal static bool? SelectRetainerByName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new Exception($"Name can not be null or empty");
            }
            if (TryGetAddonByName<AtkUnitBase>("RetainerList", out var retainerList) && IsAddonReady(retainerList))
            {
                var list = new AddonMaster.RetainerList(retainerList);
                foreach (var retainer in list.Retainers)
                {
                    if (retainer.Name == name)
                    {
                        if (RestockService.GenericThrottle)
                        {
                            Svc.Log.Debug($"Selecting retainer {retainer.Name} with index {retainer.Index}");
                            retainer.Select();
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        internal static bool? CloseRetainerList()
        {
            if (TryGetAddonByName<AtkUnitBase>("RetainerList", out var retainerList) && IsAddonReady(retainerList))
            {
                if (RestockService.GenericThrottle)
                {
                    Callback.Fire(retainerList, false, -1);
                    return true;
                }
            }
            return false;
        }
    }

    internal unsafe static class RetainerHandlers
    {
        internal static unsafe bool WaitOnRetainerInventory()
        {
            return TryGetAddonByName<AtkUnitBase>("InventoryRetainer", out var addon) && IsAddonReady(addon)
                || TryGetAddonByName<AtkUnitBase>("InventoryRetainerLarge", out var addon2) && IsAddonReady(addon2);
        }
        internal static bool? SelectQuit()
        {
            var text = Svc.Data.GetExcelSheet<Addon>().GetRow(2383).Text.ToDalamudString().ExtractText();
            return TrySelectSpecificEntry(text);
        }

        internal static bool? SelectEntrustItems()
        {
            //2378	Entrust or withdraw items.
            var text = Svc.Data.GetExcelSheet<Addon>().GetRow(2378).Text.ToDalamudString().ExtractText(true);
            return TrySelectSpecificEntry(text);
        }

        internal static bool? OpenItemContextMenu(uint ItemId, bool lookingForHQ, out int quantity)
        {
            quantity = 0;
            var inventories = new List<InventoryType>
            {
                InventoryType.RetainerPage1,
                InventoryType.RetainerPage2,
                InventoryType.RetainerPage3,
                InventoryType.RetainerPage4,
                InventoryType.RetainerPage5,
                InventoryType.RetainerPage6,
                InventoryType.RetainerPage7,
                InventoryType.RetainerCrystals
            };

            foreach (var inv in inventories)
            {
                for (int i = 0; i < InventoryManager.Instance()->GetInventoryContainer(inv)->Size; i++)
                {
                    var item = InventoryManager.Instance()->GetInventoryContainer(inv)->GetInventorySlot(i);
                    if (item->ItemId == ItemId && (lookingForHQ && item->Flags == InventoryItem.ItemFlags.HighQuality || !lookingForHQ))
                    {
                        quantity = (int)item->Quantity;
                        var ag = AgentInventoryContext.Instance();
                        ag->OpenForItemSlot(inv, i, AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer)->GetAddonId());
                        var contextMenu = (AtkUnitBase*)Svc.GameGui.GetAddonByName("ContextMenu", 1);
                        if (contextMenu != null)
                        {
                            var contextAgent = AgentInventoryContext.Instance();
                            var indexOfRetrieveAll = -1;
                            var indexOfRetrieveQuantity = -1;

                            int looper = 0;
                            foreach (var contextObj in contextAgent->EventParams)
                            {
                                if (contextObj.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String)
                                {
                                    var label = MemoryHelper.ReadSeStringNullTerminated(new nint(contextObj.String));

                                    if (Svc.Data.GetExcelSheet<Addon>().GetRow(98).Text == label.TextValue) indexOfRetrieveAll = looper;
                                    if (Svc.Data.GetExcelSheet<Addon>().GetRow(773).Text == label.TextValue) indexOfRetrieveQuantity = looper;

                                    looper++;
                                }
                            }

                            if (item->Quantity == 1 || item->ItemId <= 19)
                            {
                                if (indexOfRetrieveAll == -1) return true;
                                Callback.Fire(contextMenu, true, 0, indexOfRetrieveAll, 0, 0, 0);
                            }
                            else
                            {
                                if (indexOfRetrieveQuantity == -1) return true;
                                Callback.Fire(contextMenu, true, 0, indexOfRetrieveQuantity, 0, 0, 0);
                            }
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        internal static bool InputNumericValue(int value)
        {
            var numeric = (AtkUnitBase*)Svc.GameGui.GetAddonByName("InputNumeric", 1);
            if (numeric != null)
            {
                Svc.Log.Debug($"{value}");
                Callback.Fire(numeric, true, value);
                return true;
            }
            return false;
        }

        internal static bool? CloseAgentRetainer()
        {
            var a = CSFramework.Instance()->UIModule->GetAgentModule()->GetAgentByInternalId(AgentId.Retainer);
            if (a->IsAgentActive())
            {
                a->Hide();
                return true;
            }
            return false;
        }

        internal static bool TrySelectSpecificEntry(string text)
        {
            return TrySelectSpecificEntry([text]);
        }

        internal static bool TrySelectSpecificEntry(IEnumerable<string> text)
        {
            if (TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase))
            {
                var entry = GetEntries(addon).FirstOrDefault(x => x.StartsWithAny(text));
                if (entry != null)
                {
                    var index = GetEntries(addon).IndexOf(entry);
                    if (index >= 0 && IsSelectItemEnabled(addon, index) && RestockService.GenericThrottle)
                    {
                        new AddonMaster.SelectString((nint)addon).Entries[(ushort)index].Select();
                        return true;
                    }
                }
            }
            else
            {
                RestockService.RethrottleGeneric();
            }
            return false;
        }

        internal static bool IsSelectItemEnabled(AddonSelectString* addon, int index)
        {
            var step1 = (AtkTextNode*)addon->AtkUnitBase
                        .UldManager.NodeList[2]
                        ->GetComponent()->UldManager.NodeList[index + 1]
                        ->GetComponent()->UldManager.NodeList[3];
            return GenericHelpers.IsSelectItemEnabled(step1);
        }

        internal static List<string> GetEntries(AddonSelectString* addon)
        {
            var list = new List<string>();
            for (int i = 0; i < addon->PopupMenu.PopupMenu.EntryCount; i++)
            {
                list.Add(MemoryHelper.ReadSeStringNullTerminated((nint)addon->PopupMenu.PopupMenu.EntryNames[i]).ExtractText());
            }
            return list;
        }
    }
}

namespace InventoryTools.IPC
{
    internal static class AutoRetainer
    {
        internal static bool ReEnable = false;
        internal static bool IsEnabled()
        {
            if (DalamudReflector.TryGetDalamudPlugin("AutoRetainer", out var _, false, true))
            {
                ReEnable = Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed").InvokeFunc();
                return ReEnable;
            }
            return false;
        }

        internal static void Suppress()
        {
            if (IsEnabled() && DalamudReflector.TryGetDalamudPlugin("AutoRetainer", out var _, false, true))
            {
                Svc.PluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(true);
            }
        }

        internal static void Unsuppress()
        {
            if (ReEnable && DalamudReflector.TryGetDalamudPlugin("AutoRetainer", out var _, false, true))
            {
                Svc.PluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(false);
                ReEnable = false;
            }
        }
    }

    internal static class YesAlready
    {
        internal static Version Version => Svc.PluginInterface.InstalledPlugins.FirstOrDefault(x => x.IsLoaded && x.InternalName == "YesAlready")?.Version ?? new();
        internal static readonly Version NewVersion = new("1.4.0.0");
        internal static bool Reenable = false;
        internal static HashSet<string>? Data = null;

        internal static void GetData()
        {
            if (Data != null) return;
            if (Svc.PluginInterface.TryGetData<HashSet<string>>("YesAlready.StopRequests", out var data))
            {
                Data = data;
            }
        }

        internal static void Lock()
        {
            if (Version != null)
            {
                if (Version < NewVersion)
                {
                    if (DalamudReflector.TryGetDalamudPlugin("Yes Already", out var pl, false, true))
                    {
                        Svc.Log.Information("Disabling Yes Already (old)");
                        pl.GetStaticFoP("YesAlready.Service", "Configuration").SetFoP("Enabled", false);
                        Reenable = true;
                    }
                }
                else
                {
                    GetData();
                    if (Data != null)
                    {
                        Svc.Log.Information("Disabling Yes Already (new)");
                        Data.Add(Svc.PluginInterface.InternalName);
                        Reenable = true;
                    }
                }
            }
        }

        internal static void Unlock()
        {
            if (Reenable && Version != null)
            {
                if (Version < NewVersion)
                {
                    if (DalamudReflector.TryGetDalamudPlugin("Yes Already", out var pl, false, true))
                    {
                        Svc.Log.Information("Enabling Yes Already");
                        pl.GetStaticFoP("YesAlready.Service", "Configuration").SetFoP("Enabled", true);
                        Reenable = false;
                    }
                }
                else
                {
                    GetData();
                    if (Data != null)
                    {
                        Svc.Log.Information("Enabling Yes Already (new)");
                        Data.Remove(Svc.PluginInterface.InternalName);
                        Reenable = false;
                    }
                }
            }
        }

        internal static bool IsEnabled()
        {
            if (Version != null)
            {
                if (Version < NewVersion)
                {
                    if (DalamudReflector.TryGetDalamudPlugin("Yes Already", out var pl, false, true))
                    {
                        return pl.GetStaticFoP("YesAlready.Service", "Configuration").GetFoP<bool>("Enabled");
                    }
                }
                else
                {
                    GetData();
                    if (Data != null)
                    {
                        return !Data.Contains(Svc.PluginInterface.InternalName);
                    }
                }
            }

            return false;
        }

        internal static bool? WaitForYesAlreadyDisabledTask()
        {
            return !IsEnabled();
        }
    }

}