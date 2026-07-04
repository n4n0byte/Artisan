# Retrieve Crafting Outputs from Retainers — Design

**Date:** 2026-07-04
**Status:** Approved

## Overview

Companion feature to auto-deposit (`AutoDepositManager`). During long list runs,
crafted outputs get entrusted to retainers when bags fill up. This feature adds a
manual button that pulls those outputs back: for the selected crafting list, it
computes the list's final output items, works out the shortfall against what is
already in the player's bags, and withdraws that shortfall from any retainer that
has stock.

It is the mirror of "Restock Inventory From Retainers", keyed on the list's
outputs instead of its ingredients, and reuses the same withdrawal machinery in
`IPC/RetainerInfo.cs`.

## Requirements

- Manual trigger only: a "Retrieve Craft Outputs From Retainers" button. No
  automatic between-crafts or end-of-list hook.
- Final outputs only: skip any recipe output that is used as an ingredient by
  another recipe in the same list (precrafts/intermediates).
- Retrieve up to the list quantity: target per item is
  `recipe.Quantity × RecipeSheet[recipe.ID].AmountResult`, summed when the same
  output item appears in multiple non-skipped recipes.
- Count bags: withdraw only the shortfall (target minus current player inventory
  count) so the player ends up holding the list quantity in total.
- Pull from all retainers, not just the configured auto-deposit retainer.
- Quality-agnostic: HQ and NQ both count toward the target and both get
  withdrawn, matching the deposit side. Collectables are valid outputs and are
  retrieved like anything else.
- No new configuration fields; the button itself is the option.
- Requires AllaganTools (`RetainerInfo.ATools`), a reachable summoning bell, and
  an idle `RetainerInfo.TM` queue (no collision with a running deposit or
  restock).

## Computing retrieval targets — new `RetainerInfo.GetRetrievalItems`

`GetRetrievalItems(NewCraftingList list)` returns
`Dictionary<uint itemId, int quantity>`:

1. Enumerate `list.Recipes`, skipping entries with `Quantity == 0` or
   `ListItemOptions.Skipping`. Output item is
   `LuminaSheets.RecipeSheet[recipe.ID].ItemResult.RowId`; target quantity is
   `recipe.Quantity × AmountResult`. Same output item across multiple recipes
   sums.
2. Drop any output that appears as an ingredient of another recipe in the list
   (final-outputs-only rule).
3. Subtract current bag count via `CraftingListUI.NumberOfIngredient(itemId)`
   (counts NQ+HQ, handles collectables). Drop entries ≤ 0.
4. Guard out junk item IDs (≤ 19), same as `AutoDepositManager.GetDepositItems`.

## Withdrawal flow — refactor in `IPC/RetainerInfo.cs`

`RestockFromRetainers(NewCraftingList)` already does the right thing once it has
a `requiredItems` dictionary: refresh the AllaganTools cache per item
(`GetRetainerItemCount`), open the bell (Tick hook, `AutoRetainerIPC.Suppress`,
`TM.EnqueueBell`), visit each retainer with stock
(`RetainerListHandlers.SelectRetainerByID` →
`RetainerHandlers.SelectEntrustItems` → `ExtractItem` per item → close → quit),
then clean up (`CloseRetainerList`, `YesAlready.Unlock`,
`AutoRetainerIPC.Unsuppress`, remove Tick).

Extract that back half into a shared private method,
`WithdrawItemsFromRetainers(Dictionary<uint, int> requiredItems)`. Then:

- `RestockFromRetainers(NewCraftingList)` = build materials shortfall (existing
  logic, unchanged behavior) → shared chain.
- New `RetrieveOutputsFromRetainers(NewCraftingList list)` = `GetRetrievalItems`
  → if empty, notify "Nothing to retrieve" and stop → shared chain.

Guards on the new entry point mirror restock: `ATools`, reachable bell,
`TM.IsBusy` check.

## UI

"Retrieve Craft Outputs From Retainers" button in both places the restock button
lives, with identical gating (hidden behind the AllaganTools install check,
disabled when `RetainerInfo.GetReachableRetainerBell()` is null):

- Main crafting-list panel, `CraftingList/CraftingListUI.cs`, under "Restock
  Inventory From Retainers".
- List editor, `UI/ListEditor.cs`, next to "Restock From Retainers".

The existing "Abort Collecting From Retainer" button (shown while `TM.IsBusy`)
covers cancellation since this runs on the same queue.

## Error handling

- Nothing needed (all targets met from bags, or list has no final outputs):
  notification, no bell interaction.
- Retainers without stock are skipped via the `RetainerData` cache;
  `UnavailableRetainerIDs` retainers are excluded, as in restock.
- Safe to press mid-run or before a run: the math is purely current bags vs.
  list targets.
- Chain abort/timeout behavior is inherited from the existing hardened
  restock/deposit chain on `RetainerInfo.TM`.

## Testing

- Build with the user-local .NET 10 SDK (KamiToolKit TFM override), output to
  devPlugins.
- Manual in-game verification watching `dalamud.log`: deposit outputs to a
  retainer (or place them manually), press the button, confirm shortfall math
  (bags counted, intermediates excluded) and that items land in bags; confirm
  the button disables away from a bell and while a deposit/restock is running.
