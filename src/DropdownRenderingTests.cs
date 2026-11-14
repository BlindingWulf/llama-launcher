using System.Runtime.InteropServices;

namespace LlamaLauncher;

internal static class DropdownRenderingTests
{
    internal sealed class TestHost : Form
    {
        protected override bool ShowWithoutActivation => true;
    }
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int metric, uint dpi);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);

    static Bitmap LivePixels(Control control)
    {
        var bitmap = new Bitmap(control.ClientSize.Width, control.ClientSize.Height);
        using var graphics = Graphics.FromImage(bitmap);
        IntPtr destination = graphics.GetHdc(), source = GetDC(control.Handle);
        try
        {
            if (source == IntPtr.Zero || !BitBlt(destination, 0, 0, bitmap.Width, bitmap.Height, source, 0, 0, 0x00CC0020))
                throw new InvalidOperationException("Could not capture live dropdown pixels.");
        }
        finally { if (source != IntPtr.Zero) ReleaseDC(control.Handle, source); graphics.ReleaseHdc(destination); }
        return bitmap;
    }

    static void Hover(ComboBox combo, Point point, Action? afterMessage = null)
    {
        // Exercise native hover messages, not DrawToBitmap, which forces a fresh
        // WM_PRINT repaint and hides the stale native hover pixels being tested.
        // Invoke the control's own enter callback without moving the real pointer.
        SendMessage(combo.Handle, 0x0200, IntPtr.Zero, (IntPtr)((point.Y << 16) | (point.X & 0xffff)));
        Application.DoEvents();
        typeof(Control).GetMethod("OnMouseEnter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(combo, new object[] { EventArgs.Empty });
        afterMessage?.Invoke();
        combo.Refresh();
        Thread.Sleep(35);
        Application.DoEvents();
    }

    static void Leave(ComboBox combo)
    {
        SendMessage(combo.Handle, 0x02A3, IntPtr.Zero, IntPtr.Zero);
        typeof(Control).GetMethod("OnMouseLeave", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(combo, new object[] { EventArgs.Empty });
    }

    internal static void Verify(ThemedComboBox combo, string name, string directory)
    {
        Leave(combo);
        combo.Refresh();
        using var normal = LivePixels(combo);
        int width = normal.Width, height = normal.Height;
        int buttonLeft = combo.ChromeButtonBounds.Left;
        if (combo.DropDownStyle == ComboBoxStyle.DropDownList)
            buttonLeft = Math.Min(buttonLeft, width - GetSystemMetricsForDpi(21, (uint)combo.DeviceDpi) - 2);
        // WinForms sizes its native flat arrow by the control DPI. The theme
        // overlay must cover that entire region, including at 125-200% DPI.
        int backgroundX = buttonLeft + 3;
        if (normal.GetPixel(backgroundX, 4).ToArgb() != combo.ChromeBackColor.ToArgb())
        {
            Directory.CreateDirectory(directory);
            normal.Save(Path.Combine(directory, name + "-dpi-coverage.png"));
            throw new InvalidOperationException($"Dropdown DPI chrome gap: {name} / {combo.DeviceDpi} DPI.");
        }
        void AssertChrome(string state, Bitmap? expected = null)
        {
            using var actual = LivePixels(combo);
            expected ??= normal;
            // Compare the full arrow area and every outer border pixel. The text
            // and its selection may change while the surrounding chrome stays fixed.
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                if ((x >= buttonLeft || x == 0 || y == 0 || y == height - 1)
                    && actual.GetPixel(x, y).ToArgb() != expected.GetPixel(x, y).ToArgb())
                {
                    Directory.CreateDirectory(directory);
                    expected.Save(Path.Combine(directory, name + "-normal.png"));
                    actual.Save(Path.Combine(directory, name + "-" + state + ".png"));
                    throw new InvalidOperationException($"Dropdown hover regression: {name} / {state} at {x},{y}.");
                }
        }
        using var highlight = new Bitmap(normal);
        bool haveHighlight = false;
        void AssertHover(string state)
        {
            using var actual = LivePixels(combo);
            if (actual.GetPixel(backgroundX, 4).ToArgb() != combo.ChromeHoverBackColor.ToArgb()
                || actual.GetPixel(0, 0).ToArgb() != combo.ChromeHoverBorderColor.ToArgb())
                throw new InvalidOperationException($"Dropdown hover highlight disappeared: {name} / {state}.");
            if (!haveHighlight)
            {
                using var graphics = Graphics.FromImage(highlight);
                graphics.DrawImageUnscaled(actual, 0, 0);
                haveHighlight = true;
            }
            AssertChrome(state, highlight);
        }
        for (int pass = 0; pass < 2; pass++)
        {
            Hover(combo, new Point(5, height / 2), () => AssertHover("text-hover-message"));
            AssertHover("text-hover");
            Hover(combo, new Point(width - 6, height / 2), () => AssertHover("arrow-hover-message"));
            AssertHover("arrow-hover");
            Leave(combo);
            AssertChrome("leave");
        }
        combo.Focus();
        AssertChrome("focus");
        combo.DroppedDown = true;
        Application.DoEvents();
        if (!combo.DroppedDown) throw new InvalidOperationException("Dropdown did not open: " + name);
        combo.DroppedDown = false;
        AssertChrome("closed");
        int original = combo.SelectedIndex;
        for (int i = 0; i < combo.Items.Count; i++)
        {
            combo.SelectedIndex = i;
            combo.Refresh();
            Hover(combo, new Point(width - 6, height / 2));
            AssertHover("selected-" + i);
        }
        combo.SelectedIndex = original;
        if (combo.DropDownStyle == ComboBoxStyle.DropDown)
        {
            string originalText = combo.Text;
            combo.Text = "23";
            if (combo.Text != "23") throw new InvalidOperationException("Editable dropdown rejected a custom value: " + name);
            combo.Text = originalText;
        }
        combo.Enabled = false;
        combo.Refresh();
        using var disabled = LivePixels(combo);
        Hover(combo, new Point(width - 6, height / 2));
        AssertChrome("disabled-hover", disabled);
        combo.Enabled = true;
        Leave(combo);
    }
}
