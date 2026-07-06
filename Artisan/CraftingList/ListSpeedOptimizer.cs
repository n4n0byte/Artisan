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

// "Fastest Raphael": plans a crafting list so that only the components Raphael actually
// needs HQ are crafted HQ; everything else is demoted to NQ. See
// docs/superpowers/specs/2026-07-06-fastest-raphael-design.md.
public static class ListSpeedOptimizer
{
    public enum OptimizerState { Idle, Running, Done, Cancelled, Failed }

    public static OptimizerState Status { get; private set; } = OptimizerState.Idle;
    public static string CurrentItemName { get; private set; } = "";
    public static int SolvesDone { get; private set; }
    public static List<string> LastResults { get; private set; } = [];

    private static CancellationTokenSource? _cts;

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

    private static async Task Optimize(NewCraftingList list, CancellationToken token)
    {
        try
        {
            // output itemId -> list item that crafts it
            Dictionary<uint, ListItem> producedBy = [];
            foreach (var li in list.Recipes)
            {
                if (li.ListItemOptions?.Skipping == true || li.Quantity == 0) continue;
                producedBy.TryAdd(LuminaSheets.RecipeSheet[li.ID].ItemResult.RowId, li);
            }

            var finalOutputs = RetainerInfo.GetListFinalOutputs(list);
            HashSet<uint> hqNeeded = [];      // output item ids that must be crafted HQ
            HashSet<uint> processed = [];     // recipe ids already planned as must-HQ
            Queue<ListItem> queue = new();

            foreach (var itemId in finalOutputs)
                if (producedBy.TryGetValue(itemId, out var li) && processed.Add(li.ID))
                    queue.Enqueue(li);

            while (queue.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                await PlanMustHQItem(list, queue.Dequeue(), producedBy, hqNeeded, processed, queue, token);
            }

            token.ThrowIfCancellationRequested();
            await DemoteRemainingComponents(list, producedBy, hqNeeded, finalOutputs, token);

            list.SpeedOptimized = true;
            P.Config.Save(); // also persists RaphaelCache
            Status = OptimizerState.Done;
        }
        catch (OperationCanceledException)
        {
            Status = OptimizerState.Cancelled;
        }
        catch (Exception ex)
        {
            ex.Log("List speed optimization failed.");
            Status = OptimizerState.Failed;
        }
        finally
        {
            CurrentItemName = "";
        }
    }

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

    private static async Task PlanMustHQItem(NewCraftingList list, ListItem li, Dictionary<uint, ListItem> producedBy, HashSet<uint> hqNeeded, HashSet<uint> processed, Queue<ListItem> queue, CancellationToken token)
    {
        var recipe = LuminaSheets.RecipeSheet[li.ID];
        var itemName = recipe.ItemResult.Value.Name.ToString();
        CurrentItemName = itemName;
        var (craft, config) = BuildCraft(recipe);

        // conservative: on skip/failure, force this item's craftable components HQ so its
        // subtree keeps today's behavior
        void ForceComponentsHQ()
        {
            foreach (var ing in recipe.Ingredients().Where(x => x.Amount > 0 && x.Item.RowId > 0))
                if (producedBy.TryGetValue(ing.Item.RowId, out var producer))
                {
                    hqNeeded.Add(ing.Item.RowId);
                    if (processed.Add(producer.ID))
                        queue.Enqueue(producer);
                }
        }

        if (craft.StatLevel < 7 || craft.IsCosmic)
        {
            ForceComponentsHQ();
            LastResults.Add($"{itemName}: skipped ({(craft.IsCosmic ? "cosmic recipe" : "Raphael not unlocked")}), left as-is.");
            return;
        }

        var canBeHq = LuminaSheets.ItemSheet[recipe.ItemResult.RowId].CanBeHq;
        if (!canBeHq && !craft.CraftCollectible)
        {
            // no quality target: nothing forces its components HQ either
            LastResults.Add($"{itemName}: no quality target, components eligible for NQ.");
            return;
        }

        var target = craft.CraftCollectible && !craft.IsCosmic ? craft.CraftQualityMin3 : craft.CraftQualityMax;

        // slots aligned with the FULL recipe.Ingredients() enumeration (GetStartingQuality
        // indexes hqCount by that enumeration, including empty slots)
        var ings = recipe.Ingredients().ToList();
        var hqCounts = new int[ings.Count];
        List<(int Slot, uint ItemId, int Contribution)> candidates = [];
        for (int i = 0; i < ings.Count; i++)
        {
            var ing = ings[i];
            if (ing.Amount == 0 || ing.Item.RowId <= 0) continue;
            if (!LuminaSheets.ItemSheet[ing.Item.RowId].CanBeHq) continue;
            if (!producedBy.TryGetValue(ing.Item.RowId, out var producer)) continue;   // not crafted by this list
            if (producer.ListItemOptions?.NQOnly == true) continue;                    // user wants it NQ; respect
            if (hqNeeded.Contains(ing.Item.RowId)) { hqCounts[i] = ing.Amount; continue; } // already forced by another parent
            var solo = new int[ings.Count];
            solo[i] = ing.Amount;
            candidates.Add((i, ing.Item.RowId, Calculations.GetStartingQuality(recipe, solo)));
        }

        var baseIQ = Calculations.GetStartingQuality(recipe, hqCounts);
        var probe = await RaphaelCache.RunRaphaelAsync(craft, config, baseIQ, null, token);
        SolvesDone++;
        if (probe == null)
        {
            ForceComponentsHQ();
            LastResults.Add($"{itemName}: Raphael solve failed/timed out, left unoptimized.");
            return;
        }

        RaphaelRunResult production;
        List<(int Slot, uint ItemId, int Contribution)> chosen = [];
        int plannedIQ;
        if (probe.FinalQuality >= target)
        {
            production = probe;
            plannedIQ = baseIQ;
        }
        else
        {
            // quality is additive: probe maxed out action quality, so the deficit is exact
            var actionQualityMax = probe.FinalQuality - baseIQ;
            var neededIQ = target - actionQualityMax;
            foreach (var c in candidates.OrderByDescending(x => x.Contribution))
            {
                if (Calculations.GetStartingQuality(recipe, hqCounts) >= neededIQ) break;
                hqCounts[c.Slot] = ings[c.Slot].Amount;
                chosen.Add(c);
            }
            plannedIQ = Calculations.GetStartingQuality(recipe, hqCounts);
            if (plannedIQ < neededIQ)
            {
                ForceComponentsHQ();
                LastResults.Add($"{itemName}: target quality unreachable even with all components HQ, left unoptimized.");
                return;
            }
            var second = await RaphaelCache.RunRaphaelAsync(craft, config, plannedIQ, null, token);
            SolvesDone++;
            if (second == null || second.FinalQuality < target)
            {
                ForceComponentsHQ();
                LastResults.Add($"{itemName}: production solve failed, left unoptimized.");
                return;
            }
            production = second;
        }

        // build + verify + cache the production macro (mirrors RaphaelCache.Build)
        craft.InitialQuality = plannedIQ;
        var macro = new MacroSolverSettings.Macro
        {
            ID = RaphaelCache.GetNewID(),
            Name = RaphaelCache.GetTextKey(craft, config),
            Steps = MacroUI.ParseMacro(production.ActionIds, craft),
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
                InitialQuality = plannedIQ,
                IsSpecialist = craft.Specialist,
                SteadyHandUses = Math.Min(craft.CurrentSteadyHandCharges, P.Config.RaphaelSolverConfig.MaxStellarHand),
                SolutionConfig = config
            }
        };

        var sim = SolverUtils.SimulateSolverExecution(new MacroSolver(macro, craft), craft, plannedIQ);
        if (sim == null || sim.Progress < craft.CraftProgress || sim.Quality < target)
        {
            ForceComponentsHQ();
            LastResults.Add($"{itemName}: simulator verification failed, left unoptimized.");
            return;
        }

        RaphaelCache.CurrentCache[RaphaelCache.GetOptions(craft, config)] = macro;

        li.ListItemOptions ??= new();
        var plan = NewPlan(li.ID, craft);
        plan.HQCounts = hqCounts;
        plan.PlannedIQ = plannedIQ;
        plan.DemotedToNQ = false;
        li.ListItemOptions.SpeedPlan = plan;
        AssignSolver(li.ID, typeof(RaphaelSolverDefintion).FullName!, 3);

        foreach (var c in chosen)
        {
            hqNeeded.Add(c.ItemId);
            var producer = producedBy[c.ItemId];
            if (processed.Add(producer.ID))
                queue.Enqueue(producer);
        }

        var hqNames = hqCounts.Select((n, i) => n > 0 && producedBy.ContainsKey(ings[i].Item.RowId) ? LuminaSheets.ItemSheet[ings[i].Item.RowId].Name.ToString() : null).Where(x => x != null).ToList();
        LastResults.Add($"{itemName}: HQ with starting quality {plannedIQ} ({production.Steps} steps){(hqNames.Count > 0 ? $", needs HQ: {string.Join(", ", hqNames)}" : ", all crafted components NQ")}.");
    }

    private static async Task DemoteRemainingComponents(NewCraftingList list, Dictionary<uint, ListItem> producedBy, HashSet<uint> hqNeeded, HashSet<uint> finalOutputs, CancellationToken token)
    {
        foreach (var li in list.Recipes)
        {
            token.ThrowIfCancellationRequested();
            if (li.ListItemOptions?.Skipping == true || li.Quantity == 0) continue;
            var recipe = LuminaSheets.RecipeSheet[li.ID];
            var outId = recipe.ItemResult.RowId;
            if (finalOutputs.Contains(outId)) continue;      // final output, not a component
            if (!producedBy.ContainsKey(outId)) continue;
            if (hqNeeded.Contains(outId)) continue;          // must stay HQ
            if (li.ListItemOptions?.NQOnly == true) continue; // user already handles it

            var itemName = recipe.ItemResult.Value.Name.ToString();
            CurrentItemName = itemName;
            var (craft, config) = BuildCraft(recipe);

            li.ListItemOptions ??= new();
            var plan = NewPlan(li.ID, craft);
            plan.DemotedToNQ = true;
            plan.HQCounts = new int[recipe.Ingredients().Count()]; // all NQ

            if (recipe.CanQuickSynth && P.ri.HasRecipeCrafted(recipe.RowId))
            {
                LastResults.Add($"{itemName}: demoted to NQ (quick synth).");
            }
            else if (craft.StatLevel >= 7 && !craft.IsCosmic)
            {
                var run = await RaphaelCache.RunRaphaelAsync(craft, config, 0, 0, token);
                SolvesDone++;
                if (run != null)
                {
                    var name = $"Speed: {itemName} (NQ)";
                    var macro = P.Config.MacroSolverConfig.Macros.Find(m => m.Name == name);
                    if (macro == null)
                    {
                        macro = new MacroSolverSettings.Macro { Name = name };
                        P.Config.MacroSolverConfig.AddNewMacro(macro);
                    }
                    macro.Steps = MacroUI.ParseMacro(run.ActionIds, craft);
                    macro.Options = new MacroSolverSettings.MacroOptions
                    {
                        MinCraftsmanship = craft.StatCraftsmanship,
                    };
                    AssignSolver(li.ID, typeof(MacroSolverDefinition).FullName!, macro.ID);
                    plan.NQMacroId = macro.ID;
                    LastResults.Add($"{itemName}: demoted to NQ (progress-only macro, {run.Steps} steps).");
                }
                else
                {
                    LastResults.Add($"{itemName}: demoted to NQ (progress macro generation failed; existing solver kept).");
                }
            }
            else
            {
                LastResults.Add($"{itemName}: demoted to NQ (existing solver kept).");
            }

            li.ListItemOptions.NQOnly = true;
            li.ListItemOptions.SpeedPlan = plan;
        }
    }

    public static void Clear(NewCraftingList list)
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
                li.ListItemOptions!.NQOnly = false;   // the optimizer set it (user-NQOnly items never get a plan)
            li.ListItemOptions!.SpeedPlan = null;
        }
        list.SpeedOptimized = false;
        P.Config.Save();
    }
}
