# Retrieve Crafting Outputs from Retainers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a manual "Retrieve Craft Outputs From Retainers" button that withdraws a crafting list's final output items from retainers, up to the list quantity minus what is already in the player's bags.

**Architecture:** Everything new lives beside the existing material-restock code in `Artisan\IPC\RetainerInfo.cs`. The back half of `RestockFromRetainers(NewCraftingList)` (the bell-open/withdraw task chain) is extracted into a shared private helper `WithdrawItemsFromRetainers(Dictionary<int, int>)`; a new public entry point `RetrieveOutputsFromRetainers(NewCraftingList)` builds an outputs-shortfall dictionary via a new `GetRetrievalItems(NewCraftingList)` and feeds it to the same helper. Two UI buttons call the entry point, mirroring the existing restock buttons' gating.

**Tech Stack:** C# Dalamud plugin (FFXIV). Game-interop code with **no automated test infrastructure** — per task, verification = `dotnet build Artisan.sln` compiling with no NEW errors, plus the in-game manual checklist in Task 4.

**Spec:** `docs/superpowers/specs/2026-07-04-retrieve-outputs-retainer-design.md`

## Global Constraints

- Build with the user-local .NET 10 SDK; the solution builds via `dotnet build Artisan.sln` from the repo root (KamiToolKit TFM override is already configured; plugin output goes to devPlugins).
- Do NOT change the behavior of the existing `RestockFromRetainers(NewCraftingList)` material restock — Task 1 is a pure extract-method refactor.
- No new configuration fields. The button itself is the option.
- Button label everywhere: `Retrieve Craft Outputs From Retainers` (exact copy).
- All retainer work runs on the shared `RetainerInfo.TM` task manager queue; never enqueue when `TM.IsBusy`.
- Commit after each task with the trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.

---

### Task 1: Extract shared withdrawal chain `WithdrawItemsFromRetainers`

**Files:**
- Modify: `Artisan\IPC\RetainerInfo.cs:429-499` (method `RestockFromRetainers(NewCraftingList list)`)

**Interfaces:**
- Consumes: existing fields/methods of `RetainerInfo` (`TM`, `RetainerData`, `Tick`, `ExtractItem`) — unchanged.
- Produces: `private static void WithdrawItemsFromRetainers(Dictionary<int, int> requiredItems)` in `Artisan.IPC.RetainerInfo` — Task 2 calls this. Keys are item IDs (int), values are quantities still needed. The dictionary is mutated by the chain as items are withdrawn.

This is a pure refactor: move the bottom half of `RestockFromRetainers(NewCraftingList)` (everything from the `if (RetainerData.SelectMany(...)` check at line 459 through the end of the method at line 498) into a new private method, and call it. No line inside the moved block changes.

- [ ] **Step 1: Perform the extraction**

Replace the whole method `RestockFromRetainers(NewCraftingList list)` (`Artisan\IPC\RetainerInfo.cs:429-499`) with the following two methods (the moved block is verbatim from the current file):

```csharp
        public static void RestockFromRetainers(NewCraftingList list)
        {
            Dictionary<int, int> requiredItems = new();
            Dictionary<uint, int> materialList = new();

            Svc.Log.Debug($"Making material list");

            materialList = list.ListMaterials();

            Svc.Log.Debug($"Creating Fetch List");

            foreach (var material in materialList.OrderByDescending(x => x.Key))
            {
                Svc.Log.Debug($"{material}");
                bool isCrafted = LuminaSheets.RecipeSheet.Values.Any(x => x.ItemResult.RowId == material.Key);
                if (isCrafted && list.OnlyRestockNonCrafted)
                    continue;

                var invCount = CraftingListUI.NumberOfIngredient(material.Key);
                if (invCount < material.Value)
                {
                    var diffcheck = material.Value - invCount;
                    Svc.Log.Debug($"{material.Key} {diffcheck}");
                    requiredItems.Add((int)material.Key, diffcheck);
                }

                //Refresh retainer cache if empty
                GetRetainerItemCount(material.Key);
            }

            WithdrawItemsFromRetainers(requiredItems);
        }

        private static void WithdrawItemsFromRetainers(Dictionary<int, int> requiredItems)
        {
            if (RetainerData.SelectMany(x => x.Value).Any(x => requiredItems.Any(y => y.Key == x.Value.ItemId)))
            {
                Svc.Log.Debug($"Processing Retainer Data");
                TM.Enqueue(() => Svc.Framework.Update += Tick);
                TM.Enqueue(() => AutoRetainerIPC.Suppress());
                TM.EnqueueBell();
                TM.DelayNext("BellInteracted", 1000);
                TM.Enqueue(() => Svc.Condition[ConditionFlag.OccupiedSummoningBell]);

                foreach (var retainer in RetainerData)
                {
                    if (retainer.Value.Values.Any(x => requiredItems.Any(y => y.Value > 0 && y.Key == x.ItemId && x.Quantity > 0)))
                    {
                        TM.Enqueue(() => RetainerListHandlers.SelectRetainerByID(retainer.Key));
                        TM.DelayNext("WaitToSelectEntrust", 200);
                        TM.Enqueue(() => RetainerHandlers.SelectEntrustItems());
                        TM.DelayNext("EntrustSelected", 200);
                        foreach (var item in requiredItems)
                        {
                            if (retainer.Value.Values.Any(x => x.ItemId == item.Key && x.Quantity > 0))
                            {
                                TM.DelayNext("SwitchItems", 200);
                                TM.Enqueue(() =>
                                {
                                    ExtractItem(requiredItems, item, retainer.Key);
                                });
                            }
                        }
                        TM.DelayNext("CloseRetainer", 200);
                        TM.Enqueue(() => RetainerHandlers.CloseAgentRetainer());
                        TM.DelayNext("ClickQuit", 200);
                        TM.Enqueue(() => RetainerHandlers.SelectQuit());
                    }
                }
                TM.DelayNext("CloseRetainerList", 200);
                TM.Enqueue(() => RetainerListHandlers.CloseRetainerList());
                TM.Enqueue(() => YesAlready.Unlock());
                TM.Enqueue(() => AutoRetainerIPC.Unsuppress());
                TM.Enqueue(() => Svc.Framework.Update -= Tick);
            }
        }
```

Sanity check while editing: the only differences from the original method are (a) the original inline `if (RetainerData...` block is replaced by the call `WithdrawItemsFromRetainers(requiredItems);` and (b) that block now lives in the new private method. `git diff` should show no other changes.

- [ ] **Step 2: Build**

Run: `dotnet build Artisan.sln`
Expected: Build succeeds with no new errors (pre-existing warnings are fine).

- [ ] **Step 3: Commit**

```bash
git add Artisan/IPC/RetainerInfo.cs
git commit -m "Extract WithdrawItemsFromRetainers from RestockFromRetainers"
```

---

### Task 2: Add `GetRetrievalItems` and `RetrieveOutputsFromRetainers`

**Files:**
- Modify: `Artisan\IPC\RetainerInfo.cs` (add two methods directly below `RestockFromRetainers(NewCraftingList)` / above `WithdrawItemsFromRetainers`; add one `using`)

**Interfaces:**
- Consumes: `WithdrawItemsFromRetainers(Dictionary<int, int>)` from Task 1; existing `GetRetainerItemCount(uint)`, `CraftingListUI.NumberOfIngredient(uint)`, `LuminaSheets.RecipeSheet`, `Recipe.Ingredients()` extension (`Artisan\RawInformation\HelperExtensions.cs:142`, namespace already imported).
- Produces:
  - `public static Dictionary<uint, int> GetRetrievalItems(NewCraftingList list)` — itemId → shortfall quantity of the list's final outputs.
  - `public static void RetrieveOutputsFromRetainers(NewCraftingList list)` — the entry point Tasks 3 and 4 wire to buttons.

- [ ] **Step 1: Add the using directive**

`Notify` lives in `ECommons.ImGuiMethods`, which `RetainerInfo.cs` does not yet import. In the using block at the top of `Artisan\IPC\RetainerInfo.cs`, add (alphabetical position, after `using ECommons.ExcelServices.TerritoryEnumeration;`):

```csharp
using ECommons.ImGuiMethods;
```

- [ ] **Step 2: Add the two methods**

Insert immediately after the closing brace of `RestockFromRetainers(NewCraftingList list)`:

```csharp
        public static Dictionary<uint, int> GetRetrievalItems(NewCraftingList list)
        {
            Dictionary<uint, int> targets = new();
            HashSet<uint> usedAsIngredient = new();

            foreach (var item in list.Recipes)
            {
                if (item.ListItemOptions?.Skipping == true || item.Quantity == 0) continue;
                var recipe = LuminaSheets.RecipeSheet[item.ID];

                var itemId = recipe.ItemResult.RowId;
                if (itemId > 19)
                {
                    var quantity = item.Quantity * (int)recipe.AmountResult;
                    if (!targets.TryAdd(itemId, quantity))
                        targets[itemId] += quantity;
                }

                foreach (var ing in recipe.Ingredients().Where(x => x.Amount > 0))
                    usedAsIngredient.Add(ing.Item.RowId);
            }

            Dictionary<uint, int> required = new();
            foreach (var target in targets)
            {
                // Final outputs only: skip intermediates consumed by other recipes in this list.
                if (usedAsIngredient.Contains(target.Key)) continue;

                var needed = target.Value - CraftingListUI.NumberOfIngredient(target.Key);
                if (needed > 0)
                    required.Add(target.Key, needed);
            }

            return required;
        }

        public static void RetrieveOutputsFromRetainers(NewCraftingList list)
        {
            if (!ATools) return;

            if (TM.IsBusy)
            {
                Notify.Error("Cannot retrieve craft outputs: retainer tasks are already running.");
                return;
            }

            var retrievalItems = GetRetrievalItems(list);
            if (retrievalItems.Count == 0)
            {
                Notify.Info("Nothing to retrieve: you already have this list's outputs in your inventory.");
                return;
            }

            Dictionary<int, int> requiredItems = new();
            foreach (var item in retrievalItems)
            {
                requiredItems.Add((int)item.Key, item.Value);

                //Refresh retainer cache if empty
                GetRetainerItemCount(item.Key);
            }

            WithdrawItemsFromRetainers(requiredItems);
        }
```

Notes for the implementer (all decided in the spec — do not "improve"):
- Quantity math is `item.Quantity * (int)recipe.AmountResult` (crafts × yield per craft). Duplicate output items across recipes sum.
- `CraftingListUI.NumberOfIngredient` counts NQ+HQ in the player's bags and handles collectables; quality is deliberately not distinguished.
- `WithdrawItemsFromRetainers` itself no-ops when no retainer holds any required item, so `RetrieveOutputsFromRetainers` needs no bell/stock check of its own beyond what the UI gates.

- [ ] **Step 3: Build**

Run: `dotnet build Artisan.sln`
Expected: Build succeeds with no new errors. If `Ingredients()` fails to resolve, the extension is `Artisan.RawInformation.HelperExtensions.Ingredients` and `using Artisan.RawInformation;` already exists at the top of the file — do not invent an alternative.

- [ ] **Step 4: Commit**

```bash
git add Artisan/IPC/RetainerInfo.cs
git commit -m "Add RetrieveOutputsFromRetainers to pull list outputs from retainers"
```

---

### Task 3: Button in the main crafting-list panel

**Files:**
- Modify: `Artisan\CraftingList\CraftingListUI.cs:107-127` (the `RetainerInfo.ATools` block)

**Interfaces:**
- Consumes: `RetainerInfo.RetrieveOutputsFromRetainers(NewCraftingList)` from Task 2; existing `selectedList`, `disable` gating pattern.
- Produces: UI only.

- [ ] **Step 1: Add the button**

In `Artisan\CraftingList\CraftingListUI.cs`, inside the existing `using (ImRaii.Disabled(disable))` block, directly below the "Restock Inventory From Retainers" button (currently lines 119-125), add the second button so the block reads:

```csharp
                        bool disable = !Player.Available ? false : RetainerInfo.GetReachableRetainerBell() == null;
                        using (ImRaii.Disabled(disable))
                        {
                            if (ImGui.Button("Restock Inventory From Retainers", new Vector2(ImGui.GetContentRegionAvail().X, 30)))
                            {
                                Task.Run(() => RetainerInfo.RestockFromRetainers(selectedList));
                            }

                            if (ImGui.Button("Retrieve Craft Outputs From Retainers", new Vector2(ImGui.GetContentRegionAvail().X, 30)))
                            {
                                Task.Run(() => RetainerInfo.RetrieveOutputsFromRetainers(selectedList));
                            }
                        }
```

This inherits every existing gate for free: hidden unless `RetainerInfo.ATools`, replaced by the "Abort Collecting From Retainer" button while `RetainerInfo.TM.IsBusy`, disabled away from a bell, and disabled while `Endurance.Enable || Processing`.

- [ ] **Step 2: Build**

Run: `dotnet build Artisan.sln`
Expected: Build succeeds with no new errors.

- [ ] **Step 3: Commit**

```bash
git add Artisan/CraftingList/CraftingListUI.cs
git commit -m "Add retrieve craft outputs button to crafting list panel"
```

---

### Task 4: Button in the list editor + in-game verification

**Files:**
- Modify: `Artisan\UI\ListEditor.cs:216-235` (the `RetainerInfo.ATools` block in the editor header)

**Interfaces:**
- Consumes: `RetainerInfo.RetrieveOutputsFromRetainers(NewCraftingList)` from Task 2; existing `SelectedList` field.
- Produces: UI only. Feature complete after this task.

- [ ] **Step 1: Add the button**

In `Artisan\UI\ListEditor.cs`, inside the `if (RetainerInfo.ATools)` block, after the "Only Restock Non-Crafted Items" checkbox (currently lines 230-231) and before the `Endurance.Enable || CraftingListUI.Processing` `EndDisabled()`, add:

```csharp
                if (ImGui.Button($"Retrieve Craft Outputs From Retainers"))
                {
                    Task.Run(() => RetainerInfo.RetrieveOutputsFromRetainers(SelectedList));
                }
```

The resulting block:

```csharp
                if (ImGui.Button($"Restock From Retainers"))
                {
                    Task.Run(() => RetainerInfo.RestockFromRetainers(SelectedList));
                }

                ImGui.SameLine();
                if (ImGui.Checkbox("Only Restock Non-Crafted Items", ref SelectedList.OnlyRestockNonCrafted))
                    P.Config.Save();

                if (ImGui.Button($"Retrieve Craft Outputs From Retainers"))
                {
                    Task.Run(() => RetainerInfo.RetrieveOutputsFromRetainers(SelectedList));
                }
```

(No `ImGui.SameLine()` before the new button — it starts a new row above the tab bar. It sits inside the same `Endurance.Enable || CraftingListUI.Processing` disabled region as the restock button, matching that location's gating; the method's own `TM.IsBusy` guard from Task 2 covers a queue collision.)

- [ ] **Step 2: Build**

Run: `dotnet build Artisan.sln`
Expected: Build succeeds with no new errors.

- [ ] **Step 3: Commit**

```bash
git add Artisan/UI/ListEditor.cs
git commit -m "Add retrieve craft outputs button to list editor"
```

- [ ] **Step 4: In-game manual verification (user assists; watch `dalamud.log`)**

The plugin builds to devPlugins; reload it in-game. Checklist:

1. With a crafting list selected whose outputs sit on a retainer (e.g. deposited by auto-deposit), stand at a bell and press "Retrieve Craft Outputs From Retainers" (main panel): the bell chain runs and the shortfall lands in bags.
2. Shortfall math: with some of the output already in bags, only the difference (list quantity × yield − bag count) is withdrawn.
3. Intermediates excluded: for a list with precrafts (output consumed by a later recipe in the list), the precraft item is not retrieved.
4. Nothing needed: with all outputs already in bags, pressing the button only shows the "Nothing to retrieve" notification — no bell interaction.
5. Gating: button disabled away from a bell (main panel); while a restock/deposit chain runs, the main panel shows "Abort Collecting From Retainer" instead, and the editor button no-ops with the "already running" notification.
6. Regression: "Restock Inventory From Retainers" still restocks materials as before (Task 1 refactor changed no behavior).
