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
    // plugin. Shown in the plugin settings inside a collapsible panel; hovering a row shows the
    // history of that item.
    public static class SalesLedger
    {
        private static readonly object Sync = new();
        private static List<SaleRecord> _records = [];
        private static string _path;
        private static string _filter = string.Empty;
        private static bool _confirmClear;

        private static readonly NumVector4 Green = new(0.3f, 1f, 0.4f, 1f);
        private static readonly NumVector4 Red = new(1f, 0.4f, 0.4f, 1f);
        private static readonly NumVector4 Grey = new(0.8f, 0.8f, 0.8f, 1f);

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

        private static string ShortCurrency(string name) =>
            string.Equals(name, WantedCurrencies.Divine, StringComparison.OrdinalIgnoreCase) ? "div"
            : string.Equals(name, WantedCurrencies.Chaos, StringComparison.OrdinalIgnoreCase) ? "c"
            : name;

        // percent change from `from` to `to`, or null when it cannot be compared
        private static double? Change(SaleRecord from, SaleRecord to)
        {
            if (from == null ||
                to == null ||
                from.Rate <= 0 ||
                !string.Equals(from.WantedCurrency, to.WantedCurrency, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return (to.Rate - from.Rate) / from.Rate * 100d;
        }

        private static NumVector4 ChangeColor(double change) =>
            change > 0.05 ? Green : change < -0.05 ? Red : Grey;

        // TextUnformatted everywhere: the text may hold a %, which printf-style ImGui calls
        // would turn into garbage
        private static void ColoredText(string text, NumVector4 color)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.TextUnformatted(text);
            ImGui.PopStyleColor();
        }

        public static void Draw()
        {
            SaleRecord[] all;

            lock (Sync)
                all = _records.ToArray();

            // the whole ledger sits behind this header, closed by default
            if (!ImGui.CollapsingHeader($"Sales history ({all.Length})###SalesHistoryHeader"))
                return;

            ImGui.TextDisabled("Hover a row for the full history of that item. Rate = wanted currency per item.");

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
                    7,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                return;
            }

            foreach (var header in new[] { "Item", "Last rate", "Previous", "Change", "Best", "Sales", "Last sold" })
                ImGui.TableSetupColumn(header);

            ImGui.TableHeadersRow();

            foreach (var group in groups)
            {
                var history = group.OrderBy(record => record.TimeUtc).ToList();
                var last = history[^1];
                var previous = history.Count > 1 ? history[^2] : null;
                var change = Change(previous, last);

                // best rate among sales for the same currency as the last one
                var best = history
                    .Where(record => string.Equals(record.WantedCurrency, last.WantedCurrency, StringComparison.OrdinalIgnoreCase))
                    .Max(record => record.Rate);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                // a selectable over the whole row, so hovering anywhere in it shows the tooltip
                ImGui.Selectable(
                    $"{group.Key}##ledger_{group.Key}",
                    false,
                    ImGuiSelectableFlags.SpanAllColumns);

                if (ImGui.IsItemHovered())
                    DrawItemHistoryTooltip(group.Key, history);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{FormatRate(last.Rate)} {ShortCurrency(last.WantedCurrency)}");

                ImGui.TableNextColumn();

                if (previous == null)
                    ImGui.TextDisabled("-");
                else
                    ImGui.TextUnformatted($"{FormatRate(previous.Rate)} {ShortCurrency(previous.WantedCurrency)}");

                ImGui.TableNextColumn();

                if (change == null)
                    ImGui.TextDisabled("-");
                else
                    ColoredText($"{change.Value:+0.0;-0.0;0.0}%", ChangeColor(change.Value));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{FormatRate(best)} {ShortCurrency(last.WantedCurrency)}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(history.Count.ToString(CultureInfo.InvariantCulture));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(
                    last.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            }

            ImGui.EndTable();
        }

        private static void DrawItemHistoryTooltip(string item, List<SaleRecord> history)
        {
            ImGui.BeginTooltip();

            ImGui.TextUnformatted(item);
            ImGui.Separator();

            var last = history[^1];
            var sameCurrency = history
                .Where(record => string.Equals(record.WantedCurrency, last.WantedCurrency, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (sameCurrency.Count > 1)
            {
                var avg = sameCurrency.Average(record => record.Rate);
                var min = sameCurrency.Min(record => record.Rate);
                var max = sameCurrency.Max(record => record.Rate);
                var overall = Change(sameCurrency[0], sameCurrency[^1]);

                ImGui.TextUnformatted(
                    $"{sameCurrency.Count} sales   avg {FormatRate(avg)}   min {FormatRate(min)}   max {FormatRate(max)} {ShortCurrency(last.WantedCurrency)}");

                if (overall != null)
                {
                    ImGui.TextUnformatted("Since the first sale:");
                    ImGui.SameLine();
                    ColoredText($"{overall.Value:+0.0;-0.0;0.0}%", ChangeColor(overall.Value));
                }

                ImGui.Separator();
            }

            // newest first, each with its change against the sale before it
            var shown = 0;

            for (var i = history.Count - 1; i >= 0 && shown < 15; i--, shown++)
            {
                var record = history[i];
                var change = i > 0 ? Change(history[i - 1], record) : null;

                ImGui.TextUnformatted(
                    $"{record.TimeUtc.ToLocalTime():yyyy-MM-dd HH:mm}   " +
                    $"{record.Offered} -> {record.Wanted} {ShortCurrency(record.WantedCurrency)}   " +
                    $"rate {FormatRate(record.Rate)}{(record.Reverse ? " (reverse)" : string.Empty)}");

                if (change != null)
                {
                    ImGui.SameLine();
                    ColoredText($"{change.Value:+0.0;-0.0;0.0}%", ChangeColor(change.Value));
                }
            }

            if (history.Count > shown)
                ImGui.TextDisabled($"... and {history.Count - shown} older");

            ImGui.EndTooltip();
        }
    }
}
