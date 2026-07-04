# Auto-Deposit Crafting Outputs to Retainer — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When free inventory slots drop to a configured threshold between crafts, Artisan deposits the current session's crafted outputs into a user-chosen retainer via the nearest bell, then resumes crafting.

**Architecture:** A new static `AutoDepositManager` (modeled on `RepairManager`'s "true = done, false = busy" contract) is called from the two existing between-craft interposition points (Endurance update loop and crafting-list processor, right after the repair check). It enqueues a deposit task chain on the existing `RetainerInfo.TM` bell machinery. A new `RetainerHandlers.EntrustItem` mirrors the existing withdraw-side `OpenItemContextMenu`, using Addon-sheet rows 97 ("Entrust to Retainer") and 772 ("Entrust Quantity") instead of 98/773.

**Tech Stack:** C# Dalamud plugin (net9.0-windows), FFXIVClientStructs, ECommons, ImGui.

**Spec:** `docs/superpowers/specs/2026-07-03-auto-deposit-retainer-design.md`

## Global Constraints

- No automated test infrastructure exists in this repo and the code is game-interop; per task, verification = `dotnet build Artisan.sln` compiling with no NEW errors or warnings, plus the in-game manual checklist in Task 6.
- Follow existing file idioms exactly: `changed |= ImGui.Checkbox(...)` pattern in `PluginUI.cs`, `TM.Enqueue`/`TM.DelayNext` task chains as in `RetainerInfo.cs`, addon-sheet label matching (never hardcoded English strings, never hardcoded context-menu indices).
- Failure mode is notify-once-and-keep-crafting: on any deposit failure (no bell, no retainer selected, retainer full), print ONE `DuoLog.Warning` and set a back-off flag that is only cleared when Endurance or a list is (re)started. Never pause crafting, never loop on the bell.
- Never deposit: crystals (ItemId ≤ 19), collectibles (`Item.IsCollectable`), or items still required as ingredients by remaining uncrafted list entries.
- All commits on `main` (repo convention — no feature branches in this repo's history), message style: short imperative summary.

---

### Task 1: Configuration fields

**Files:**
- Modify: `Artisan\Configuration.cs:89-90` (add after the existing retainer maps)

**Interfaces:**
- Produces: `P.Config.AutoDepositCrafts` (bool), `P.Config.AutoDepositRetainers` (`Dictionary<ulong, ulong>`, character ContentId → RetainerId), `P.Config.AutoDepositFreeSlotThreshold` (int, default 5). All later tasks consume these exact names.

- [ ] **Step 1: Add the three config fields**

In `Artisan\Configuration.cs`, directly below these existing lines (currently lines 89–90):

```csharp
        public Dictionary<ulong, ulong> RetainerIDs = new Dictionary<ulong, ulong>();
        public HashSet<ulong> UnavailableRetainerIDs = new HashSet<ulong>();
```

add:

```csharp
        public bool AutoDepositCrafts = false;
        public Dictionary<ulong, ulong> AutoDepositRetainers = new Dictionary<ulong, ulong>();
        public int AutoDepositFreeSlotThreshold = 5;
```

(`AutoDepositRetainers` is keyed by character ContentId, value is the chosen RetainerId — note this is the reverse orientation of `RetainerIDs`, which maps RetainerId → ContentId.)

- [ ] **Step 2: Build**

Run: `dotnet build Artisan.sln`
Expected: Build succeeds with no new errors (pre-existing warnings are fine).

- [ ] **Step 3: Commit**

```bash
git add Artisan/Configuration.cs
git commit -m "Add auto-deposit config fields"
```

---

### Task 2: EntrustItem handler + expose RetainerInfo.Tick

**Files:**
- Modify: `Artisan\Tasks\TaskSelectRetainer.cs` (add method to `RetainerHandlers`, after `OpenItemContextMenu` which ends at line 198)
- Modify: `Artisan\IPC\RetainerInfo.cs:501` (visibility change only)

**Interfaces:**
- Consumes: existing `RetainerHandlers.InputNumericValue(int)`, `LuminaSheets.AddonSheet`, `RetainerInfo.GenericThrottle` patterns already in the file.
- Produces: `RetainerHandlers.EntrustItem(uint ItemId, out int quantity)` returning `bool?` — `true` if it clicked an entrust context-menu entry (quantity = stack size found; a numeric popup follows when quantity > 1), `true` with `quantity == 0` if no matching stack remains in player inventory (normal termination), `false` if the context menu was not available. Also `RetainerInfo.Tick` becomes `internal` so `AutoDepositManager` can hook it.

- [ ] **Step 1: Make `RetainerInfo.Tick` internal**

In `Artisan\IPC\RetainerInfo.cs` line 501, change:

```csharp
        private static unsafe void Tick(IFramework framework)
```

to:

```csharp
        internal static unsafe void Tick(IFramework framework)
```

- [ ] **Step 2: Add `EntrustItem` to `RetainerHandlers`**

In `Artisan\Tasks\TaskSelectRetainer.cs`, inside `internal unsafe static class RetainerHandlers`, immediately after the closing brace of `OpenItemContextMenu` (line 198), add:

```csharp
    internal static bool? EntrustItem(uint ItemId, out int quantity)
    {
        quantity = 0;
        var inventories = new List<InventoryType>
        {
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
        };

        foreach (var inv in inventories)
        {
            for (int i = 0; i < InventoryManager.Instance()->GetInventoryContainer(inv)->Size; i++)
            {
                var item = InventoryManager.Instance()->GetInventoryContainer(inv)->GetInventorySlot(i);
                if (item->ItemId == ItemId)
                {
                    quantity = item->Quantity;
                    var ag = AgentInventoryContext.Instance();
                    ag->OpenForItemSlot(inv, i, 0, AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer)->GetAddonId());
                    var contextMenu = (AtkUnitBase*)Svc.GameGui.GetAddonByName("ContextMenu", 1).Address;
                    var contextAgent = AgentInventoryContext.Instance();
                    var indexOfEntrust = -1;
                    var indexOfEntrustQuantity = -1;

                    int looper = 0;
                    foreach (var contextObj in contextAgent->EventParams)
                    {
                        if (contextObj.Type == AtkValueType.String)
                        {
                            var label = MemoryHelper.ReadSeStringNullTerminated(new IntPtr(contextObj.String));

                            if (LuminaSheets.AddonSheet[97].Text == label.TextValue) indexOfEntrust = looper;
                            if (LuminaSheets.AddonSheet[772].Text == label.TextValue) indexOfEntrustQuantity = looper;

                            looper++;
                        }
                    }

                    if (contextMenu != null)
                    {
                        if (item->Quantity == 1)
                        {
                            if (indexOfEntrust == -1) return true;
                            Callback.Fire(contextMenu, true, 0, indexOfEntrust, 0, 0, 0);
                        }
                        else
                        {
                            if (indexOfEntrustQuantity == -1) return true;
                            Callback.Fire(contextMenu, true, 0, indexOfEntrustQuantity, 0, 0, 0);
                        }
                        return true;
                    }
                }
            }
        }
        return true;
    }
```

Notes on intent (mirrors `OpenItemContextMenu` exactly, with these deliberate differences):
- Scans player pages `Inventory1–4` instead of retainer pages.
- Addon rows 97/772 ("Entrust to Retainer"/"Entrust Quantity") instead of 98/773 (verified against the game's Addon sheet — same rows the retrieve code uses, mirrored).
- Final `return true` (not `false`): "no stack found" is the normal termination signal for the recursive deposit loop; the caller distinguishes it by `quantity == 0`.
- Matches any stack regardless of NQ/HQ flags — both get deposited, one stack per call.

No new usings are needed: the file already imports `FFXIVClientStructs.FFXIV.Client.Game`, `FFXIVClientStructs.FFXIV.Client.UI.Agent`, `ECommons.Automation`, and aliases `MemoryHelper`.

- [ ] **Step 3: Build**

Run: `dotnet build Artisan.sln`
Expected: Build succeeds with no new errors.

- [ ] **Step 4: Commit**

```bash
git add Artisan/Tasks/TaskSelectRetainer.cs Artisan/IPC/RetainerInfo.cs
git commit -m "Add EntrustItem retainer handler for depositing player inventory items"
```

---

### Task 3: AutoDepositManager

**Files:**
- Create: `Artisan\Autocraft\AutoDepositManager.cs`

**Interfaces:**
- Consumes: Task 1 config fields; Task 2 `RetainerHandlers.EntrustItem(uint, out int)` and `RetainerInfo.Tick`; existing `RetainerInfo.TM`, `RetainerInfo.GetReachableRetainerBell()`, `TaskInteractWithNearestBell.EnqueueBell()`, `RetainerListHandlers.SelectRetainerByID(ulong)`, `RetainerHandlers.SelectEntrustItems()`, `RetainerHandlers.InputNumericValue(int)`, `RetainerHandlers.CloseAgentRetainer()`, `RetainerHandlers.SelectQuit()`, `RetainerListHandlers.CloseRetainerList()`, `AutoRetainerIPC.Suppress()/Unsuppress()`, `YesAlready.Unlock()`, `CraftingListUI.NumberOfIngredient(uint)`, `CraftingListFunctions.CurrentIndex`, `Endurance.RecipeID`, `LuminaSheets.RecipeSheet/ItemSheet`, `Recipe.Ingredients()` extension (`Artisan.RawInformation.HelperExtensions`).
- Produces (consumed by Tasks 4–5):
  - `internal static bool ProcessDeposit(NewCraftingList? list = null)` — true = nothing to do / backed off, false = deposit chain running (caller exits craft stance and waits)
  - `internal static void ResetBackoff()`
  - `public static int GetFreeInventorySlots()`
  - `public static List<(ulong Id, string Name)> GetCharacterRetainers()`

- [ ] **Step 1: Create the file with the full implementation**

Create `Artisan\Autocraft\AutoDepositManager.cs`:

```csharp
using Artisan.CraftingLists;
using Artisan.IPC;
using Artisan.RawInformation;
using Artisan.Tasks;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.Logging;
using FFXIVClientStructs.FFXIV.Client.Game;
using System.Collections.Generic;
using System.Linq;

namespace Artisan.Autocraft
{
    public static unsafe class AutoDepositManager
    {
        internal static bool DepositFailed;
        private static bool depositRunning;
        private static int foundQuantity;

        public static int GetFreeInventorySlots()
        {
            var inventories = new[]
            {
                InventoryType.Inventory1,
                InventoryType.Inventory2,
                InventoryType.Inventory3,
                InventoryType.Inventory4,
            };

            int free = 0;
            foreach (var inv in inventories)
            {
                var container = InventoryManager.Instance()->GetInventoryContainer(inv);
                if (container == null) continue;
                for (int i = 0; i < container->Size; i++)
                {
                    if (container->GetInventorySlot(i)->ItemId == 0)
                        free++;
                }
            }
            return free;
        }

        public static List<(ulong Id, string Name)> GetCharacterRetainers()
        {
            List<(ulong Id, string Name)> result = new();
            if (!Svc.ClientState.IsLoggedIn) return result;
            var rm = RetainerManager.Instance();
            if (rm == null) return result;
            for (uint i = 0; i < 10; i++)
            {
                var retainer = rm->GetRetainerBySortedIndex(i);
                if (retainer == null || retainer->RetainerId == 0 || !retainer->Available) continue;
                result.Add((retainer->RetainerId, retainer->NameString));
            }
            return result;
        }

        internal static void ResetBackoff() => DepositFailed = false;

        internal static bool ProcessDeposit(NewCraftingList? list = null)
        {
            if (!P.Config.AutoDepositCrafts || DepositFailed) return true;
            if (depositRunning || RetainerInfo.TM.IsBusy) return !depositRunning;
            if (GetFreeInventorySlots() > P.Config.AutoDepositFreeSlotThreshold) return true;

            if (!P.Config.AutoDepositRetainers.TryGetValue(Svc.PlayerState.ContentId, out var retainerId) || retainerId == 0)
            {
                Fail("Auto-deposit is enabled but no retainer is selected in the Artisan settings. Continuing to craft.");
                return true;
            }

            var items = GetDepositItems(list);
            if (items.Count == 0)
            {
                Fail("Auto-deposit: inventory is nearly full but there are no depositable crafted items. Continuing to craft.");
                return true;
            }

            if (RetainerInfo.GetReachableRetainerBell() == null)
            {
                Fail("Auto-deposit: no retainer bell within interaction range. Continuing to craft.");
                return true;
            }

            EnqueueDeposit(retainerId, items);
            return false;
        }

        private static List<uint> GetDepositItems(NewCraftingList? list)
        {
            HashSet<uint> outputs = new();

            if (list != null)
            {
                foreach (var recipeItem in list.Recipes)
                    outputs.Add(LuminaSheets.RecipeSheet[recipeItem.ID].ItemResult.RowId);

                HashSet<uint> remainingIngredients = new();
                for (int i = CraftingListFunctions.CurrentIndex; i < list.ExpandedList.Count; i++)
                {
                    foreach (var ing in LuminaSheets.RecipeSheet[list.ExpandedList[i]].Ingredients().Where(x => x.Amount > 0))
                        remainingIngredients.Add(ing.Item.RowId);
                }

                outputs.ExceptWith(remainingIngredients);
            }
            else if (Endurance.RecipeID > 0)
            {
                outputs.Add(LuminaSheets.RecipeSheet[Endurance.RecipeID].ItemResult.RowId);
            }

            outputs.RemoveWhere(x => x <= 19 || LuminaSheets.ItemSheet[x].IsCollectable);
            outputs.RemoveWhere(x => CraftingListUI.NumberOfIngredient(x) == 0);
            return outputs.ToList();
        }

        private static void EnqueueDeposit(ulong retainerId, List<uint> items)
        {
            depositRunning = true;
            var TM = RetainerInfo.TM;

            TM.Enqueue(() => Svc.Framework.Update += RetainerInfo.Tick);
            TM.Enqueue(() => AutoRetainerIPC.Suppress());
            TM.EnqueueBell();
            TM.DelayNext("DepositBellInteracted", 1000);
            TM.Enqueue(() => Svc.Condition[ConditionFlag.OccupiedSummoningBell]);
            TM.Enqueue(() => RetainerListHandlers.SelectRetainerByID(retainerId), 5000, true, "SelectDepositRetainer");
            TM.DelayNext("DepositWaitToSelectEntrust", 200);
            TM.Enqueue(() => RetainerHandlers.SelectEntrustItems());
            TM.DelayNext("DepositEntrustSelected", 200);

            foreach (var item in items)
            {
                TM.Enqueue(() => DepositSingular(item), $"DepositSingular{item}");
            }

            TM.DelayNext("DepositCloseRetainer", 200);
            TM.Enqueue(() => RetainerHandlers.CloseAgentRetainer());
            TM.DelayNext("DepositClickQuit", 200);
            TM.Enqueue(() => RetainerHandlers.SelectQuit());
            TM.DelayNext("DepositCloseRetainerList", 200);
            TM.Enqueue(() => RetainerListHandlers.CloseRetainerList());
            TM.Enqueue(() => YesAlready.Unlock());
            TM.Enqueue(() => AutoRetainerIPC.Unsuppress());
            TM.Enqueue(() => Svc.Framework.Update -= RetainerInfo.Tick);
            TM.Enqueue(() => FinishDeposit());
        }

        private static bool DepositSingular(uint itemId)
        {
            var TM = RetainerInfo.TM;
            TM.DelayNextImmediate("DepositWaitOnInventory", 500);
            TM.EnqueueImmediate(() => RetainerHandlers.EntrustItem(itemId, out foundQuantity), 300);
            TM.DelayNextImmediate("DepositWaitOnNumericPopup", 200);
            TM.EnqueueImmediate(() =>
            {
                if (foundQuantity == 0) return true;
                if (foundQuantity == 1)
                {
                    TM.EnqueueImmediate(() => DepositSingular(itemId));
                    return true;
                }
                if (RetainerHandlers.InputNumericValue(foundQuantity))
                {
                    TM.EnqueueImmediate(() => DepositSingular(itemId));
                    return true;
                }
                return false;
            }, 1000);
            return true;
        }

        private static bool FinishDeposit()
        {
            depositRunning = false;
            if (GetFreeInventorySlots() <= P.Config.AutoDepositFreeSlotThreshold)
            {
                Fail("Auto-deposit finished but inventory is still nearly full (the retainer may be full). Auto-deposit is paused until the next craft session.");
            }
            return true;
        }

        private static void Fail(string message)
        {
            DepositFailed = true;
            DuoLog.Warning(message);
        }
    }
}
```

Behavioral notes locked in by this code:
- `ProcessDeposit` returns `true` immediately when disabled/backed-off/above threshold — hooks are zero-cost in the common case.
- `return !depositRunning` when TM is busy: if *our* chain is running, report busy (false) so crafting waits; if some *other* RetainerInfo chain is busy (e.g. material restock), report done (true) and re-check next update.
- `DepositSingular` recursion mirrors `RetainerInfo.ExtractSingular`: one stack per pass, terminates when `EntrustItem` reports `foundQuantity == 0`.
- `FinishDeposit` is the retainer-full detector: if the trip didn't free slots above the threshold, back off with one warning.

- [ ] **Step 2: Build**

Run: `dotnet build Artisan.sln`
Expected: Build succeeds with no new errors. If `Svc.PlayerState.ContentId` fails to resolve, use the identical accessor already used at `Artisan\IPC\RetainerInfo.cs:248` — do not invent an alternative.

- [ ] **Step 3: Commit**

```bash
git add Artisan/Autocraft/AutoDepositManager.cs
git commit -m "Add AutoDepositManager for depositing craft outputs to a retainer"
```

---

### Task 4: Hook into Endurance and crafting lists

**Files:**
- Modify: `Artisan\Autocraft\Endurance.cs:77-90` (ToggleEndurance) and `:351-355` (Update hook)
- Modify: `Artisan\CraftingList\CraftingList.cs:372-376` (ProcessList hook)
- Modify: `Artisan\CraftingList\CraftingListUI.cs:200` (back-off reset on list start)

**Interfaces:**
- Consumes: `AutoDepositManager.ProcessDeposit(NewCraftingList?)`, `AutoDepositManager.ResetBackoff()` from Task 3; `P.Config.AutoDepositCrafts` from Task 1.

- [ ] **Step 1: Endurance hook**

In `Artisan\Autocraft\Endurance.cs`, `Update()`, directly after this existing block (lines 351–355):

```csharp
                if (P.Config.Repair && !RepairManager.ProcessRepair())
                {
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                    return;
                }
```

add:

```csharp
                if (P.Config.AutoDepositCrafts && !AutoDepositManager.ProcessDeposit())
                {
                    PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                    return;
                }
```

- [ ] **Step 2: Endurance back-off reset**

In the same file, `ToggleEndurance` (line 77), add the reset inside the enable branch:

```csharp
        internal static void ToggleEndurance(bool enable)
        {
            if (RecipeID > 0 && enable)
            {
                Enable = enable;
                AutoDepositManager.ResetBackoff();
            }
```

(Only the `AutoDepositManager.ResetBackoff();` line is new.)

- [ ] **Step 3: Crafting list hook**

In `Artisan\CraftingList\CraftingList.cs`, `ProcessList`, directly after this existing block (lines 372–376):

```csharp
            if (selectedList.Repair && !RepairManager.ProcessRepair(selectedList))
            {
                PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                return;
            }
```

add:

```csharp
            if (P.Config.AutoDepositCrafts && !AutoDepositManager.ProcessDeposit(selectedList))
            {
                PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                return;
            }
```

(`CraftingList.cs` already uses `RepairManager` from `Artisan.Autocraft`, so no new using is needed; verify and add `using Artisan.Autocraft;` only if the compiler asks.)

- [ ] **Step 4: List back-off reset**

In `Artisan\CraftingList\CraftingListUI.cs`, in the method that starts list processing, this existing code (lines 199–201):

```csharp
            Crafting.CraftFinished += UpdateListTimer;
            Processing = true;
            Endurance.ToggleEndurance(false);
```

becomes:

```csharp
            Crafting.CraftFinished += UpdateListTimer;
            Processing = true;
            AutoDepositManager.ResetBackoff();
            Endurance.ToggleEndurance(false);
```

(`CraftingListUI.cs` already references `Endurance`, so `Artisan.Autocraft` is already imported.)

- [ ] **Step 5: Build**

Run: `dotnet build Artisan.sln`
Expected: Build succeeds with no new errors.

- [ ] **Step 6: Commit**

```bash
git add Artisan/Autocraft/Endurance.cs Artisan/CraftingList/CraftingList.cs Artisan/CraftingList/CraftingListUI.cs
git commit -m "Trigger auto-deposit between crafts in Endurance and list processing"
```

---

### Task 5: Settings UI

**Files:**
- Modify: `Artisan\UI\PluginUI.cs:1018` (insert new collapsing header between the end of the "List Settings" block and the `if (changed)` save at line 1020)

**Interfaces:**
- Consumes: Task 1 config fields; `AutoDepositManager.GetCharacterRetainers()` from Task 3.

- [ ] **Step 1: Add the "Retainer Deposit Settings" section**

In `Artisan\UI\PluginUI.cs`, `DrawMainWindow()`, after the closing brace of the `if (ImGui.CollapsingHeader("List Settings"))` block (line 1018) and before:

```csharp
            if (changed)
            {
                P.Config.Save();
            }
```

insert:

```csharp
            if (ImGui.CollapsingHeader("Retainer Deposit Settings"))
            {
                ImGui.Dummy(new Vector2(0, 2f));
                ImGui.Indent();

                changed |= ImGui.Checkbox("Automatically deposit crafted items into a retainer", ref P.Config.AutoDepositCrafts);
                ImGuiComponents.HelpMarker("When your free inventory slots drop to the threshold below between crafts, Artisan will use the nearest retainer bell to entrust this session's crafted items to the selected retainer, then resume crafting.\n\nRequires a retainer bell within interaction range. If no bell is reachable or the retainer cannot accept items, Artisan will notify you once and keep crafting.\n\nItems still needed as ingredients by the remaining list entries, collectibles, and crystals are never deposited.");

                if (P.Config.AutoDepositCrafts)
                {
                    if (!Svc.ClientState.IsLoggedIn)
                    {
                        ImGui.TextWrapped("Log in to select a retainer.");
                    }
                    else
                    {
                        var retainers = AutoDepositManager.GetCharacterRetainers();
                        P.Config.AutoDepositRetainers.TryGetValue(Svc.PlayerState.ContentId, out var selectedRetainer);
                        var preview = retainers.FirstOrDefault(x => x.Id == selectedRetainer).Name ?? "";

                        ImGui.PushItemWidth(250);
                        if (ImGui.BeginCombo("Deposit retainer", preview.Length == 0 ? "Select a retainer..." : preview))
                        {
                            foreach (var (id, name) in retainers)
                            {
                                if (ImGui.Selectable(name, id == selectedRetainer))
                                {
                                    P.Config.AutoDepositRetainers[Svc.PlayerState.ContentId] = id;
                                    P.Config.Save();
                                }
                            }
                            ImGui.EndCombo();
                        }
                    }

                    ImGui.PushItemWidth(250);
                    if (ImGui.SliderInt("Deposit when free inventory slots reach", ref P.Config.AutoDepositFreeSlotThreshold, 1, 20))
                    {
                        if (P.Config.AutoDepositFreeSlotThreshold < 1) P.Config.AutoDepositFreeSlotThreshold = 1;
                        if (P.Config.AutoDepositFreeSlotThreshold > 20) P.Config.AutoDepositFreeSlotThreshold = 20;
                        P.Config.Save();
                    }
                }

                ImGui.Unindent();
                ImGui.Dummy(new Vector2(0, 10f));
            }
```

Add `using Artisan.Autocraft;` to the top of `PluginUI.cs` only if it is not already present (it likely is — the file references Endurance settings).

- [ ] **Step 2: Build**

Run: `dotnet build Artisan.sln`
Expected: Build succeeds with no new errors.

- [ ] **Step 3: Commit**

```bash
git add Artisan/UI/PluginUI.cs
git commit -m "Add retainer deposit settings section to settings menu"
```

---

### Task 6: Final verification

**Files:** none (verification only)

- [ ] **Step 1: Clean build of the whole solution**

Run: `dotnet build Artisan.sln`
Expected: Build succeeds; no new warnings compared to a pre-change baseline build.

- [ ] **Step 2: Confirm the spec's requirements each map to committed code**

Checklist against `docs/superpowers/specs/2026-07-03-auto-deposit-retainer-design.md`:
- Config fields → Task 1 commit
- Settings UI (toggle, retainer combo, threshold slider) → Task 5 commit
- `EntrustItem` handler with Addon rows 97/772 → Task 2 commit
- `AutoDepositManager` (free-slot count, deposit chain, back-off, notify-once) → Task 3 commit
- Hooks + back-off resets in Endurance and lists → Task 4 commit
- Sub-craft protection (`ExceptWith(remainingIngredients)`), collectible/crystal exclusion → Task 3 commit

- [ ] **Step 3: Report the in-game manual test checklist to the user**

These cannot be automated; the user runs them in-game with the plugin loaded:
1. Enable the setting, pick a retainer, set threshold high (e.g. 20), start Endurance near a bell → deposit triggers between crafts, outputs land on the retainer, crafting resumes.
2. Run a list containing sub-crafts → intermediate outputs still needed by later entries are NOT deposited.
3. Craft away from any bell with the feature on → exactly one warning message, crafting continues.
4. Fill the chosen retainer's inventory → deposit trip happens once, one warning, crafting continues, no bell loop.
5. Relog / switch character → retainer selection is per character; settings combo shows "Log in to select a retainer." when logged out.

---

## Self-review notes

- Spec coverage: every spec section maps to a task (see Task 6 Step 2).
- The spec names `ClickCloseEntrustWindow` in the chain; the actual chain (mirroring `RestockFromRetainers`) uses `CloseAgentRetainer` + `SelectQuit`, which subsumes it — the entrust transfer-progress dialog only appears for the duplicates button flow, not per-item context entrust.
- Type consistency: `ProcessDeposit(NewCraftingList?)`, `EntrustItem(uint, out int)`, `GetCharacterRetainers() → List<(ulong Id, string Name)>` are used with identical signatures in Tasks 3, 4, and 5.
