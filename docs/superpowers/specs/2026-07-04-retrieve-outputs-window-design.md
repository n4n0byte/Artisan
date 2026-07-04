# Retrieve Craft Outputs Selection Window — Design

**Date:** 2026-07-04
**Status:** Approved
**Supersedes:** the direct-fire behavior of the buttons added in
`2026-07-04-retrieve-outputs-retainer-design.md` (the underlying withdrawal
chain and gating are unchanged).

## Overview

The "Retrieve Craft Outputs From Retainers" buttons currently fire a retrieval
immediately, computing quantities as list-target-minus-bags. Replace that with
a selection window: the button opens a window listing the list's final output
items with the quantity sitting on retainers, lets the user retrieve everything
in one click or type per-item amounts, then feeds the chosen quantities into
the existing withdrawal chain.

## Requirements

- Both existing buttons (main crafting-list panel, list editor) keep their
  label and gating but now open the window for the selected list instead of
  firing a retrieval. Opening is instant (no game interaction on click).
- The window lists the list's **final outputs** — same item set as today:
  recipe outputs excluding items consumed as ingredients by other non-skipped
  recipes in the list, `itemId > 19`.
- Each row shows: item name, total quantity on retainers (all retainers,
  HQ+NQ combined, via the AllaganTools cache), and an amount input clamped to
  0–available, **defaulting to the full available amount**.
- Rows with 0 on retainers are dropped.
- **Retrieve All**: withdraws every listed item at its full available
  quantity, ignoring the inputs.
- **Retrieve Selected**: withdraws the entered amounts, skipping zeros.
- Both actions run through the existing `WithdrawItemsFromRetainers` chain
  with the existing guards (ATools, `TM.IsBusy` notification, per-item cache
  refresh). The window closes when a retrieval starts.
- The bags-shortfall math from the previous design is retired: nothing
  computes list-quantity-minus-inventory anymore. The final-outputs item-set
  logic is kept.
- No new configuration fields.

## Window — new `UI/RetrieveItemSelectionWindow.cs`

`internal class RetrieveItemSelectionWindow : Window`, following the
`ListEditor` lifecycle pattern:

- Constructor takes the `NewCraftingList`; registers itself with `P.ws`,
  sets `IsOpen = true`. Title: `Retrieve Craft Outputs - [list name]###`
  suffixed with the list ID so ImGui state is per-list.
- A static open helper ensures one window per list: opening for a list that
  already has a live window brings that window to front instead of stacking
  a duplicate; a new list replaces nothing (windows for different lists can
  coexist, matching how `ListEditor` behaves).
- On close, the window unregisters itself from `P.ws`.

### Row computation

On construction, a background task (`Task.Run`, matching how the buttons
called retrieval before) computes the rows:

1. Item set: the list's final outputs (shared helper in `RetainerInfo`, see
   below).
2. For each item, available = `RetainerInfo.GetRetainerItemCount(itemId)`
   (refreshes the AllaganTools cache; sums all retainers, NQ+HQ).
3. Row = item id, item name (`LuminaSheets.ItemSheet`), available, amount
   (initialized to available). Rows with available ≤ 0 are dropped.

While the task runs the window draws "Scanning retainers…". If the list has
no final outputs, the window draws "This list has no final outputs to
retrieve." If outputs exist but none are on retainers: "None of this list's
outputs are on your retainers."

### Drawing

- Table: Item | On Retainers | Amount. Amount is `ImGui.InputInt` clamped to
  `[0, available]` after edit.
- Footer: **Retrieve All** and **Retrieve Selected** buttons, wrapped in the
  same bell gating the panels use
  (`ImRaii.Disabled(!Player.Available ? false : RetainerInfo.GetReachableRetainerBell() == null)`),
  computed on the draw thread. Also disabled while `RetainerInfo.TM.IsBusy`.
- Retrieve All builds `{itemId → available}` for all rows; Retrieve Selected
  builds `{itemId → amount}` for rows with amount > 0 (if all are zero, do
  nothing). Both then call
  `Task.Run(() => RetainerInfo.RetrieveFromRetainers(dict))` and close the
  window.

## RetainerInfo changes (`IPC/RetainerInfo.cs`)

- `GetRetrievalItems(NewCraftingList)` is replaced by
  `public static HashSet<uint> GetListFinalOutputs(NewCraftingList list)` —
  the existing enumeration (skip `Skipping`/zero-quantity recipes, output =
  `RecipeSheet[id].ItemResult.RowId`, drop `itemId ≤ 19`, drop outputs used
  as ingredients by other non-skipped recipes in the list). Quantity targets
  and bag subtraction are removed.
- `RetrieveOutputsFromRetainers(NewCraftingList)` is replaced by
  `public static void RetrieveFromRetainers(Dictionary<uint, int> items)`:
  guards (`ATools` silent return; `TM.IsBusy` → `Notify.Error`), per-item
  `GetRetainerItemCount` refresh, convert to `Dictionary<int, int>`, call
  `WithdrawItemsFromRetainers`. Empty dictionary → return without action
  (the window never sends one, but the guard is cheap).
- `WithdrawItemsFromRetainers` and `ExtractItem` (including the quantity-1
  recursion fix) are unchanged.

## Button changes

- `CraftingList/CraftingListUI.cs`: the main-panel button's body becomes
  `RetrieveItemSelectionWindow.Open(selectedList)` (no `Task.Run` — opening
  is UI-only). Gating unchanged.
- `UI/ListEditor.cs`: same change for the editor button. Gating unchanged
  (including the bell disable added post-review; the window's footer re-checks
  the bell anyway, so a user who walks away after opening still can't fire).

## Error handling & edge cases

- Stale counts (items moved between scan and retrieve): the chain withdraws
  `min(requested, found)` per stack and stops when no matching slots remain —
  same tolerance as restock. No new handling.
- Requested amounts exceeding free bag space fail the same way restock does
  today (chain stalls/times out on the full-inventory dialog); no new
  handling.
- Collectables are listed and retrieved like anything else (quantity-1
  recursion fix already in place).
- The window holds a reference to the list only for id/name/recipes; list
  edits while the window is open are not re-scanned — close and reopen to
  refresh (also the answer for stale retainer counts).

## Testing

- `dotnet build Artisan.sln` clean per change (no automated test infra;
  game-interop code).
- In-game checklist: window opens instantly from both buttons with correct
  names/counts; inputs clamp to 0–available; Retrieve All drains everything;
  Retrieve Selected honors typed amounts and skips zeros; empty states show
  the right message ("no final outputs" vs "none on retainers"); footer
  buttons disable away from a bell and while retainer tasks run; duplicate
  open for the same list focuses the existing window.
