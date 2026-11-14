using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace LlamaLauncher;

public sealed class ThemedScrollHost : Control
{
    const int BarWidth = 14;
    Control? content;
    int offset, dragStartY, dragStartOffset, maxContentWidth;
    bool dragging, layingOut, pending;
    public Color TrackBackColor { get; set; } = Color.FromArgb(25, 25, 25);
    public Color ThumbColor { get; set; } = Color.FromArgb(105, 105, 105);
    public Color ThumbHoverColor { get; set; } = Color.FromArgb(130, 130, 130);
    // Content never grows wider than this (device pixels; 0 = fill), so wide windows keep readable proportions.
    public int MaxContentWidth { get => maxContentWidth; set { maxContentWidth = Math.Max(0, value); UpdateContentBounds(); } }

    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        maxContentWidth = (int)Math.Round(maxContentWidth * factor.Width);
        base.ScaleControl(factor, specified);
    }

    public void ScrollBy(int delta) => ScrollTo(offset + delta);

    // Wheel input that an unfocused field ignores scrolls the page that contains it.
    internal static bool ForwardWheel(Control source, int delta)
    {
        for (var parent = source.Parent; parent != null; parent = parent.Parent)
            if (parent is ThemedScrollHost host)
            {
                host.ScrollBy(-Math.Sign(delta) * 54);
                return true;
            }
        return false;
    }

    public ThemedScrollHost()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = "Scrollable settings page";
        ResizeRedraw = true;
    }

    public void SetContent(Control value)
    {
        Controls.Clear();
        content = value;
        value.Dock = DockStyle.None;
        // Sized only by UpdateContentBounds; anchoring to the right edge would lay it out twice per resize step.
        value.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        value.SizeChanged += (_, _) => UpdateContentBounds();
        Controls.Add(value);
        UpdateContentBounds();
    }

    int MaximumOffset => content == null ? 0 : Math.Max(0, content.Height - ClientSize.Height);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        UpdateContentBounds();
    }

    bool deferred;
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && deferred)
            UpdateContentBounds();
    }

    // While the user drags the window edge the page reflows at most every 80 ms (and once more on release),
    // so the frame keeps up with the mouse instead of waiting for every intermediate layout.
    public static bool LiveResize { get; set; }
    System.Windows.Forms.Timer? settle;
    public void Refit() => UpdateContentBounds(true);

    void UpdateContentBounds() => UpdateContentBounds(false);

    void UpdateContentBounds(bool force)
    {
        if (content == null || ClientSize.Width <= BarWidth)
            return;
        if (!Visible)
        {
            // Hidden pages are measured once, when they are shown, instead of on every resize step.
            deferred = true;
            return;
        }
        deferred = false;
        if (layingOut)
        {
            // A nested size change (wrapped text settling) arrived mid-update; measure again
            // afterwards instead of keeping a stale height.
            pending = true;
            return;
        }
        if (LiveResize && !force)
        {
            int target = maxContentWidth > 0 ? Math.Min(Math.Max(1, ClientSize.Width - BarWidth), maxContentWidth) : Math.Max(1, ClientSize.Width - BarWidth);
            if (target != content.Width)
            {
                if (settle == null)
                {
                    settle = new System.Windows.Forms.Timer { Interval = 80 };
                    settle.Tick += (_, _) => { settle.Stop(); UpdateContentBounds(true); };
                }
                if (!settle.Enabled)
                    settle.Start();
                Invalidate();
                return;
            }
        }
        settle?.Stop();
        layingOut = true;
        try
        {
            for (int pass = 0; pass < 3; pass++)
            {
                pending = false;
                int available = Math.Max(1, ClientSize.Width - BarWidth);
                int width = maxContentWidth > 0 ? Math.Min(available, maxContentWidth) : available;
                var preferred = content.GetPreferredSize(new Size(width, 0));
                content.Size = new Size(width, Math.Max(preferred.Height, content.MinimumSize.Height));
                offset = Math.Clamp(offset, 0, MaximumOffset);
                content.Location = new Point(0, -offset);
                if (!pending)
                    break;
            }
            Invalidate();
        }
        finally { layingOut = false; pending = false; }
    }

    Rectangle ThumbRectangle()
    {
        var track = new Rectangle(Width - BarWidth, 0, BarWidth, Height);
        if (content == null || MaximumOffset == 0 || track.Height <= 0)
            return Rectangle.Empty;
        int thumbHeight = Math.Clamp((int)Math.Round(track.Height * track.Height / (double)Math.Max(track.Height, content.Height)), 30, track.Height);
        int travel = Math.Max(1, track.Height - thumbHeight - 4);
        int top = 2 + (int)Math.Round(travel * offset / (double)Math.Max(1, MaximumOffset));
        return new Rectangle(track.Left + 4, top, 6, thumbHeight);
    }

    void ScrollTo(int value)
    {
        int next = Math.Clamp(value, 0, MaximumOffset);
        if (next == offset)
            return;
        offset = next;
        if (content != null)
            content.Top = -offset;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var track = new Rectangle(Math.Max(0, Width - BarWidth), 0, BarWidth, Height);
        using var trackBrush = new SolidBrush(TrackBackColor);
        e.Graphics.FillRectangle(trackBrush, track);
        var thumb = ThumbRectangle();
        if (!thumb.IsEmpty)
        {
            using var thumbBrush = new SolidBrush(ClientRectangle.Contains(PointToClient(Cursor.Position)) ? ThumbHoverColor : ThumbColor);
            e.Graphics.FillRectangle(thumbBrush, thumb);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollTo(offset - Math.Sign(e.Delta) * 54);
        base.OnMouseWheel(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.X < Width - BarWidth)
            return;
        Focus();
        var thumb = ThumbRectangle();
        if (thumb.Contains(e.Location))
        {
            dragging = true;
            dragStartY = e.Y;
            dragStartOffset = offset;
            Capture = true;
        }
        else
            ScrollTo(offset + (e.Y < thumb.Top ? -ClientSize.Height : ClientSize.Height));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging)
        {
            Invalidate(new Rectangle(Math.Max(0, Width - BarWidth), 0, BarWidth, Height));
            return;
        }
        var thumb = ThumbRectangle();
        int travel = Math.Max(1, Height - thumb.Height - 4);
        ScrollTo(dragStartOffset + (int)Math.Round((e.Y - dragStartY) * MaximumOffset / (double)travel));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        dragging = false;
        Capture = false;
        base.OnMouseUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Up) ScrollTo(offset - 36);
        else if (e.KeyCode == Keys.Down) ScrollTo(offset + 36);
        else if (e.KeyCode == Keys.PageUp) ScrollTo(offset - ClientSize.Height);
        else if (e.KeyCode == Keys.PageDown) ScrollTo(offset + ClientSize.Height);
        else if (e.KeyCode == Keys.Home) ScrollTo(0);
        else if (e.KeyCode == Keys.End) ScrollTo(MaximumOffset);
        else { base.OnKeyDown(e); return; }
        e.Handled = true;
    }
}

public sealed class ThemedComboBox : ComboBox
{
    const int WmPaint = 0x000F, WmPrint = 0x0317, WmPrintClient = 0x0318;
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int metric, uint dpi);
    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct ComboInfo
    {
        public int Size;
        public NativeRect Item, Button;
        public int ButtonState;
        public IntPtr Combo, Edit, List;
    }
    [DllImport("user32.dll")] static extern bool GetComboBoxInfo(IntPtr window, ref ComboInfo info);
    public Color ChromeBackColor { get; set; } = Color.FromArgb(48, 48, 48);
    public Color ChromeForeColor { get; set; } = Color.FromArgb(210, 210, 210);
    public Color ChromeBorderColor { get; set; } = Color.FromArgb(76, 76, 76);
    public Color ChromeHoverBackColor { get; set; } = Color.FromArgb(62, 62, 62);
    public Color ChromeHoverForeColor { get; set; } = Color.FromArgb(230, 230, 230);
    public Color ChromeHoverBorderColor { get; set; } = Color.FromArgb(104, 104, 104);
    // Optional friendly label for an item whose stored value is an identifier (engine IDs).
    public Func<string, string>? DisplayText { get; set; }
    bool mouseOver;
    internal Rectangle ChromeButtonBounds
    {
        get
        {
            int width = Math.Clamp(GetSystemMetricsForDpi(21, (uint)DeviceDpi) + 2, 3, Math.Max(3, ClientSize.Width));
            int left = Math.Max(0, ClientSize.Width - width);
            var native = new ComboInfo { Size = Marshal.SizeOf<ComboInfo>() };
            if (IsHandleCreated && GetComboBoxInfo(Handle, ref native) && native.Button.Right > native.Button.Left
                && native.Button.Left >= 1 && native.Button.Right <= ClientSize.Width)
                left = DropDownStyle == ComboBoxStyle.DropDown ? native.Button.Left - 1 : Math.Min(left, native.Button.Left - 1);
            return new Rectangle(left, 1, Math.Max(1, ClientSize.Width - left - 1), Math.Max(1, ClientSize.Height - 2));
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WmMouseWheel = 0x020A;
        if (m.Msg == WmMouseWheel && !Focused && !DroppedDown)
        {
            // Scrolling the page must not silently change a closed, unfocused dropdown.
            ThemedScrollHost.ForwardWheel(this, (short)((long)m.WParam >> 16));
            return;
        }
        base.WndProc(ref m);
        // Native hover/focus/button messages can repaint chrome directly, without
        // WM_PAINT. Apply our palette after the native control handles them too.
        if (IsHandleCreated && !IsDisposed && !Disposing &&
            m.Msg is WmPaint or 0x0200 or 0x02A3 or 0x0201 or 0x0202 or 0x0007 or 0x0008 or 0x0085 or 0x000A or 0x031A
                or 0x000C or 0x014E or 0x014F)
        {
            using var graphics = Graphics.FromHwnd(Handle);
            DrawChrome(graphics);
        }
        else if ((m.Msg == WmPrint || m.Msg == WmPrintClient) && m.WParam != IntPtr.Zero)
        {
            using var graphics = Graphics.FromHdc(m.WParam);
            DrawChrome(graphics);
        }
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    // Windows selects all text in an editable combo whenever it is resized; outside of editing that
    // just looks like a stray blue highlight (the GPU layers box), so clear it.
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ClearSelectionSoon();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ClearSelectionSoon();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        ClearSelectionSoon();
    }

    void ClearSelectionSoon()
    {
        if (DropDownStyle != ComboBoxStyle.DropDown || !IsHandleCreated || Focused)
            return;
        BeginInvoke(() =>
        {
            if (!IsDisposed && !Focused && SelectionLength > 0)
            {
                SelectionStart = Text.Length;
                SelectionLength = 0;
            }
        });
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        mouseOver = true;
        base.OnMouseEnter(e);
        RefreshChrome();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        mouseOver = false;
        base.OnMouseLeave(e);
        RefreshChrome();
    }

    void RefreshChrome()
    {
        if (!IsHandleCreated || IsDisposed || Disposing) return;
        using var graphics = Graphics.FromHwnd(Handle);
        DrawChrome(graphics);
    }

    void DrawChrome(Graphics graphics)
    {
        if (ClientSize.Width < 3 || ClientSize.Height < 3) return;
        // Match the metric used by WinForms' flat combo adapter. A fixed 22px
        // overlay leaves native button pixels exposed on monitors above 125%.
        // Use the native rectangle to keep the editable GPU field's child window
        // outside the themed arrow area; painting over that child causes flicker.
        var button = ChromeButtonBounds;
        bool highlighted = Enabled && (mouseOver || DroppedDown);
        using var background = new SolidBrush(highlighted ? ChromeHoverBackColor : ChromeBackColor);
        using var border = new Pen(highlighted ? ChromeHoverBorderColor : ChromeBorderColor);
        using var arrow = new Pen(highlighted ? ChromeHoverForeColor : ChromeForeColor, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.FillRectangle(background, button);
        if (DropDownStyle == ComboBoxStyle.DropDownList && IsHandleCreated)
        {
            var info = new ComboInfo { Size = Marshal.SizeOf<ComboInfo>() };
            if (GetComboBoxInfo(Handle, ref info) && info.Item.Right > info.Item.Left)
            {
                // Everything between our 1 px border and the item text: covers the light frame a disabled
                // native combo draws there.
                using var gap = new Region(new Rectangle(1, 1, Math.Max(0, button.Left - 1), Math.Max(0, ClientSize.Height - 2)));
                gap.Exclude(Rectangle.FromLTRB(info.Item.Left, info.Item.Top, info.Item.Right, info.Item.Bottom));
                using var fill = new SolidBrush(ChromeBackColor);
                graphics.FillRegion(fill, gap);
            }
        }
        graphics.DrawLine(border, button.Left, button.Top, button.Left, button.Bottom);
        graphics.DrawRectangle(border, 0, 0, Math.Max(0, ClientSize.Width - 1), Math.Max(0, ClientSize.Height - 1));
        int cx = button.Left + button.Width / 2, cy = button.Top + button.Height / 2;
        graphics.DrawLine(arrow, cx - 3, cy - 2, cx, cy + 1);
        graphics.DrawLine(arrow, cx, cy + 1, cx + 3, cy - 2);
    }
}

public sealed class ThemedNumericUpDown : NumericUpDown
{
    const int WmPaint = 0x000F, WmPrint = 0x0317, WmPrintClient = 0x0318;
    public Color ChromeBackColor { get; set; } = Color.FromArgb(48, 48, 48);
    public Color ChromeForeColor { get; set; } = Color.FromArgb(210, 210, 210);
    public Color ChromeBorderColor { get; set; } = Color.FromArgb(76, 76, 76);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // Only a focused number field reacts to the wheel; otherwise the page scrolls.
        if (!ContainsFocus)
        {
            if (e is HandledMouseEventArgs handled)
                handled.Handled = true;
            ThemedScrollHost.ForwardWheel(this, e.Delta);
            return;
        }
        base.OnMouseWheel(e);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WmPaint)
        {
            using var graphics = Graphics.FromHwnd(Handle);
            DrawChrome(graphics);
        }
        else if ((m.Msg == WmPrint || m.Msg == WmPrintClient) && m.WParam != IntPtr.Zero)
        {
            using var graphics = Graphics.FromHdc(m.WParam);
            DrawChrome(graphics);
        }
    }

    void DrawChrome(Graphics graphics)
    {
        int buttonWidth = Math.Min(22, Math.Max(18, SystemInformation.VerticalScrollBarWidth + 2));
        var buttons = new Rectangle(Math.Max(0, ClientSize.Width - buttonWidth), 1, buttonWidth - 1, Math.Max(0, ClientSize.Height - 2));
        using var background = new SolidBrush(ChromeBackColor);
        using var border = new Pen(ChromeBorderColor);
        using var arrow = new SolidBrush(ChromeForeColor);
        graphics.FillRectangle(background, buttons);
        int middle = buttons.Top + buttons.Height / 2;
        graphics.DrawLine(border, buttons.Left, buttons.Top, buttons.Left, buttons.Bottom);
        graphics.DrawLine(border, buttons.Left, middle, buttons.Right, middle);
        graphics.DrawRectangle(border, 0, 0, Math.Max(0, ClientSize.Width - 1), Math.Max(0, ClientSize.Height - 1));
        int cx = buttons.Left + buttons.Width / 2;
        graphics.FillPolygon(arrow, new[] { new Point(cx, middle - 5), new Point(cx - 3, middle - 2), new Point(cx + 3, middle - 2) });
        graphics.FillPolygon(arrow, new[] { new Point(cx, middle + 5), new Point(cx - 3, middle + 2), new Point(cx + 3, middle + 2) });
    }
}

public sealed class CompactTrackBar : Control
{
    int minimum;
    int maximum = 1;
    int value;
    bool dragging;
    public event EventHandler? ValueChanged;
    public event EventHandler? ValueCommitted;
    public int Minimum { get => minimum; set { minimum = value; Value = this.value; } }
    public int Maximum { get => maximum; set { maximum = Math.Max(minimum + 1, value); Value = this.value; } }
    public int Value
    {
        get => value;
        set
        {
            int next = Math.Clamp(value, Minimum, Maximum);
            if (this.value == next)
                return;
            this.value = next;
            AccessibleDescription = $"Value {next} of {Maximum}";
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public Color TrackColor { get; set; } = Color.FromArgb(100, 100, 100);
    public Color AccentColor { get; set; } = Color.FromArgb(0, 120, 212);

    public CompactTrackBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        Height = 24;
        TabStop = true;
        AccessibleRole = AccessibleRole.Slider;
        Margin = new Padding(0, 0, 8, 0);
        MaximumSize = new Size(440, 24);
        MinimumSize = new Size(110, 24);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int left = 7, right = Math.Max(left + 1, Width - 8), y = Height / 2 - 1;
        using var track = new Pen(TrackColor, 2);
        e.Graphics.DrawLine(track, left, y, right, y);
        int steps = Maximum - Minimum;
        if (steps <= 16)
            for (int i = 0; i <= steps; i++)
            {
                int x = left + (int)Math.Round((right - left) * i / (double)Math.Max(1, steps));
                e.Graphics.DrawLine(track, x, y + 4, x, y + 7);
            }
        int thumbX = left + (int)Math.Round((right - left) * (Value - Minimum) / (double)Math.Max(1, steps));
        using var thumb = new SolidBrush(AccentColor);
        e.Graphics.FillEllipse(thumb, thumbX - 5, y - 5, 11, 11);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle, ForeColor, BackColor);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        Focus();
        dragging = true;
        Capture = true;
        SetFromX(e.X);
        ValueCommitted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging)
            SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        dragging = false;
        Capture = false;
        base.OnMouseUp(e);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture) dragging = false;
        base.OnMouseCaptureChanged(e);
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is
        Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Left or Keys.Down) { Value--; e.Handled = true; }
        else if (e.KeyCode is Keys.Right or Keys.Up) { Value++; e.Handled = true; }
        else if (e.KeyCode == Keys.Home) { Value = Minimum; e.Handled = true; }
        else if (e.KeyCode == Keys.End) { Value = Maximum; e.Handled = true; }
        if (e.Handled) ValueCommitted?.Invoke(this, EventArgs.Empty);
        base.OnKeyDown(e);
    }

    void SetFromX(int x)
    {
        double ratio = Math.Clamp((x - 7d) / Math.Max(1, Width - 15d), 0, 1);
        Value = Minimum + (int)Math.Round(ratio * (Maximum - Minimum));
    }
}

public sealed class InfoButton : Button
{
    bool hover;
    public string LongHelp { get; }
    public Color HoverColor { get; set; } = Color.FromArgb(59, 130, 246);

    public InfoButton(string longHelp)
    {
        LongHelp = longHelp;
        Text = "?";
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        Size = new Size(19, 19);
        MinimumSize = Size;
        MaximumSize = Size;
        Padding = Padding.Empty;
        Margin = new Padding(3, 4, 2, 2);
        TabStop = true;
        AccessibleName = "More information";
        AccessibleDescription = longHelp;
        AccessibleRole = AccessibleRole.PushButton;
        Resize += (_, _) => MakeCircular();
        MakeCircular();
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        RectangleF circle = new(1, 1, Width - 2.5f, Height - 2.5f);
        Color color = hover ? HoverColor : ForeColor;
        using var border = new Pen(color, 1.2f);
        e.Graphics.DrawEllipse(border, circle);
        TextRenderer.DrawText(e.Graphics, "?", Font, ClientRectangle, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(Rectangle.Round(circle), -3, -3), ForeColor, BackColor);
    }

    void MakeCircular()
    {
        using var path = new GraphicsPath();
        path.AddEllipse(ClientRectangle);
        Region?.Dispose();
        Region = new Region(path);
    }
}

public enum ButtonKind { Secondary, Primary }

// Rounded flat button: Primary (accent fill) for the main action, Secondary (outlined) otherwise.
public sealed class ThemedButton : Button
{
    bool hover, pressed;
    public ButtonKind Kind { get; set; }
    public Palette Palette { get; set; } = Palette.Create(true, false);
    public Color DisabledBackColor { get; set; } = Color.FromArgb(52, 52, 52);
    public Color DisabledForeColor { get; set; } = Color.FromArgb(168, 168, 168);
    public Color DisabledBorderColor { get; set; } = Color.FromArgb(72, 72, 72);

    public ThemedButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseMnemonic = false;
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Palette;
        bool primary = Kind == ButtonKind.Primary;
        Color fill, line, text;
        if (!Enabled)
        {
            fill = DisabledBackColor; line = DisabledBorderColor; text = DisabledForeColor;
        }
        else if (primary)
        {
            fill = pressed ? Palette.Blend(p.Accent, Color.Black, 0.85) : hover ? p.AccentHover : p.Accent;
            line = fill; text = p.AccentFore;
        }
        else
        {
            fill = pressed ? p.Pressed : hover ? p.Hover : p.Input;
            line = hover ? Palette.Blend(p.Muted, p.Border, 0.45) : p.Border; text = p.Fore;
        }
        float radius = Math.Max(3, Font.Height * 0.36f);
        using (var path = Shapes.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), radius))
        {
            using var brush = new SolidBrush(fill);
            using var pen = new Pen(line);
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues)
        {
            using var focus = Shapes.Rounded(new RectangleF(2.5f, 2.5f, Width - 5.5f, Height - 5.5f), Math.Max(2, radius - 2));
            using var pen = new Pen(primary ? p.AccentFore : p.Accent, 1.4f);
            g.DrawPath(pen, focus);
        }
    }
}

public sealed class HelpPopover : Form, IMessageFilter
{
    readonly bool transient;
    bool filterInstalled;
    public HelpPopover(string text, Font ownerFont, Color background, Color foreground, Color border, Rectangle workArea, Point anchor, int autoCloseMilliseconds = 0)
    {
        transient = autoCloseMilliseconds > 0;
        Text = "Setting help";
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        BackColor = border;
        Padding = new Padding(1);
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = background, Padding = new Padding(12) };
        var label = new Label
        {
            AutoSize = true,
            Font = ownerFont,
            ForeColor = foreground,
            BackColor = background,
            Text = BreakLongTokens(text),
            UseMnemonic = false
        };
        int maxWidth = Math.Max(180, Math.Min(380, workArea.Width - 24));
        int maxHeight = Math.Max(120, Math.Min(420, workArea.Height - 24));
        label.MaximumSize = new Size(maxWidth - 26, 0);
        panel.Controls.Add(label);
        Controls.Add(panel);
        var measured = TextRenderer.MeasureText(label.Text, label.Font, label.MaximumSize, TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        ClientSize = new Size(Math.Clamp(measured.Width + 30, 220, maxWidth), Math.Min(maxHeight, measured.Height + 28));
        int x = anchor.X;
        int y = anchor.Y + 5;
        if (x + Width > workArea.Right)
            x = workArea.Right - Width;
        if (y + Height > workArea.Bottom)
            y = anchor.Y - Height - 10;
        x = Math.Max(workArea.Left, x);
        y = Math.Max(workArea.Top, y);
        Location = new Point(x, y);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        if (!transient)
            Deactivate += (_, _) => Close();
        if (transient)
        {
            var closeTimer = new System.Windows.Forms.Timer { Interval = autoCloseMilliseconds };
            closeTimer.Tick += (_, _) => { closeTimer.Stop(); closeTimer.Dispose(); if (!IsDisposed) Close(); };
            Shown += (_, _) => { Application.AddMessageFilter(this); filterInstalled = true; };
            FormClosed += (_, _) =>
            {
                closeTimer.Dispose();
                if (filterInstalled)
                {
                    Application.RemoveMessageFilter(this);
                    filterInstalled = false;
                }
            };
            closeTimer.Start();
        }
    }

    protected override bool ShowWithoutActivation => transient;

    public bool PreFilterMessage(ref Message m)
    {
        const int WmLeftButtonDown = 0x0201, WmRightButtonDown = 0x0204, WmMiddleButtonDown = 0x0207;
        if (transient && (m.Msg is WmLeftButtonDown or WmRightButtonDown or WmMiddleButtonDown) && !Bounds.Contains(Cursor.Position))
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(Close);
        }
        return false;
    }

    static string BreakLongTokens(string text)
    {
        var words = text.Split(' ');
        for (int i = 0; i < words.Length; i++)
            if (words[i].Length > 48)
                words[i] = string.Join("\u200B", Enumerable.Range(0, (words[i].Length + 47) / 48).Select(n => words[i].Substring(n * 48, Math.Min(48, words[i].Length - n * 48))));
        return string.Join(" ", words);
    }
}

public static class NativeTheme
{
    const int DwmUseImmersiveDarkMode = 20;
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr window, string? subAppName, string? subIdList);

    public static void Apply(Form form, bool dark)
    {
        if (!form.IsHandleCreated || SystemInformation.HighContrast)
            return;
        try
        {
            int enabled = dark ? 1 : 0;
            DwmSetWindowAttribute(form.Handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch { }
        ApplyChildren(form, dark);
    }

    public static void ApplyTo(Control control, bool dark)
    {
        if (!SystemInformation.HighContrast)
            ApplyChildren(control, dark);
    }

    static void ApplyChildren(Control control, bool dark)
    {
        if (control.IsHandleCreated)
        {
            try
            {
                string theme = dark && control is ComboBox ? "DarkMode_CFD" : dark ? "DarkMode_Explorer" : "Explorer";
                SetWindowTheme(control.Handle, theme, null);
            }
            catch { }
        }
        foreach (Control child in control.Controls)
            ApplyChildren(child, dark);
    }
}
