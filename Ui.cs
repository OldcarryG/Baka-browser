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
    /// BAKA is always opaque graphite: the BAKA Windows desktop has no see-through chrome either.
    public static Color ChromeBg => CustomMode ? Blend(CustomSurface, DarkMode ? Color.Black : Color.White, 0.12f)
        : BakaMode ? Hex(0x0E0E10)
        : GlassOn ? (DarkMode ? Color.Black : Color.White) : DarkMode
            ? Color.FromArgb(0x24, 0x24, 0x26) : Color.FromArgb(0xF7, 0xF7, 0xF9);

    /// The Mica backdrop and the gradient "glass" highlights. BAKA turns both off for flat, crisp
    /// surfaces that match the desktop; the other themes keep the setting.
    public static bool GlassStyle => Config.Current.LiquidGlassEnabled && !BakaMode && !CustomMode;

    // Dark and light system palettes. Custom keeps the dark surfaces and changes the accent.
    public static Color Content => CustomMode ? CustomSurface : BakaMode ? Hex(0x161619)
        : DarkMode ? Color.FromArgb(0x1C, 0x1C, 0x1E) : Color.FromArgb(0xFA, 0xFA, 0xFC);
    /// The tab row has to be opaque (see TabStrip), so it cannot be glass — but Content is twice as
    /// bright as Mica-over-black measures (~#0D0A0E here), which read as a pale band across the top.
    /// This is close enough to the backdrop that the seam disappears.
    public static Color Strip => CustomMode ? Blend(Content, DarkMode ? Color.Black : Color.White, 0.22f) : BakaMode ? Hex(0x09090B)
        : DarkMode ? Color.FromArgb(0x10, 0x10, 0x12) : Color.FromArgb(0xEF, 0xEF, 0xF3);
    public static Color Fill1 => CustomMode ? Blend(Content, DarkMode ? Color.White : Color.Black, 0.07f) : BakaMode ? Hex(0x1C1C20)
        : DarkMode ? Color.FromArgb(0x2C, 0x2C, 0x2E) : Color.White;
    public static Color Fill2 => CustomMode ? Blend(Content, DarkMode ? Color.White : Color.Black, 0.14f) : BakaMode ? Hex(0x26262B)
        : DarkMode ? Color.FromArgb(0x3A, 0x3A, 0x3C) : Color.FromArgb(0xF2, 0xF2, 0xF6);
    public static Color Fill3 => CustomMode ? Blend(Content, DarkMode ? Color.White : Color.Black, 0.22f) : BakaMode ? Hex(0x33333A)
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
    public static Color Line => CustomMode ? Blend(Content, DarkMode ? Color.White : Color.Black, 0.28f) : BakaMode ? Hex(0x2E2E35)
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

    // Compact chrome metrics, shared by the toolbar, tab rail and sidebars.
    public static readonly int BarHeight = S(40), ButtonSize = S(30), RailRow = S(36), RailRowHeight = S(32);

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

    // Menus, settings navigation and extensions. All exist in Segoe MDL2 Assets and Segoe Fluent Icons.
    /// A real shield (the older Shield constant is the padlock glyph, which read as "secure page").
    public const string ShieldIcon = "\uEA18";
    public const string Puzzle = "\uEA86", More = "\uE712", Download = "\uE896", HistoryIcon = "\uE81C",
        ChevronLeft = "\uE76B", ChevronRight = "\uE76C", Home = "\uE80F", Info = "\uE946", Folder = "\uE8B7",
        Delete = "\uE74D", Paint = "\uE790", Lightning = "\uE945", Code = "\uE943", BookmarkList = "\uE728",
        Tabs = "\uE7C4", Print = "\uE749", ZoomIn = "\uE8A3", ZoomOut = "\uE71F", Store = "\uE719",
        Check = "\uE73E", Sync = "\uE895", OpenNew = "\uE8A7", Copy = "\uE8C8", Picture = "\uE8B9",
        Mouse = "\uE962", Link = "\uE71B", Window = "\uE737", Volume = "\uE767", MuteIcon = "\uE74F", Undo = "\uE7A7";

    /// A glyph rendered once into a bitmap, for places that take an Image (menu items). Cached by
    /// glyph, size and colour; the cache is cleared when the theme changes.
    static readonly Dictionary<(string, int, int), Bitmap> _glyphImages = [];
    public static Image GlyphImage(string glyph, Color? color = null, float points = 10f)
    {
        var c = color ?? Text;
        var key = (glyph, (int)(points * 10), c.ToArgb());
        if (_glyphImages.TryGetValue(key, out var cached)) return cached;
        var n = S(16);
        var bmp = new Bitmap(n, n);
        using (var g = Graphics.FromImage(bmp))
        using (var font = new Font(IconFamily, points, GraphicsUnit.Point))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            Draw.Glyph(g, glyph, font, c, new RectangleF(0, 0, n, n));
        }
        _glyphImages[key] = bmp;
        return bmp;
    }

    public static void ResetGlyphCache()
    {
        foreach (var b in _glyphImages.Values) b.Dispose();
        _glyphImages.Clear();
    }

    /// Menus in the BAKA palette: graphite sheet, hairline border, rounded hover row.
    public static void Style(ToolStripDropDown menu)
    {
        menu.Renderer = new MenuRenderer();
        menu.BackColor = Fill1;
        menu.ForeColor = Text;
        menu.Font = Ui;
        menu.Padding = new Padding(S(4), S(6), S(4), S(6));
        if (menu is ToolStripDropDownMenu m) { m.ShowImageMargin = true; m.ShowCheckMargin = false; }
        foreach (ToolStripItem item in menu.Items) StyleItem(item);
    }

    public static void StyleItem(ToolStripItem item)
    {
        item.ForeColor = item.Enabled ? Text : Muted;
        if (item is ToolStripMenuItem mi)
        {
            mi.Padding = new Padding(S(2), S(5), S(2), S(5));
            if (mi.DropDown is { } sub && mi.HasDropDownItems) Style(sub);
        }
    }

    sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(Fill1);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var p = new Pen(Line);
            e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new RectangleF(S(2), 0, e.Item.Width - S(4), e.Item.Height);
            Draw.Fill(e.Graphics, r, Fill2, Sf(6));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Muted;
            if (e.Item is ToolStripMenuItem { ShortcutKeyDisplayString: not null } && e.Text == ((ToolStripMenuItem)e.Item).ShortcutKeyDisplayString)
                e.TextColor = Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var p = new Pen(Line);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(p, S(10), y, e.Item.Width - S(10), y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Muted;
            base.OnRenderArrow(e);
        }
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Fill2;
        public override Color ToolStripDropDownBackground => Fill1;
        public override Color ImageMarginGradientBegin => Fill1;
        public override Color ImageMarginGradientMiddle => Fill1;
        public override Color ImageMarginGradientEnd => Fill1;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
    }

    /// Flat dark button. WinForms' stock button ignores BackColor unless FlatStyle is Flat.
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
        if (!backdrop || !GlassStyle)
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
            if (Theme.GlassStyle) Draw.Glass(g, r, _down ? Theme.Fill3 : Theme.Fill2, Theme.Sf(8));
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
        Input.MouseEnter += (_, _) => Cursor = Cursors.IBeam;
        Click += (_, _) => Input.Focus();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Place(); }

    void Place()
    {
        var left = Theme.S(_leading.Length > 0 ? 34 : 16);
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
        // With no backdrop (BAKA, Custom, or glass off) nothing shows through the TextBox, so the field can
        // be a solid pill and the TextBox simply takes the same colour.
        var radius = Theme.GlassOn ? Theme.R : (Height - 1) / 2f;
        var solid = !Theme.GlassOn;
        var fill = _focused ? Theme.Fill2 : Theme.Fill1;
        if (solid)
        {
            if (Input.BackColor != fill) Input.BackColor = fill;
            using (var bg = new SolidBrush(Theme.ChromeBg)) g.FillRectangle(bg, ClientRectangle);
            Draw.Fill(g, field, fill, radius);
        }
        else if (Theme.GlassStyle)
            Draw.Glass(g, field, Theme.Fill1, radius);
        Draw.Stroke(g, field, _focused ? Theme.Accent : Theme.Line, radius, _focused ? Theme.Sf(1.4f) : 1f);
        if (_leading.Length > 0)
            Draw.Glyph(g, _leading, _mark, _leading == Theme.Insecure ? Theme.Bad : Theme.Muted,
                       new RectangleF(Theme.Sf(10), 0, Theme.Sf(18), Height));
    }

    /// Re-reads the theme after a settings change.
    public void ApplyTheme()
    {
        BackColor = Theme.ChromeBg;
        Input.BackColor = Theme.GlassOn ? Theme.ChromeBg : (_focused ? Theme.Fill2 : Theme.Fill1);
        Input.ForeColor = Theme.Text;
        Invalidate();
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
    /// The page is playing sound / the site is muted (drawn as a speaker in place of the icon).
    public bool Audio, Muted;
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
    /// The speaker on a tab that is playing sound (or muted) was clicked.
    public event Action<int>? AudioClicked;
    /// Right-click on a tab: index and the screen point to open the menu at.
    public event Action<int, Point>? TabMenuRequested;
    /// A tab dragged along the strip to a new position (from, to).
    public event Action<int, int>? TabMoved;
    /// A tab dragged off the strip and dropped: index and the screen point it was dropped at.
    public event Action<int, Point>? TabDraggedOut;

    // Dragging: starts after the pointer moves a few pixels with the left button down on a tab.
    int _dragIndex = -1;
    Point _dragStart;
    bool _dragging;
    static readonly int DragThreshold = Theme.S(6), TearOffDistance = Theme.S(48);

    /// True while the pointer is far enough from the strip that letting go opens a new window.
    bool TearingOff(Point p)
    {
        var r = ClientRectangle;
        r.Inflate(TearOffDistance, TearOffDistance);
        return !r.Contains(p);
    }

    int SlotAt(Point p)
    {
        if (Tabs.Count == 0) return -1;
        if (Vertical) return Math.Clamp((p.Y - RailTop + _scrollOffset) / Math.Max(1, Theme.RailRow), 0, Tabs.Count - 1);
        return Math.Clamp((p.X - Edge) / Math.Max(1, TabWidth + Gap), 0, Tabs.Count - 1);
    }

    /// Moves a tab inside the strip, keeping the selected tab selected (the form mirrors the move).
    public void MoveTab(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= Tabs.Count || to >= Tabs.Count) return;
        var selected = SelectedIndex >= 0 && SelectedIndex < Tabs.Count ? Tabs[SelectedIndex] : null;
        var t = Tabs[from];
        Tabs.RemoveAt(from);
        Tabs.Insert(to, t);
        if (selected is not null) SelectedIndex = Tabs.IndexOf(selected);
        Invalidate();
    }

    public TabStrip()
    {
        _iconFont = new Font(Theme.IconFamily, 7f);
        _plusFont = new Font(Theme.IconFamily, 10f);
        // Tab widths and the window buttons are both computed from ClientSize, so every pixel of a
        // resize moves them: the whole strip has to repaint, not just the band that was uncovered.
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Strip;   // opaque, see OnPaint
        Height = Theme.S(38);
    }

    public void Select(int index)
    {
        if (index < 0 || index >= Tabs.Count || index == SelectedIndex) { Invalidate(); return; }
        SelectedIndex = index;
        Invalidate();
        SelectionChanged?.Invoke(index);
    }

    /// Called after a tab was removed from Tabs, with the tab that should be selected now. Hover and drag
    /// state are reset: they pointed at positions that just shifted.
    public void Removed(int select)
    {
        (_hotTab, _hotClose, _dragIndex, _dragging) = (-1, -1, -1, false);
        if (Tabs.Count == 0) { SelectedIndex = -1; Invalidate(); return; }
        SelectedIndex = Math.Clamp(select, 0, Tabs.Count - 1);
        Invalidate();
        SelectionChanged?.Invoke(SelectedIndex);
    }

    int TabWidth => Tabs.Count == 0 ? 0
        : Math.Clamp((Width - PlusWidth - CapW * 3 - Edge * 2) / Tabs.Count - Gap, MinTabWidth, MaxTabWidth);

    public void RefreshLayout()
    {
        _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, Tabs.Count * Theme.RailRow - Height + Theme.RailRow));
        Invalidate();
    }

    static readonly int RailTop = Theme.S(40);

    Rectangle TabRect(int i) => Vertical
        ? new Rectangle(Theme.S(6), RailTop + i * Theme.RailRow - _scrollOffset,
            Math.Max(Theme.S(34), Width - Theme.S(12)), Theme.RailRowHeight)
        : new Rectangle(Edge + i * (TabWidth + Gap), Inset, TabWidth, Height - Inset * 2);

    /// Where the tab's site icon sits; a tab playing sound shows a speaker there instead, and clicking it mutes.
    Rectangle MarkRect(int i)
    {
        var r = TabRect(i);
        return Vertical && Compact
            ? new Rectangle(r.X + (r.Width - IconBox) / 2, r.Y + (r.Height - IconBox) / 2, IconBox, IconBox)
            : new Rectangle(r.X + Theme.S(10), r.Y + (r.Height - IconBox) / 2, IconBox, IconBox);
    }

    void DrawMark(Graphics g, StripTab tab, Rectangle mark)
    {
        if (tab.Muted || tab.Audio)
            Draw.Glyph(g, tab.Muted ? Theme.MuteIcon : Theme.Volume, _iconFont, tab.Muted ? Theme.Muted : Theme.Accent, mark);
        else if (tab.Icon is { } icon) g.DrawImage(icon, mark);
        else Draw.Glyph(g, Theme.Globe, _iconFont, Theme.Muted, mark);
    }

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
        if (Vertical) return new Rectangle(Theme.S(6), Theme.S(4), Math.Max(Theme.S(34), Width - Theme.S(12)), Theme.RailRowHeight);
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
        if (_dragIndex >= 0 && (MouseButtons & MouseButtons.Left) != 0)
        {
            DragTo(e.Location);
            if (_dragging) { base.OnMouseMove(e); return; }
        }
        else if (_dragIndex >= 0) EndDrag();
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
            var row = Theme.RailRow;
            var max = Math.Max(0, Tabs.Count * row - Math.Max(0, Height - RailTop));
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
            else if (e.Button == MouseButtons.Left && (Tabs[i].Audio || Tabs[i].Muted) && MarkRect(i).Contains(e.Location))
                AudioClicked?.Invoke(i);
            else if (e.Button == MouseButtons.Left)
            {
                Select(i);
                (_dragIndex, _dragStart, _dragging) = (i, e.Location, false);
            }
            else if (e.Button == MouseButtons.Right)
            {
                Select(i);
                TabMenuRequested?.Invoke(i, PointToScreen(e.Location));
            }
            return;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        var (index, dragging) = (_dragIndex, _dragging);
        EndDrag();
        if (dragging && index >= 0 && index < Tabs.Count && TearingOff(e.Location))
            TabDraggedOut?.Invoke(index, PointToScreen(e.Location));
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture && _dragging) EndDrag();
        base.OnMouseCaptureChanged(e);
    }

    void EndDrag()
    {
        var was = _dragging;
        (_dragIndex, _dragging) = (-1, false);
        if (was) { Capture = false; Cursor = Cursors.Default; Invalidate(); }
    }

    /// Called from OnMouseMove while the left button is down on a tab.
    void DragTo(Point p)
    {
        if (!_dragging)
        {
            if (Math.Abs(p.X - _dragStart.X) < DragThreshold && Math.Abs(p.Y - _dragStart.Y) < DragThreshold) return;
            _dragging = true;
            Capture = true;
        }
        var off = TearingOff(p);
        Cursor = off ? Cursors.SizeAll : Cursors.Default;
        _tips.SetToolTip(this, off ? "Release to open in a new window" : "");
        if (off) return;
        var slot = SlotAt(p);
        if (slot >= 0 && slot != _dragIndex)
        {
            var from = _dragIndex;
            MoveTab(from, slot);
            _dragIndex = slot;
            TabMoved?.Invoke(from, slot);
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
            var plusRect = PlusRect();
            if (_hotPlus) Draw.Fill(g, plusRect, Theme.Fill2, Theme.R - 1);
            if (Compact) Draw.Glyph(g, Theme.Add, _plusFont, Theme.Text, plusRect);
            else
            {
                var plusMark = new Rectangle(plusRect.X + Theme.S(8), plusRect.Y, IconBox + Theme.S(4), plusRect.Height);
                Draw.Glyph(g, Theme.Add, _iconFont, Theme.Text, plusMark);
                Draw.Label(g, "New tab", Theme.Ui, _hotPlus ? Theme.Text : Theme.Muted,
                    new RectangleF(plusMark.Right + Theme.S(6), plusRect.Y, plusRect.Width - plusMark.Width - Theme.S(16), plusRect.Height));
            }
            using (var sep = new Pen(Theme.Line))
                g.DrawLine(sep, Theme.S(10), RailTop - Theme.S(3), Width - Theme.S(10), RailTop - Theme.S(3));
            for (var i = 0; i < Tabs.Count; i++)
            {
                var r = TabRect(i);
                if (!r.IntersectsWith(ClientRectangle)) continue;
                var active = i == SelectedIndex;
                if (active || i == _hotTab)
                {
                    if (Theme.GlassStyle) Draw.Glass(g, r, active ? Theme.Fill2 : Theme.Fill1, Theme.R - 1);
                    else Draw.Fill(g, r, active ? Theme.Fill2 : Theme.Fill1, Theme.R - 1);
                }
                if (active)
                {
                    using var activeMark = new SolidBrush(Theme.Accent);
                    g.FillRectangle(activeMark, r.X, r.Y + Theme.S(8), Theme.S(2), r.Height - Theme.S(16));
                }
                var mark = MarkRect(i);
                DrawMark(g, Tabs[i], mark);
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
                if (Theme.GlassStyle) Draw.Glass(g, r, active ? Theme.Fill2 : Theme.Fill1, Theme.R - 1);
                else Draw.Fill(g, r, active ? Theme.Fill2 : Theme.Fill1, Theme.R - 1);
            }
            if (active)
            {
                using var activeMark = new SolidBrush(Theme.Accent);
                g.FillRectangle(activeMark, r.X + Theme.S(12), r.Bottom - Theme.S(3), r.Width - Theme.S(24), Theme.S(2));
            }

            var mark = MarkRect(i);
            DrawMark(g, Tabs[i], mark);

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

/// The bookmarks bar: rows (left rail) or pills (top bar), each with the site's icon. Owner-drawn
/// because a row of WinForms Buttons would be opaque square boxes with no room for an icon.
sealed class PillBar : Control
{
    readonly List<(string Text, Image? Icon, Rectangle R)> _items = [];
    readonly ToolTip _tips = new() { InitialDelay = 350, ReshowDelay = 100 };
    readonly Font _glyphFont = new(Theme.IconFamily, 8f);
    (string Text, Image? Icon)[] _entries = [];
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

    public void Set(IEnumerable<(string Text, Image? Icon)> entries, bool vertical = false, bool compact = false)
    {
        _entries = [.. entries];
        _vertical = vertical;
        _compact = compact;
        _scrollOffset = 0;
        LayoutItems();
    }

    static readonly int IconBox = Theme.S(16);

    void LayoutItems()
    {
        _items.Clear();
        _hot = -1;
        if (!_vertical && !IsHandleCreated) { Invalidate(); return; }
        using var g = _vertical ? null : CreateGraphics();
        if (_vertical)
        {
            var row = Theme.RailRow;
            var x = Theme.S(6);
            var w = Math.Max(Theme.S(30), Width - Theme.S(12));
            for (var i = 0; i < _entries.Length; i++)
                _items.Add((_entries[i].Text, _entries[i].Icon,
                    new Rectangle(x, Theme.S(2) + i * row - _scrollOffset, w, Theme.RailRowHeight)));
        }
        else
        {
            var x = Theme.S(8);
            foreach (var (text, icon) in _entries)
            {
                var w = Math.Min(Theme.S(200), (int)Math.Ceiling(g!.MeasureString(text, Theme.Ui).Width) + Theme.S(18) + IconBox + Theme.S(6));
                _items.Add((text, icon, new Rectangle(x, Theme.S(4), w, Height - Theme.S(8))));
                x += w + Theme.S(4);
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
            var row = Theme.RailRow;
            var max = Math.Max(0, _entries.Length * row - Height);
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
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        if (_items.Count == 0 && _vertical && !_compact)
        {
            Draw.Label(g, "Ctrl+D adds a bookmark", Theme.Ui, Theme.Muted,
                new RectangleF(Theme.S(12), Theme.S(6), Width - Theme.S(20), Theme.S(24)));
            return;
        }
        for (var i = 0; i < _items.Count; i++)
        {
            var (text, icon, r) = _items[i];
            if (!r.IntersectsWith(ClientRectangle)) continue;
            // Rows on the rail stay unfilled until hovered, like the tab list; the top bar keeps its pills.
            if (i == _hot || !_vertical)
            {
                if (Theme.GlassStyle) Draw.Glass(g, r, i == _hot ? Theme.Fill2 : Theme.Fill1, Theme.Sf(7));
                else Draw.Fill(g, r, i == _hot ? Theme.Fill2 : Theme.Fill1, Theme.Sf(7));
            }
            var mark = _compact
                ? new Rectangle(r.X + (r.Width - IconBox) / 2, r.Y + (r.Height - IconBox) / 2, IconBox, IconBox)
                : new Rectangle(r.X + Theme.S(10), r.Y + (r.Height - IconBox) / 2, IconBox, IconBox);
            if (icon is not null) g.DrawImage(icon, mark);
            else Draw.Glyph(g, Theme.Globe, _glyphFont, Theme.Muted, mark);
            if (_compact) continue;
            var textRect = new RectangleF(mark.Right + Theme.S(8), r.Y, Math.Max(4, r.Right - mark.Right - Theme.S(14)), r.Height);
            Draw.Label(g, text, Theme.Ui, i == _hot ? Theme.Text : Theme.Muted, textRect);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tips.Dispose(); _glyphFont.Dispose(); }
        base.Dispose(disposing);
    }
}

/// Rounded text button with an optional leading glyph. Replaces the stock flat Button in settings and
/// dialogs so every button has the same pill shape, hover, and keyboard focus ring.
sealed class PillButton : Control
{
    readonly Font _glyphFont = new(Theme.IconFamily, 9f);
    bool _hot, _down;
    public string Glyph { get; set; }
    /// Accent buttons are the one primary action in a group (Add to Baka, Install).
    public bool Accent { get; set; }

    public PillButton(string text, string glyph = "", bool accent = false)
    {
        Text = text;
        Glyph = glyph;
        Accent = accent;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
        Font = Theme.Ui;
        AccessibleName = text;
        AccessibleRole = AccessibleRole.PushButton;
        Height = Theme.S(30);
        Width = PreferredWidth;
        TabStop = true;
    }

    // Measured with GDI+ like the label is drawn (it runs wider than TextRenderer), plus the padding.
    public int PreferredWidth
    {
        get
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            return (int)Math.Ceiling(g.MeasureString(Text, Theme.Ui).Width) + Theme.S(Glyph.Length > 0 ? 50 : 30);
        }
    }

    public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }
    protected override void OnTextChanged(EventArgs e) { AccessibleName = Text; Invalidate(); base.OnTextChanged(e); }
    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys k) => k is Keys.Space or Keys.Enter || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { PerformClick(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new SolidBrush(Parent?.BackColor ?? Theme.Content)) g.FillRectangle(bg, ClientRectangle);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        var radius = (Height - 1) / 2f;
        Color fill, text;
        if (Accent) { fill = _down ? Theme.Fill3 : Theme.Accent; text = _down ? Theme.Text : (Theme.Contrast(Theme.Accent, Color.Black) > 7 ? Color.Black : Color.White); }
        else { fill = _down ? Theme.Fill3 : _hot ? Theme.Fill3 : Theme.Fill2; text = Theme.Text; }
        if (!Enabled) { fill = Theme.Fill1; text = Theme.Muted; }
        Draw.Fill(g, r, fill, radius);
        if (!Accent) Draw.Stroke(g, r, Theme.Line, radius);
        if (Focused && ShowFocusCues) Draw.Stroke(g, RectangleF.Inflate(r, -1.5f, -1.5f), Theme.Accent, radius, Theme.Sf(1.5f));
        var x = Theme.S(14);
        if (Glyph.Length > 0)
        {
            Draw.Glyph(g, Glyph, _glyphFont, text, new RectangleF(x - Theme.S(2), 0, Theme.S(18), Height));
            x += Theme.S(20);
        }
        Draw.Label(g, Text, Theme.Ui, text, new RectangleF(x, 0, Width - x - Theme.S(10), Height));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _glyphFont.Dispose();
        base.Dispose(disposing);
    }
}

/// On/off switch with its label on the left, the way Windows 11 and macOS settings show booleans.
/// A CheckBox underneath would draw a tick box; this is owner-drawn and keyboard-operable (Space).
sealed class ToggleSwitch : Control
{
    bool _checked, _hot;
    public string Description { get; set; } = "";
    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); AccessibilityNotifyClients(AccessibleEvents.StateChange, -1); }
    }

    public ToggleSwitch(string text, bool value)
    {
        Text = text;
        _checked = value;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Font = Theme.Ui;
        AccessibleName = text;
        AccessibleRole = AccessibleRole.CheckButton;
        TabStop = true;
        Height = Theme.S(34);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new SwitchAccessible(this);

    sealed class SwitchAccessible(ToggleSwitch owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleStates State => base.State | (owner.Checked ? AccessibleStates.Checked : 0);
        public override string DefaultAction => owner.Checked ? "Turn off" : "Turn on";
        public override void DoDefaultAction() => owner.Checked = !owner.Checked;
    }

    protected override void OnClick(EventArgs e) { Checked = !Checked; Focus(); base.OnClick(e); }
    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys k) => k == Keys.Space || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new SolidBrush(Parent?.BackColor ?? Theme.Content)) g.FillRectangle(bg, ClientRectangle);
        int w = Theme.S(40), h = Theme.S(20);
        var track = new RectangleF(Width - w - 1, (Height - h) / 2f, w, h);
        var on = _checked;
        Draw.Fill(g, track, on ? Theme.Accent : (_hot ? Theme.Fill3 : Theme.Fill2), h / 2f);
        if (!on) Draw.Stroke(g, track, Theme.Muted, h / 2f);
        var knob = Theme.Sf(14);
        var kx = on ? track.Right - knob - Theme.Sf(3) : track.X + Theme.Sf(3);
        var knobColor = on ? (Theme.Contrast(Theme.Accent, Color.Black) > 7 ? Color.Black : Color.White) : Theme.Text;
        Draw.Fill(g, new RectangleF(kx, track.Y + (h - knob) / 2f, knob, knob), knobColor, knob / 2f);
        if (Focused && ShowFocusCues) Draw.Stroke(g, RectangleF.Inflate(track, Theme.Sf(2.5f), Theme.Sf(2.5f)), Theme.Accent, h / 2f + 2, Theme.Sf(1.4f));
        var textWidth = Width - w - Theme.S(16);
        if (Text.Length == 0 || textWidth <= 0) return;
        if (Description.Length > 0)
        {
            Draw.Label(g, Text, Theme.Ui, Theme.Text, new RectangleF(0, Theme.S(2), textWidth, Height / 2f));
            Draw.Label(g, Description, Theme.Ui, Theme.Muted, new RectangleF(0, Height / 2f - Theme.S(1), textWidth, Height / 2f));
        }
        else Draw.Label(g, Text, Theme.Ui, Theme.Text, new RectangleF(0, 0, textWidth, Height));
    }
}

/// A rounded settings card: Fill1 sheet with a hairline border. Rows inside paint on Fill1.
sealed class CardPanel : Panel
{
    public CardPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Fill1;
        Padding = new Padding(Theme.S(16), Theme.S(10), Theme.S(16), Theme.S(10));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new SolidBrush(Parent?.BackColor ?? Theme.Content)) g.FillRectangle(bg, ClientRectangle);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Draw.Fill(g, r, Theme.Fill1, Theme.Sf(10));
        Draw.Stroke(g, r, Theme.Line, Theme.Sf(10));
    }
}
