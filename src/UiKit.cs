using System.Drawing.Drawing2D;
using System.Windows.Forms.Layout;

namespace LlamaLauncher;

public enum Tone { Neutral, Accent, Positive, Caution, Danger }

// One set of colors for every custom-painted control, rebuilt when the theme changes.
public sealed class Palette
{
    public bool Dark { get; init; }
    public bool HighContrast { get; init; }
    public Color Chrome { get; init; }      // header, sidebar and footer
    public Color Page { get; init; }        // content area behind the cards
    public Color Card { get; init; }
    public Color Input { get; init; }
    public Color Border { get; init; }
    public Color Hover { get; init; }
    public Color Pressed { get; init; }
    public Color Selected { get; init; }    // selected sidebar item
    public Color Fore { get; init; }
    public Color Muted { get; init; }
    public Color Accent { get; init; }
    public Color AccentHover { get; init; }
    public Color AccentFore { get; init; }
    public Color Positive { get; init; }
    public Color Caution { get; init; }
    public Color Danger { get; init; }

    public static Palette Create(bool dark, bool highContrast)
    {
        if (highContrast)
            return new Palette
            {
                Dark = dark, HighContrast = true,
                Chrome = SystemColors.Window, Page = SystemColors.Window, Card = SystemColors.Window, Input = SystemColors.Window,
                Border = SystemColors.WindowText, Hover = SystemColors.Highlight, Pressed = SystemColors.Highlight, Selected = SystemColors.Highlight,
                Fore = SystemColors.WindowText, Muted = SystemColors.GrayText, Accent = SystemColors.Highlight, AccentHover = SystemColors.Highlight,
                AccentFore = SystemColors.HighlightText, Positive = SystemColors.WindowText, Caution = SystemColors.WindowText, Danger = SystemColors.WindowText
            };
        return dark
            ? new Palette
            {
                Dark = true,
                Chrome = Color.FromArgb(22, 23, 27), Page = Color.FromArgb(27, 28, 33), Card = Color.FromArgb(35, 37, 43),
                Input = Color.FromArgb(43, 45, 52), Border = Color.FromArgb(56, 59, 68), Hover = Color.FromArgb(50, 53, 61),
                Pressed = Color.FromArgb(60, 64, 74), Selected = Color.FromArgb(40, 46, 60),
                Fore = Color.FromArgb(233, 235, 239), Muted = Color.FromArgb(158, 164, 176),
                Accent = Color.FromArgb(59, 130, 246), AccentHover = Color.FromArgb(88, 151, 250), AccentFore = Color.White,
                Positive = Color.FromArgb(80, 200, 128), Caution = Color.FromArgb(240, 184, 72), Danger = Color.FromArgb(242, 104, 104)
            }
            : new Palette
            {
                Dark = false,
                Chrome = Color.FromArgb(238, 240, 243), Page = Color.FromArgb(246, 247, 249), Card = Color.White,
                Input = Color.FromArgb(250, 251, 252), Border = Color.FromArgb(214, 218, 225), Hover = Color.FromArgb(229, 233, 239),
                Pressed = Color.FromArgb(218, 223, 231), Selected = Color.FromArgb(222, 233, 250),
                Fore = Color.FromArgb(24, 25, 28), Muted = Color.FromArgb(92, 98, 110),
                Accent = Color.FromArgb(0, 99, 204), AccentHover = Color.FromArgb(0, 86, 178), AccentFore = Color.White,
                Positive = Color.FromArgb(21, 128, 61), Caution = Color.FromArgb(161, 98, 7), Danger = Color.FromArgb(190, 30, 30)
            };
    }

    public Color ToneColor(Tone tone) => tone switch
    {
        Tone.Accent => Accent,
        Tone.Positive => Positive,
        Tone.Caution => Caution,
        Tone.Danger => Danger,
        _ => Muted
    };

    public static Color Blend(Color top, Color bottom, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(top.R * amount + bottom.R * (1 - amount)),
            (int)Math.Round(top.G * amount + bottom.G * (1 - amount)),
            (int)Math.Round(top.B * amount + bottom.B * (1 - amount)));
    }
}

internal static class Shapes
{
    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        radius = Math.Max(0, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
        if (radius < 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // Icon font shared by Windows 11 (Segoe Fluent Icons) and Windows 10 (Segoe MDL2 Assets).
    static string? iconFamily;
    public static string IconFamily => iconFamily ??=
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
}

// A wrapping row of buttons/labels. A table asks its children for their narrowest size while it measures
// itself, so a plain FlowLayoutPanel wraps there and the card reserves a second line it never uses. This one
// always measures at the width it will actually get (its MaximumSize tracks the parent's width).
public sealed class WrapFlow : FlowLayoutPanel
{
    public override Size GetPreferredSize(Size proposedSize)
    {
        if (WrapContents && MaximumSize.Width > 0 && proposedSize.Width < MaximumSize.Width)
            proposedSize.Width = MaximumSize.Width;
        return base.GetPreferredSize(proposedSize);
    }
}

public enum Fit { Auto, Fill, Fixed }

// Lightweight stack (vertical) or row (horizontal) layout for the settings pages.
// Nested auto-sized TableLayoutPanels re-measure every descendant several times per pass, which made a
// page switch take ~0.4 s and window resizing run at ~5 fps. A Box measures each child once per width,
// caches the result until something inside it changes, and skips layout entirely while hidden.
// Row items: Fixed (Size px), Auto (natural width) or Fill (shares the rest by weight, Size = weight).
// Stack items are full width unless added as Auto; FillHeight takes the stack's leftover height.
public class Box : Panel
{
    sealed class Item
    {
        public Control Control = null!;
        public Fit Fit;
        public int Size;
        public bool FillHeight;
        public bool Shown;
    }
    readonly List<Item> items = new();
    readonly Dictionary<Control, Item> byControl = new();
    Size lastReported = new(-1, -1);
    bool dirty = true, arrangePending;
    public bool Horizontal { get; set; }

    public Box()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        Margin = Padding.Empty;
    }

    public override LayoutEngine LayoutEngine => BoxLayout.Instance;

    // Fixed column widths are written for 96 DPI; scale them with the window (125–200 % displays and
    // monitor changes), as TableLayoutPanel does for its absolute columns.
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        if ((specified & BoundsSpecified.Width) != 0 && Math.Abs(factor.Width - 1f) > 0.001f)
            foreach (var item in items)
                if (item.Fit == Fit.Fixed)
                    item.Size = (int)Math.Round(item.Size * factor.Width);
        MarkDirty();
        base.ScaleControl(factor, specified);
    }

    public T Add<T>(T control, Fit fit, int size = 0, bool fillHeight = false) where T : Control
    {
        if (!byControl.TryGetValue(control, out var item))
        {
            item = new Item { Control = control };
            items.Add(item);
            byControl[control] = item;
            Watch(item);
        }
        item.Fit = fit;
        item.Size = size;
        item.FillHeight = fillHeight;
        control.Dock = DockStyle.None;
        if (control.Parent != this)
            Controls.Add(control);
        Invalidate(false);
        MarkDirty();
        return control;
    }

    public void Clear()
    {
        foreach (var item in items.ToArray())
            if (item.Control.Parent == this)
                item.Control.Dispose();
        items.Clear();
        byControl.Clear();
        Controls.Clear();
        MarkDirty();
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        // Controls added directly (Controls.Add) stack full width; a control moved away and back keeps its place.
        if (e.Control != null && !byControl.ContainsKey(e.Control))
        {
            var item = new Item { Control = e.Control, Fit = Horizontal ? Fit.Auto : Fit.Fill };
            items.Add(item);
            byControl[e.Control] = item;
            Watch(item);
            e.Control.Dock = DockStyle.None;
        }
        MarkDirty();
        base.OnControlAdded(e);
    }

    protected override void OnControlRemoved(ControlEventArgs e)
    {
        MarkDirty();
        base.OnControlRemoved(e);
    }

    // A child shown or hidden by code re-arranges this box. VisibleChanged also fires for every control when a
    // page is shown, so only a change of the child's own flag counts.
    void Watch(Item item)
    {
        item.Shown = OwnVisible(item.Control);
        item.Control.VisibleChanged += (_, _) =>
        {
            bool shown = OwnVisible(item.Control);
            if (shown == item.Shown)
                return;
            item.Shown = shown;
            if (item.Control.Parent == this)
            {
                MarkDirty();
                PerformLayout(item.Control, "Visible");
            }
        };
    }

    // Any change inside invalidates the cached measurements here and in every enclosing Box.
    void MarkDirty()
    {
        dirty = true;
        for (var parent = Parent; parent != null; parent = parent.Parent)
            if (parent is Box box)
                box.dirty = true;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        // Bounds changes are this box placing its children (or being placed); they don't change what any
        // child wants, so measurements stay cached. Text, visibility, fonts and the like invalidate them.
        if (e.AffectedProperty != "Bounds" && !(e.AffectedControl == this && e.AffectedProperty == "Visible"))
            MarkDirty();
        base.OnLayout(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && arrangePending)
            PerformLayout(this, "Visible");
    }

    // One measurement per width, shared by GetPreferredSize (for the parent) and Arrange (for the children).
    sealed class Plan
    {
        public int Width;
        public Item[] Shown = Array.Empty<Item>();
        public int[] Columns = Array.Empty<int>();
        public Size[] Sizes = Array.Empty<Size>();
        public int Extent;
        public Size Content;
    }
    Plan? plan;

    Plan Measure(int inner)
    {
        if (!dirty && plan != null && plan.Width == inner)
            return plan;
        var result = Horizontal ? MeasureRow(inner) : MeasureStack(inner);
        result.Width = inner;
        plan = result;
        dirty = false;
        return result;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = proposedSize.Width >= int.MaxValue / 2 ? 0 : proposedSize.Width;
        int inner = width > 0 ? Math.Max(0, width - Padding.Horizontal) : 0;
        var content = Measure(inner).Content;
        var result = new Size(content.Width + Padding.Horizontal, content.Height + Padding.Vertical);
        if (MinimumSize.Height > 0) result.Height = Math.Max(result.Height, MinimumSize.Height);
        return result;
    }

    // Returns true when the parent should lay out again because this box's preferred size changed.
    internal bool Arrange()
    {
        if (!Visible)
        {
            arrangePending = true;
            return false;
        }
        arrangePending = false;
        var area = DisplayRectangle;
        var p = Measure(Math.Max(0, area.Width));
        if (Horizontal) ApplyRow(p, area);
        else ApplyStack(p, area);
        var preferred = GetPreferredSize(new Size(Width, 0));
        if (preferred == lastReported)
            return false;
        bool first = lastReported.Width < 0;
        lastReported = preferred;
        return !first || preferred.Height != Height;
    }

    static bool Measured(Control c) => c.AutoSize || c is Box || c is FlowLayoutPanel || c is TableLayoutPanel;

    static Size Natural(Control c, int width)
    {
        Size s = Measured(c) ? c.GetPreferredSize(new Size(Math.Max(0, width), 0)) : c.Size;
        if (c.MinimumSize.Width > 0) s.Width = Math.Max(s.Width, c.MinimumSize.Width);
        if (c.MinimumSize.Height > 0) s.Height = Math.Max(s.Height, c.MinimumSize.Height);
        if (c.MaximumSize.Width > 0) s.Width = Math.Min(s.Width, c.MaximumSize.Width);
        if (c.MaximumSize.Height > 0) s.Height = Math.Min(s.Height, c.MaximumSize.Height);
        return s;
    }

    // The control's own Visible flag; Control.Visible is also false while any parent is hidden,
    // which would collapse a page that is measured before it is shown.
    static readonly Func<Control, bool> OwnVisible = CreateOwnVisible();
    static Func<Control, bool> CreateOwnVisible()
    {
        var method = typeof(Control).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "GetState" && m.ReturnType == typeof(bool) && m.GetParameters() is [{ ParameterType.IsEnum: true }]);
        if (method == null)
            return c => c.Visible;
        var control = System.Linq.Expressions.Expression.Parameter(typeof(Control));
        var flag = System.Linq.Expressions.Expression.Constant(Enum.ToObject(method.GetParameters()[0].ParameterType, 0x00000002));
        return System.Linq.Expressions.Expression.Lambda<Func<Control, bool>>(System.Linq.Expressions.Expression.Call(control, method, flag), control).Compile();
    }

    Item[] ShownItems()
    {
        var shown = new List<Item>(items.Count);
        foreach (var item in items)
            if (item.Control.Parent == this && OwnVisible(item.Control))
                shown.Add(item);
        return shown.ToArray();
    }

    Plan MeasureStack(int width)
    {
        var shown = ShownItems();
        var p = new Plan { Shown = shown, Columns = new int[shown.Length], Sizes = new Size[shown.Length] };
        int y = 0, widest = 0;
        for (int i = 0; i < shown.Length; i++)
        {
            var item = shown[i];
            var m = item.Control.Margin;
            int available = width > 0 ? Math.Max(0, width - m.Horizontal) : 0;
            var s = Natural(item.Control, item.Fit == Fit.Auto ? 0 : available);
            int h = item.Fit == Fit.Fixed ? item.Size : s.Height;
            int w = item.Fit == Fit.Auto || width <= 0 ? (available > 0 ? Math.Min(s.Width, available) : s.Width) : available;
            p.Sizes[i] = new Size(w, h);
            widest = Math.Max(widest, s.Width + m.Horizontal);
            y += h + m.Vertical;
        }
        p.Extent = y;
        p.Content = new Size(width > 0 ? width : widest, y);
        return p;
    }

    void ApplyStack(Plan p, Rectangle area)
    {
        int extra = Math.Max(0, area.Height - p.Extent), y = 0;
        bool filled = false;
        for (int i = 0; i < p.Shown.Length; i++)
        {
            var item = p.Shown[i];
            var m = item.Control.Margin;
            int h = p.Sizes[i].Height;
            if (item.FillHeight && !filled) { h += extra; filled = true; }
            item.Control.SetBounds(area.X + m.Left, area.Y + y + m.Top, p.Sizes[i].Width, h);
            y += h + m.Vertical;
        }
    }

    Plan MeasureRow(int width)
    {
        var shown = ShownItems();
        var p = new Plan { Shown = shown, Columns = new int[shown.Length], Sizes = new Size[shown.Length] };
        int used = 0, weights = 0, fillCount = 0;
        for (int i = 0; i < shown.Length; i++)
        {
            var item = shown[i];
            var m = item.Control.Margin;
            if (item.Fit == Fit.Fixed)
                p.Columns[i] = item.Size;
            else if (item.Fit == Fit.Auto || width <= 0)
            {
                p.Sizes[i] = Natural(item.Control, 0);
                p.Columns[i] = p.Sizes[i].Width + m.Horizontal;
            }
            else
            {
                weights += Math.Max(1, item.Size);
                fillCount++;
            }
            if (item.Fit != Fit.Fill || width <= 0)
                used += p.Columns[i];
        }
        if (width > 0 && weights > 0)
        {
            int remaining = Math.Max(0, width - used), given = 0, fills = 0;
            for (int i = 0; i < shown.Length; i++)
                if (shown[i].Fit == Fit.Fill)
                {
                    fills++;
                    p.Columns[i] = fills == fillCount ? remaining - given : remaining * Math.Max(1, shown[i].Size) / weights;
                    given += p.Columns[i];
                }
        }
        int rowHeight = 0, total = 0;
        for (int i = 0; i < shown.Length; i++)
        {
            var item = shown[i];
            var m = item.Control.Margin;
            if (item.Fit == Fit.Fixed || (item.Fit == Fit.Fill && width > 0))
            {
                int w = Math.Max(0, p.Columns[i] - m.Horizontal);
                p.Sizes[i] = new Size(w, Natural(item.Control, w).Height);
            }
            rowHeight = Math.Max(rowHeight, p.Sizes[i].Height + m.Vertical);
            total += p.Columns[i];
        }
        p.Extent = rowHeight;
        p.Content = new Size(width > 0 ? width : total, rowHeight);
        return p;
    }

    void ApplyRow(Plan p, Rectangle area)
    {
        int x = area.X;
        int rowHeight = Math.Max(p.Extent, area.Height);
        for (int i = 0; i < p.Shown.Length; i++)
        {
            var item = p.Shown[i];
            var m = item.Control.Margin;
            int h = item.FillHeight ? Math.Max(0, rowHeight - m.Vertical) : p.Sizes[i].Height;
            int top = item.FillHeight ? area.Y + m.Top : area.Y + m.Top + Math.Max(0, (p.Extent - m.Vertical - h) / 2);
            item.Control.SetBounds(x + m.Left, top, p.Sizes[i].Width, h);
            x += p.Columns[i];
        }
    }

    sealed class BoxLayout : LayoutEngine
    {
        public static readonly BoxLayout Instance = new();
        public override bool Layout(object container, LayoutEventArgs layoutEventArgs) => ((Box)container).Arrange();
    }
}

// Holds the app's pages; the sidebar picks one. A native TabControl redrew itself and every control on the
// visible page whenever the window width changed, which made dragging the window edge stutter. Here pages
// are plain panels: switching shows the new page with drawing paused, so it appears in one finished frame.
public sealed class PageHost : Panel
{
    const int WmSetRedraw = 0x000B, GwlExStyle = -20;
    const long WsExComposited = 0x02000000;
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    // While a page is being switched, Windows composes this panel and all its controls in one off-screen
    // buffer and presents it at once, so the new page never shows up half-painted over the old one.
    // It is switched off again right after, because composition would slow down window resizing.
    void Composited(bool on)
    {
        long style = (long)GetWindowLongPtr(Handle, GwlExStyle);
        long next = on ? style | WsExComposited : style & ~WsExComposited;
        if (next != style)
            SetWindowLongPtr(Handle, GwlExStyle, (IntPtr)next);
    }
    int selected = -1;
    public List<Panel> TabPages { get; } = new();
    public event EventHandler? SelectedIndexChanged;

    public PageHost()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    // Creates every page's window handles up front (after startup), so the first visit to a page is not
    // also creating and theming dozens of controls in the middle of a switch.
    public void Prepare()
    {
        static void Create(Control c)
        {
            _ = c.Handle;
            foreach (Control child in c.Controls)
                Create(child);
        }
        foreach (var page in TabPages)
            Create(page);
    }

    public void AddPage(Panel page)
    {
        page.Dock = DockStyle.Fill;
        page.Margin = Padding.Empty;
        page.Visible = TabPages.Count == 0;
        TabPages.Add(page);
        Controls.Add(page);
        if (selected < 0)
            selected = 0;
    }

    public int SelectedIndex
    {
        get => selected;
        set
        {
            if (value < 0 || value >= TabPages.Count || value == selected)
                return;
            var previous = selected >= 0 ? TabPages[selected] : null;
            var next = TabPages[value];
            selected = value;
            bool pause = IsHandleCreated && Visible;
            if (pause)
            {
                Composited(true);
                SendMessage(Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
            }
            try
            {
                next.Visible = true;
                next.BringToFront();
                if (previous != null && previous != next)
                    previous.Visible = false;
            }
            finally
            {
                if (pause)
                {
                    SendMessage(Handle, WmSetRedraw, (IntPtr)1, IntPtr.Zero);
                    Invalidate(true);
                    Update();
                    BeginInvoke(() => { if (IsHandleCreated) Composited(false); });
                }
            }
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Panel? SelectedTab
    {
        get => selected >= 0 && selected < TabPages.Count ? TabPages[selected] : null;
        set
        {
            int index = value == null ? -1 : TabPages.IndexOf(value);
            if (index >= 0)
                SelectedIndex = index;
        }
    }
}

// A rounded settings card: a vertical Box with a painted background.
public sealed class CardPanel : Box
{
    public Palette Palette { get; set; } = Palette.Create(true, false);

    public CardPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Padding = new Padding(18, 14, 18, 16);
        Margin = new Padding(0, 0, 0, 12);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Palette.Page);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float radius = Math.Max(4, Font.Height * 0.5f);
        using var path = Shapes.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), radius);
        using var fill = new SolidBrush(Palette.Card);
        using var border = new Pen(Palette.HighContrast ? Palette.Border : Palette.Border);
        g.FillPath(fill, path);
        g.DrawPath(border, path);
    }
}

// Sidebar page button.
public sealed class NavButton : Button
{
    bool hover, selected;
    public Palette Palette { get; set; } = Palette.Create(true, false);
    public string Glyph { get; set; } = "";
    public bool Selected
    {
        get => selected;
        set { selected = value; AccessibleDescription = value ? "Current page" : null; Invalidate(); }
    }

    public NavButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Height = 38;
        Dock = DockStyle.Top;
        Margin = new Padding(0, 1, 0, 1);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Palette.Chrome);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        if (selected || hover)
        {
            using var path = Shapes.Rounded(bounds, Height / 5f);
            using var fill = new SolidBrush(selected ? Palette.Selected : Palette.Hover);
            g.FillPath(fill, path);
        }
        if (selected)
        {
            using var bar = Shapes.Rounded(new RectangleF(1, Height * 0.27f, Math.Max(3, Height / 12f), Height * 0.46f), 1.5f);
            using var accent = new SolidBrush(Palette.Accent);
            g.FillPath(accent, bar);
        }
        Color text = selected ? Palette.Fore : Palette.Muted;
        if (hover && !selected) text = Palette.Fore;
        int iconLeft = (int)(Height * 0.32f), iconWidth = (int)(Height * 0.55f);
        using (var iconFont = new Font(Shapes.IconFamily, Font.SizeInPoints * 1.08f, FontStyle.Regular, GraphicsUnit.Point))
            TextRenderer.DrawText(g, Glyph, iconFont, new Rectangle(iconLeft, 0, iconWidth, Height), selected ? Palette.Accent : text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        int textLeft = iconLeft + iconWidth + (int)(Height * 0.25f);
        using var font = new Font(Font, selected ? FontStyle.Bold : FontStyle.Regular);
        TextRenderer.DrawText(g, Text, selected ? font : Font, new Rectangle(textLeft, 0, Math.Max(1, Width - textLeft - 6), Height), text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues)
        {
            using var focus = Shapes.Rounded(new RectangleF(2, 2, Width - 5, Height - 5), Height / 6f);
            using var pen = new Pen(Palette.Accent, 1.5f);
            g.DrawPath(pen, focus);
        }
    }
}

// Rounded status chip, optionally with a colored dot (header server status, card badges).
public sealed class StatusPill : Control
{
    Tone tone;
    public Palette Palette { get; set; } = Palette.Create(true, false);
    public bool ShowDot { get; set; } = true;
    public Tone Tone { get => tone; set { tone = value; Invalidate(); } }

    public StatusPill()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        AutoSize = true;
        Font = new Font("Segoe UI Semibold", 9f);
        Margin = new Padding(0, 2, 8, 2);
        AccessibleRole = AccessibleRole.StaticText;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        int pad = Font.Height * 2 / 3;
        int dot = ShowDot ? Font.Height / 2 + Font.Height / 3 : 0;
        return new Size(text.Width + pad * 2 + dot, Font.Height + Font.Height / 2);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AccessibleName = Text;
        if (AutoSize) Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (AutoSize) Size = GetPreferredSize(Size.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color color = Palette.ToneColor(tone);
        Color surface = Parent?.BackColor ?? Palette.Card;
        using var path = Shapes.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), (Height - 1) / 2f);
        using var fill = new SolidBrush(Palette.HighContrast ? surface : Palette.Blend(color, surface, Palette.Dark ? 0.16 : 0.11));
        using var border = new Pen(Palette.HighContrast ? Palette.Border : Palette.Blend(color, surface, Palette.Dark ? 0.38 : 0.30));
        g.FillPath(fill, path);
        g.DrawPath(border, path);
        int pad = Font.Height * 2 / 3;
        int x = pad;
        if (ShowDot)
        {
            int d = Font.Height / 2;
            using var dot = new SolidBrush(color);
            g.FillEllipse(dot, x, (Height - d) / 2f, d, d);
            x += d + Font.Height / 3;
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Math.Max(1, Width - x - pad + 2), Height),
            tone == Tone.Neutral ? Palette.Muted : Palette.Blend(color, Palette.Fore, 0.85),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }
}

// Horizontal used/available meter for the memory estimate.
public sealed class UsageBar : Control
{
    public Palette Palette { get; set; } = Palette.Create(true, false);
    public string Title { get; set; } = "";
    public string Value { get; set; } = "";
    public double Fraction { get; set; }
    public Tone Tone { get; set; } = Tone.Positive;

    public UsageBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Margin = new Padding(0, 4, 0, 4);
        AccessibleRole = AccessibleRole.ProgressBar;
        Height = 42;
    }

    public void Set(string title, string value, double fraction, Tone tone)
    {
        Title = title; Value = value; Fraction = fraction; Tone = tone;
        AccessibleName = title; AccessibleDescription = value;
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Height = Font.Height * 2 + Font.Height / 2 + 2;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        int line = Font.Height;
        using var bold = new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(g, Title, bold, new Rectangle(0, 0, Width / 2, line + 2), Palette.Fore,
            TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, Value, Font, new Rectangle(Width / 3, 0, Width - Width / 3, line + 2), Palette.Muted,
            TextFormatFlags.Right | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float barHeight = Math.Max(6, line * 0.45f);
        float top = Height - barHeight - 2;
        var track = new RectangleF(0.5f, top, Width - 1.5f, barHeight);
        using (var path = Shapes.Rounded(track, barHeight / 2))
        using (var brush = new SolidBrush(Palette.Input))
        using (var pen = new Pen(Palette.Border))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        double fraction = Math.Clamp(double.IsFinite(Fraction) ? Fraction : 0, 0, 1);
        if (fraction > 0)
        {
            var filled = new RectangleF(track.X, track.Y, Math.Max(barHeight, (float)(track.Width * fraction)), barHeight);
            using var path = Shapes.Rounded(filled, barHeight / 2);
            using var brush = new SolidBrush(Palette.ToneColor(Tone));
            g.FillPath(brush, path);
        }
    }
}

// On/off switch with the CheckBox API, so Checked and CheckedChanged keep working everywhere.
public sealed class ToggleSwitch : CheckBox
{
    bool hover;
    public Palette Palette { get; set; } = Palette.Create(true, false);

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = true;
        Margin = new Padding(0, 5, 10, 5);
        Cursor = Cursors.Hand;
        UseMnemonic = false;
    }

    int TrackWidth => (int)Math.Round(Font.Height * 2.05);
    int TrackHeight => (int)Math.Round(Font.Height * 1.12);
    int Gap => Text.Length > 0 ? Font.Height / 2 + 2 : 0;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = Text.Length == 0 ? Size.Empty : TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        return new Size(TrackWidth + Gap + text.Width + 3, Math.Max(TrackHeight, text.Height) + 4);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int tw = TrackWidth, th = TrackHeight;
        float y = (Height - th) / 2f;
        var track = new RectangleF(1, y, tw - 2, th);
        bool on = Checked;
        Color fill = !Enabled ? Palette.Blend(Palette.Border, BackColor, 0.6)
            : on ? (hover ? Palette.AccentHover : Palette.Accent)
            : (hover ? Palette.Hover : Palette.Input);
        using (var path = Shapes.Rounded(track, th / 2f))
        {
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            if (!on || !Enabled)
            {
                using var pen = new Pen(Enabled ? Palette.Muted : Palette.Border, 1.2f);
                g.DrawPath(pen, path);
            }
        }
        float knob = on ? th - 6 : th - 8;
        float knobX = on ? track.Right - knob - 3 : track.X + 4;
        using (var brush = new SolidBrush(!Enabled ? Palette.Muted : on ? Palette.AccentFore : Palette.Muted))
            g.FillEllipse(brush, knobX, y + (th - knob) / 2f, knob, knob);
        if (Text.Length > 0)
            TextRenderer.DrawText(g, Text, Font, new Rectangle(tw + Gap, 0, Math.Max(1, Width - tw - Gap), Height),
                Enabled ? ForeColor : Palette.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
        {
            using var focus = Shapes.Rounded(new RectangleF(0, y - 2, tw, th + 4), (th + 4) / 2f);
            using var pen = new Pen(Palette.Accent, 1.5f);
            g.DrawPath(pen, focus);
        }
    }
}

// Selectable card used by the engine chooser. Radio buttons in one container are mutually exclusive.
public sealed class OptionCard : RadioButton
{
    bool hover;
    public Palette Palette { get; set; } = Palette.Create(true, false);
    public string Description { get; set; } = "";
    public string Badge { get; set; } = "";
    public Tone BadgeTone { get; set; } = Tone.Accent;
    public string Kind { get; set; } = "";
    Font? small;

    public OptionCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
        Dock = DockStyle.Top;
        Margin = new Padding(0, 0, 0, 8);
        Cursor = Cursors.Hand;
        UseMnemonic = false;
        UpdateHeight();
    }

    Font Small => small ??= new Font(Font.FontFamily, Font.SizeInPoints * 0.93f);

    void UpdateHeight() => Height = Font.Height + Small.Height * 2 + Font.Height + 2;

    protected override void OnFontChanged(EventArgs e)
    {
        small?.Dispose();
        small = null;
        base.OnFontChanged(e);
        UpdateHeight();
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Palette.Page);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool on = Checked;
        var bounds = new RectangleF(1, 1, Width - 2.5f, Height - 2.5f);
        using (var path = Shapes.Rounded(bounds, Font.Height * 0.45f))
        {
            using var fill = new SolidBrush(on ? Palette.Blend(Palette.Accent, Palette.Card, Palette.Dark ? 0.13 : 0.07) : hover ? Palette.Hover : Palette.Card);
            using var pen = new Pen(on ? Palette.Accent : Palette.Border, on ? 1.6f : 1f);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
        int unit = Font.Height;
        float r = unit * 0.5f;
        float cx = unit * 1.05f, cy = Height / 2f;
        using (var pen = new Pen(on ? Palette.Accent : Palette.Muted, 1.5f))
            g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
        if (on)
            using (var dot = new SolidBrush(Palette.Accent))
                g.FillEllipse(dot, cx - r * 0.5f, cy - r * 0.5f, r, r);
        int left = (int)(unit * 2.1f);
        int right = Width - unit;
        int badgeWidth = 0;
        if (Badge.Length > 0)
        {
            var size = TextRenderer.MeasureText(Badge, Small, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            badgeWidth = size.Width + unit;
            var badge = new RectangleF(right - badgeWidth, unit * 0.55f, badgeWidth, Small.Height + 4);
            Color color = Palette.ToneColor(BadgeTone);
            using var path = Shapes.Rounded(badge, badge.Height / 2);
            using var fill = new SolidBrush(Palette.Blend(color, Palette.Card, Palette.Dark ? 0.18 : 0.12));
            g.FillPath(fill, path);
            TextRenderer.DrawText(g, Badge, Small, Rectangle.Round(badge), Palette.Blend(color, Palette.Fore, 0.85),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
        int top = (int)(unit * 0.5f);
        using var title = new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(g, Text, title, new Rectangle(left, top, Math.Max(1, right - left - badgeWidth - 6), unit + 2), Palette.Fore,
            TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, Description, Small, new Rectangle(left, top + unit + 3, Math.Max(1, right - left), Small.Height * 2 + 2), Palette.Muted,
            TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
        if (Focused && ShowFocusCues)
        {
            using var focus = Shapes.Rounded(new RectangleF(3, 3, Width - 7, Height - 7), Font.Height * 0.35f);
            using var pen = new Pen(Palette.Accent, 1f) { DashStyle = DashStyle.Dot };
            g.DrawPath(pen, focus);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) small?.Dispose();
        base.Dispose(disposing);
    }
}
