using Fileman.Core;
using Fileman.Core.Catalog;
using Fileman.Core.Metadata;

namespace Fileman.App;

/// <summary>Zwei Versionen nebeneinander; Unterschiede sind hervorgehoben.</summary>
internal sealed class CompareForm : Form
{
    private static readonly Color DifferenceBack = Color.FromArgb(255, 244, 206);

    private readonly IReadOnlyList<ComparisonRow> _rows;
    private readonly ListView _list;
    private readonly CheckBox _onlyDifferences;

    public CompareForm(VersionRecord older, VersionRecord newer, IReadOnlyList<ComparisonRow> rows)
    {
        _rows = rows;
        Text = $"Vergleich – {Path.GetFileName(newer.Path)}";
        Size = new Size(1000, 560);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        _list = Ui.DetailsList(("Eigenschaft", 190), ($"Stand {DisplayText.Time(older.LastWriteUtc)}", 380), ($"Stand {DisplayText.Time(newer.LastWriteUtc)}", 380));
        _list.ShowItemToolTips = true;
        _onlyDifferences = new CheckBox { Text = "Nur Unterschiede anzeigen", AutoSize = true, Margin = Ui.Gap };
        _onlyDifferences.CheckedChanged += (_, _) => Fill();

        var differences = rows.Count(r => r.Differs);
        var summary = new Label
        {
            AutoSize = true,
            Margin = Ui.Gap,
            Text = rows.Any(r => r.Property.StartsWith("Prüfsumme", StringComparison.Ordinal) && !r.Differs)
                ? "Der Inhalt beider Versionen ist identisch."
                : $"{differences} Eigenschaften unterscheiden sich (gelb markiert).",
        };

        var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        bottom.Controls.Add(_onlyDifferences);
        bottom.Controls.Add(Ui.Button("Schließen", (_, _) => Close()));

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(summary, 0, 0);
        layout.Controls.Add(_list, 0, 1);
        layout.Controls.Add(bottom, 0, 2);
        Controls.Add(layout);
        Fill();
    }

    private void Fill()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var row in _rows.Where(r => !_onlyDifferences.Checked || r.Differs))
        {
            var item = new ListViewItem(row.Property) { ToolTipText = $"{row.Left}\n{row.Right}" };
            item.SubItems.Add(row.Left);
            item.SubItems.Add(row.Right);
            if (row.Differs)
            {
                item.BackColor = DifferenceBack;
                item.Font = new Font(_list.Font, FontStyle.Bold);
            }
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }
}
