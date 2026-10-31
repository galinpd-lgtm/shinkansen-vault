using System.Drawing;
using System.Runtime.InteropServices;
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

    internal static Form NewForm(string title, int w, int h)
    {
        var form = new Form
        {
            Text = title, Width = w, Height = h, StartPosition = FormStartPosition.CenterScreen,
            TopMost = true, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = true,
            FormBorderStyle = FormBorderStyle.FixedDialog,
        };
        // Claude starts MCP servers with a "hidden window" startup flag, and Windows applies it to the
        // first window the process shows, so the dialog would exist but stay invisible. Show it again
        // explicitly once the modal loop runs; a second ShowWindow call is always honoured.
        form.Load += (_, _) => form.BeginInvoke(() =>
        {
            ShowWindow(form.Handle, SwShowNormal);
            SetForegroundWindow(form.Handle);
            form.Activate();
        });
        return form;
    }

    private const int SwShowNormal = 1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

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
    public byte[]? ReadSecret(string reference, string? label, string? reason = null) => Sta.Run<byte[]?>(() =>
    {
        Application.EnableVisualStyles();
        using var form = ConsentDialog.NewForm("Shinkansen Vault — нова тайна / new secret", 560, reason == null ? 230 : 300);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), RowCount = 5 };
        if (reason != null)
            layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(520, 0), Text = $"AI агентът иска тази тайна / An AI agent asks for this secret:\n{reason}" });
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

/// <summary>Simple yes/no question in the vault's style (top-most, visible even when started hidden).</summary>
internal static class Ask
{
    public static bool YesNo(string title, string message, string yes, string no) => Sta.Run(() =>
    {
        Application.EnableVisualStyles();
        using var form = ConsentDialog.NewForm(title, 620, 360);
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericSansSerif, 10), Text = message.Replace("\n", "\r\n"),
        };
        var noButton = new Button { Text = no, DialogResult = DialogResult.Cancel, Width = 180, Height = 34 };
        var yesButton = new Button { Text = yes, DialogResult = DialogResult.OK, Width = 180, Height = 34 };
        ConsentDialog.AddButtons(form, box, noButton, yesButton);
        form.CancelButton = noButton;
        form.Shown += (_, _) => { yesButton.Focus(); box.SelectionLength = 0; };
        return form.ShowDialog() == DialogResult.OK;
    });
}

/// <summary>File pickers and opening a web page in the default browser.</summary>
internal static class Pickers
{
    private static string Downloads => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public static string? OpenCsv() => Sta.Run<string?>(() =>
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Файлът с пароли от Google / Google Password Manager export",
            Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(Downloads) ? Downloads : "",
        };
        return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : null;
    });

    public static string? SaveCsv() => Sta.Run<string?>(() =>
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Къде да запиша файла за Google / where to save the file for Google",
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"svault-for-google-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            InitialDirectory = Directory.Exists(Downloads) ? Downloads : "",
            OverwritePrompt = true,
        };
        return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : null;
    });

    /// <summary>Opens only http(s) addresses.</summary>
    public static void OpenUrl(string? url)
    {
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* no browser: the human opens it */ }
    }
}

/// <summary>Guides the human through changing one password on a website.</summary>
internal sealed class RotationDialog : IRotationPrompt
{
    public bool ConfirmChanged(string reference, string? site, Action copyNew, Action copyOld) => Sta.Run(() =>
    {
        Application.EnableVisualStyles();
        using var form = ConsentDialog.NewForm("Shinkansen Vault — смяна на парола / change password", 680, 420);
        var text =
            $"Тайна / Secret:  {reference}\r\n" +
            (site != null ? $"Сайт / Site:  {site}\r\n" : "") +
            "\r\nНОВАТА парола вече е в клипборда.\r\n\r\n" +
            "1. Отворете страницата за смяна на паролата на сайта.\r\n" +
            "2. Ако сайтът иска старата парола — „Копирай старата“ и я поставете.\r\n" +
            "3. „Копирай новата“ и я поставете (Ctrl+V) в полетата за нова парола.\r\n" +
            "4. Запазете на сайта. Ако Chrome предложи да обнови паролата в Google — натиснете „Обнови“.\r\n" +
            "5. Само ако сайтът прие новата парола — „Да, сайтът я прие“.\r\n\r\n" +
            "The NEW password is in the clipboard. Paste it on the website, save there, then confirm here.\r\n" +
            "Clipboard is cleared when this window closes.";
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericSansSerif, 10), Text = text,
        };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 96, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };
        Button Btn(string t, int w = 200) => new() { Text = t, Width = w, Height = 34 };
        var openSite = Btn("Отвори сайта / Open site");
        var old = Btn("Копирай старата / Copy old");
        var neu = Btn("Копирай новата / Copy new");
        var no = Btn("Не, остави старата / Keep old", 230);
        var yes = Btn("Да, сайтът я прие / Saved", 230);
        openSite.Enabled = site != null && site.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        openSite.Click += (_, _) => Pickers.OpenUrl(site);
        old.Click += (_, _) => copyOld();
        neu.Click += (_, _) => copyNew();
        no.DialogResult = DialogResult.Cancel;
        yes.DialogResult = DialogResult.OK;
        panel.Controls.AddRange(new Control[] { openSite, old, neu, no, yes });
        form.Controls.Add(box);
        form.Controls.Add(panel);
        form.CancelButton = no;
        form.Shown += (_, _) => { neu.Focus(); box.SelectionLength = 0; };
        return form.ShowDialog() == DialogResult.OK;
    });
}
