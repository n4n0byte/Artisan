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
