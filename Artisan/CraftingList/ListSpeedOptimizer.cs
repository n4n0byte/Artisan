using Artisan.CraftingLogic;
using Artisan.CraftingLogic.Solvers;
using Artisan.GameInterop;
using Artisan.IPC;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Artisan.UI;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Artisan.CraftingLists;

// "Fastest Raphael" — time-based list optimizer. Chooses NQ/HQ per craftable item to
// minimize total batch craft time (final items still guaranteed 100% HQ). Two passes:
// bottom-up cost table, then top-down HQ/NQ assignment. See
// docs/superpowers/specs/2026-07-06-fastest-raphael-time-optimizer-design.md.
public static class ListSpeedOptimizer
{
    public enum OptimizerState { Idle, Running, Done, Cancelled, Failed }

    public static OptimizerState Status { get; private set; } = OptimizerState.Idle;
    public static string CurrentItemName { get; private set; } = "";
    public static int SolvesDone { get; private set; }
    public static List<string> LastResults { get; private set; } = [];

    private static CancellationTokenSource? _cts;

    // Per-item cost/plan produced by pass 1 and consumed by pass 2.
    private sealed class ItemCost
    {
        public double TNq;                       // per-craft NQ seconds (quick synth const, or progress-macro duration)
        public double THq;                       // per-craft HQ rotation seconds (Raphael duration at PlannedIQ)
        public double PricePerUnit;              // fully-loaded marginal HQ extra time, per produced unit
        public int PlannedIQ;
        public int[] HQCounts = [];              // slot-aligned chosen HQ child amounts for this item's HQ craft
        public RaphaelRunResult? Production;     // HQ rotation actions (for building the cached macro in pass 2)
        public List<uint> ChosenHQChildIds = []; // ingredient item ids this item's HQ plan keeps HQ
        public bool NqIsQuickSynth;
        public RaphaelRunResult? NqProgressRun;  // progress-only run when not quick-synthable
        public bool Unreachable;                 // target unreachable -> conservative all-HQ
        public bool NoQualityTarget;             // item can't be HQ and isn't collectible
        public bool SkipOptimize;                // Raphael locked / cosmic -> leave as-is
    }

    public static void Run(NewCraftingList list)
    {
        if (Status == OptimizerState.Running) return;
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(P.Config.RaphaelSolverConfig.TimeOutMins * 10));
        Status = OptimizerState.Running;
        SolvesDone = 0;
        CurrentItemName = "";
        LastResults = [];
        var token = _cts.Token;
        Task.Run(() => Optimize(list, token));
    }

    public static void Cancel() => _cts?.Cancel();

    private static double QuickSynthSeconds() => P.Config.RaphaelSolverConfig.SpeedOptimizerAssume2sQuickSynth ? 2.0 : 3.0;

    private static async Task Optimize(NewCraftingList list, CancellationToken token)
    {
        try
        {
            // start from the user's original solvers/flags, not a prior optimizer run
            RestorePlans(list);

            // output itemId -> list item that crafts it; and total units of each item the list consumes
            Dictionary<uint, ListItem> producedBy = [];
            Dictionary<uint, int> consumedQty = [];
            foreach (var li in list.Recipes)
            {
                if (li.ListItemOptions?.Skipping == true || li.Quantity == 0) continue;
                producedBy.TryAdd(LuminaSheets.RecipeSheet[li.ID].ItemResult.RowId, li);
                foreach (var ing in LuminaSheets.RecipeSheet[li.ID].Ingredients().Where(x => x.Amount > 0 && x.Item.RowId > 0))
                    consumedQty[ing.Item.RowId] = consumedQty.GetValueOrDefault(ing.Item.RowId) + ing.Amount * li.Quantity;
            }

            var finalOutputs = RetainerInfo.GetListFinalOutputs(list);

            // must-HQ roots: final outputs, plus components produced in surplus of what the list consumes
            HashSet<uint> mustHQ = [];
            foreach (var itemId in finalOutputs)
                if (producedBy.TryGetValue(itemId, out var li))
                    mustHQ.Add(li.ID);
            foreach (var (itemId, li) in producedBy)
                if (li.Quantity * (int)LuminaSheets.RecipeSheet[li.ID].AmountResult > consumedQty.GetValueOrDefault(itemId, 0))
                    mustHQ.Add(li.ID);

            // PASS 1 — bottom-up cost table
            var table = await ComputeCostTable(list, producedBy, token);

            token.ThrowIfCancellationRequested();

            // PASS 2 — top-down assignment
            await ApplyAssignments(list, producedBy, mustHQ, table, token);

            list.SpeedOptimized = true;
            P.Config.Save(); // also persists RaphaelCache
            Status = OptimizerState.Done;
        }
        catch (OperationCanceledException)
        {
            Status = OptimizerState.Cancelled;
            P.Config.Save();
        }
        catch (Exception ex)
        {
            ex.Log("List speed optimization failed.");
            Status = OptimizerState.Failed;
            P.Config.Save();
        }
        finally
        {
            CurrentItemName = "";
        }
    }

    // ---- shared helpers (unchanged from the shipped feature) ----

    private static (CraftState craft, RaphaelSolutionConfig config) BuildCraft(Recipe recipe)
    {
        var recipeConfig = P.Config.RecipeConfigs.GetValueOrDefault(recipe.RowId) ?? new();
        var stats = CharacterStats.GetBaseStatsForClassHeuristic((Job)((uint)Job.CRP + recipe.CraftType.RowId));
        stats.AddConsumables(new(recipeConfig.RequiredFood, recipeConfig.RequiredFoodHQ), new(recipeConfig.RequiredPotion, recipeConfig.RequiredPotionHQ), CharacterInfo.FCCraftsmanshipbuff);
        var craft = Crafting.BuildCraftStateForRecipe(stats, (Job)((uint)Job.CRP + recipe.CraftType.RowId), recipe);
        return (craft, RaphaelCache.GetRaphConfig(craft));
    }

    private static SpeedPlan NewPlan(uint recipeId, CraftState craft)
    {
        var prev = P.Config.RecipeConfigs.GetValueOrDefault(recipeId) ?? new();
        return new SpeedPlan
        {
            PreviousSolverType = prev.SolverType,
            PreviousSolverFlavour = prev.SolverFlavour,
            SnapshotCraftsmanship = craft.StatCraftsmanship,
            SnapshotControl = craft.StatControl,
            SnapshotCP = craft.StatCP,
        };
    }

    private static void AssignSolver(uint recipeId, string solverType, int flavour)
    {
        var cfg = P.Config.RecipeConfigs.GetValueOrDefault(recipeId) ?? new();
        cfg.SolverType = solverType;
        cfg.SolverFlavour = flavour;
        P.Config.RecipeConfigs[recipeId] = cfg;
    }

    // ---- PASS 1: cost table ----

    private static async Task<Dictionary<uint, ItemCost>> ComputeCostTable(NewCraftingList list, Dictionary<uint, ListItem> producedBy, CancellationToken token)
    {
        var table = new Dictionary<uint, ItemCost>();
        var remaining = producedBy.Values.GroupBy(x => x.ID).Select(g => g.First()).ToList();

        while (remaining.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            int before = remaining.Count;
            for (int i = remaining.Count - 1; i >= 0; i--)
            {
                var li = remaining[i];
                var recipe = LuminaSheets.RecipeSheet[li.ID];
                var childRecipeIds = recipe.Ingredients()
                    .Where(x => x.Amount > 0 && x.Item.RowId > 0 && producedBy.ContainsKey(x.Item.RowId))
                    .Select(x => producedBy[x.Item.RowId].ID)
                    .Distinct();
                if (childRecipeIds.All(id => id == li.ID || table.ContainsKey(id)))
                {
                    table[li.ID] = await CostItem(li, producedBy, table, token);
                    remaining.RemoveAt(i);
                }
            }
            if (remaining.Count == before)
            {
                // dependency cycle / self-consumption: cost the rest with whatever children are known
                foreach (var li in remaining)
                    table.TryAdd(li.ID, await CostItem(li, producedBy, table, token));
                break;
            }
        }
        return table;
    }

    private static async Task<ItemCost> CostItem(ListItem li, Dictionary<uint, ListItem> producedBy, Dictionary<uint, ItemCost> table, CancellationToken token)
    {
        var recipe = LuminaSheets.RecipeSheet[li.ID];
        CurrentItemName = recipe.ItemResult.Value.Name.ToString();
        var (craft, config) = BuildCraft(recipe);
        var cost = new ItemCost { HQCounts = new int[recipe.Ingredients().Count()] };

        if (craft.StatLevel < 7 || craft.IsCosmic)
        {
            cost.SkipOptimize = true;
            cost.TNq = cost.THq = QuickSynthSeconds();
            // conservative: force this item's crafted HQ-able components HQ (matches the old
            // ForceComponentsHQ behavior) so a skipped parent's subtree keeps today's quality.
            foreach (var ing in recipe.Ingredients().Where(x => x.Amount > 0 && x.Item.RowId > 0))
                if (LuminaSheets.ItemSheet[ing.Item.RowId].CanBeHq
                    && producedBy.TryGetValue(ing.Item.RowId, out var skipProducer)
                    && skipProducer.ListItemOptions?.NQOnly != true)
                    cost.ChosenHQChildIds.Add(ing.Item.RowId);
            return cost;
        }

        // NQ cost
        if (recipe.CanQuickSynth && P.ri.HasRecipeCrafted(recipe.RowId))
        {
            cost.NqIsQuickSynth = true;
            cost.TNq = QuickSynthSeconds();
        }
        else
        {
            var nq = await RaphaelCache.RunRaphaelAsync(craft, config, 0, 0, token);
            SolvesDone++;
            if (nq != null) { cost.NqProgressRun = nq; cost.TNq = nq.DurationSeconds; }
            else { cost.NqIsQuickSynth = true; cost.TNq = QuickSynthSeconds(); } // last-resort: treat as cheap
        }

        // HQ plan (exact subset search)
        await SelectHQ(recipe, craft, config, producedBy, table, cost, token);

        // fully-loaded per-unit price of keeping this item HQ
        var amountResult = Math.Max(1, (int)recipe.AmountResult);
        var ings = recipe.Ingredients().ToList();
        double childExtra = 0;
        foreach (var childId in cost.ChosenHQChildIds)
        {
            var amt = ings.Where(x => x.Item.RowId == childId).Sum(x => x.Amount);
            if (producedBy.TryGetValue(childId, out var pr) && table.TryGetValue(pr.ID, out var cc))
                childExtra += amt * cc.PricePerUnit;
        }
        cost.PricePerUnit = cost.NoQualityTarget ? 0 : ((cost.THq - cost.TNq) + childExtra) / amountResult;
        return cost;
    }

    // Fills cost.PlannedIQ/HQCounts/Production/ChosenHQChildIds/THq/Unreachable/NoQualityTarget.
    private static async Task SelectHQ(Recipe recipe, CraftState craft, RaphaelSolutionConfig config, Dictionary<uint, ListItem> producedBy, Dictionary<uint, ItemCost> table, ItemCost cost, CancellationToken token)
    {
        var ings = recipe.Ingredients().ToList();
        cost.HQCounts = new int[ings.Count];

        var canBeHq = LuminaSheets.ItemSheet[recipe.ItemResult.RowId].CanBeHq;
        if (!canBeHq && !craft.CraftCollectible)
        {
            cost.NoQualityTarget = true;
            cost.Production = cost.NqProgressRun ?? await RaphaelCache.RunRaphaelAsync(craft, config, 0, 0, token);
            if (cost.NqProgressRun == null && cost.Production != null) SolvesDone++;
            cost.THq = cost.Production?.DurationSeconds ?? cost.TNq;
            return;
        }

        var target = craft.CraftCollectible && !craft.IsCosmic ? craft.CraftQualityMin3 : craft.CraftQualityMax;

        // candidates: craftable, HQ-able, produced by this list, not user-marked NQOnly
        var candidates = new List<(int Slot, uint ItemId, int Amount, int Contribution, double Price)>();
        for (int i = 0; i < ings.Count; i++)
        {
            var ing = ings[i];
            if (ing.Amount == 0 || ing.Item.RowId <= 0) continue;
            if (!LuminaSheets.ItemSheet[ing.Item.RowId].CanBeHq) continue;
            if (!producedBy.TryGetValue(ing.Item.RowId, out var producer)) continue;
            if (producer.ListItemOptions?.NQOnly == true) continue;
            var solo = new int[ings.Count];
            solo[i] = ing.Amount;
            var contribution = Calculations.GetStartingQuality(recipe, solo);
            var price = ing.Amount * (table.TryGetValue(producer.ID, out var cc) ? cc.PricePerUnit : 0.0);
            candidates.Add((i, ing.Item.RowId, ing.Amount, contribution, price));
        }

        void ForceAllHQ()
        {
            cost.HQCounts = new int[ings.Count];
            cost.ChosenHQChildIds = [];
            foreach (var c in candidates) { cost.HQCounts[c.Slot] = c.Amount; cost.ChosenHQChildIds.Add(c.ItemId); }
        }

        var probe = await RaphaelCache.RunRaphaelAsync(craft, config, 0, null, token);
        SolvesDone++;
        if (probe == null)
        {
            ForceAllHQ();
            cost.PlannedIQ = Calculations.GetStartingQuality(recipe, cost.HQCounts);
            cost.Unreachable = true;
            cost.THq = cost.TNq; // unknown; won't beat anything
            return;
        }

        if (probe.FinalQuality >= target)
        {
            // base (all children NQ) already reaches target — fastest
            cost.PlannedIQ = 0;
            cost.Production = probe;
            cost.THq = probe.DurationSeconds;
            return;
        }

        var neededIQ = target - probe.FinalQuality; // additivity: probe maxed action quality at IQ 0

        int cap = Math.Max(1, P.Config.RaphaelSolverConfig.SpeedOptimizerCandidateCap);
        var cands = candidates;
        if (cands.Count > cap)
        {
            LastResults.Add($"{recipe.ItemResult.Value.Name.ToString()}: limited component search to the {cap} most time-efficient candidates.");
            cands = candidates.OrderByDescending(c => c.Contribution / Math.Max(c.Price, 1e-6)).Take(cap).ToList();
        }

        if (cands.Sum(c => c.Contribution) < neededIQ)
        {
            // can't cover the deficit even with all candidates HQ -> conservative all-HQ (uncapped)
            ForceAllHQ();
            cost.PlannedIQ = Calculations.GetStartingQuality(recipe, cost.HQCounts);
            var prod = await RaphaelCache.RunRaphaelAsync(craft, config, cost.PlannedIQ, null, token);
            SolvesDone++;
            cost.Production = prod;
            cost.THq = prod?.DurationSeconds ?? cost.TNq;
            cost.Unreachable = prod == null || prod.FinalQuality < target;
            return;
        }

        // exact subset search over the (<= cap) candidates; parent solved once per distinct IQ
        var solveCache = new Dictionary<int, RaphaelRunResult?>();
        double bestTotal = double.MaxValue;
        bool found = false;
        for (int mask = 0; mask < (1 << cands.Count); mask++)
        {
            token.ThrowIfCancellationRequested();
            var hq = new int[ings.Count];
            double pcost = 0;
            var chosen = new List<uint>();
            for (int b = 0; b < cands.Count; b++)
                if ((mask & (1 << b)) != 0)
                {
                    var c = cands[b];
                    hq[c.Slot] = c.Amount;
                    pcost += c.Price;
                    chosen.Add(c.ItemId);
                }
            var pIQ = Calculations.GetStartingQuality(recipe, hq);
            if (pIQ < neededIQ) continue;
            if (!solveCache.TryGetValue(pIQ, out var prod))
            {
                prod = await RaphaelCache.RunRaphaelAsync(craft, config, pIQ, null, token);
                SolvesDone++;
                solveCache[pIQ] = prod;
            }
            if (prod == null || prod.FinalQuality < target) continue;
            var total = prod.DurationSeconds + pcost;
            if (total < bestTotal)
            {
                bestTotal = total;
                found = true;
                cost.PlannedIQ = pIQ;
                cost.HQCounts = hq;
                cost.Production = prod;
                cost.THq = prod.DurationSeconds;
                cost.ChosenHQChildIds = chosen;
            }
        }

        if (!found)
        {
            ForceAllHQ();
            cost.PlannedIQ = Calculations.GetStartingQuality(recipe, cost.HQCounts);
            var prod = await RaphaelCache.RunRaphaelAsync(craft, config, cost.PlannedIQ, null, token);
            SolvesDone++;
            cost.Production = prod;
            cost.THq = prod?.DurationSeconds ?? cost.TNq;
            cost.Unreachable = prod == null || prod.FinalQuality < target;
        }
    }

    // ---- PASS 2: assignment ----

    private static async Task ApplyAssignments(NewCraftingList list, Dictionary<uint, ListItem> producedBy, HashSet<uint> mustHQ, Dictionary<uint, ItemCost> table, CancellationToken token)
    {
        // propagate HQ from must-HQ roots through each item's chosen HQ children
        var hqSet = new HashSet<uint>();
        var queue = new Queue<uint>();
        var seen = new HashSet<uint>();
        foreach (var id in mustHQ)
            if (hqSet.Add(id)) queue.Enqueue(id);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id)) continue;
            if (!table.TryGetValue(id, out var c)) continue;
            foreach (var childId in c.ChosenHQChildIds)
                if (producedBy.TryGetValue(childId, out var pr) && hqSet.Add(pr.ID))
                    queue.Enqueue(pr.ID);
        }

        double oldBatch = 0, newBatch = 0;

        foreach (var li in list.Recipes)
        {
            token.ThrowIfCancellationRequested();
            if (li.ListItemOptions?.Skipping == true || li.Quantity == 0) continue;
            if (!table.TryGetValue(li.ID, out var cost)) continue;
            var recipe = LuminaSheets.RecipeSheet[li.ID];
            var itemName = recipe.ItemResult.Value.Name.ToString();
            bool isHQ = hqSet.Contains(li.ID);

            // user manually marked this NQOnly — leave its flag and solver untouched, no plan
            // (preserves the "user-NQOnly items never get a plan" invariant RestorePlans relies on)
            if (!isHQ && li.ListItemOptions?.NQOnly == true) continue;

            oldBatch += li.Quantity * cost.THq;
            newBatch += li.Quantity * (isHQ ? cost.THq : cost.TNq);

            if (cost.SkipOptimize)
            {
                LastResults.Add($"{itemName}: skipped (Raphael not unlocked / cosmic), left as-is.");
                continue;
            }
            if (isHQ && cost.NoQualityTarget)
            {
                LastResults.Add($"{itemName}: no HQ quality target, left as-is (its components eligible for NQ).");
                continue;
            }
            if (isHQ)
                ApplyHQ(li, recipe, cost, itemName);
            else
                ApplyNQ(li, recipe, cost, itemName);
        }

        LastResults.Insert(0, $"Estimated batch time: {FmtSeconds(oldBatch)} all-HQ → {FmtSeconds(newBatch)} optimized (saved ~{FmtSeconds(Math.Max(0, oldBatch - newBatch))}).");
    }

    private static void ApplyHQ(ListItem li, Recipe recipe, ItemCost cost, string itemName)
    {
        var (craft, config) = BuildCraft(recipe);
        var target = craft.CraftCollectible && !craft.IsCosmic ? craft.CraftQualityMin3 : craft.CraftQualityMax;

        void LeaveUnoptimized(string why)
        {
            li.ListItemOptions ??= new();
            var p = NewPlan(li.ID, craft);
            // all-HQ ingredient split reproduces today's behavior for this item
            var full = new int[recipe.Ingredients().Count()];
            int s = 0;
            foreach (var ing in recipe.Ingredients()) { if (ing.Amount > 0 && ing.Item.RowId > 0 && LuminaSheets.ItemSheet[ing.Item.RowId].CanBeHq) full[s] = ing.Amount; s++; }
            p.HQCounts = full;
            p.PlannedIQ = cost.PlannedIQ;
            p.DemotedToNQ = false;
            p.EstimatedSeconds = cost.THq;
            li.ListItemOptions.SpeedPlan = p;
            LastResults.Add($"{itemName} x{li.Quantity}: {why}");
        }

        if (cost.Production == null || cost.Unreachable)
        {
            LeaveUnoptimized("left unoptimized (all components HQ).");
            return;
        }

        craft.InitialQuality = cost.PlannedIQ;
        var macro = new MacroSolverSettings.Macro
        {
            ID = RaphaelCache.GetNewID(),
            Name = RaphaelCache.GetTextKey(craft, config),
            Steps = MacroUI.ParseMacro(cost.Production.ActionIds, craft),
            Options = new RaphaelOptions
            {
                SkipQualityIfMet = false,
                UpgradeProgressActions = false,
                UpgradeQualityActions = false,
                MinCP = craft.StatCP,
                MinControl = craft.StatControl,
                MinCraftsmanship = craft.StatCraftsmanship,
                Level = craft.CraftLevel,
                StatLevel = craft.StatLevel,
                Progress = craft.CraftProgress,
                QualityMax = craft.CraftQualityMax,
                Durability = craft.CraftDurability,
                IsExpert = craft.CraftExpert,
                InitialQuality = cost.PlannedIQ,
                IsSpecialist = craft.Specialist,
                SteadyHandUses = Math.Min(craft.CurrentSteadyHandCharges, P.Config.RaphaelSolverConfig.MaxStellarHand),
                SolutionConfig = config
            }
        };

        var sim = SolverUtils.SimulateSolverExecution(new MacroSolver(macro, craft), craft, cost.PlannedIQ);
        if (sim == null || sim.Progress < craft.CraftProgress || sim.Quality < target)
        {
            LeaveUnoptimized("simulator verification failed, left unoptimized.");
            return;
        }

        RaphaelCache.CurrentCache[RaphaelCache.GetOptions(craft, config)] = macro;

        li.ListItemOptions ??= new();
        var plan = NewPlan(li.ID, craft);
        plan.HQCounts = cost.HQCounts;
        plan.PlannedIQ = cost.PlannedIQ;
        plan.DemotedToNQ = false;
        plan.EstimatedSeconds = cost.THq;
        li.ListItemOptions.SpeedPlan = plan;
        AssignSolver(li.ID, typeof(RaphaelSolverDefintion).FullName!, 3);

        var hqNames = cost.ChosenHQChildIds.Select(id => LuminaSheets.ItemSheet[id].Name.ToString()).ToList();
        LastResults.Add($"{itemName} x{li.Quantity}: HQ (start quality {cost.PlannedIQ}, {cost.Production.Steps} steps){(hqNames.Count > 0 ? $", needs HQ: {string.Join(", ", hqNames)}" : ", all components NQ")}.");
    }

    private static void ApplyNQ(ListItem li, Recipe recipe, ItemCost cost, string itemName)
    {
        var (craft, config) = BuildCraft(recipe);
        li.ListItemOptions ??= new();
        var plan = NewPlan(li.ID, craft);
        plan.DemotedToNQ = true;
        plan.HQCounts = new int[recipe.Ingredients().Count()];
        plan.EstimatedSeconds = cost.TNq;

        if (cost.NqIsQuickSynth)
        {
            LastResults.Add($"{itemName} x{li.Quantity}: NQ (quick synth, ~{cost.TNq:0.#}s ea).");
        }
        else if (cost.NqProgressRun != null)
        {
            var name = $"Speed: {itemName} (NQ)";
            var macro = P.Config.MacroSolverConfig.Macros.Find(m => m.Name == name);
            if (macro == null)
            {
                macro = new MacroSolverSettings.Macro { Name = name };
                P.Config.MacroSolverConfig.AddNewMacro(macro);
            }
            macro.Steps = MacroUI.ParseMacro(cost.NqProgressRun.ActionIds, craft);
            macro.Options = new MacroSolverSettings.MacroOptions { MinCraftsmanship = craft.StatCraftsmanship };
            AssignSolver(li.ID, typeof(MacroSolverDefinition).FullName!, macro.ID);
            plan.NQMacroId = macro.ID;
            LastResults.Add($"{itemName} x{li.Quantity}: NQ (progress macro, {cost.NqProgressRun.Steps} steps).");
        }
        else
        {
            LastResults.Add($"{itemName} x{li.Quantity}: NQ (existing solver kept).");
        }

        li.ListItemOptions.NQOnly = true;
        li.ListItemOptions.SpeedPlan = plan;
    }

    private static string FmtSeconds(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Round(seconds));
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h{ts.Minutes:00}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m{ts.Seconds:00}s";
        return $"{ts.Seconds}s";
    }

    // ---- clear / restore (unchanged) ----

    private static void RestorePlans(NewCraftingList list)
    {
        foreach (var li in list.Recipes)
        {
            var plan = li.ListItemOptions?.SpeedPlan;
            if (plan == null) continue;

            var cfg = P.Config.RecipeConfigs.GetValueOrDefault(li.ID);
            if (cfg != null)
            {
                cfg.SolverType = plan.PreviousSolverType;
                cfg.SolverFlavour = plan.PreviousSolverFlavour;
            }
            if (plan.DemotedToNQ)
                li.ListItemOptions!.NQOnly = false;
            li.ListItemOptions!.SpeedPlan = null;
        }
        list.SpeedOptimized = false;
    }

    public static void Clear(NewCraftingList list)
    {
        RestorePlans(list);
        P.Config.Save();
    }
}
