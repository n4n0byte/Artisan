# Auto-Deposit Crafting Outputs to Retainer — Design

**Date:** 2026-07-03
**Status:** Approved

## Overview

Add a settings option that, when free inventory slots drop to a configured threshold
between crafts, pauses the craft loop, deposits the current session's crafted output
items into a user-chosen retainer via the nearest retainer bell, and resumes crafting.

Reuses the existing bell/retainer task machinery (`RetainerInfo.TM`,
`RetainerHandlers`, `TaskInteractWithNearestBell`) that already powers material
restocking from retainers — the deposit is the mirror direction.

## Requirements

- Settings toggle for the feature, with a per-character retainer picker and a
  free-slot threshold.
- Trigger: between crafts (Endurance or crafting list), when free inventory slots
  ≤ threshold.
- Deposit scope: only the current session's output items. For lists, exclude any
  item still required as an ingredient by remaining uncrafted list entries (protects
  sub-craft chains). Exclude crystals always; exclude collectibles only when the "Also deposit collectable crafts" toggle (AutoDepositCollectables, default ON) is off.
- Failure mode: if no bell is reachable, the chosen retainer is unavailable, or the
  entrust fails (retainer full), print one chat warning and keep crafting until the
  game's native inventory-full stop. Do not retry until the craft session restarts.

## Configuration (`Configuration.cs`)

| Field | Type | Default | Purpose |
|---|---|---|---|
| `AutoDepositCrafts` | `bool` | `false` | Master toggle |
| `AutoDepositRetainers` | `Dictionary<ulong, ulong>` | empty | Character ContentId → chosen RetainerId (per-character, mirrors `RetainerIDs` pattern) |
| `AutoDepositFreeSlotThreshold` | `int` | `5` | Trigger when free slots ≤ N (range 1–20) |

## Settings UI (`UI/PluginUI.cs`, `DrawMainWindow`)

New `"Retainer Deposit Settings"` collapsing header after `"List Settings"`:

- Checkbox: "Automatically deposit crafted items into a retainer when inventory is
  nearly full", with a help marker covering the bell-proximity requirement and the
  notify-and-continue failure behavior.
- When enabled:
  - Combo of the logged-in character's retainers, enumerated from
    `RetainerManager.Instance()->GetRetainerBySortedIndex(i)` (same as
    `RetainerInfo`); placeholder text when not logged in or no retainers.
  - Slider: "Deposit when free inventory slots ≤ N" (1–20).

## Deposit engine — new `Autocraft/AutoDepositManager.cs`

Modeled on `RepairManager`'s contract (`ProcessRepair` returns `true` when nothing
to do, `false` while busy):

- `GetFreeInventorySlots()` — counts empty slots across `InventoryType.Inventory1–4`
  via `InventoryManager`.
- `ShouldDeposit()` — enabled, retainer configured for current character, free slots
  ≤ threshold, not backed off.
- `ProcessDeposit()` — enqueues on `RetainerInfo.TM`:
  1. Suppress AutoRetainer (`AutoRetainerIPC.Suppress`), hook `Tick` for Talk skip
  2. `TM.EnqueueBell()` (existing nearest-bell interaction)
  3. `RetainerListHandlers.SelectRetainerByID(configured)`
  4. `RetainerHandlers.SelectEntrustItems()`
  5. For each deposit item: `RetainerHandlers.EntrustItem(itemId, hq)` +
     `InputNumericValue` for stacks
  6. Close entrust window / agent, `SelectQuit`, `CloseRetainerList`
  7. Unsuppress AutoRetainer, unhook `Tick`
- Back-off: one-shot flag set on unreachable bell / unavailable retainer / failed
  entrust; prints `Svc.Chat.PrintError` once; cleared when Endurance or a list is
  (re)started.

### New handler `RetainerHandlers.EntrustItem`

Deposit mirror of the existing withdraw `OpenItemContextMenu`: scans *player*
inventory pages (`Inventory1–4`) for the item, opens the inventory context menu
targeting the retainer agent, selects "Entrust"/"Entrust quantity" by addon-sheet
label (locale-safe, same technique as the Retrieve labels), reuses
`InputNumericValue`.

## Deposit scope

- **Endurance:** the current recipe's `ItemResult` (NQ and HQ stacks).
- **Lists:** result items of the list's recipes, minus any item that appears as an
  ingredient of a remaining uncrafted entry.
- Always excluded: crystals. Collectibles excluded only when AutoDepositCollectables (default ON) is disabled.

## Hook points

Both follow the established materia/repair interposition pattern (deposit check runs
after the repair check; on `false`, exit craft stance and wait):

- `Autocraft/Endurance.cs` — `Update()`, after `RepairManager.ProcessRepair()`
  (~line 351).
- `CraftingList/CraftingList.cs` — after `RepairManager.ProcessRepair(selectedList)`
  (~line 372).

## Error handling

All bell interaction inherits existing throttles (`GenericThrottle`) and AutoRetainer
suppression. Talk dialogs are clicked through by the existing `Tick` handler pattern.

## Testing

No automated test infrastructure exists in this repo. Verification:

1. `dotnet build Artisan.sln` compiles clean.
2. In-game manual checklist:
   - Threshold trigger mid-Endurance deposits outputs and resumes.
   - Mid-list with sub-crafts: intermediate outputs still needed by the list are NOT
     deposited.
   - No bell nearby: single warning, crafting continues.
   - Retainer full: single warning, crafting continues.
   - Setting persists per character; retainer combo behaves when logged out.
