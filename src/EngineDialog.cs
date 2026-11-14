using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LlamaLauncher;

// What the engine chooser returns. Kind is Auto, CUDA, Vulkan or CPU for the built-in llama.cpp,
// or Basalt, PolyStrata or Custom for an engine that is installed separately.
public sealed record EngineChoice(string Kind, string Folder, string Name, string ModelsDirectory);

// First-run welcome (pick the engine and models folder) and Engines > Add engine share this dialog.
public sealed class EngineDialog : Form
{
    public const string BasaltUrl = "https://github.com/jesdga95/basalt";
    public const string BasaltConvertUrl = "https://github.com/jesdga95/basalt/blob/main/docs/model-file.md";
    public const string PolyStrataUrl = "https://github.com/VecSzn/PolyStrata";
    public const string LlamaCppUrl = "https://github.com/ggml-org/llama.cpp/releases";

    readonly Palette pal;
    readonly bool firstRun;
    readonly Settings settings;
    readonly List<OptionCard> options = new();
    readonly TextBox folder = new(), name = new(), modelsDir = new();
    readonly Label folderCaption = new(), folderStatus = new(), headline = new();
    readonly LinkLabel getLink = new();
    readonly TableLayoutPanel folderPanel = new(), namePanel = new();
    readonly ThemedButton ok = new(), cancel = new();
    string getUrl = "";
    public EngineChoice? Choice { get; private set; }

    public static bool HasBlackwell(Hardware hardware) =>
        hardware.Gpus.Any(g => Regex.IsMatch(g.Name, @"RTX\s*(PRO\s*\d+\s*)?50\d0|Blackwell", RegexOptions.IgnoreCase));

    public EngineDialog(Palette palette, Hardware hardware, Settings settings, bool firstRun, string? preselect = null)
    {
        pal = palette;
        this.firstRun = firstRun;
        this.settings = settings;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75f);
        Text = firstRun ? "Welcome to llama.cpp Launcher" : "Add an engine";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = firstRun;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = pal.Page;
        ForeColor = pal.Fore;
        ClientSize = new Size(660, 640);

        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = pal.Page };
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(outer);
        var host = new ThemedScrollHost { Dock = DockStyle.Fill, Margin = Padding.Empty, TrackBackColor = pal.Page, ThumbColor = pal.Border, ThumbHoverColor = pal.Muted };
        outer.Controls.Add(host, 0, 0);
        var stack = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(28, 22, 22, 8), BackColor = pal.Page };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.SetContent(stack);

        void Add(Control c)
        {
            int row = stack.RowCount++;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(c, 0, row);
            c.Dock = DockStyle.Top;
        }
        Label Line(string text, float size, FontStyle style, Color color, Padding margin) =>
            new() { Text = text, AutoSize = true, Font = new Font("Segoe UI", size, style), ForeColor = color, Margin = margin, UseMnemonic = false };

        Add(Line(firstRun ? "Welcome to llama.cpp Launcher" : "Add an engine", 17f, FontStyle.Bold, pal.Fore, new Padding(0, 0, 0, 2)));
        Add(Line(firstRun
            ? "Choose the engine that runs your models. You can add more engines or change this any time on the Engines page."
            : "Add another engine. Once it's set up, choose it from the Engine list on the Model page.", 9.75f, FontStyle.Regular, pal.Muted, new Padding(0, 0, 0, 10)));
        string gpus = hardware.Gpus.Count == 0 ? "no dedicated graphics card found" : string.Join(", ", hardware.Gpus.Select(g => $"{g.Name} ({g.TotalGiB:0} GB)"));
        Add(Line($"This PC: {gpus} · {hardware.RamGiB:0} GB RAM", 9f, FontStyle.Regular, pal.Muted, new Padding(0, 0, 0, 12)));
        Add(Line("Engine", 11f, FontStyle.Bold, pal.Fore, new Padding(0, 0, 0, 6)));

        bool nvidia = hardware.Gpus.Any(g => g.Vendor == "NVIDIA");
        bool anyGpu = hardware.Gpus.Count > 0;
        bool blackwell = HasBlackwell(hardware);
        var cards = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, BackColor = pal.Page };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Option(string kind, string title, string description, string badge = "", Tone tone = Tone.Accent)
        {
            var card = new OptionCard { Kind = kind, Text = title, Description = description, Badge = badge, BadgeTone = tone, Palette = pal, BackColor = pal.Page, ForeColor = pal.Fore, AccessibleName = title, AccessibleDescription = description };
            card.CheckedChanged += (_, _) => { if (card.Checked) Selected(card); };
            int row = cards.RowCount++;
            cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            cards.Controls.Add(card, 0, row);
            options.Add(card);
        }
        if (firstRun)
        {
            Option("CUDA", "llama.cpp · NVIDIA graphics (CUDA)", "The fastest choice for NVIDIA GeForce and RTX graphics cards.", nvidia ? "Recommended" : "");
            Option("Vulkan", "llama.cpp · Any graphics card (Vulkan)", "Works with AMD, Intel and NVIDIA graphics cards.", !nvidia && anyGpu ? "Recommended" : "");
            Option("CPU", "llama.cpp · Processor only (CPU)", "No graphics card needed. The slowest option, but it works on every PC.", !anyGpu ? "Recommended" : "");
        }
        Option("Basalt", "Basalt · NVIDIA RTX 50-series", "Only for NVIDIA Blackwell cards (RTX 5050 to 5090). Runs Qwen3.8 Flash-Next from a converted .basalt pack. Installed separately.",
            blackwell ? "RTX 50-series found" : "RTX 50-series only", blackwell ? Tone.Positive : Tone.Caution);
        Option("PolyStrata", "PolyStrata · large MoE models", "Experimental engine for big mixture-of-experts models such as Qwen3.8 Flash-Next. Installed separately.", "Experimental", Tone.Neutral);
        Option("Custom", "Custom engine", "Use your own llama.cpp build: any folder that contains llama-server.exe.");
        Add(cards);

        // Folder for an engine that is installed separately.
        folderPanel.ColumnCount = 3;
        folderPanel.AutoSize = true;
        folderPanel.Margin = new Padding(0, 4, 0, 0);
        folderPanel.BackColor = pal.Page;
        folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderCaption.AutoSize = true; folderCaption.Anchor = AnchorStyles.Left; folderCaption.Margin = new Padding(0, 0, 10, 0); folderCaption.ForeColor = pal.Fore;
        Style(folder);
        folder.Dock = DockStyle.Fill; folder.Margin = new Padding(0, 4, 8, 4);
        folder.TextChanged += (_, _) => Recheck();
        var browse = DialogButton("Browse…", ButtonKind.Secondary);
        browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = folderCaption.Text, UseDescriptionForTitle = true };
            if (Directory.Exists(folder.Text)) d.InitialDirectory = folder.Text;
            if (d.ShowDialog(this) == DialogResult.OK)
            {
                folder.Text = d.SelectedPath;
                if (name.Text.Length == 0) name.Text = Path.GetFileName(d.SelectedPath.TrimEnd('\\'));
            }
        };
        folderPanel.Controls.Add(folderCaption, 0, 0);
        folderPanel.Controls.Add(folder, 1, 0);
        folderPanel.Controls.Add(browse, 2, 0);
        folderStatus.AutoSize = true; folderStatus.Margin = new Padding(0, 2, 0, 2); folderStatus.UseMnemonic = false;
        folderPanel.Controls.Add(folderStatus, 1, 1);
        folderPanel.SetColumnSpan(folderStatus, 2);
        getLink.AutoSize = true; getLink.Margin = new Padding(0, 2, 0, 6);
        getLink.LinkColor = getLink.ActiveLinkColor = getLink.VisitedLinkColor = pal.Accent;
        getLink.LinkClicked += (_, _) => { if (getUrl.Length > 0) Process.Start(new ProcessStartInfo(getUrl) { UseShellExecute = true }); };
        folderPanel.Controls.Add(getLink, 1, 2);
        folderPanel.SetColumnSpan(getLink, 2);
        Add(folderPanel);

        namePanel.ColumnCount = 2;
        namePanel.AutoSize = true;
        namePanel.Margin = Padding.Empty;
        namePanel.BackColor = pal.Page;
        namePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        namePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var nameCaption = new Label { Text = "Name", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0), ForeColor = pal.Fore };
        Style(name);
        name.Dock = DockStyle.Fill; name.Margin = new Padding(0, 4, 0, 4); name.MaxLength = 48;
        name.TextChanged += (_, _) => Recheck();
        namePanel.Controls.Add(nameCaption, 0, 0);
        namePanel.Controls.Add(name, 1, 0);
        Add(namePanel);

        if (firstRun)
        {
            Add(Line("Models folder", 11f, FontStyle.Bold, pal.Fore, new Padding(0, 14, 0, 2)));
            Add(Line("Where your .gguf model files are. You can still add models from anywhere later.", 9f, FontStyle.Regular, pal.Muted, new Padding(0, 0, 0, 4)));
            var modelsRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = Padding.Empty, BackColor = pal.Page };
            modelsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            modelsRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Style(modelsDir);
            modelsDir.Text = settings.ModelsDirectory;
            modelsDir.Dock = DockStyle.Fill; modelsDir.Margin = new Padding(0, 4, 8, 4);
            var browseModels = DialogButton("Browse…", ButtonKind.Secondary);
            browseModels.Click += (_, _) =>
            {
                using var d = new FolderBrowserDialog { Description = "Choose your models folder", UseDescriptionForTitle = true };
                if (Directory.Exists(modelsDir.Text)) d.InitialDirectory = modelsDir.Text;
                if (d.ShowDialog(this) == DialogResult.OK) modelsDir.Text = d.SelectedPath;
            };
            modelsRow.Controls.Add(modelsDir, 0, 0);
            modelsRow.Controls.Add(browseModels, 1, 0);
            Add(modelsRow);
        }

        // Buttons stay visible at the bottom while the options scroll.
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Padding = new Padding(28, 12, 22, 16), Margin = Padding.Empty, BackColor = pal.Chrome };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headline.AutoSize = true; headline.Anchor = AnchorStyles.Left; headline.ForeColor = pal.Muted; headline.BackColor = pal.Chrome; headline.UseMnemonic = false;
        bar.Controls.Add(headline, 0, 0);
        cancel.Text = firstRun ? "Skip for now" : "Cancel";
        StyleButton(cancel, ButtonKind.Secondary, pal.Chrome);
        cancel.Click += (_, _) => { Choice = null; DialogResult = DialogResult.Cancel; Close(); };
        ok.Text = firstRun ? "Continue" : "Add engine";
        StyleButton(ok, ButtonKind.Primary, pal.Chrome);
        ok.Margin = Padding.Empty;
        ok.Click += (_, _) => Accept();
        bar.Controls.Add(cancel, 2, 0);
        bar.Controls.Add(ok, 3, 0);
        outer.Controls.Add(bar, 0, 1);
        AcceptButton = ok;
        CancelButton = cancel;

        string initial = preselect ?? (firstRun ? (nvidia ? "CUDA" : anyGpu ? "Vulkan" : "CPU") : "Custom");
        (options.FirstOrDefault(o => o.Kind == initial) ?? options[0]).Checked = true;
        ResumeLayout(true);
        Shown += (_, _) =>
        {
            NativeTheme.Apply(this, pal.Dark);
            int wanted = stack.GetPreferredSize(new Size(host.ClientSize.Width - 14, 0)).Height + bar.Height + (Height - ClientSize.Height);
            var work = Screen.FromControl(this).WorkingArea;
            Height = Math.Min(wanted, work.Height - 40);
            Top = Math.Max(work.Top + 10, Math.Min(Top, work.Bottom - Height - 10));
            (options.FirstOrDefault(o => o.Checked) ?? options[0]).Focus();
        };
    }

    OptionCard? Current => options.FirstOrDefault(o => o.Checked);

    void Selected(OptionCard card)
    {
        string kind = card.Kind;
        bool external = kind is "Basalt" or "PolyStrata" or "Custom";
        folderPanel.Visible = external;
        namePanel.Visible = kind == "Custom";
        folderCaption.Text = kind switch { "Basalt" => "Basalt folder", "PolyStrata" => "PolyStrata folder", _ => "Engine folder" };
        if (kind == "Basalt" && folder.Tag as string != "Basalt") folder.Text = settings.BasaltRoot;
        if (kind == "PolyStrata" && folder.Tag as string != "PolyStrata") folder.Text = settings.PolyStrataRoot;
        if (kind == "Custom" && folder.Tag as string != "Custom") folder.Text = "";
        folder.Tag = kind;
        (getUrl, getLink.Text) = kind switch
        {
            "Basalt" => (BasaltUrl, "Get Basalt on GitHub ↗"),
            "PolyStrata" => (PolyStrataUrl, "Get PolyStrata on GitHub ↗"),
            "Custom" => (LlamaCppUrl, "Get official llama.cpp builds on GitHub ↗"),
            _ => ("", "")
        };
        Recheck();
    }

    void Recheck()
    {
        var card = Current;
        if (card == null) return;
        string kind = card.Kind, path = folder.Text.Trim();
        string? problem = null, good = null;
        bool block = false;
        switch (kind)
        {
            case "Basalt":
                if (path.Length == 0) problem = "Choose the folder where Basalt is installed, or skip it and set it up later on the Engines page.";
                else if (!File.Exists(Basalt.Engine(path)) || !File.Exists(Basalt.Python(path))) problem = "This folder doesn't contain bin\\basalt.exe and its .venv Python environment.";
                else good = "Basalt found.";
                break;
            case "PolyStrata":
                if (path.Length == 0) problem = "Choose the folder where PolyStrata is installed, or set it up later on the Engines page.";
                else problem = PolyStrata.InstallProblem(path);
                good ??= problem == null ? "PolyStrata found." : null;
                break;
            case "Custom":
                if (!CustomEngine.HasServer(path)) { problem = path.Length == 0 ? "Choose a folder that contains llama-server.exe." : "No llama-server.exe in this folder (or in its cpu, cuda or vulkan subfolders)."; block = true; }
                else if (name.Text.Trim().Length == 0) { problem = "Give this engine a name."; block = true; }
                else if (settings.CustomEngines.Any(e => e.Name.Equals(name.Text.Trim(), StringComparison.OrdinalIgnoreCase))) { problem = "An engine with this name already exists."; block = true; }
                else good = "llama-server.exe found.";
                break;
        }
        folderStatus.Text = problem ?? good ?? "";
        folderStatus.ForeColor = problem != null ? pal.Caution : pal.Positive;
        ok.Enabled = !block;
        headline.Text = kind switch
        {
            "CUDA" or "Vulkan" or "CPU" => "Built in. Nothing else to install.",
            "Basalt" => "Basalt needs an NVIDIA RTX 50-series card.",
            "Custom" => "The build must be a llama.cpp llama-server.",
            _ => ""
        };
    }

    void Accept()
    {
        var card = Current;
        if (card == null) return;
        Recheck();
        if (!ok.Enabled) return;
        Choice = new EngineChoice(card.Kind, folder.Text.Trim(), name.Text.Trim(), modelsDir.Text.Trim());
        DialogResult = DialogResult.OK;
        Close();
    }

    void Style(TextBox box)
    {
        box.BackColor = pal.Input;
        box.ForeColor = pal.Fore;
        box.BorderStyle = BorderStyle.FixedSingle;
    }

    ThemedButton DialogButton(string text, ButtonKind kind)
    {
        var b = new ThemedButton { Text = text };
        StyleButton(b, kind, pal.Page);
        return b;
    }

    void StyleButton(ThemedButton b, ButtonKind kind, Color background)
    {
        b.Kind = kind;
        b.Palette = pal;
        b.BackColor = background;
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.MinimumSize = new Size(88, 32);
        b.Padding = new Padding(12, 4, 12, 4);
        b.Margin = new Padding(0, 2, 8, 2);
        b.DisabledBackColor = Palette.Blend(pal.Border, background, 0.5);
        b.DisabledForeColor = pal.Muted;
        b.DisabledBorderColor = pal.Border;
    }
}
