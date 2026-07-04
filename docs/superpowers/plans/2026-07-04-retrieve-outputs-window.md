# Retrieve Craft Outputs Selection Window Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the direct-fire "Retrieve Craft Outputs From Retainers" buttons with a selection window that lists the list's final outputs with on-retainer quantities and lets the user retrieve everything or typed per-item amounts.

**Architecture:** A new `RetrieveItemSelectionWindow` (Dalamud `Window` subclass, `ListEditor`-style lifecycle: registers with `P.ws` on construction, removes itself on close) is opened by both existing buttons. It scans on a background task (`GetListFinalOutputs` × `GetRetainerItemCount`), draws a 3-column table with clamped amount inputs defaulting to the full available quantity, and feeds an itemId→quantity dictionary into a new thin `RetainerInfo.RetrieveFromRetainers` entry point that reuses the existing guards and `WithdrawItemsFromRetainers` chain. The old shortfall-based `GetRetrievalItems`/`RetrieveOutputsFromRetainers` are deleted at the end.

**Tech Stack:** C# Dalamud plugin (FFXIV). Game-interop code with **no automated test infrastructure** — per task, verification = `dotnet build Artisan.sln` compiling with no NEW errors/warnings referencing changed files, plus the in-game checklist in Task 3.

**Spec:** `docs/superpowers/specs/2026-07-04-retrieve-outputs-window-design.md`

## Global Constraints

- Build with the user-local .NET 10 SDK from the repo root: `$env:DOTNET_ROOT="$env:USERPROFILE\.dotnet"; $env:PATH="$env:USERPROFILE\.dotnet;$env:PATH"; dotnet build Artisan.sln` (PowerShell). Baseline warnings are submodule noise; check no warning references changed files.
- `KamiToolKit` and `OtterGui` submodules carry uncommitted local modifications — never touch, revert, or commit them. Stage only the files each task names.
- Exact copy: window title `Retrieve Craft Outputs - {list.Name}###RetrieveOutputs{list.ID}`; buttons `Retrieve All` and `Retrieve Selected`; empty states `Scanning retainers...`, `This list has no final outputs to retrieve.`, `None of this list's outputs are on your retainers.`; the two panel buttons keep their existing label `Retrieve Craft Outputs From Retainers` and existing gating.
- No new configuration fields.
- `WithdrawItemsFromRetainers` and `ExtractItem` in `Artisan\IPC\RetainerInfo.cs` must not change.
- Every task must leave the solution compiling (old entry points are deleted only in Task 3, when the last caller is rewired).
- Commit after each task with the trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.

---

### Task 1: New RetainerInfo entry points `GetListFinalOutputs` and `RetrieveFromRetainers`

**Files:**
- Modify: `Artisan\IPC\RetainerInfo.cs` (insert two methods immediately after the existing `RetrieveOutputsFromRetainers` method, which currently ends around line 526; do NOT delete or alter any existing method in this task)

**Interfaces:**
- Consumes: existing `ATools`, `TM`, `Notify` (using already present), `GetRetainerItemCount(uint)`, `WithdrawItemsFromRetainers(Dictionary<int, int>)`, `LuminaSheets.RecipeSheet`, `Recipe.Ingredients()` extension.
- Produces (Tasks 2–3 rely on these exact signatures):
  - `public static HashSet<uint> GetListFinalOutputs(NewCraftingList list)` — item IDs of the list's final outputs (no quantities).
  - `public static void RetrieveFromRetainers(Dictionary<uint, int> items)` — withdraws the given itemId→quantity map via the existing chain.

- [ ] **Step 1: Add the two methods**

Insert immediately after the closing brace of `RetrieveOutputsFromRetainers(NewCraftingList list)` in `Artisan\IPC\RetainerInfo.cs`:

```csharp
        public static HashSet<uint> GetListFinalOutputs(NewCraftingList list)
        {
            HashSet<uint> outputs = new();
            HashSet<uint> usedAsIngredient = new();

            foreach (var item in list.Recipes)
            {
                if (item.ListItemOptions?.Skipping == true || item.Quantity == 0) continue;
                var recipe = LuminaSheets.RecipeSheet[item.ID];

                var itemId = recipe.ItemResult.RowId;
                if (itemId > 19)
                    outputs.Add(itemId);

                foreach (var ing in recipe.Ingredients().Where(x => x.Amount > 0))
                    usedAsIngredient.Add(ing.Item.RowId);
            }

            // Final outputs only: drop intermediates consumed by other recipes in this list.
            outputs.ExceptWith(usedAsIngredient);
            return outputs;
        }

        public static void RetrieveFromRetainers(Dictionary<uint, int> items)
        {
            if (!ATools) return;
            if (items.Count == 0) return;

            if (TM.IsBusy)
            {
                Notify.Error("Cannot retrieve craft outputs: retainer tasks are already running.");
                return;
            }

            Dictionary<int, int> requiredItems = new();
            foreach (var item in items)
            {
                requiredItems.Add((int)item.Key, item.Value);

                //Refresh retainer cache if empty
                GetRetainerItemCount(item.Key);
            }

            WithdrawItemsFromRetainers(requiredItems);
        }
```

(This intentionally duplicates the final-outputs enumeration that lives inside the existing `GetRetrievalItems` — that method and `RetrieveOutputsFromRetainers` are deleted in Task 3 once their last caller is rewired, leaving a single copy.)

- [ ] **Step 2: Build**

Run: `dotnet build Artisan.sln`
Expected: Build succeeds with no new errors/warnings referencing RetainerInfo.cs.

- [ ] **Step 3: Commit**

```bash
git add Artisan/IPC/RetainerInfo.cs
git commit -m "Add GetListFinalOutputs and RetrieveFromRetainers entry points"
```

---

### Task 2: Create `RetrieveItemSelectionWindow`

**Files:**
- Create: `Artisan\UI\RetrieveItemSelectionWindow.cs`

**Interfaces:**
- Consumes: `RetainerInfo.GetListFinalOutputs(NewCraftingList)` and `RetainerInfo.RetrieveFromRetainers(Dictionary<uint, int>)` from Task 1; existing `RetainerInfo.GetRetainerItemCount(uint)`, `RetainerInfo.GetReachableRetainerBell()`, `RetainerInfo.TM`, `P.ws` (WindowSystem), `Player` (project-wide global alias for `ECommons.GameHelpers.Player`, declared in `Artisan\Artisan.cs:1`), `uint.NameOfItem()` extension (`Artisan\RawInformation\LuminaSheets.cs:198`), `Window.BringToFront()` (public — already called the same way at `Artisan\UI\ListEditor.cs:1681`).
- Produces (Task 3 relies on this): `public static void Open(NewCraftingList list)` on `Artisan.UI.RetrieveItemSelectionWindow` — opens (or refocuses) the window for a list.

Nothing references this class yet; the solution keeps compiling.

- [ ] **Step 1: Create the file**

Write `Artisan\UI\RetrieveItemSelectionWindow.cs` with exactly this content:

```csharp
using Artisan.CraftingLists;
using Artisan.IPC;
using Artisan.RawInformation;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace Artisan.UI
{
    internal class RetrieveItemSelectionWindow : Window
    {
        internal readonly NewCraftingList List;

        private class Row
        {
            public uint ItemId;
            public string Name = string.Empty;
            public int Available;
            public int Amount;
        }

        private volatile bool scanning = true;
        private bool hasFinalOutputs;
        private List<Row> rows = new();

        public RetrieveItemSelectionWindow(NewCraftingList list)
            : base($"Retrieve Craft Outputs - {list.Name}###RetrieveOutputs{list.ID}")
        {
            List = list;
            IsOpen = true;
            P.ws.AddWindow(this);
            Size = new Vector2(500, 400);
            SizeCondition = ImGuiCond.Appearing;
            ShowCloseButton = true;

            Task.Run(ScanRetainers);
        }

        public static void Open(NewCraftingList list)
        {
            if (P.ws.Windows.FirstOrDefault(x => x is RetrieveItemSelectionWindow w && w.List.ID == list.ID) is RetrieveItemSelectionWindow existing)
            {
                existing.IsOpen = true;
                existing.BringToFront();
                return;
            }

            _ = new RetrieveItemSelectionWindow(list);
        }

        private void ScanRetainers()
        {
            try
            {
                var outputs = RetainerInfo.GetListFinalOutputs(List);
                hasFinalOutputs = outputs.Count > 0;

                List<Row> result = new();
                foreach (var itemId in outputs)
                {
                    var available = RetainerInfo.GetRetainerItemCount(itemId);
                    if (available <= 0) continue;
                    result.Add(new Row { ItemId = itemId, Name = itemId.NameOfItem(), Available = available, Amount = available });
                }

                rows = result.OrderBy(x => x.Name).ToList();
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "Scanning retainers for craft outputs");
            }
            finally
            {
                scanning = false;
            }
        }

        public override void Draw()
        {
            if (scanning)
            {
                ImGui.TextUnformatted("Scanning retainers...");
                return;
            }

            if (!hasFinalOutputs)
            {
                ImGui.TextUnformatted("This list has no final outputs to retrieve.");
                return;
            }

            if (rows.Count == 0)
            {
                ImGui.TextUnformatted("None of this list's outputs are on your retainers.");
                return;
            }

            if (ImGui.BeginTable("###RetrieveOutputsTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, ImGui.GetContentRegionAvail().Y - 35f)))
            {
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("On Retainers", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("Amount", ImGuiTableColumnFlags.WidthFixed, 120f);
                ImGui.TableHeadersRow();

                foreach (var row in rows)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row.Name);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row.Available.ToString());
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(110f);
                    if (ImGui.InputInt($"###Amount{row.ItemId}", ref row.Amount))
                        row.Amount = Math.Clamp(row.Amount, 0, row.Available);
                }

                ImGui.EndTable();
            }

            bool disableBell = !Player.Available ? false : RetainerInfo.GetReachableRetainerBell() == null;
            using (ImRaii.Disabled(disableBell || RetainerInfo.TM.IsBusy))
            {
                if (ImGui.Button("Retrieve All"))
                {
                    StartRetrieval(rows.ToDictionary(x => x.ItemId, x => x.Available));
                }

                ImGui.SameLine();
                if (ImGui.Button("Retrieve Selected"))
                {
                    var items = rows.Where(x => x.Amount > 0).ToDictionary(x => x.ItemId, x => x.Amount);
                    if (items.Count > 0)
                        StartRetrieval(items);
                }
            }
        }

        private void StartRetrieval(Dictionary<uint, int> items)
        {
            Task.Run(() => RetainerInfo.RetrieveFromRetainers(items));
            IsOpen = false;
        }

        public override void OnClose()
        {
            P.ws.RemoveWindow(this);
        }
    }
}
```

Notes locked in by the spec — do not "improve":
- Amount defaults to the full available quantity and clamps to `[0, available]` after edit.
- `Retrieve All` ignores the inputs and uses `Available`; `Retrieve Selected` uses the inputs, skipping zeros, and does nothing when all are zero.
- The window closes (`IsOpen = false`, which triggers `OnClose` → `RemoveWindow`) when a retrieval starts.
- The scan runs off-thread because `GetRetainerItemCount` does AllaganTools IPC per retainer — the same reason the old buttons used `Task.Run`.
- `hasFinalOutputs`/`rows` are written by the scan task before the `volatile` `scanning` flip that `Draw` gates on.

- [ ] **Step 2: Build**

Run: `dotnet build Artisan.sln`
Expected: Build succeeds with no new errors/warnings referencing RetrieveItemSelectionWindow.cs. If `ImRaii` fails to resolve, the using is `Dalamud.Interface.Utility.Raii` (same as `Artisan\CraftingList\CraftingListUI.cs:10`); if `P` or `Player` fail to resolve, they are project-wide global usings in `Artisan\Artisan.cs` — do not add local aliases.

- [ ] **Step 3: Commit**

```bash
git add Artisan/UI/RetrieveItemSelectionWindow.cs
git commit -m "Add retrieve craft outputs selection window"
```

---

### Task 3: Rewire buttons, delete old entry points, harden ListEditor window lookup

**Files:**
- Modify: `Artisan\CraftingList\CraftingListUI.cs` (the "Retrieve Craft Outputs From Retainers" button body, currently ~line 126-129)
- Modify: `Artisan\UI\ListEditor.cs` (the editor's retrieve button body ~line 238-242, and the list-picker window lookup ~line 1671-1682)
- Modify: `Artisan\IPC\RetainerInfo.cs` (delete `GetRetrievalItems` and `RetrieveOutputsFromRetainers`)

**Interfaces:**
- Consumes: `RetrieveItemSelectionWindow.Open(NewCraftingList)` from Task 2.
- Produces: feature complete; `RetainerInfo.GetRetrievalItems` and `RetainerInfo.RetrieveOutputsFromRetainers` no longer exist.

- [ ] **Step 1: Rewire the main-panel button**

In `Artisan\CraftingList\CraftingListUI.cs`, change the button body (keep the button line and its `ImRaii.Disabled` wrapper exactly as they are):

```csharp
                            if (ImGui.Button("Retrieve Craft Outputs From Retainers", new Vector2(ImGui.GetContentRegionAvail().X, 30)))
                            {
                                RetrieveItemSelectionWindow.Open(selectedList);
                            }
```

(The old body was `Task.Run(() => RetainerInfo.RetrieveOutputsFromRetainers(selectedList));` — opening a window is UI-only, so no `Task.Run`. `using Artisan.UI;` is already present at `CraftingListUI.cs:7`.)

- [ ] **Step 2: Rewire the list-editor button**

In `Artisan\UI\ListEditor.cs`, change the retrieve button body (keep its `ImRaii.Disabled(disableBell)` wrapper as-is):

```csharp
                    if (ImGui.Button($"Retrieve Craft Outputs From Retainers"))
                    {
                        RetrieveItemSelectionWindow.Open(SelectedList);
                    }
```

- [ ] **Step 3: Harden the list-picker window lookup in ListEditor**

The new window's name embeds the list ID (`###RetrieveOutputs{list.ID}`), and the list picker's duplicate-editor check matches any window whose name contains the ID digits — without a type filter, clicking a list in the picker while its retrieval window is open would focus the retrieval window instead of opening the editor. In `Artisan\UI\ListEditor.cs` (~lines 1671-1682), add a type filter to both lambdas:

```csharp
            if (!P.ws.Windows.Any(x => x is ListEditor && x.WindowName.Contains(l.ID.ToString())))
            {
                Interface.SetupValues();
                ListEditor editor = new(l);
            }
            else
            {
                P.ws.Windows.TryGetFirst(
                    x => x is ListEditor && x.WindowName.Contains(l.ID.ToString()),
                    out var window);
                window.BringToFront();
            }
```

(Only `x is ListEditor && ` is added to each predicate; everything else is unchanged.)

- [ ] **Step 4: Delete the superseded RetainerInfo methods**

In `Artisan\IPC\RetainerInfo.cs`, delete the entire methods `GetRetrievalItems(NewCraftingList list)` and `RetrieveOutputsFromRetainers(NewCraftingList list)` (currently ~lines 467-526, between `RestockFromRetainers(NewCraftingList)` and `GetListFinalOutputs`). Do not touch `GetListFinalOutputs`, `RetrieveFromRetainers`, `WithdrawItemsFromRetainers`, or anything else. Then verify no references remain:

Run: `git grep -n "RetrieveOutputsFromRetainers\|GetRetrievalItems" -- "*.cs"`
Expected: no matches.

- [ ] **Step 5: Build**

Run: `dotnet build Artisan.sln`
Expected: Build succeeds with no new errors/warnings referencing the three changed files.

- [ ] **Step 6: Commit**

```bash
git add Artisan/CraftingList/CraftingListUI.cs Artisan/UI/ListEditor.cs Artisan/IPC/RetainerInfo.cs
git commit -m "Open retrieval selection window from retrieve craft outputs buttons"
```

- [ ] **Step 7: In-game manual verification (user assists; watch `dalamud.log`)**

Reload the dev plugin, then:

1. Both buttons open the window instantly; title shows the list name; rows show correct item names and on-retainer totals; amounts default to the full amount.
2. Inputs clamp: typing above the available count or below 0 snaps back into range.
3. `Retrieve All` drains every listed item; `Retrieve Selected` withdraws exactly the typed amounts and skips zero rows; the window closes when retrieval starts.
4. Empty states: a list of only precrafts shows "This list has no final outputs to retrieve."; a list whose outputs aren't banked shows "None of this list's outputs are on your retainers."
5. Footer buttons disable away from a bell and while a restock/deposit/retrieval chain runs.
6. Opening the window twice for the same list focuses the existing window; clicking that list in the editor's list picker still opens/focuses the List Editor (not the retrieval window).
