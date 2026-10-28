namespace Baka;

/// Small themed message and confirmation dialogs, so the browser's own prompts look like the rest of
/// the BAKA chrome instead of the grey Windows MessageBox.
sealed class BakaDialog : Form
{
    BakaDialog(string title, string message, IReadOnlyList<string> bullets, string glyph, string okText, string? cancelText)
    {
        Text = App.Name;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Content;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        KeyPreview = true;

        var width = Theme.S(420);
        var pad = Theme.S(22);
        var iconFont = new Font(Theme.IconFamily, 16f);
        Disposed += (_, _) => iconFont.Dispose();
        var icon = new Label
        {
            Text = glyph, Font = iconFont, ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(pad, pad, Theme.S(36), Theme.S(36)), BackColor = Theme.Fill2,
        };
        Controls.Add(icon);
        var textLeft = pad + Theme.S(50);
        var textWidth = width - textLeft - pad;
        var y = pad;
        var titleLabel = new Label { Text = title, Font = Theme.Title, ForeColor = Theme.Text, AutoSize = false };
        var titleHeight = TextRenderer.MeasureText(title, Theme.Title, new Size(textWidth, 0), TextFormatFlags.WordBreak).Height + Theme.S(2);
        titleLabel.SetBounds(textLeft, y, textWidth, titleHeight);
        Controls.Add(titleLabel);
        y += titleHeight + Theme.S(6);
        if (message.Length > 0)
        {
            var h = TextRenderer.MeasureText(message, Theme.Ui, new Size(textWidth, 0), TextFormatFlags.WordBreak).Height + Theme.S(2);
            Controls.Add(new Label { Text = message, ForeColor = Theme.Muted, AutoSize = false, Bounds = new Rectangle(textLeft, y, textWidth, h) });
            y += h + Theme.S(6);
        }
        foreach (var line in bullets)
        {
            var text = "•  " + line;
            var h = TextRenderer.MeasureText(text, Theme.Ui, new Size(textWidth, 0), TextFormatFlags.WordBreak).Height + Theme.S(2);
            Controls.Add(new Label { Text = text, ForeColor = Theme.Text, AutoSize = false, Bounds = new Rectangle(textLeft, y, textWidth, h) });
            y += h + Theme.S(3);
        }
        y = Math.Max(y, pad + Theme.S(36)) + Theme.S(18);

        var ok = new PillButton(okText, cancelText is null ? "" : Theme.Check, accent: true);
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var right = width - pad;
        ok.SetBounds(right - ok.PreferredWidth, y, ok.PreferredWidth, Theme.S(32));
        Controls.Add(ok);
        if (cancelText is not null)
        {
            var cancel = new PillButton(cancelText);
            cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            cancel.SetBounds(ok.Left - Theme.S(8) - cancel.PreferredWidth, y, cancel.PreferredWidth, Theme.S(32));
            Controls.Add(cancel);
        }
        ClientSize = new Size(width, y + Theme.S(32) + pad);
        Shown += (_, _) => { Theme.Glass(this, backdrop: false); ok.Focus(); };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            else if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; Close(); }
        };
    }

    public static bool Confirm(IWin32Window? owner, string title, string message, IReadOnlyList<string> bullets,
        string okText, string glyph = Theme.Info)
    {
        using var dlg = new BakaDialog(title, message, bullets, glyph, okText, "Cancel");
        return (owner is null ? dlg.ShowDialog() : dlg.ShowDialog(owner)) == DialogResult.OK;
    }

    public static void Info(IWin32Window? owner, string title, string message, string glyph = Theme.Info)
    {
        using var dlg = new BakaDialog(title, message, [], glyph, "OK", null);
        if (owner is null) dlg.ShowDialog(); else dlg.ShowDialog(owner);
    }
}
