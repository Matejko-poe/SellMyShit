using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NumVector4 = System.Numerics.Vector4;

namespace SellMyShit
{
    public sealed class SaleRecord
    {
        public DateTime TimeUtc { get; set; }
        public string Item { get; set; } = string.Empty;
        public string WantedCurrency { get; set; } = string.Empty;
        public int Offered { get; set; }
        public int Wanted { get; set; }

        // wanted currency per offered item
        public double Rate { get; set; }

        public bool Reverse { get; set; }
    }

    // A simple book of what was listed and at which ratio, kept in sales_ledger.json next to the
    // plugin. Shown in the plugin settings, with the change against the previous sale of the
    // same item.
    public static class SalesLedger
    {
        private static readonly object Sync = new();
        private static List<SaleRecord> _records = [];
        private static string _path;
        private static string _filter = string.Empty;
        private static bool _confirmClear;

        public static void Init(string directory)
        {
            lock (Sync)
            {
                _path = Path.Combine(directory, "sales_ledger.json");

                try
                {
                    _records = File.Exists(_path)
                        ? JsonConvert.DeserializeObject<List<SaleRecord>>(File.ReadAllText(_path)) ?? []
                        : [];
                }
                catch
                {
                    _records = [];
                }
            }
        }

        public static void Add(SaleRecord record)
        {
            if (record == null || record.Offered <= 0 || record.Wanted <= 0)
                return;

            lock (Sync)
            {
                _records.Add(record);
                Save();
            }
        }

        private static void Save()
        {
            if (string.IsNullOrEmpty(_path))
                return;

            try
            {
                File.WriteAllText(_path, JsonConvert.SerializeObject(_records, Formatting.Indented));
            }
            catch
            {
                // a locked file must not break selling; it is retried on the next sale
            }
        }

        private static string FormatRate(double rate) =>
            rate.ToString(rate >= 100 ? "0" : rate >= 1 ? "0.##" : "0.####", CultureInfo.InvariantCulture);

        public static void Draw()
        {
            SaleRecord[] all;

            lock (Sync)
                all = _records.ToArray();

            ImGui.TextDisabled(
                $"{all.Length} sale(s) recorded in sales_ledger.json. " +
                "Rate = wanted currency per item.");

            ImGui.SetNextItemWidth(220);
            ImGui.InputTextWithHint("##LedgerFilter", "Filter by item", ref _filter, 128);

            ImGui.SameLine();

            if (!_confirmClear)
            {
                if (ImGui.SmallButton("Clear ledger"))
                    _confirmClear = true;
            }
            else
            {
                if (ImGui.SmallButton("Really delete all records?"))
                {
                    lock (Sync)
                    {
                        _records.Clear();
                        Save();
                    }

                    _confirmClear = false;
                }

                ImGui.SameLine();

                if (ImGui.SmallButton("Cancel"))
                    _confirmClear = false;
            }

            var groups = all
                .Where(record => string.IsNullOrWhiteSpace(_filter) ||
                                 record.Item.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase))
                .GroupBy(record => record.Item, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Max(record => record.TimeUtc))
                .ToList();

            if (groups.Count == 0)
            {
                ImGui.TextDisabled("No sales recorded yet.");
                return;
            }

            if (!ImGui.BeginTable(
                    "SalesLedgerTable",
                    6,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                return;
            }

            foreach (var header in new[] { "Item", "Last rate", "Previous", "Change", "Sales", "Last sold" })
                ImGui.TableSetupColumn(header);

            ImGui.TableHeadersRow();

            foreach (var group in groups)
            {
                var history = group.OrderBy(record => record.TimeUtc).ToList();
                var last = history[^1];
                var previous = history.Count > 1 ? history[^2] : null;

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(group.Key);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{FormatRate(last.Rate)} {ShortCurrency(last.WantedCurrency)}");

                ImGui.TableNextColumn();

                if (previous == null)
                {
                    ImGui.TextDisabled("-");
                }
                else
                {
                    ImGui.TextUnformatted($"{FormatRate(previous.Rate)} {ShortCurrency(previous.WantedCurrency)}");
                }

                ImGui.TableNextColumn();

                // a ratio only compares between sales for the same currency
                if (previous == null ||
                    previous.Rate <= 0 ||
                    !string.Equals(previous.WantedCurrency, last.WantedCurrency, StringComparison.OrdinalIgnoreCase))
                {
                    ImGui.TextDisabled("-");
                }
                else
                {
                    var change = (last.Rate - previous.Rate) / previous.Rate * 100d;

                    ImGui.PushStyleColor(
                        ImGuiCol.Text,
                        change > 0.05
                            ? new NumVector4(0.3f, 1f, 0.4f, 1f)
                            : change < -0.05
                                ? new NumVector4(1f, 0.4f, 0.4f, 1f)
                                : new NumVector4(0.8f, 0.8f, 0.8f, 1f));

                    // TextUnformatted: a % in a printf-style call would print garbage
                    ImGui.TextUnformatted($"{change:+0.0;-0.0;0.0}%");
                    ImGui.PopStyleColor();
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(history.Count.ToString(CultureInfo.InvariantCulture));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(
                    last.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            }

            ImGui.EndTable();

            ImGui.Spacing();
            ImGui.TextDisabled("History per item (newest first):");

            foreach (var group in groups)
            {
                if (!ImGui.TreeNode($"{group.Key}##ledger_{group.Key}"))
                    continue;

                foreach (var record in group.OrderByDescending(record => record.TimeUtc).Take(30))
                {
                    ImGui.TextUnformatted(
                        $"{record.TimeUtc.ToLocalTime():yyyy-MM-dd HH:mm}  " +
                        $"{record.Offered} -> {record.Wanted} {ShortCurrency(record.WantedCurrency)}  " +
                        $"(rate {FormatRate(record.Rate)}{(record.Reverse ? ", reverse" : string.Empty)})");
                }

                ImGui.TreePop();
            }
        }

        private static string ShortCurrency(string name) =>
            string.Equals(name, WantedCurrencies.Divine, StringComparison.OrdinalIgnoreCase) ? "div"
            : string.Equals(name, WantedCurrencies.Chaos, StringComparison.OrdinalIgnoreCase) ? "c"
            : name;
    }
}
