using Fileman.Core;
using Fileman.Core.Legacy;

namespace Fileman.App;

/// <summary>Zeigt die in einem Oberordner gefundenen alten Sicherungen zur Bestätigung (Häkchen = als Altbestand eintragen).</summary>
internal sealed class LegacyDetectForm : Form
{
    private readonly ListView _list;

    public LegacyDetectForm(string parent, IReadOnlyList<LegacyCandidate> candidates)
    {
        Text = "Fileman – Alte Sicherungen gefunden";
        Size = new Size(980, 560);
        MinimumSize = new Size(700, 400);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        var suggested = candidates.Count(c => c.Suggested);
        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(920, 0),
            Margin = Ui.Gap,
            Text = suggested == 0
                ? $"In „{parent}“ wurden keine Ordner gefunden, die wie alte Sicherungen aussehen. Sie können Ordner trotzdem von Hand anhaken."
                : $"In „{parent}“ sehen {suggested} Ordner wie alte Sicherungen aus (Datum im Namen oder gleiche Ordnerstruktur). " +
                  "Bitte prüfen: Angehakte Ordner werden als Altbestand eingetragen. Ordner, in denen noch gearbeitet wird, bitte NICHT anhaken.",
        };

        _list = Ui.DetailsList(("Ordner", 430), ("Datum", 90), ("Passt zu gesicherten Ordnern", 170), ("Begründung", 260));
        _list.CheckBoxes = true;
        foreach (var c in candidates)
        {
            var item = new ListViewItem(c.Path) { Tag = c, Checked = c.Suggested };
            item.SubItems.Add(c.DateFromName is { } d ? d.ToLocalTime().ToString("dd.MM.yyyy") : "");
            item.SubItems.Add(c.MasterOverlap > 0 ? c.MasterOverlap.ToString("P0") : "");
            item.SubItems.Add(c.Reason);
            if (c.AlreadyAdded)
                item.ForeColor = SystemColors.GrayText;
            _list.Items.Add(item);
        }
        _list.ItemCheck += (_, e) =>
        {
            if (((LegacyCandidate)_list.Items[e.Index].Tag!).AlreadyAdded)
                e.NewValue = CheckState.Unchecked; // bereits eingetragen
        };

        var ok = Ui.Button("Angehakte eintragen", (_, _) => { DialogResult = DialogResult.OK; Close(); });
        var cancel = Ui.Button("Abbrechen", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        AcceptButton = ok;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([cancel, ok]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(intro, 0, 0);
        layout.Controls.Add(_list, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
    }

    public IReadOnlyList<string> SelectedFolders =>
        _list.CheckedItems.Cast<ListViewItem>().Select(i => ((LegacyCandidate)i.Tag!).Path).ToList();
}
