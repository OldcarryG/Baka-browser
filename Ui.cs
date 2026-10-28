using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Baka;

/// Apple's dark system palette on a Windows 11 Mica backdrop. The chrome lets WinForms erase its own
/// background — that erase is a GDI FillRect, which leaves the alpha channel at zero, so DWM shows the
/// frosted wallpaper through it. The blur is composited by the OS: no app-side blur, no extra surface,
/// no per-frame cost. Everything drawn on top is one of Apple's fill greys, painted opaque by GDI+.
static class Theme
{
    public static bool GlassOn { get; private set; }
    public static bool CustomMode => Config.Current.ThemeMode == "Custom";
    /// BAKA: the Carbon Mono palette of BAKA Windows (graphite glass, white type, a white accent), so the
    /// browser reads as part of the desktop instead of a separate app. Default on new installs.
    public static bool BakaMode => Config.Current.ThemeMode == "Baka";
    static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    static Color CustomSurface
    {
        get
        {
            try { return ColorTranslator.FromHtml(Config.Current.CustomBackgroundColor); }
            catch { return Color.FromArgb(0x30, 0x24, 0x3B); }
        }
    }
    static double Luminance(Color c)
    {
        static double Channel(int v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
    static Color Blend(Color color, Color target, float amount) => Color.FromArgb(
        (int)(color.R + (target.R - color.R) * amount),
        (int)(color.G + (target.G - color.G) * amount),
        (int)(color.B + (target.B - color.B) * amount));
    public static bool DarkMode => CustomMode ? Luminance(CustomSurface) < 0.18 : Config.Current.ThemeMode != "Light";

    /// Black once the backdrop is up, a flat near-black on older Windows where it is not.
    public static Color ChromeBg => CustomMode ? Blend(CustomSurface, DarkMode ? Color.Black : Color.White, 0.12f)
        : GlassOn ? (DarkMode ? Color.Black : Color.White) : DarkMode
            ? Color.FromArgb(0x24, 0x24, 0x26) : Color.FromArgb(0xF7, 0xF7, 0xF9);

    // Dark and light system palettes. Custom keeps the dark surfaces and changes the accent.
    public static Color Content => CustomMode ? CustomSurface : BakaMode ? Hex(0x161619)
        : DarkMode ? Color.FromArgb(0x1C, 0x1C, 0x1E) : Color.FromArgb(0xFA, 0xFA, 0xFC);
    /// The tab row has to be opaque (see TabStrip), so it cannot be glass — but Content is twice as
    /// bright as Mica-over-black measures (~#0D0A0E here), which read as a pale band across the top.
    /// This is close enough to the backdrop that the seam disappears.
    public static Color Strip => CustomMode ? Blend(Content, DarkMode ? Color.Black : Color.White, 0.22f) : BakaMode ? Hex(0x0B0B0D)
        : DarkMode ? Color.FromArgb(0x10, 0x10, 0x12) : Color.FromArgb(0xEF, 0xEF, 0xF3);
    public static Color Fill1 => CustomMode ? Blend(Content, DarkMode ? Color.White : Color.Black, 0.07f) : BakaMode ? Hex(0x1F1F23)
        : DarkMode ? Color.FromArgb(0x2C, 0x2C, 0x2E) : Color.White;
    public static Color Fill2 => CustomMode ? Blend(Content, DarkMode ? Color.White : Color.Black, 0.14f) : BakaMode ? Hex(0x27272A)
        : DarkMode ? Color.FromArgb(0x3A, 0x3A, 0x3C) : Color.FromArgb(0xF2, 0xF2, 0xF6);
    public static Color Fill3 => CustomMode ? Blend(Content, DarkMode ? Color.White : Color.Black, 0.22f) : BakaMode ? Hex(0x34343A)
        : DarkMode ? Color.FromArgb(0x48, 0x48, 0x4A) : Color.FromArgb(0xE5, 0xE5, 0xEA);
    public static Color Text => BakaMode ? Hex(0xFAFAFA) : DarkMode ? Color.FromArgb(0xF2, 0xF2, 0xF7) : Color.FromArgb(0x1C, 0x1C, 0x1E);
    /// secondaryLabel, which on Apple's dark backgrounds is white at 60% rather than systemGray:
    /// #8E8E93 only made 4.06:1 against Fill1, under the 4.5:1 floor for text this size. This makes 5.3:1.
    public static Color Muted => BakaMode ? Hex(0xA1A1AA) : DarkMode ? Color.FromArgb(0x9E, 0x9E, 0xA3) : Color.FromArgb(0x63, 0x63, 0x66);
    static string? _accentValue;
    static Color _accentColor;
    public static Color Accent
    {
        get
        {
            var value = Config.Current.AccentColor;
            if (_accentValue != value)
            {
                _accentColor = ColorTranslator.FromHtml(value);
                _accentValue = value;
            }
            return _accentColor;
        }
    }
    public static Color Line => CustomMode ? Blend(Content, DarkMode ? Color.White : Color.Black, 0.28f) : BakaMode ? Hex(0x3F3F46)
        : DarkMode ? Color.FromArgb(0x38, 0x38, 0x3A) : Color.FromArgb(0xD1, 0xD1, 0xD6);
    /// systemRed, lightened. This keeps error and warning text readable against a card background.
    public static Color Bad => DarkMode ? Color.FromArgb(0xFF, 0x69, 0x61) : Color.FromArgb(0xD7, 0x00, 0x15);

    [DllImport("user32.dll")] static extern uint GetDpiForSystem();

    /// Every layout number in the app is written for a 96-dpi screen and passed through S() on its way
    /// out. It has to be: the app is PerMonitorV2, so Windows does not bitmap-stretch it, and fonts in
    /// points grow with the display while a hardcoded pixel does not — text in a box that stayed the
    /// same size. 1.0 at 100%, 1.5 at 150%.
    /// Read here, at first touch of Theme, rather than from a form: the chrome is built in field
    /// initializers that run before any control has a handle to ask.
    /// ponytail: one scale for the session, the system's. Dragging the window to a monitor at a
    /// different scale keeps these metrics — WM_DPICHANGED plus a full relayout is the upgrade.
    public static readonly double Scale = GetDpiForSystem() / 96.0;

    public static int S(int px) => (int)Math.Round(px * Scale);
    public static float Sf(float px) => (float)(px * Scale);

    // Radii: Apple's toolbars sit around 8-10 for controls, more for panels.
    public static readonly int R = S(9);

    /// WCAG relative-luminance contrast ratio, 1.0 to 21.0. Text wants 4.5 or better, an icon or a
    /// border 3.0; the palette is checked against those in --self-check rather than by eye.
    public static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double Ch(int v) =>
                v / 255.0 is var s && s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        var (x, y) = (L(a), L(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    static string Pick(params string[] names) =>
        names.FirstOrDefault(n => FontFamily.Families.Any(f => f.Name == n)) ?? "Segoe UI";

    /// Win11 ships Segoe Fluent Icons; Win10 only has MDL2. The glyph points we use exist in both.
    public static readonly string IconFamily = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");

    // Segoe UI Variable is Win11's optical-size family - the nearest thing to SF Pro's Text/Display split.
    public static readonly Font Ui = new(Pick("Segoe UI Variable Text", "Segoe UI"), 9f);
    /// The address bar, a step up from body text because it is the one field you read at a glance.
    /// Through Pick like the rest: naming the family outright would silently fall back to whatever
    /// GDI+ substitutes on a Windows 10 machine, which has no Segoe UI Variable.
    public static readonly Font Field = new(Pick("Segoe UI Variable Text", "Segoe UI"), 10.5f);
    public static readonly Font UiBold = new(Pick("Segoe UI Variable Text", "Segoe UI"), 9f, FontStyle.Bold);
    public static readonly Font Title = new(Pick("Segoe UI Variable Display", "Segoe UI"), 12f, FontStyle.Bold);

    // Segoe icon glyphs.
    public const string Back = "", Fwd = "", Reload = "", Stop = "";
    public const string Add = "", Close = "", Settings = "";
    public const string Shield = "", NoShield = "";
    public const string Star = "", StarFill = "";
    public const string Fullscreen = "";
    public const string Private = "";

    // Address-bar and tab marks: padlock on https, warning on http, magnifier when the bar is empty,
    // globe for a tab whose site has no favicon.
    public const string Lock = "", Search = "", Insecure = "", Globe = "";

    // Window controls, because the title bar that used to hold them is gone.
    public const string Min = "", Max = "", Restore = "";

    /// Flat dark button. WinForms' stock button ignores BackColor unless FlatStyle is Flat.
    public static Button Flat(string text)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Fill2,
            ForeColor = Text,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = Line;
        b.FlatAppearance.MouseOverBackColor = Fill3;
        return b;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);

    [StructLayout(LayoutKind.Sequential)]
    struct Margins { public int Left, Right, Top, Bottom; }

    const int DarkModeAttribute = 20, CornerPref = 33, CaptionColor = 35, BackdropType = 38;

    /// Dark frame + rounded corners, and on Win11 22H2+ a Mica backdrop behind the whole client area.
    /// Mica is picked over Acrylic on purpose: it is a one-off blur of the wallpaper, so it costs no
    /// per-frame GPU work, where Acrylic re-blurs whatever is behind the window every frame.
    /// `backdrop: false` keeps the dark frame but leaves the client area an opaque sheet.
    public static void Glass(Form f, bool backdrop = true, bool updateGlobalBackdrop = false)
    {
        if (updateGlobalBackdrop) GlassOn = false;
        var on = DarkMode ? 1 : 0;
        try { DwmSetWindowAttribute(f.Handle, DarkModeAttribute, ref on, sizeof(int)); } catch { return; }
        var round = 2 /* DWMWCP_ROUND */;
        DwmSetWindowAttribute(f.Handle, CornerPref, ref round, sizeof(int));
        // Dark mode alone is not enough: with "show accent colour on title bars" on, the caption of any
        // window that still has one comes up in the accent colour. This pins it to the sheet colour.
        var caption = Content.B << 16 | Content.G << 8 | Content.R;   // COLORREF is 0x00BBGGRR
        DwmSetWindowAttribute(f.Handle, CaptionColor, ref caption, sizeof(int));
        if (!backdrop || !Config.Current.LiquidGlassEnabled || CustomMode)
        {
            var none = 1 /* DWMSBT_NONE */;
            DwmSetWindowAttribute(f.Handle, BackdropType, ref none, sizeof(int));
            var clear = new Margins();
            DwmExtendFrameIntoClientArea(f.Handle, ref clear);
            return;
        }

        var mica = 4 /* DWMSBT_TABBEDWINDOW */;
        if (DwmSetWindowAttribute(f.Handle, BackdropType, ref mica, sizeof(int)) != 0) return;  // pre-22H2
        // The backdrop shows through wherever the window leaves the alpha channel alone. WinForms' own
        // background erase is a GDI FillRect, which does exactly that — so the glassy controls below
        // paint no background of their own and let it happen.
        var all = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (DwmExtendFrameIntoClientArea(f.Handle, ref all) == 0 && updateGlobalBackdrop) GlassOn = true;
    }
}

static class Draw
{
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Fill(Graphics g, RectangleF r, Color c, float radius)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Round(r, radius);
        using var brush = new SolidBrush(c);
        g.FillPath(brush, path);
    }

    /// Static layered highlight for glass-style controls. It deliberately avoids animation and large
    /// blur surfaces; the OS backdrop and the dashboard provide depth while this stays cheap to repaint.
    public static void Glass(Graphics g, RectangleF r, Color baseColor, float radius)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Round(r, radius);
        var top = Theme.DarkMode
            ? Color.FromArgb(Math.Min(255, baseColor.R + 17), Math.Min(255, baseColor.G + 17), Math.Min(255, baseColor.B + 17))
            : Color.FromArgb(Math.Max(0, baseColor.R - 5), Math.Max(0, baseColor.G - 5), Math.Max(0, baseColor.B - 5));
        using (var brush = new LinearGradientBrush(r, top, baseColor, LinearGradientMode.Vertical))
            g.FillPath(brush, path);
        using var edge = new Pen(Color.FromArgb(Theme.DarkMode ? 58 : 116, Color.White), 1f);
        g.DrawPath(edge, path);
        var shine = new RectangleF(r.X + 1.5f, r.Y + 1.5f, Math.Max(0, r.Width - 3), Math.Max(1, r.Height * .34f));
        using var shinePath = Round(shine, Math.Min(radius, shine.Height / 2));
        using var shineBrush = new LinearGradientBrush(shine, Color.FromArgb(Theme.DarkMode ? 18 : 38, Color.White),
            Color.FromArgb(0, Color.White), LinearGradientMode.Vertical);
        g.FillPath(shineBrush, shinePath);
    }

    public static void Stroke(Graphics g, RectangleF r, Color c, float radius, float width = 1f)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Round(r, radius);
        using var pen = new Pen(c, width);
        g.DrawPath(pen, path);
    }

    static readonly StringFormat Ellipsis = new(StringFormatFlags.NoWrap)
    {
        Trimming = StringTrimming.EllipsisCharacter,
        LineAlignment = StringAlignment.Center,
        Alignment = StringAlignment.Near,
    };

    static readonly StringFormat Centre = new(StringFormatFlags.NoWrap)
    {
        Trimming = StringTrimming.EllipsisCharacter,
        LineAlignment = StringAlignment.Center,
        Alignment = StringAlignment.Center,
    };

    // Grid-fit ClearType would fringe against the transparent black of the glass; plain AA does not,
    // and the softer edge is closer to how macOS renders its chrome anyway.
    public static void Label(Graphics g, string text, Font font, Color color, RectangleF r, bool centre = false)
    {
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, r, centre ? Centre : Ellipsis);
    }

    public static void Glyph(Graphics g, string glyph, Font font, Color color, RectangleF r) =>
        Label(g, glyph, font, color, r, centre: true);
}

/// Rounded icon button with a hover pad. Owner-drawn so it can sit on glass: it paints only the pad and
/// the glyph and inherits the toolbar's colour, so the backdrop is left showing everywhere else.
sealed class IconButton : Control
{
    readonly Font _font;
    bool _hot, _down;

    public string Glyph { get; set; }
    public IconButton(string glyph, string tip, float size = 10f)
    {
        Glyph = glyph;
        _font = new Font(Theme.IconFamily, size);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        Size = new Size(Theme.S(32), Theme.S(32));
        Cursor = Cursors.Hand;
        // Owner-drawn and captionless: without this a screen reader reads out an unnamed control.
        AccessibleName = tip;
        AccessibleRole = AccessibleRole.PushButton;
        if (tip.Length > 0) new ToolTip { InitialDelay = 500 }.SetToolTip(this, tip);
    }

    public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    // Reachable and operable from the keyboard, like the stock button these replace.
    protected override bool IsInputKey(Keys k) => k is Keys.Space or Keys.Enter || base.IsInputKey(k);
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { PerformClick(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var pad = Theme.Sf(2);
        if (Enabled && (_hot || _down))
        {
            var r = new RectangleF(pad, pad, Width - pad * 2, Height - pad * 2);
            if (Config.Current.LiquidGlassEnabled) Draw.Glass(g, r, _down ? Theme.Fill3 : Theme.Fill2, Theme.Sf(8));
            else Draw.Fill(g, r, _down ? Theme.Fill3 : Theme.Fill2, Theme.Sf(8));
        }
        // ShowFocusCues is false until the user actually uses the keyboard, so a mouse click does not
        // leave a ring behind and the button that happens to hold focus at startup stays quiet.
        if (Focused && ShowFocusCues)
            Draw.Stroke(g, new RectangleF(1.5f, 1.5f, Width - 3, Height - 3), Theme.Accent, Theme.R, Theme.Sf(1.6f));

        Draw.Glyph(g, Glyph, Ascii(Glyph) ? Theme.UiBold : _font, Enabled ? Theme.Text : Theme.Muted, ClientRectangle);
    }

    /// A pin with no icon of its own falls back to a monogram, which the icon font cannot draw.
    static bool Ascii(string s) => s.Length == 1 && s[0] < 0x80;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}

/// Safari's address field: a rounded, hairline-bordered rect on the glass toolbar, with a borderless
/// TextBox inside it so the caret and selection stay native.
sealed class OmniBox : Panel
{
    public readonly TextBox Input = new()
    {
        BorderStyle = BorderStyle.None,
        BackColor = Theme.ChromeBg,
        ForeColor = Theme.Text,
        Font = Theme.Field,
        // native, and it answers "what does this field take?" without a line of paint code
        PlaceholderText = "Search or enter address",
    };

    readonly Font _mark = new(Theme.IconFamily, 8.5f);
    string _leading = "";
    bool _focused;

    /// Padlock / warning / magnifier in the leading corner. Empty hides it and the text starts further left.
    public string Leading
    {
        get => _leading;
        set { if (_leading != value) { _leading = value; Place(); Invalidate(); } }
    }

    /// The security mark for a URL: https gets the padlock, plain http the warning, and anything with no
    /// transport to speak of (about:blank or a local file) the magnifier.
    public static string Mark(string url) => url.StartsWith("https://") ? Theme.Lock
        : url.StartsWith("http://") ? Theme.Insecure : Theme.Search;

    public OmniBox()
    {
        // ResizeRedraw because the hairline spans the whole width: without it a shrink only invalidates
        // the strip that was uncovered, and the old right-hand cap stays painted where it was.
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.ChromeBg;
        Height = Theme.S(32);
        Controls.Add(Input);
        Input.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        Input.LostFocus += (_, _) => { _focused = false; Invalidate(); };
        Click += (_, _) => Input.Focus();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Place(); }

    void Place()
    {
        var left = Theme.S(_leading.Length > 0 ? 32 : 14);
        // vertical centring has to be manual: a borderless TextBox is exactly one line tall
        Input.SetBounds(left, (Height - Input.PreferredHeight) / 2 + 1,
                        Math.Max(10, Width - left - Theme.S(14)), Input.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var field = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        // Border only, no fill: a native TextBox paints itself with GDI, which leaves the alpha channel
        // alone, so its rect is a hole the Mica backdrop shows through no matter what we put behind it.
        // Filling the rest of the pill made that hole a differently-tinted smear; letting the whole field
        // be glass makes it one surface with the toolbar, and the hairline still says "type here".
        if (Config.Current.LiquidGlassEnabled)
            Draw.Glass(g, field, Theme.Fill1, Theme.R);
        Draw.Stroke(g, field, _focused ? Theme.Accent : Theme.Line, Theme.R, _focused ? Theme.Sf(1.6f) : 1f);
        if (_leading.Length > 0)
            Draw.Glyph(g, _leading, _mark, _leading == Theme.Insecure ? Theme.Bad : Theme.Muted,
                       new RectangleF(Theme.Sf(9), 0, Theme.Sf(18), Height));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _mark.Dispose();
        base.Dispose(disposing);
    }
}

sealed class StripTab
{
    public string Title = "New tab";
    /// The site's own favicon at 16 px, owned by the strip and disposed when it changes or the tab closes.
    public Image? Icon;
    public object? Tag;
}

/// Safari-style tab strip: rounded pills, only the active and hovered ones filled, per-tab close,
/// trailing "+". WinForms' TabControl cannot be restyled and destroys child handles when pages hide,
/// so it is gone.
/// The strip is the one opaque band in the chrome (see OnPaint), which is also what lets an inactive
/// tab go unfilled: its title still lands on solid pixels, so the glass cannot eat the antialiasing.
/// It is also the window's caption now that there is no title bar: the empty space in it drags the
/// window (the form hands that region to Windows as HTCAPTION) and the three buttons on the right are
/// the ones the caption used to carry.
sealed class TabStrip : Control
{
    static readonly int MaxTabWidth = Theme.S(220), MinTabWidth = Theme.S(90), CloseSize = Theme.S(24),
                        PlusWidth = Theme.S(36), Gap = Theme.S(5), Inset = Theme.S(6), CapW = Theme.S(46),
                        Edge = Theme.S(8);
    /// The favicon box. Public because the bitmaps are scaled to it once, on load, rather than every paint.
    public static readonly int IconBox = Theme.S(16);

    readonly Font _iconFont;
    /// The "+" is the one glyph in the strip that is a target rather than a label, so it is drawn at
    /// the size you can actually aim at. 7pt Fluent renders a ~6 px hairline cross: legible as an
    /// icon, invisible as a button.
    readonly Font _plusFont;
    readonly ToolTip _tips = new() { InitialDelay = 450, ReshowDelay = 120 };
    public List<StripTab> Tabs { get; } = [];
    public int SelectedIndex { get; private set; } = -1;
    public bool Vertical { get; set; }
    public bool Compact { get; set; }

    int _hotTab = -1, _hotClose = -1, _hotCap = -1;
    bool _hotPlus;
    int _scrollOffset;

    public event Action<int>? SelectionChanged;
    public event Action<int>? CloseClicked;
    public event Action? NewTabClicked;

    public TabStrip()
    {
        _iconFont = new Font(Theme.IconFamily, 7f);
        _plusFont = new Font(Theme.IconFamily, 10f);
        // Tab widths and the window buttons are both computed from ClientSize, so every pixel of a
        // resize moves them: the whole strip has to repaint, not just the band that was uncovered.
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Strip;   // opaque, see OnPaint
        Height = Theme.S(42);
    }

    public void Select(int index)
    {
        if (index < 0 || index >= Tabs.Count || index == SelectedIndex) { Invalidate(); return; }
        SelectedIndex = index;
        Invalidate();
        SelectionChanged?.Invoke(index);
    }

    /// Selection follows the list after a removal, without firing for an index that no longer exists.
    public void Removed(int index)
    {
        if (Tabs.Count == 0) { SelectedIndex = -1; Invalidate(); return; }
        SelectedIndex = Math.Clamp(index >= Tabs.Count ? Tabs.Count - 1 : index, 0, Tabs.Count - 1);
        Invalidate();
        SelectionChanged?.Invoke(SelectedIndex);
    }

    int TabWidth => Tabs.Count == 0 ? 0
        : Math.Clamp((Width - PlusWidth - CapW * 3 - Edge * 2) / Tabs.Count - Gap, MinTabWidth, MaxTabWidth);

    public void RefreshLayout()
    {
        _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, Tabs.Count * Theme.S(44) - Height + Theme.S(44)));
        Invalidate();
    }

    Rectangle TabRect(int i) => Vertical
        ? new Rectangle(Theme.S(4), Theme.S(40) + i * Theme.S(44) - _scrollOffset,
            Math.Max(Theme.S(34), Width - Theme.S(8)), Theme.S(40))
        : new Rectangle(Edge + i * (TabWidth + Gap), Inset, TabWidth, Height - Inset * 2);

    Rectangle CloseRect(int i)
    {
        var t = TabRect(i);
        return new Rectangle(t.Right - CloseSize - Theme.S(4), t.Y + (t.Height - CloseSize) / 2, CloseSize, CloseSize);
    }

    /// Full `PlusWidth` of hit area — the strip already reserves that much in TabWidth, so the button
    /// was throwing 8 px of its own target away.
    /// Clamped left of the window buttons because OnMouseDown gives those the first look: a "+" that
    /// drifted under them (12 tabs at 1280 px does it) is not a hard-to-hit button, it is a button that
    /// minimises the window.
    public Rectangle PlusRect()
    {
        if (Vertical) return new Rectangle(Theme.S(4), Theme.S(3), Math.Max(Theme.S(34), Width - Theme.S(8)), Theme.S(32));
        var x = Math.Min(Edge + Tabs.Count * (TabWidth + Gap) + Theme.S(2),
                         Width - CapW * 3 - PlusWidth - Theme.S(4));
        return new Rectangle(Math.Max(Edge, x), Inset, PlusWidth, Height - Inset * 2);
    }

    /// Minimise, maximise, close — 0..2, hard against the right edge and full height, as on every
    /// other Win11 window.
    Rectangle CapRect(int i) => new(Width - CapW * (3 - i), 0, CapW, Height);

    int CapHit(Point p) => !Vertical &&
        p.X >= Width - CapW * 3 ? Math.Clamp((p.X - (Width - CapW * 3)) / CapW, 0, 2) : -1;

    /// The leftover space, which the form reports to Windows as the caption it no longer draws.
    public bool Caption(Point p) =>
        !Vertical && CapHit(p) < 0 && !PlusRect().Contains(p) &&
        !Enumerable.Range(0, Tabs.Count).Any(i => TabRect(i).Contains(p));

    /// Where maximise is. The form reports this one rect to Windows as HTMAXBUTTON so that hovering it
    /// opens the Win11 snap-layouts flyout — which also makes it non-client, so its hover and its click
    /// arrive at the form as WM_NC* messages and come back in through HotCap and CapClick.
    public Rectangle MaxRect => Vertical ? Rectangle.Empty : CapRect(1);

    /// -1 for none. Set from the form while the pointer is on the non-client maximise button, because
    /// OnMouseMove below never sees that rect.
    public int HotCap
    {
        set { if (_hotCap != value) { _hotCap = value; Invalidate(); } }
    }

    /// Minimise / maximise-restore / close by index. Two doors reach it: a plain click in OnMouseDown,
    /// and the form's WM_NCLBUTTONUP for the maximise button that Windows now owns.
    public void CapClick(int i)
    {
        if (FindForm() is not { } form) return;
        if (i == 0) form.WindowState = FormWindowState.Minimized;
        else if (i == 1)
            form.WindowState = form.WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal : FormWindowState.Maximized;
        else form.Close();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int tab = -1, close = -1;
        for (var i = 0; i < Tabs.Count; i++)
            if (TabRect(i).Contains(e.Location))
            {
                tab = i;
                if (CloseRect(i).Contains(e.Location)) close = i;
                break;
            }
        var plus = PlusRect().Contains(e.Location);
        var cap = CapHit(e.Location);
        if (tab != _hotTab || close != _hotClose || plus != _hotPlus || cap != _hotCap)
        {
            (_hotTab, _hotClose, _hotPlus, _hotCap) = (tab, close, plus, cap);
            // set here rather than per-move: the hot flags already change only when the pointer
            // crosses a boundary, which is exactly when the cursor should change
            Cursor = plus ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        _tips.SetToolTip(this, _hotPlus ? "New tab" : Vertical && Compact && tab >= 0 ? Tabs[tab].Title : "");
        base.OnMouseMove(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Vertical)
        {
            var row = Theme.S(44);
            var max = Math.Max(0, Tabs.Count * row - Math.Max(0, Height - Theme.S(40)));
            _scrollOffset = Math.Clamp(_scrollOffset - Math.Sign(e.Delta) * row * 3, 0, max);
            Invalidate();
        }
        base.OnMouseWheel(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        (_hotTab, _hotClose, _hotPlus, _hotCap) = (-1, -1, false, -1);
        Cursor = Cursors.Default;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Vertical && e.Button == MouseButtons.Left && CapHit(e.Location) is var cap and >= 0 && FindForm() is not null)
        {
            CapClick(cap);
            return;
        }
        if (PlusRect().Contains(e.Location) && e.Button == MouseButtons.Left) { NewTabClicked?.Invoke(); return; }
        for (var i = 0; i < Tabs.Count; i++)
        {
            if (!TabRect(i).Contains(e.Location)) continue;
            // middle-click anywhere on a tab closes it, same as every other browser
            if (e.Button == MouseButtons.Middle ||
                (e.Button == MouseButtons.Left && !Compact && CloseRect(i).Contains(e.Location)))
                CloseClicked?.Invoke(i);
            else if (e.Button == MouseButtons.Left)
                Select(i);
            return;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        // The one place that wants an opaque fill: DWM still paints the caption strip behind us (in the
        // accent colour, if the user turned that on) and glass-black would let it through as a coloured
        // band across the tabs. A GDI+ brush writes alpha 255 and covers it. Everything below still glasses.
        using (var bg = new SolidBrush(Theme.Strip)) g.FillRectangle(bg, ClientRectangle);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        if (Vertical)
        {
            if (_hotPlus) Draw.Fill(g, PlusRect(), Theme.Fill2, Theme.R - 1);
            Draw.Glyph(g, Theme.Add, _plusFont, Theme.Text, PlusRect());
            for (var i = 0; i < Tabs.Count; i++)
            {
                var r = TabRect(i);
                if (!r.IntersectsWith(ClientRectangle)) continue;
                var active = i == SelectedIndex;
                if (active || i == _hotTab)
                {
                    if (Config.Current.LiquidGlassEnabled) Draw.Glass(g, r, active ? Theme.Fill2 : Theme.Fill1, Theme.R - 1);
                    else Draw.Fill(g, r, active ? Theme.Fill2 : Theme.Fill1, Theme.R - 1);
                }
                if (active)
                {
                    using var activeMark = new SolidBrush(Theme.Accent);
                    g.FillRectangle(activeMark, r.X, r.Y + Theme.S(8), Theme.S(2), r.Height - Theme.S(16));
                }
                var mark = Compact
                    ? new Rectangle(r.X + (r.Width - IconBox) / 2, r.Y + (r.Height - IconBox) / 2, IconBox, IconBox)
                    : new Rectangle(r.X + Theme.S(10), r.Y + (r.Height - IconBox) / 2, IconBox, IconBox);
                if (Tabs[i].Icon is { } icon) g.DrawImage(icon, mark);
                else Draw.Glyph(g, Theme.Globe, _iconFont, Theme.Muted, mark);
                if (!Compact)
                {
                    var showClose = active || i == _hotTab;
                    var right = showClose ? CloseRect(i).Left - Theme.S(2) : r.Right - Theme.S(7);
                    Draw.Label(g, Tabs[i].Title, active ? Theme.UiBold : Theme.Ui, active ? Theme.Text : Theme.Muted,
                        new RectangleF(mark.Right + Theme.S(8), r.Y, Math.Max(4, right - mark.Right - Theme.S(8)), r.Height));
                    if (showClose)
                    {
                        var close = CloseRect(i);
                        if (i == _hotClose) Draw.Fill(g, Rectangle.Inflate(close, -Theme.S(2), -Theme.S(2)), Theme.Fill3, close.Height / 2f);
                        Draw.Glyph(g, Theme.Close, _iconFont, i == _hotClose ? Theme.Text : Theme.Muted, close);
                    }
                }
            }
            return;
        }

        for (var i = 0; i < Tabs.Count; i++)
        {
            var r = TabRect(i);
            var active = i == SelectedIndex;
            // Unfilled unless active or hovered: nine identical grey pills is a lot of furniture for
            // information the title already carries.
            if (active || i == _hotTab)
            {
                if (Config.Current.LiquidGlassEnabled) Draw.Glass(g, r, active ? Theme.Fill2 : Theme.Fill1, Theme.R - 1);
                else Draw.Fill(g, r, active ? Theme.Fill2 : Theme.Fill1, Theme.R - 1);
            }
            if (active)
            {
                using var activeMark = new SolidBrush(Theme.Accent);
                g.FillRectangle(activeMark, r.X + Theme.S(12), r.Bottom - Theme.S(3), r.Width - Theme.S(24), Theme.S(2));
            }

            var mark = new Rectangle(r.X + Theme.S(10), r.Y + (r.Height - IconBox) / 2, IconBox, IconBox);
            if (Tabs[i].Icon is { } icon) g.DrawImage(icon, mark);
            else Draw.Glyph(g, Theme.Globe, _iconFont, Theme.Muted, mark);

            var showClose = active || i == _hotTab;
            var textRight = showClose ? CloseRect(i).Left - Theme.S(2) : r.Right - Theme.S(10);
            var textLeft = mark.Right + Theme.S(8);
            Draw.Label(g, Tabs[i].Title, active ? Theme.UiBold : Theme.Ui, active ? Theme.Text : Theme.Muted,
                new RectangleF(textLeft, r.Y, Math.Max(4, textRight - textLeft), r.Height));

            if (!showClose) continue;
            var c = CloseRect(i);
            // 24 px of hit area, a 20 px pad drawn inside it: the target meets the desktop minimum
            // without the tab looking like it has a button glued to the end.
            var trim = Theme.S(2);
            if (i == _hotClose) Draw.Fill(g, Rectangle.Inflate(c, -trim, -trim), Theme.Fill3, (c.Height - trim * 2) / 2f);
            Draw.Glyph(g, Theme.Close, _iconFont, i == _hotClose ? Theme.Text : Theme.Muted, c);
        }

        var plus = PlusRect();
        // Full text brightness at rest, not Muted: this is the only button in the strip with nothing
        // drawn behind it, so the glyph is the whole affordance. The hover pill is the feedback.
        if (_hotPlus) Draw.Fill(g, plus, Theme.Fill2, Theme.R - 1);
        Draw.Glyph(g, Theme.Add, _plusFont, Theme.Text, plus);

        var maximised = FindForm()?.WindowState == FormWindowState.Maximized;
        for (var i = 0; i < 3; i++)
        {
            var r = CapRect(i);
            if (i == _hotCap) Draw.Fill(g, r, i == 2 ? Theme.Bad : Theme.Fill2, Theme.Sf(2));
            Draw.Glyph(g, i switch { 0 => Theme.Min, 1 => maximised ? Theme.Restore : Theme.Max, _ => Theme.Close },
                _iconFont, i == _hotCap ? Theme.Text : Theme.Muted, r);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _iconFont.Dispose(); _plusFont.Dispose(); _tips.Dispose(); }
        base.Dispose(disposing);
    }
}

/// The bookmarks bar: labels as rounded pills on the glass. Owner-drawn because a row of WinForms
/// Buttons would be opaque square boxes sitting on the backdrop.
sealed class PillBar : Control
{
    readonly List<(string Text, Rectangle R)> _items = [];
    readonly ToolTip _tips = new() { InitialDelay = 350, ReshowDelay = 100 };
    string[] _labels = [];
    bool _vertical, _compact;
    int _scrollOffset;
    int _hot = -1;

    /// index, which button. Right-click and middle-click both matter here.
    public event Action<int, MouseButtons>? ItemClicked;

    public PillBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.ChromeBg;
        Height = Theme.S(30);
    }

    public void Set(IEnumerable<string> labels, bool vertical = false, bool compact = false)
    {
        _labels = [.. labels];
        _vertical = vertical;
        _compact = compact;
        _scrollOffset = 0;
        LayoutItems();
    }

    void LayoutItems()
    {
        _items.Clear();
        _hot = -1;
        if (!_vertical && !IsHandleCreated) { Invalidate(); return; }
        using var g = _vertical ? null : CreateGraphics();
        if (_vertical)
        {
            var row = Theme.S(34);
            var x = Theme.S(4);
            var w = Math.Max(Theme.S(30), Width - Theme.S(8));
            for (var i = 0; i < _labels.Length; i++)
                _items.Add((_labels[i], new Rectangle(x, Theme.S(2) + i * row - _scrollOffset,
                    w, row - Theme.S(4))));
        }
        else
        {
            var x = Theme.S(8);
            foreach (var text in _labels)
            {
                var w = Math.Min(Theme.S(190), (int)Math.Ceiling(g!.MeasureString(text, Theme.Ui).Width) + Theme.S(18));
                _items.Add((text, new Rectangle(x, Theme.S(3), w, Height - Theme.S(7))));
                x += w + Theme.S(3);
            }
        }
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutItems();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        LayoutItems();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_vertical)
        {
            var row = Theme.S(34);
            var max = Math.Max(0, _labels.Length * row - Height);
            _scrollOffset = Math.Clamp(_scrollOffset - Math.Sign(e.Delta) * row * 3, 0, max);
            LayoutItems();
        }
        base.OnMouseWheel(e);
    }

    int HitTest(Point p) => _items.FindIndex(i => i.R.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hot = HitTest(e.Location);
        if (hot != _hot) { _hot = hot; Invalidate(); }
        Cursor = hot >= 0 ? Cursors.Hand : Cursors.Default;
        _tips.SetToolTip(this, _compact && hot >= 0 ? _items[hot].Text : "");
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hot = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (HitTest(e.Location) is var i and >= 0) ItemClicked?.Invoke(i, e.Button);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        for (var i = 0; i < _items.Count; i++)
        {
            var (text, r) = _items[i];
            if (!r.IntersectsWith(ClientRectangle)) continue;
            if (Config.Current.LiquidGlassEnabled) Draw.Glass(g, r, i == _hot ? Theme.Fill2 : Theme.Fill1, Theme.Sf(7));
            else Draw.Fill(g, r, i == _hot ? Theme.Fill2 : Theme.Fill1, Theme.Sf(7));
            var shown = _compact ? (text.Length == 0 ? "•" : text[..1].ToUpperInvariant()) : text;
            var labelRect = _vertical && !_compact
                ? Rectangle.Inflate(r, -Theme.S(8), -Theme.S(1)) : r;
            Draw.Label(g, shown, Theme.Ui, i == _hot ? Theme.Text : Theme.Muted, labelRect,
                centre: !_vertical || _compact);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
