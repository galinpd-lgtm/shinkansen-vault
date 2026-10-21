using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace ShinkansenVault.Cli;

/// <summary>Runs WinForms code on a fresh STA thread (needed for dialogs and the clipboard).</summary>
internal static class Sta
{
    public static T Run<T>(Func<T> func)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = func(); } catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw error;
        return result;
    }
}

/// <summary>Consent dialog: shows action, reason, references and target. "Deny" is the default (Esc / close).</summary>
internal sealed class ConsentDialog : IConsentPrompt
{
    public bool Confirm(ConsentRequest r) => Sta.Run(() =>
    {
        Application.EnableVisualStyles();
        using var form = NewForm("Shinkansen Vault — заявка за достъп / access request", 560, 420);

        var text = new StringBuilder()
            .AppendLine($"Действие / Action:  {r.Action}")
            .AppendLine()
            .AppendLine("Причина / Reason:")
            .AppendLine("    " + r.Reason)
            .AppendLine()
            .AppendLine("Тайни / Secrets:");
        foreach (var x in r.Refs) text.AppendLine("    " + x);
        text.AppendLine()
            .AppendLine("Къде / Target:")
            .AppendLine("    " + r.Target)
            .AppendLine()
            .AppendLine("Проверете командата/целта. При „Разреши“ Windows Hello ще поиска PIN.")
            .AppendLine("Check the command/target. \"Allow\" will ask for your Windows Hello PIN.");

        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 10),
            Text = text.ToString(),
        };
        var deny = new Button { Text = "Откажи / Deny", DialogResult = DialogResult.Cancel, Width = 160, Height = 34 };
        var allow = new Button { Text = "Разреши / Allow", DialogResult = DialogResult.OK, Width = 160, Height = 34 };
        AddButtons(form, box, deny, allow);
        form.CancelButton = deny;
        form.Shown += (_, _) => { deny.Focus(); box.SelectionLength = 0; };
        return form.ShowDialog() == DialogResult.OK;
    });

    internal static Form NewForm(string title, int w, int h) => new()
    {
        Text = title, Width = w, Height = h, StartPosition = FormStartPosition.CenterScreen,
        TopMost = true, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = true,
        FormBorderStyle = FormBorderStyle.FixedDialog,
    };

    internal static void AddButtons(Form form, Control body, Button left, Button right)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        panel.Controls.Add(right);
        panel.Controls.Add(left);
        form.Controls.Add(body);
        form.Controls.Add(panel);
    }
}

/// <summary>Dialog where the human types a new secret. The agent never handles the value.</summary>
internal sealed class SecretInputDialog : ISecretInput
{
    public byte[]? ReadSecret(string reference, string? label) => Sta.Run<byte[]?>(() =>
    {
        Application.EnableVisualStyles();
        using var form = ConsentDialog.NewForm("Shinkansen Vault — нова тайна / new secret", 520, 230);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), RowCount = 4 };
        layout.Controls.Add(new Label { AutoSize = true, Text = $"Референция / Reference: {reference}" });
        layout.Controls.Add(new Label { AutoSize = true, Text = $"Етикет / Label: {label ?? "-"}" });
        layout.Controls.Add(new Label { AutoSize = true, Text = "Стойност / Value:" });
        var input = new TextBox { UseSystemPasswordChar = true, Width = 460 };
        layout.Controls.Add(input);
        var cancel = new Button { Text = "Откажи / Cancel", DialogResult = DialogResult.Cancel, Width = 150, Height = 32 };
        var save = new Button { Text = "Запази / Save", DialogResult = DialogResult.OK, Width = 150, Height = 32 };
        ConsentDialog.AddButtons(form, layout, cancel, save);
        form.CancelButton = cancel;
        form.AcceptButton = save;
        form.Shown += (_, _) => { form.Activate(); input.Focus(); };
        if (form.ShowDialog() != DialogResult.OK) return null;
        // WinForms TextBox only exposes the value as a string; convert and clear the control at once.
        var bytes = Encoding.UTF8.GetBytes(input.Text);
        input.Clear();
        return bytes;
    });
}

/// <summary>Clipboard that keeps the value out of Windows clipboard history and cloud clipboard.</summary>
internal sealed class WinClipboard : IClipboard
{
    public void SetText(string text) => Sta.Run(() =>
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 1, 0, 0, 0 }));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[] { 0, 0, 0, 0 }));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[] { 0, 0, 0, 0 }));
        Clipboard.SetDataObject(data, copy: true);
        return true;
    });

    public void ClearIfEquals(string text) => Sta.Run(() =>
    {
        if (Clipboard.ContainsText() && Clipboard.GetText() == text) Clipboard.Clear();
        return true;
    });
}
