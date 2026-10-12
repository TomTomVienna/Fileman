using Fileman.Core.Protection;

namespace Fileman.App;

/// <summary>
/// Warnung bei ungewöhnlich vielen Änderungen. Der Anwender entscheidet: weiter sichern (war gewollt) oder angehalten lassen.
/// </summary>
internal sealed class MassChangeForm : Form
{
    public MassChangeForm(MassChangeReport report)
    {
        Text = "Fileman – Sicherung angehalten";
        Size = new Size(760, 600);
        MinimumSize = new Size(560, 460);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        Icon = StatusIcons.For(Core.Automation.ServiceStatus.MassChange);

        var title = new Label
        {
            Text = "Ungewöhnlich viele Änderungen – die automatische Sicherung wurde angehalten",
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            ForeColor = StatusIcons.ColorFor(Core.Automation.ServiceStatus.MassChange),
            Margin = Ui.Gap,
        };
        var explanation = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Margin = Ui.Gap,
            Text = $"""
                {report.Reason}
                Erkannt am {report.DetectedUtc.ToLocalTime():dd.MM.yyyy} um {report.DetectedUtc.ToLocalTime():HH:mm} Uhr.

                Das kann harmlos sein (z. B. wenn Sie gerade viele Dateien aufgeräumt haben), kann aber auch auf einen
                Verschlüsselungstrojaner hindeuten. Ihre bisher gesicherten Versionen sind unverändert und sicher.

                Wenn Sie diese Änderungen NICHT selbst vorgenommen haben:
                  1. Ziehen Sie die Sicherungsplatte sofort ab.
                  2. Arbeiten Sie nicht weiter an diesem PC und verständigen Sie Ihre IT-Betreuung.
                """,
        };

        var details = Ui.DetailsList(("Betroffen (Beispiele und Ordner)", 680));
        details.HeaderStyle = ColumnHeaderStyle.None;
        foreach (var folder in report.Folders)
            details.Items.Add(new ListViewItem("Ordner: " + folder) { Font = new Font(details.Font, FontStyle.Bold) });
        foreach (var example in report.Examples)
            details.Items.Add(example);

        var confirm = Ui.Button("Das war ich – Änderungen sichern", (_, _) => Decide(DialogResult.Yes));
        var keep = Ui.Button("Angehalten lassen", (_, _) => Decide(DialogResult.No));
        CancelButton = keep;
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([keep, confirm]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(explanation, 0, 1);
        layout.Controls.Add(details, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        Shown += (_, _) => keep.Focus(); // die sichere Wahl ist vorausgewählt
    }

    private void Decide(DialogResult result)
    {
        if (result == DialogResult.Yes
            && MessageBox.Show(this, "Haben Sie diese Änderungen wirklich selbst vorgenommen?\n\nDie geänderten Stände werden dann gesichert und die Automatik läuft weiter.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        DialogResult = result;
        Close();
    }
}
