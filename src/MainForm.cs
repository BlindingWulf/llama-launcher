using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LlamaLauncher;
public sealed class MainForm : Form
{
    public const string TailscaleDownloadUrl = "https://tailscale.com/download/windows";
    public const string HuggingFaceGgufUrl = "https://huggingface.co/models?library=gguf&sort=trending";
    readonly ConfigStore store; readonly EngineManager engine; readonly RuntimeManager runtime;
    readonly bool testMode;
    protected override bool ShowWithoutActivation => testMode;
    Hardware hardware = new(); ModelInfo? info; Probe? probe; string backend = "Detecting…"; bool loading = true, busy, closing, unsaved, restartRequired;
    readonly ThemedComboBox models = new(), theme = new(), acceleration = new(), cacheK = new(), cacheV = new(), gpu = new(), backendChoice = new(), runtimeChoice = new(), effort = new(), ngram = new(), memorySafety = new(), customAction = new(), defaultMemorySafety = new(), turboPreset = new(), turboK = new(), turboV = new(), defaultEngine = new();
    readonly ThemedNumericUpDown llamaParallel = new(), polyParallel = new(), context = new(), threads = new(), batch = new(), ubatch = new(), draft = new(), draftPMin = new(), temperature = new(), cpuMoeLayers = new(), workingSetLimit = new(), port = new(), customVramReserve = new(), customRamReserve = new();
    readonly CheckBox ramKv = new ToggleSwitch { Text = "Keep the context cache in system RAM" };
    readonly CheckBox thinking = new ToggleSwitch { Text = "Thinking" }, flash = new ToggleSwitch { Text = "Flash Attention" }, cpuMoe = new ToggleSwitch { Text = "Use hybrid MoE placement" }, moeCache = new ToggleSwitch { Text = "Dynamic GPU expert cache" }, draftSampling = new ToggleSwitch { Text = "Probabilistic draft verification" }, turboEnabled = new ToggleSwitch { Text = "Use TurboQuant for this model" }, showTurbo = new ToggleSwitch { Text = "Show TurboQuant controls on the Model page" }, lan = new ToggleSwitch { Text = "Allow devices on my local network to connect" }, autoCheck = new ToggleSwitch { Text = "Check for engine updates when I open this page" };
    readonly TextBox custom = new(), projector = new(), template = new(), draftModel = new(), key = new() { UseSystemPasswordChar = true }, modelFolder = new();
    // Experimental PolyStrata engine switch (PolyStrata.cs); off by default.
    readonly CheckBox showPoly = new ToggleSwitch { Text = "Enable PolyStrata" };
    readonly TextBox polyRoot = new(), basaltRoot = new(), basaltPack = new();
    readonly Label polyVersion = new() { AutoSize = true, Text = "PolyStrata version: not checked", UseMnemonic = false, Margin = new Padding(0, 8, 10, 0) };
    readonly Label basaltVersion = new() { AutoSize = true, Text = "Basalt version: not checked", UseMnemonic = false, Margin = new Padding(0, 8, 10, 0) };
    Button getBasalt = null!, getPoly = null!, basaltDownload = null!;
    Basalt.Release? basaltRelease;
    // Ollama-compatible API on its own port (Ollama.cs); the footer switch and the Network page share one setting.
    readonly CheckBox ollama = new ToggleSwitch { Text = $"Ollama-compatible API on port {Ollama.Port}" }, ollamaQuick = new ToggleSwitch { Text = "Ollama API", Margin = new Padding(0, 6, 0, 0) }, ollamaTailnet = new ToggleSwitch { Text = "Share it on my Tailscale network" }, ollamaRequireKey = new ToggleSwitch { Text = "Require an API key (for apps that can send one)" }, ollamaDebug = new ToggleSwitch { Text = "Debug log (troubleshooting only; never logs chat text)" };
    readonly TextBox ollamaKey = new() { UseSystemPasswordChar = true, ReadOnly = true };
    readonly Label ollamaStatus = Label("Ollama API: off");
    readonly System.Windows.Forms.Timer ollamaTimer = new() { Interval = 5000 };
    bool ollamaSyncing, ollamaRefreshing; Ollama.Tailnet? tailnet; DateTime ollamaRestarted;
    readonly CompactTrackBar contextSlider = new(), cacheKSlider = new(), cacheVSlider = new();
    readonly Label hardwareText = Label("Detecting hardware…"), modelTitle = Label("Add a GGUF to begin", 11.5f), modelDetails = Secondary(""), memory = Secondary(""), memoryFit = Label(""), compatibility = Secondary(""), compatibilitySummary = Secondary(""), placement = Secondary("CPU placement: Unknown"), accelerationSummary = Secondary("Acceleration: Detecting…"), effectiveCache = Secondary("Effective cache: ordinary K/V settings"), runtimeSummary = Secondary("Runtime: Bundled"), network = Secondary(""), version = Label("Checking bundled engine…"), updateStatus = Secondary(""), notice = Secondary(""), contextHint = Secondary(""), engineHint = Secondary(""), changesLabel = Secondary(""), localStatus = Label(""), lanStatus = Label(""), tailscaleStatus = Label(""), basaltStatus = Label(""), polyStatus = Secondary(""), appVersion = Secondary("");
    readonly StatusPill statusPill = new() { Text = "Stopped" };
    readonly UsageBar vramBar = new(), ramBar = new();
    readonly TextBox logs = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
    readonly PageHost tabs = new() { Dock = DockStyle.Fill }; readonly ToolTip tips = new() { AutoPopDelay = 12000, InitialDelay = 350, ReshowDelay = 100, OwnerDraw = true };
    readonly List<Button> mutationButtons = new(); readonly List<InfoButton> helpButtons = new(); readonly System.Windows.Forms.Timer timer = new() { Interval = 1200 };
    readonly List<NavButton> nav = new();
    readonly Box customMemoryPanel = new(), turboPanel = new(), turboCustomPanel = new(), mtpPanel = new(), ngramPanel = new(), compatibilityPanel = new();
    readonly Box customEngineList = new();
    readonly int[] cacheStops = { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
    internal static readonly string[] SupportedCacheTypes = { "F32", "F16", "BF16", "Q8_0", "Q5_1", "Q5_0", "Q4_1", "Q4_0", "IQ4_NL" };
    readonly string[] cacheTypes = SupportedCacheTypes;
    List<int> contextStops = new();
    Button start = null!, stop = null!, restart = null!, save = null!, compatibilityToggle = null!, restorePreset = null!;
    Button installUpdateButton = null!;
    Button releaseNotesButton = null!;
    Panel modelPage = null!, enginesPage = null!, advancedPage = null!, networkPage = null!, logsPage = null!, settingsPage = null!;
    Control basaltPackRow = null!, emptyModels = null!, modelDetailsRow = null!, llamaSlotsRow = null!, polySlotsRow = null!, tailscaleInstallRow = null!, tailscaleShareRow = null!, qwenCard = null!, moeCard = null!;
    CardPanel turboCard = null!;
    bool tailscaleInstalled;
    EngineUpdateCheck? checkedEngineUpdate;
    bool suppressAutoUpdateCheck, updateCheckRunning;
    HelpPopover? helpPopover, policyPopover;
    Palette pal = Palette.Create(true, false);
    Color windowColor, surfaceColor, inputColor, borderColor, foregroundColor, secondaryColor, accentColor, positiveColor, cautionColor, dangerColor;
    Profile? Selected => models.SelectedItem as Profile;
    static string AppVersion
    {
        get
        {
            string v = Application.ProductVersion;
            int plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
    }
    public MainForm(ConfigStore store, EngineManager engine, bool testMode = false)
    {
        this.store = store;
        this.engine = engine;
        this.testMode = testMode;
        engine.CustomFolder = id => store.Value.CustomEngines.FirstOrDefault(e => CustomEngine.IdFor(e.Name) == id)?.Folder;
        runtime = new(store, engine);
        SuspendLayout();
        // Sizes below are written for 96 DPI; Windows scales them for the monitor the window opens on.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "llama.cpp Launcher";
        Font = new Font("Segoe UI", 9.75f);
        MinimumSize = new Size(1000, 640);
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        AllowDrop = true;

        // Fixed frame: header across the top, page list on the left, the page, and an action bar.
        // Nothing here rearranges itself with the window width; pages only reflow text.
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = Padding.Empty, Margin = Padding.Empty };
        root.ColumnStyles.Add(new(SizeType.Absolute, 188));
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize));
        Controls.Add(root);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1, Padding = new Padding(20, 12, 18, 10), Margin = Padding.Empty, Tag = "Chrome" };
        header.ColumnStyles.Add(new(SizeType.Percent, 100));
        header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.ColumnStyles.Add(new(SizeType.AutoSize));
        var titleStack = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Tag = "Chrome" };
        titleStack.ColumnStyles.Add(new(SizeType.Percent, 100));
        var title = new Label { Text = "llama.cpp Launcher", AutoSize = true, UseMnemonic = false, Font = new Font("Segoe UI Semibold", 14f), Margin = new Padding(0, 0, 0, 1) };
        hardwareText.AutoSize = false;
        hardwareText.AutoEllipsis = true;
        hardwareText.Height = 19;
        hardwareText.Font = new Font("Segoe UI", 9f);
        hardwareText.Margin = Padding.Empty;
        titleStack.Controls.Add(title, 0, 0);
        titleStack.Controls.Add(hardwareText, 0, 1);
        header.Controls.Add(titleStack, 0, 0);
        statusPill.Anchor = AnchorStyles.Right;
        statusPill.Margin = new Padding(12, 0, 10, 0);
        header.Controls.Add(statusPill, 1, 0);
        var updates = Button("Check for updates", OpenUpdates);
        updates.Anchor = AnchorStyles.Right;
        updates.Margin = Padding.Empty;
        header.Controls.Add(updates, 2, 0);
        root.Controls.Add(header, 0, 0);
        root.SetColumnSpan(header, 2);

        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10, 6, 10, 12), Margin = Padding.Empty, Tag = "Chrome" };
        sidebar.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.Controls.Add(sidebar, 0, 1);
        tabs.Margin = Padding.Empty;
        root.Controls.Add(tabs, 1, 1);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 2, Padding = new Padding(20, 10, 20, 8), Margin = Padding.Empty, Tag = "Chrome" };
        footer.ColumnStyles.Add(new(SizeType.AutoSize));
        footer.ColumnStyles.Add(new(SizeType.AutoSize));
        footer.ColumnStyles.Add(new(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new(SizeType.AutoSize));
        var actions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, Tag = "Chrome" };
        start = Button("Start", async () => await Start(false));
        ((ThemedButton)start).Kind = ButtonKind.Primary;
        start.MinimumSize = new Size(96, 34);
        restart = Button("Restart", async () => { await runtime.Stop(); _ = CleanVisionCache(afterStop: true); await Start(false); });
        stop = Button("Stop", async () => await Run(async () =>
        {
            if (!runtime.Active && runtime.CanStopExisting)
            {
                // A server for one of these models that this launcher did not start (a script or another tool may rely on it).
                if (MessageBox.Show(this, $"{runtime.LiveProfile!.Name} is running on port {store.Value.Port}, but this launcher did not start it. A script or another tool may be using it.\n\nStop it anyway?",
                    "Stop server", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return;
                await runtime.StopExisting();
                notice.Text = "";
            }
            else
                await runtime.Stop();
            _ = CleanVisionCache(afterStop: true);
            hardware = await Task.Run(Hardware.Detect); UpdateHardwareText(); RefreshEstimates(); UpdateStatus();
        }));
        tips.SetToolTip(stop, "Stop the server and unload the model from memory.");
        save = Button("Save", () => Save());
        foreach (var b in new[] { start, restart, stop, save })
            b.MinimumSize = new Size(Math.Max(b.MinimumSize.Width, 84), 34);
        actions.Controls.AddRange(new Control[] { start, restart, stop, save });
        footer.Controls.Add(actions, 0, 0);
        changesLabel.Dock = DockStyle.None;
        changesLabel.Anchor = AnchorStyles.Left;
        changesLabel.Margin = new Padding(6, 0, 0, 0);
        footer.Controls.Add(changesLabel, 1, 0);
        tips.SetToolTip(ollamaQuick, $"Ollama's API on port {Ollama.Port} for apps that only accept Ollama. It answers with the loaded model through this server, whose own port and API stay as they are. Details on the Network page.");
        ollamaQuick.Anchor = AnchorStyles.Right;
        footer.Controls.Add(ollamaQuick, 3, 0);
        // The message line has a fixed height so a new message never moves the page above it.
        notice.AutoSize = false;
        notice.AutoEllipsis = true;
        notice.Height = 36;
        notice.Dock = DockStyle.Fill;
        notice.Margin = new Padding(0, 6, 0, 0);
        footer.Controls.Add(notice, 0, 1);
        footer.SetColumnSpan(notice, 4);
        root.Controls.Add(footer, 0, 2);
        root.SetColumnSpan(footer, 2);

        BuildModel();
        BuildEngines();
        BuildAdvanced();
        BuildNetwork();
        BuildDiagnostics();
        BuildSettings();
        BuildNavigation(sidebar);
        // Status lines that are often empty take no space until they have something to say.
        foreach (var line in new[] { updateStatus, engineHint })
        {
            line.Visible = line.Text.Length > 0;
            line.TextChanged += (_, _) => line.Visible = line.Text.Length > 0;
        }
        tips.Popup += SizeToolTip;
        tips.Draw += DrawToolTip;
        theme.SelectedItem = store.Value.Theme;
        theme.SelectedIndexChanged += (_, _) => { store.Value.Theme = theme.Text; ApplyTheme(); if (!loading) store.Save(); };
        models.SelectedIndexChanged += async (_, _) => { if (loading) return; await SelectModel(); };
        foreach (var c in new Control[] { llamaParallel, polyParallel, threads, batch, ubatch, draft, draftPMin, temperature, cpuMoeLayers, workingSetLimit, port, customVramReserve, customRamReserve })
            ((NumericUpDown)c).ValueChanged += (_, _) => Changed();
        context.ValueChanged += (_, _) => { if (!loading) { SyncContextSlider(); Changed(); } };
        foreach (var c in new[] { gpu, acceleration, effort, ngram, customAction, turboPreset, turboK, turboV })
            c.SelectedIndexChanged += (_, _) => Changed();
        cacheK.SelectedIndexChanged += (_, _) => CacheChanged(cacheK, cacheKSlider);
        cacheV.SelectedIndexChanged += (_, _) => CacheChanged(cacheV, cacheVSlider);
        cacheK.SelectionChangeCommitted += (_, _) => CacheChanged(cacheK, cacheKSlider);
        cacheV.SelectionChangeCommitted += (_, _) => CacheChanged(cacheV, cacheVSlider);
        memorySafety.SelectedIndexChanged += MemorySafetyChanged;
        memorySafety.SelectionChangeCommitted += (_, _) => ShowPolicyNotice(memorySafety, memorySafety.Text, false);
        defaultMemorySafety.SelectedIndexChanged += DefaultMemorySafetyChanged;
        defaultMemorySafety.SelectionChangeCommitted += (_, _) => ShowPolicyNotice(defaultMemorySafety, defaultMemorySafety.Text, true);
        defaultEngine.SelectedIndexChanged += (_, _) =>
        {
            if (loading || defaultEngine.SelectedItem is not string id) return;
            store.Value.DefaultRuntime = id;
            store.Save();
            notice.Text = $"Models you add from now on will use {EngineName(id)}. Existing models keep their engine.";
        };
        gpu.TextChanged += (_, _) => Changed();
        thinking.CheckedChanged += (_, _) => Changed();
        flash.CheckedChanged += (_, _) => Changed();
        ramKv.CheckedChanged += (_, _) => Changed();
        cpuMoe.CheckedChanged += (_, _) => Changed();
        moeCache.CheckedChanged += (_, _) => Changed();
        draftSampling.CheckedChanged += (_, _) => Changed();
        turboEnabled.CheckedChanged += (_, _) => Changed();
        showTurbo.CheckedChanged += ShowTurboChanged;
        showPoly.CheckedChanged += ShowPolyChanged;
        polyRoot.Leave += (_, _) => { if (!loading && store.Value.PolyStrataRoot != polyRoot.Text.Trim()) { store.Value.PolyStrataRoot = polyRoot.Text.Trim(); store.Save(); RefreshEngineStatus(); } };
        basaltRoot.Leave += (_, _) => { if (!loading && store.Value.BasaltRoot != basaltRoot.Text.Trim()) { store.Value.BasaltRoot = basaltRoot.Text.Trim(); store.Save(); RefreshEngineStatus(); } };
        basaltPack.Leave += (_, _) => { if (!loading && Selected != null && Selected.BasaltPack != basaltPack.Text.Trim()) { Selected.BasaltPack = basaltPack.Text.Trim(); store.Save(); RefreshEstimates(); } };
        lan.CheckedChanged += (_, _) => { Changed(); UpdateStatus(); };
        port.ValueChanged += (_, _) => UpdateStatus();
        foreach (var box in new[] { custom, projector, template, draftModel, modelFolder })
            box.TextChanged += (_, _) => Changed();
        contextSlider.ValueChanged += (_, _) => { if (!loading && contextStops.Count > 0) context.Value = Math.Clamp(contextStops[contextSlider.Value], (int)context.Minimum, (int)context.Maximum); };
        cacheKSlider.ValueChanged += (_, _) => { if (!loading) cacheK.SelectedItem = cacheTypes[cacheKSlider.Value]; };
        cacheVSlider.ValueChanged += (_, _) => { if (!loading) cacheV.SelectedItem = cacheTypes[cacheVSlider.Value]; };
        cacheKSlider.ValueCommitted += (_, _) => CacheChanged(cacheK, cacheKSlider);
        cacheVSlider.ValueCommitted += (_, _) => CacheChanged(cacheV, cacheVSlider);
        runtime.Changed += () => { if (IsHandleCreated && !IsDisposed) BeginInvoke(() => { UpdateStatus(); if (Selected != null && info != null) RefreshEstimates(); }); };
        timer.Tick += (_, _) => { UpdateStatus(); if (tabs.SelectedTab == logsPage) RefreshLogs(); };
        timer.Start();
        tabs.SelectedIndexChanged += async (_, _) =>
        {
            foreach (var item in nav)
                item.Selected = item.Tag == tabs.SelectedTab;
            if (tabs.SelectedTab == logsPage)
                RefreshLogs();
            if (tabs.SelectedTab == networkPage)
                RefreshTailscale();
            if (tabs.SelectedTab == enginesPage)
            {
                RefreshEngineStatus();
                if (store.Value.CheckUpdatesOnOpen && !suppressAutoUpdateCheck)
                    await CheckForUpdates();
            }
        };
        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += async (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) await Run(async () => { tabs.SelectedTab = modelPage; foreach (var path in paths.Where(x => x.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))) await AddModel(path); }); };
        if (!testMode)
            Shown += async (_, _) =>
            {
                tabs.Prepare();
                NativeTheme.Apply(this, pal.Dark);
                await Initialize();
            };
        FormClosing += OnClosing;
        ResizeBegin += (_, _) => ThemedScrollHost.LiveResize = true;
        ResizeEnd += (_, _) => EndLiveResize();
        HandleCreated += (_, _) => ApplyTheme();
        ApplyTheme();
        ResumeLayout(false);
        PerformLayout();
        // Placement is restored after scaling so a saved size is not scaled a second time.
        RestoreWindowPlacement();
    }
    static Label Label(string text, float size = 9.75f) => new() { Text = text, UseMnemonic = false, AutoSize = true, Font = new Font("Segoe UI", size, size > 11 ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0, 3, 0, 4), Dock = DockStyle.Top };
    static Label Secondary(string text) => new() { Text = text, UseMnemonic = false, AutoSize = true, Font = new Font("Segoe UI", 9f), Margin = new Padding(0, 3, 0, 4), Dock = DockStyle.Top };
    static Label Section(string text) => new() { Text = text, UseMnemonic = false, AutoSize = true, Font = new Font("Segoe UI Semibold", 11.25f), Margin = new Padding(0, 9, 0, 3), Dock = DockStyle.Top };
    static Label CardTitle(string text) => new() { Text = text, UseMnemonic = false, AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), Margin = new Padding(0, 0, 4, 0), Anchor = AnchorStyles.Left, Tag = "SingleLineFieldLabel" };
    static FlowLayoutPanel Flow(params Control[] controls)
    {
        var p = new WrapFlow { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0, 2, 0, 2) };
        p.Controls.AddRange(controls);
        return p;
    }
    Button Button(string text, Action action)
    {
        var b = new ThemedButton { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 32), Padding = new Padding(12, 4, 12, 4), Margin = new Padding(0, 3, 8, 3) };
        b.Click += (_, _) => { try { action(); } catch (Exception ex) { ShowError(ex); } };
        return b;
    }
    Button Button(string text, Func<Task> action)
    {
        return Button(text, () => { _ = Run(action); });
    }
    Button LinkButton(string text, string url) => Button(text, () => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
    StatusPill Badge(string text, Tone tone) => new() { Text = text, Tone = tone, ShowDot = false, Anchor = AnchorStyles.Left, Margin = new Padding(6, 0, 2, 0), Font = new Font("Segoe UI Semibold", 8.25f) };
    ComboBox Combo(ComboBox c, IEnumerable<string> items, bool editable = false, int width = 150)
    {
        c.DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList;
        c.FlatStyle = FlatStyle.Flat;
        c.Items.AddRange(items.Cast<object>().ToArray());
        c.DrawMode = DrawMode.OwnerDrawFixed;
        c.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool closedControl = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool highlighted = !closedControl && (e.State & DrawItemState.Selected) != 0;
            using var bg = new SolidBrush(highlighted ? accentColor : c.BackColor);
            e.Graphics.FillRectangle(bg, e.Bounds);
            string text = c.GetItemText(c.Items[e.Index]) ?? "";
            if (c is ThemedComboBox { DisplayText: { } display })
                text = display(text);
            TextRenderer.DrawText(e.Graphics, text, c.Font, Rectangle.Inflate(e.Bounds, -2, 0), highlighted ? Color.White : c.Enabled ? c.ForeColor : secondaryColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        c.Width = width;
        c.MinimumSize = new Size(width, c.MinimumSize.Height);
        c.IntegralHeight = false;
        c.DropDownHeight = 240;
        c.Margin = new Padding(0, 3, 8, 3);
        c.DropDownClosed += (_, _) =>
        {
            if (c.DropDownStyle == ComboBoxStyle.DropDown)
            {
                c.SelectionStart = c.Text.Length;
                c.SelectionLength = 0;
            }
        };
        void ClearEditableSelection()
        {
            if (c.DropDownStyle == ComboBoxStyle.DropDown && c.IsHandleCreated)
            {
                c.SelectionStart = c.Text.Length;
                c.SelectionLength = 0;
            }
        }
        c.Enter += (_, _) => BeginInvoke(ClearEditableSelection);
        c.Leave += (_, _) => ClearEditableSelection();
        c.SelectionChangeCommitted += (_, _) => BeginInvoke(ClearEditableSelection);
        return c;
    }
    NumericUpDown Num(NumericUpDown n, int min, int max)
    {
        n.Minimum = min;
        n.Maximum = max;
        n.ThousandsSeparator = n != port;
        n.Width = 90;
        n.Margin = new Padding(0, 3, 4, 3);
        return n;
    }
    // A scrolling page with a title. Content is capped in width and left-aligned, like Windows Settings.
    Box Page(string name, string subtitle, out Panel tabPage, Control? action = null)
    {
        tabPage = new Panel { Text = name, AccessibleName = name, AccessibleRole = AccessibleRole.PropertyPage, Padding = Padding.Empty };
        tabs.AddPage(tabPage);
        var scroll = new ThemedScrollHost { Dock = DockStyle.Fill, MaxContentWidth = 1060, AccessibleName = name + " page" };
        tabPage.Controls.Add(scroll);
        var stack = new Box { Padding = new Padding(26, 20, 22, 20) };
        scroll.SetContent(stack);
        Add(stack, PageHeader(name, subtitle, action));
        return stack;
    }
    static Box VStack(Padding? padding = null, Padding? margin = null) => new() { Padding = padding ?? Padding.Empty, Margin = margin ?? Padding.Empty };
    static Box HRow(Padding? margin = null) => new() { Horizontal = true, Margin = margin ?? Padding.Empty };
    static Box PageHeader(string name, string subtitle, Control? action)
    {
        var text = VStack();
        var title = new Label { Text = name, AutoSize = true, UseMnemonic = false, Font = new Font("Segoe UI Semibold", 16f), Margin = new Padding(0, 0, 0, 2) };
        var sub = Secondary(subtitle);
        sub.Font = new Font("Segoe UI", 9.75f);
        sub.Margin = Padding.Empty;
        text.Add(title, Fit.Fill);
        text.Add(sub, Fit.Fill);
        var head = HRow(new Padding(0, 0, 0, 14));
        head.Add(text, Fit.Fill);
        if (action != null)
        {
            action.Margin = new Padding(16, 0, 0, 0);
            head.Add(action, Fit.Auto);
        }
        return head;
    }
    CardPanel Card(Box parent, string title, string? subtitle = null, string? help = null, Control? badge = null, Control? action = null)
    {
        var card = NewCard(title, subtitle, help, badge, action);
        Add(parent, card);
        return card;
    }
    CardPanel NewCard(string title, string? subtitle = null, string? help = null, Control? badge = null, Control? action = null)
    {
        var card = new CardPanel();
        var head = HRow(new Padding(0, 0, 0, subtitle == null ? 6 : 1));
        var titleRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        titleRow.Controls.Add(CardTitle(title));
        if (badge != null) titleRow.Controls.Add(badge);
        if (help != null) titleRow.Controls.Add(Info(help));
        head.Add(titleRow, Fit.Fill);
        if (action != null)
        {
            action.Margin = new Padding(12, 0, 0, 0);
            head.Add(action, Fit.Auto);
        }
        Add(card, head);
        if (subtitle != null)
        {
            var sub = Secondary(subtitle);
            sub.Margin = new Padding(0, 0, 0, 8);
            Add(card, sub);
        }
        return card;
    }
    // Stacks a control full width. Boxes measure wrapped text at its real width, so labels need no help.
    static void Add(Control parent, Control c)
    {
        if (parent is Box box)
        {
            box.Add(c, Fit.Fill);
            return;
        }
        var stack = (TableLayoutPanel)parent;
        int row = stack.RowCount++;
        stack.RowStyles.Add(new(SizeType.AutoSize));
        stack.Controls.Add(c, 0, row);
        c.Dock = DockStyle.Top;
    }
    static Control Caption(string title, InfoButton? info)
    {
        var label = new Label { Text = title, UseMnemonic = false, AutoSize = true, Margin = new Padding(0, 0, 2, 0) };
        if (info == null)
            return label;
        var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        label.Margin = new Padding(0, 4, 2, 0);
        flow.Controls.Add(label);
        flow.Controls.Add(info);
        return flow;
    }
    // A labeled setting: caption on the left in a fixed column, control on the right.
    Box Row(Box stack, string title, Control control, string? help = null, int captionWidth = 150)
    {
        var row = HRow(new Padding(0, 2, 0, 2));
        row.Add(Caption(title, help == null ? null : Info(help)), Fit.Fixed, captionWidth);
        if (string.IsNullOrWhiteSpace(control.AccessibleName))
            control.AccessibleName = title;
        row.Add(control, control is TextBox || control.Tag as string == "Fill" ? Fit.Fill : Fit.Auto);
        Add(stack, row);
        return row;
    }
    // A text box that fills the row, followed by buttons.
    static Box PathField(TextBox box, params Button[] buttons)
    {
        var field = HRow();
        field.Tag = "Fill";
        box.Margin = new Padding(0, 4, 8, 4);
        field.Add(box, Fit.Fill);
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].Margin = new Padding(0, 2, i == buttons.Length - 1 ? 0 : 6, 2);
            field.Add(buttons[i], Fit.Auto);
        }
        return field;
    }
    // A label that wraps beside a help button.
    Box LabelWithInfo(Label label, string help)
    {
        var row = HRow(new Padding(0, 2, 0, 2));
        label.Margin = new Padding(0, 3, 4, 3);
        row.Add(label, Fit.Fill);
        row.Add(Info(help), Fit.Auto);
        return row;
    }
    InfoButton Info(string text)
    {
        var b = new InfoButton(text);
        b.Click += (_, _) => ShowHelp(b, text);
        tips.SetToolTip(b, ShortHelp(text));
        helpButtons.Add(b);
        return b;
    }
    void ConfigureSlider(CompactTrackBar bar, string accessibleName)
    {
        bar.AccessibleName = accessibleName;
        bar.Minimum = 0;
        bar.Maximum = 1;
    }
    void ConfigureCacheSlider(CompactTrackBar bar, string accessibleName)
    {
        ConfigureSlider(bar, accessibleName);
        bar.Maximum = cacheStops.Length - 1;
    }
    // Caption, slider and exact value on one line, in fixed proportions.
    void SliderRow(Box stack, string title, Control sliderControl, Control exact, string? help = null)
    {
        var row = HRow(new Padding(0, 2, 0, 2));
        row.Add(Caption(title, help == null ? null : Info(help)), Fit.Fixed, 90);
        sliderControl.MinimumSize = new Size(80, sliderControl.Height);
        sliderControl.MaximumSize = new Size(0, sliderControl.Height);
        sliderControl.Margin = new Padding(0, 0, 10, 0);
        row.Add(sliderControl, Fit.Fill);
        exact.Margin = new Padding(0, 3, 0, 3);
        row.Add(exact, Fit.Auto);
        Add(stack, row);
    }
    Control Field(string title, Control control, string? help = null)
    {
        var field = HRow();
        field.Add(help == null ? FieldLabel(title) : (Control)Flow(FieldLabel(title), Info(help)), Fit.Fixed, 62);
        if (string.IsNullOrWhiteSpace(control.AccessibleName))
            control.AccessibleName = title;
        field.Add(control, Fit.Auto);
        return field;
    }
    static Label FieldLabel(string text) => new()
    {
        Text = text,
        UseMnemonic = false,
        AutoSize = false,
        AutoEllipsis = true,
        Height = 26,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Segoe UI", 9.75f),
        Margin = Padding.Empty,
        Tag = "SingleLineFieldLabel"
    };
    static Control Field(Control control) => control;
    void PairRow(Box stack, Control left, Control right)
    {
        var row = HRow(new Padding(0, 1, 0, 1));
        row.MinimumSize = new Size(0, 36);
        row.Add(left, Fit.Fill);
        row.Add(right, Fit.Fill);
        Add(stack, row);
    }
    void BuildCustomMemoryPanel()
    {
        customVramReserve.DecimalPlaces = customRamReserve.DecimalPlaces = 1;
        customVramReserve.Increment = customRamReserve.Increment = 0.5m;
        customVramReserve.Maximum = 128;
        customRamReserve.Maximum = 1024;
        customVramReserve.Width = customRamReserve.Width = 86;
        Combo(customAction, MemoryPolicies.CustomActions, width: 120);
        var stack = VStack(padding: new Padding(0, 2, 0, 4));
        customVramReserve.AccessibleName = "Custom VRAM reserve in GiB";
        customRamReserve.AccessibleName = "Custom RAM reserve in GiB";
        customAction.AccessibleName = "Custom over-budget action";
        tips.SetToolTip(customAction, "Warn allows the launch with a visible warning. Confirm asks once at launch. Block refuses the launch until the configuration or policy changes.");
        PairRow(stack, Field("VRAM", customVramReserve), Field("RAM", customRamReserve));
        Add(stack, Field("Action", customAction));
        customMemoryPanel.Controls.Add(stack);
    }
    void BuildTurboQuantPanel()
    {
        var stack = VStack(padding: new Padding(0, 6, 0, 5));
        var heading = new Label { Text = "TurboQuant KV cache", UseMnemonic = false, AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(0, 4, 4, 2), Anchor = AnchorStyles.Left, Tag = "SingleLineFieldLabel" };
        Add(stack, Flow(heading, Badge("Experimental", Tone.Caution)));
        Add(stack, Flow(turboEnabled, Info("TurboQuant is a native CUDA KV-cache codec. It compresses the attention cache after writing it, which can save substantial VRAM at long context. It does not change or requantize the model weights. Quality-first keeps K at Q8 and uses Turbo4 for V; more aggressive compression can reduce output quality and must be validated for each model.")));
        Combo(turboPreset, new[] { "Quality-first", "Balanced", "Memory saver", "Custom" }, width: 150);
        Row(stack, "Preset", turboPreset, "Quality-first uses Q8 K + Turbo4 V. Balanced uses Turbo4/Turbo4. Memory saver uses Turbo4 K + Turbo3 V on supported models, so it uses less cache memory than Balanced. Flash-Next cannot compress its indexer K cache, so Memory saver uses Q8 K + Turbo3 V there. Turbo3 remains experimental until measured on this model. Custom exposes only codecs provided by the pinned native runtime.", 98);
        var customStack = VStack();
        Combo(turboK, new[] { "Q8_0", "Turbo4", "Turbo3" }, width: 88);
        Combo(turboV, new[] { "Q8_0", "Turbo4", "Turbo3" }, width: 88);
        PairRow(customStack, Field("K cache", turboK), Field("V cache", turboV));
        turboCustomPanel.Controls.Add(customStack);
        Add(stack, turboCustomPanel);
        // A bounded two-line region: an auto-sized label would demand its full unwrapped width.
        effectiveCache.AutoSize = false;
        effectiveCache.Height = 38;
        Add(stack, effectiveCache);
        turboPanel.Controls.Add(stack);
    }
    static string ShortHelp(string text)
    {
        int end = text.IndexOfAny(new[] { '.', '\r', '\n' });
        string shortText = end >= 0 ? text[..Math.Min(end + 1, 180)] : text;
        return shortText.Length > 180 ? shortText[..177] + "…" : shortText;
    }
    void ShowHelp(InfoButton source, string text)
    {
        helpPopover?.Close();
        var screen = Screen.FromControl(source).WorkingArea;
        var anchor = source.PointToScreen(new Point(0, source.Height));
        helpPopover = new HelpPopover(text, Font, surfaceColor, foregroundColor, borderColor, screen, anchor);
        helpPopover.FormClosed += (_, _) => helpPopover = null;
        helpPopover.Show(this);
        helpPopover.Activate();
    }
    void SizeToolTip(object? sender, PopupEventArgs e)
    {
        if (e.AssociatedControl == null)
            return;
        var work = Screen.FromControl(e.AssociatedControl).WorkingArea;
        int width = Math.Max(180, Math.Min(360, work.Width - 24));
        var measured = TextRenderer.MeasureText(tips.GetToolTip(e.AssociatedControl), Font, new Size(width - 16, Math.Min(240, work.Height / 2)), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        e.ToolTipSize = new Size(Math.Min(width, measured.Width + 16), Math.Min(work.Height - 24, measured.Height + 12));
    }
    void DrawToolTip(object? sender, DrawToolTipEventArgs e)
    {
        using var background = new SolidBrush(surfaceColor);
        using var border = new Pen(borderColor);
        e.Graphics.FillRectangle(background, e.Bounds);
        e.Graphics.DrawRectangle(border, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
        TextRenderer.DrawText(e.Graphics, e.ToolTipText, Font, Rectangle.Inflate(e.Bounds, -8, -5), foregroundColor, TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
    }
    void BuildNavigation(TableLayoutPanel sidebar)
    {
        // Glyphs exist in both Segoe Fluent Icons (Windows 11) and Segoe MDL2 Assets (Windows 10).
        var entries = new (Panel Page, string Glyph)[]
        {
            (modelPage, "\uE8F1"), (enginesPage, "\uE945"), (advancedPage, "\uE9E9"),
            (networkPage, "\uE774"), (logsPage, "\uE9D9"), (settingsPage, "\uE713")
        };
        foreach (var (page, glyph) in entries)
        {
            var item = new NavButton { Text = page.Text, Glyph = glyph, Tag = page, AccessibleName = page.Text };
            item.Click += (_, _) => tabs.SelectedTab = page;
            nav.Add(item);
            sidebar.RowStyles.Add(new(SizeType.AutoSize));
            sidebar.Controls.Add(item, 0, sidebar.RowCount++);
        }
        sidebar.RowStyles.Add(new(SizeType.Percent, 100));
        sidebar.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Tag = "Chrome" }, 0, sidebar.RowCount++);
        appVersion.Text = "Version " + AppVersion;
        appVersion.Margin = new Padding(12, 0, 0, 0);
        sidebar.RowStyles.Add(new(SizeType.AutoSize));
        sidebar.Controls.Add(appVersion, 0, sidebar.RowCount++);
        nav[0].Selected = true;
    }
    void BuildModel()
    {
        var page = Page("Model", "Choose a GGUF model, the engine that runs it, and how much memory it may use.", out modelPage);
        var modelCard = Card(page, "GGUF model");
        Combo(models, Array.Empty<string>(), width: 300);
        models.AccessibleName = "Model";
        var add = Button("Add model…", () => BrowseModel());
        var remove = Button("Remove", () => RemoveModel());
        tips.SetToolTip(remove, "Remove this model from the launcher. The GGUF file itself is not deleted.");
        mutationButtons.AddRange(new[] { add, remove });
        var modelRow = HRow(new Padding(0, 0, 0, 2));
        models.Margin = new Padding(0, 4, 8, 4);
        modelRow.Add(models, Fit.Fill);
        modelRow.Add(add, Fit.Auto);
        remove.Margin = new Padding(0, 3, 0, 3);
        modelRow.Add(remove, Fit.Auto);
        Add(modelCard, modelRow);
        modelDetailsRow = modelDetails;
        Add(modelCard, modelDetails);
        var empty = VStack(margin: new Padding(0, 4, 0, 4));
        var emptyTitle = Label("No models yet");
        emptyTitle.Font = new Font("Segoe UI Semibold", 10.5f);
        Add(empty, emptyTitle);
        Add(empty, Secondary("Click Add model… and pick a .gguf file, or drag one onto this window. The file stays where it is. Need a model? Hugging Face has thousands of GGUF models; start with one that fits your graphics memory."));
        Add(empty, Flow(LinkButton("Find GGUF models on Hugging Face ↗", HuggingFaceGgufUrl)));
        emptyModels = empty;
        Add(modelCard, empty);
        ShowEmptyState();
        Combo(runtimeChoice, new[] { EngineManager.BundledRuntime }, width: 300);
        ((ThemedComboBox)runtimeChoice).DisplayText = EngineName;
        Row(modelCard, "Engine", runtimeChoice, "The engine that runs this model. llama.cpp is built in and runs every GGUF. Basalt (NVIDIA RTX 50-series only), PolyStrata and your own llama.cpp builds can be added on the Engines page. The choice is saved per model; stop the server before changing it.", 120);
        engineHint.Margin = new Padding(120, 0, 0, 4);
        Add(modelCard, engineHint);
        var chooseBasaltPack = Button("Choose…", () => { if (Selected == null) throw new InvalidOperationException("Select a model first."); using var d = new OpenFileDialog { Filter = "Basalt model pack|*.basalt", CheckFileExists = true }; if (d.ShowDialog(this) == DialogResult.OK) { basaltPack.Text = Selected.BasaltPack = d.FileName; store.Save(); notice.Text = "Basalt pack saved for this model."; RefreshEstimates(); } });
        basaltPackRow = Row(modelCard, "Basalt pack", PathField(basaltPack, chooseBasaltPack), "Basalt runs a converted .basalt pack instead of the GGUF. Convert this model with Basalt's converter, including its image projector (--mmproj) and MTP drafter (--mtp-source). The pack is saved per model.", 120);

        // Two cards side by side at every window size: the layout never swaps between one and two columns.
        var grid = HRow(new Padding(0, 0, 0, 12));
        var memoryColumn = NewCard("Context & memory", help: "How much text the model can keep in mind (context) and how precisely that memory is stored (KV cache). Larger context and higher precision use more graphics memory.");
        var performanceColumn = NewCard("Performance");
        memoryColumn.Margin = new Padding(0, 0, 6, 0);
        performanceColumn.Margin = new Padding(6, 0, 0, 0);
        grid.Add(memoryColumn, Fit.Fill, 1, fillHeight: true);
        grid.Add(performanceColumn, Fit.Fill, 1, fillHeight: true);
        Add(page, grid);
        Num(context, 256, 1048576);
        context.Width = 112;
        ConfigureSlider(contextSlider, "Context tokens");
        SliderRow(memoryColumn, "Context", contextSlider, context, "Exact imported values are preserved. Slider stops use readable context landmarks; model limits and memory recommendations are shown separately.");
        Combo(cacheK, cacheTypes, width: 112);
        Combo(cacheV, cacheTypes, width: 112);
        ConfigureCacheSlider(cacheKSlider, "K cache precision");
        ConfigureCacheSlider(cacheVSlider, "V cache precision");
        SliderRow(memoryColumn, "K cache", cacheKSlider, cacheK, "The slider and dropdown edit the same K-cache setting. Choosing an ordinary cache type turns TurboQuant off so your selected K/V values become effective. Lower precision reduces KV memory; supported combinations depend on the model and backend.");
        SliderRow(memoryColumn, "V cache", cacheVSlider, cacheV, "The slider and dropdown edit the same V-cache setting. Choosing an ordinary cache type turns TurboQuant off so your selected K/V values become effective. Imported types are preserved when opening this page. Restart a running server to apply changes.");
        Add(memoryColumn, contextHint);
        BuildTurboQuantPanel();
        Add(memoryColumn, turboPanel);
        Combo(memorySafety, MemoryPolicies.Names, width: 116);
        memorySafety.AccessibleName = "Memory safety";
        var memorySafetyField = Flow(memorySafety, Info(MemoryPolicies.Balanced.Help + " " + MemoryPolicies.Off.Help));
        memorySafetyField.WrapContents = false;
        memorySafetyField.Margin = Padding.Empty;
        BuildCustomMemoryPanel();
        Combo(effort, new[] { "low", "medium", "high", "xhigh", "max" }, width: 92);
        PairRow(performanceColumn, Field(thinking), Field("Effort", effort));
        Combo(gpu, new[] { "ALL", "64", "48", "32", "16", "0" }, true, 82);
        tips.SetToolTip(gpu, "Layers placed on the graphics card. ALL puts the whole model on the GPU; lower numbers move layers to the CPU and system RAM.");
        tips.SetToolTip(threads, "CPU threads used for generation and prompt processing.");
        PairRow(performanceColumn, Field("Threads", Num(threads, 1, 1024)), Field("GPU", gpu));
        PairRow(performanceColumn, Field("Batch", Num(batch, 1, 65536)), Field("UBatch", Num(ubatch, 1, 65536)));
        llamaSlotsRow = Row(performanceColumn, "Parallel slots", Num(llamaParallel, 1, 128), "Maximum simultaneous requests for llama.cpp. Its context is shared across slots: increasing this number reduces context per request unless total context is increased. More slots can require more memory. Save and restart to apply.", 132);
        polySlotsRow = Row(performanceColumn, "Parallel slots", Num(polyParallel, 1, 8), "Maximum simultaneous requests for PolyStrata or Basalt. Each slot uses memory; extra requests wait. Save and restart to apply.", 132);
        // Memory safety belongs with the memory settings, which also keeps both cards a similar height.
        Row(memoryColumn, "Memory safety", memorySafetyField, null, 104);
        Add(memoryColumn, customMemoryPanel);
        Add(performanceColumn, Flow(flash));
        Add(performanceColumn, Flow(ramKv, Info("For llama.cpp only: keep the context (KV) cache in system RAM instead of GPU memory. This frees VRAM but uses more RAM and can slow inference, especially with long contexts. Memory estimates may not reflect this placement. Save and restart to apply.")));
        Add(performanceColumn, LabelWithInfo(placement, "GPU offload and CPU placement share one setting. CPU placement is derived when the model layout is known; hybrid or incomplete metadata remains approximate or Unknown."));
        var advancedLink = new LinkLabel { Text = "Speculative decoding settings", AutoSize = true, Margin = new Padding(0, 4, 0, 2), TabStop = true, UseMnemonic = false };
        advancedLink.LinkClicked += (_, _) => tabs.SelectedTab = advancedPage;
        Add(performanceColumn, Flow(accelerationSummary, advancedLink));

        var autoOptimize = Button("Auto Optimize", async () => { if (Selected != null && info != null) { if (runtime.Active || runtime.ExistingServer) throw new InvalidOperationException("Stop the running server before Auto Optimize so its VRAM is not counted as unavailable."); CaptureProfile(); hardware = await Task.Run(Hardware.Detect); UpdateHardwareText(); bool repaired = engine.EnsureCompatibleProfile(Selected, info); MemoryEstimator.Optimize(Selected, info, hardware, backend); LoadProfile(); unsaved = true; notice.Text = (info.HasPle || info.Architecture.Equals("qwen4exp", StringComparison.OrdinalIgnoreCase)) && !info.UsesValidatedFlashNextPreset ? "This learned-table architecture has no validated optimization preset. Settings were retained; test a small context on a compatible runtime first." : repaired ? "Optimized values are ready. Flash-Next runtime and its verified local MTP head were repaired where needed; Save or Start to apply." : "Optimized values are ready to review; Save or Start to apply them."; UpdateStatus(); } });
        tips.SetToolTip(autoOptimize, "Pick a context size and settings that fit this PC's free memory. Nothing is saved until you Save or Start.");
        restorePreset = Button("Restore imported preset", () => { if (Selected?.KnownGood != null) { foreach (var kv in Selected.KnownGood) Selected.Options[kv.Key] = kv.Value?.DeepClone(); Selected.Acceleration = Selected.Options.B("MtpEnabled") ? "On" : "Off"; LoadProfile(); unsaved = true; notice.Text = "Imported preset restored in the editor; Save or Start to apply it."; UpdateStatus(); } });
        var memoryActions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        memoryActions.Controls.AddRange(new Control[] { restorePreset, autoOptimize });
        autoOptimize.Margin = new Padding(0, 0, 0, 0);
        restorePreset.Margin = new Padding(0, 0, 8, 0);
        var memoryCard = Card(page, "Memory fit", action: memoryActions);
        memoryFit.Font = new Font("Segoe UI Semibold", 10f);
        memoryFit.Margin = new Padding(0, 0, 0, 6);
        Add(memoryCard, memoryFit);
        var bars = HRow(new Padding(0, 0, 0, 4));
        vramBar.Margin = new Padding(0, 2, 12, 2);
        ramBar.Margin = new Padding(12, 2, 0, 2);
        bars.Add(vramBar, Fit.Fill);
        bars.Add(ramBar, Fit.Fill);
        Add(memoryCard, bars);
        Add(memoryCard, memory);

        compatibilityToggle = Button("Show details", () => { compatibilityPanel.Visible = !compatibilityPanel.Visible; compatibilityToggle.Text = compatibilityPanel.Visible ? "Hide details" : "Show details"; });
        var compatibilityCard = Card(page, "Compatibility", action: compatibilityToggle);
        Add(compatibilityCard, compatibilitySummary);
        compatibilityPanel.Controls.Add(compatibility);
        compatibilityPanel.Visible = false;
        Add(compatibilityCard, compatibilityPanel);
    }
    void BuildEngines()
    {
        var addEngine = Button("Add engine…", () => AddEngine(null));
        ((ThemedButton)addEngine).Kind = ButtonKind.Primary;
        var page = Page("Engines", "Engines run your models. llama.cpp is built in; add Basalt, PolyStrata or your own llama.cpp build when you need them.", out enginesPage, addEngine);

        var llamaCard = Card(page, "llama.cpp", "Built in. Runs any GGUF model on NVIDIA graphics (CUDA), any graphics card (Vulkan) or the processor (CPU).", badge: Badge("Built-in", Tone.Accent));
        Add(llamaCard, version);
        Row(llamaCard, "Graphics backend", Combo(backendChoice, new[] { "Auto", "CUDA", "Vulkan", "CPU" }, width: 150), "Auto picks CUDA on NVIDIA cards whose driver supports the bundled runtime, Vulkan on other graphics cards, and the CPU otherwise. Graphics drivers come from your card's maker.", 150);
        installUpdateButton = Button("Download and install update…", InstallOnlineUpdate);
        installUpdateButton.Enabled = false;
        releaseNotesButton = Button("Release notes", () =>
        {
            if (checkedEngineUpdate != null)
                Process.Start(new ProcessStartInfo(checkedEngineUpdate.Release.HtmlUrl) { UseShellExecute = true });
        });
        releaseNotesButton.Enabled = false;
        Add(llamaCard, Flow(Button("Check for updates", CheckForUpdates), releaseNotesButton, installUpdateButton));
        Add(llamaCard, updateStatus);
        Add(llamaCard, autoCheck);
        autoCheck.CheckedChanged += (_, _) => { if (!loading) { store.Value.CheckUpdatesOnOpen = autoCheck.Checked; store.Save(); } };
        Add(llamaCard, Flow(
            Button("Install offline bundle…", async () => { if (runtime.Active || runtime.ExistingServer) throw new InvalidOperationException("Stop the server before staging an engine."); using var d = new FolderBrowserDialog { Description = "Select a trusted engine bundle with manifest.json and cpu/cuda/vulkan subfolders" }; if (d.ShowDialog(this) == DialogResult.OK) { await engine.Stage(d.SelectedPath, backend); probe = await engine.Inspect(backend, Selected); version.Text = VersionText(); updateStatus.Text = "Engine probes passed. Previous engine retained. Start a model to verify inference; use Roll back engine if it fails."; } }),
            Button("Roll back engine", async () => { if (runtime.Active || runtime.ExistingServer) throw new InvalidOperationException("Stop the server before rollback."); await engine.Rollback(backend); probe = await engine.Inspect(backend, Selected); version.Text = VersionText(); updateStatus.Text = "Previous engine restored."; })));
        Add(llamaCard, Secondary("Updates come from the official llama.cpp Windows builds on GitHub, and installing always takes a separate click. Files are SHA-256 checked and tested before they become active, and the previous engine stays available for rollback. Your models and settings are not changed."));

        var basaltCard = Card(page, "Basalt", "A separate engine built for NVIDIA RTX 50-series (Blackwell) graphics cards, such as the RTX 5070, 5080 and 5090. It runs Qwen3.8 Flash-Next from a converted .basalt model pack and does not run on older GPUs or other brands.", badge: Badge("RTX 50-series only", Tone.Caution));
        basaltStatus.Font = new Font("Segoe UI Semibold", 9.5f);
        Add(basaltCard, basaltStatus);
        basaltRoot.AccessibleName = "Basalt folder";
        Row(basaltCard, "Basalt folder", PathField(basaltRoot, Button("Choose…", () => { using var d = new FolderBrowserDialog { InitialDirectory = Directory.Exists(basaltRoot.Text) ? basaltRoot.Text : @"C:\" }; if (d.ShowDialog(this) == DialogResult.OK) { basaltRoot.Text = store.Value.BasaltRoot = d.SelectedPath; store.Save(); RefreshEngineStatus(); RefreshEstimates(); } })), null, 150);
        getBasalt = LinkButton("Get Basalt ↗", EngineDialog.BasaltUrl);
        ((ThemedButton)getBasalt).Kind = ButtonKind.Primary;
        basaltDownload = Button("Download update ↗", () => Process.Start(new ProcessStartInfo(basaltRelease?.Url ?? EngineDialog.BasaltUrl + "/releases/latest") { UseShellExecute = true }));
        ((ThemedButton)basaltDownload).Kind = ButtonKind.Primary;
        basaltDownload.Visible = false;
        tips.SetToolTip(basaltDownload, "Opens the release on GitHub. The launcher never replaces Basalt's files itself, so a custom or hotfixed build is left exactly as it is. Stop the server before installing a new Basalt.");
        Add(basaltCard, Flow(basaltVersion, Button("Check for update", async () => await Run(CheckBasaltUpdate)), basaltDownload, getBasalt, LinkButton("How to convert a model ↗", EngineDialog.BasaltConvertUrl)));
        Add(basaltCard, Secondary("To use it: install Basalt with its Python environment and CUDA 13 libraries, convert your Flash-Next GGUF to a .basalt pack, then on the Model page choose Basalt as the engine and pick the pack."));

        var polyCard = Card(page, "PolyStrata", "An experimental engine for large mixture-of-experts models such as Qwen3.8 Flash-Next. It is installed separately and only runs the models it supports; others stay on llama.cpp.", badge: Badge("Experimental", Tone.Neutral));
        Add(polyCard, Flow(showPoly, Info("When enabled, PolyStrata appears in the Engine list on the Model page for the MoE models it supports. It serves the same API on the same port, bound to this PC only. Install it and run its setup for the model first; the launcher starts, checks and stops its server.")));
        Add(polyCard, polyStatus);
        polyRoot.AccessibleName = "PolyStrata folder";
        Row(polyCard, "PolyStrata folder", PathField(polyRoot, Button("Choose…", () => { using var d = new FolderBrowserDialog { InitialDirectory = Directory.Exists(polyRoot.Text) ? polyRoot.Text : @"C:\" }; if (d.ShowDialog(this) == DialogResult.OK) { polyRoot.Text = d.SelectedPath; store.Value.PolyStrataRoot = d.SelectedPath; store.Save(); notice.Text = PolyStrata.InstallProblem(d.SelectedPath) ?? $"PolyStrata found with {PolyStrata.Configs(d.SelectedPath).Count()} installed model config(s)."; RefreshEngineStatus(); } })), null, 150);
        Add(polyCard, Flow(polyVersion,
            Button("Check for update", async () => await Run(async () =>
            {
                string installed = await PolyStrata.InstalledVersion(store.Value.PolyStrataRoot), latest = await PolyStrata.LatestRelease();
                polyVersion.Text = $"PolyStrata version: {installed} · latest release {latest}";
                notice.Text = installed == latest ? "PolyStrata is up to date." : $"PolyStrata {latest} is available. Stop the server, then choose Update.";
            })),
            Button("Update", async () => await Run(async () =>
            {
                if (runtime.Active || runtime.ExistingServer)
                    throw new InvalidOperationException("Stop the server before updating PolyStrata.");
                string latest = await PolyStrata.LatestRelease(), installed = await PolyStrata.InstalledVersion(store.Value.PolyStrataRoot);
                if (installed == latest) { notice.Text = $"PolyStrata {latest} is already installed."; return; }
                if (MessageBox.Show(this, $"Update PolyStrata from {installed} to {latest}? The launcher checks out that release and runs PolyStrata's own updater (engine, Python packages, model settings). Model files are not changed; Roll back returns to {installed}.", "Update PolyStrata", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
                    return;
                notice.Text = $"Updating PolyStrata to {latest}… this can take several minutes.";
                await PolyStrata.Update(store.Value.PolyStrataRoot, latest, line => runtime.AppendExternalLog("PolyStrata update", line));
                polyVersion.Text = $"PolyStrata version: {latest}";
                notice.Text = $"PolyStrata updated to {latest}. Start a PolyStrata model to confirm it; Roll back returns to {installed}.";
            })),
            Button("Roll back", async () => await Run(async () =>
            {
                if (runtime.Active || runtime.ExistingServer)
                    throw new InvalidOperationException("Stop the server before rolling back PolyStrata.");
                string previous = await PolyStrata.Rollback(store.Value.PolyStrataRoot, line => runtime.AppendExternalLog("PolyStrata rollback", line));
                polyVersion.Text = $"PolyStrata version: {previous}";
                notice.Text = $"PolyStrata rolled back to {previous}.";
            })),
            getPoly = LinkButton("Get PolyStrata ↗", EngineDialog.PolyStrataUrl)));

        var customCard = Card(page, "Custom engines", "Your own llama.cpp builds, for example a newer release or a build for a specific GPU. Each one is a folder that contains llama-server.exe, and it then appears in every model's Engine list.", action: Button("Add custom engine…", () => AddEngine("Custom")));
        Add(customCard, customEngineList);

        turboCard = Card(page, "TurboQuant runtime", "A pinned llama.cpp fork with a compressed KV cache, used for Qwen3.8 Flash-Next. Shown only when this release includes it.", badge: Badge("Experimental", Tone.Caution));
        Add(turboCard, Flow(showTurbo, Info("Enables the per-model TurboQuant controls on the Model page. TurboQuant uses a pinned experimental native CUDA runtime to compress K/V cache memory. It never modifies model weights. Turning this switch off requires resolving any model that still has TurboQuant enabled.")));

        var pcCard = Card(page, "This PC", "Shared system memory is not counted as dedicated graphics memory.");
        var hardwareDetails = Secondary("Detecting adapters…");
        hardwareText.Tag = hardwareDetails;
        Add(pcCard, hardwareDetails);
        backendChoice.SelectedIndexChanged += async (_, _) => { if (!loading) await Run(async () => { if (runtime.Active) throw new InvalidOperationException("Stop the server before changing backend."); store.Value.Backend = backendChoice.Text; backend = await engine.Resolve(store.Value.Backend, hardware, Selected); UpdateHardwareText(); probe = await engine.Inspect(backend, Selected); version.Text = VersionText(); store.Save(); RefreshEstimates(); }); };
        runtimeChoice.SelectedIndexChanged += async (_, _) => { if (!loading) await Run(ChangeRuntime); };
    }
    void BuildAdvanced()
    {
        var page = Page("Advanced", "Fine-tuning for the selected model. The defaults work well; change these only when you know you need to.", out advancedPage);
        var speculative = Card(page, "Speculative decoding", "Generates text faster by drafting several tokens ahead and checking them in one pass. Output quality is unchanged.");
        Combo(acceleration, new[] { "Auto", "Off", "On", "Force MTP (Experimental)" }, width: 240);
        Row(speculative, "MTP", acceleration, "MTP uses a learned multi-token prediction head. Auto enables it only when compatible embedded tensors or a selected draft-head file are confirmed. On requires support. Force is an explicit unsupported experiment. MTP is independent of N-gram and both can be active together.", 170);
        var mtpStack = VStack();
        Row(mtpStack, "MTP draft tokens", Num(draft, 1, 16), null, 170);
        draftPMin.DecimalPlaces = 2;
        draftPMin.Increment = 0.05m;
        draftPMin.Minimum = 0;
        draftPMin.Maximum = 1;
        draftPMin.Width = 90;
        Row(mtpStack, "MTP confidence", draftPMin, "The draft head stops proposing tokens below this confidence. 0.75 is the quality-first value recommended for this head; lowering it may draft more tokens but can reduce acceptance or speed.", 170);
        Add(mtpStack, Flow(draftSampling, Info("Verifies drafted tokens by rejection sampling instead of an exact match. Output follows the same distribution as without MTP, and more drafted tokens are accepted at temperatures above 0. Requires an engine that offers --spec-draft-sampling (the bundled engine from build 12017; not the TurboQuant / Flash-Next runtime).")));
        Row(mtpStack, "Draft-head file", PathField(draftModel, Button("Browse…", () => BrowseFile(draftModel, "GGUF MTP draft head|*.gguf")), Button("Clear", () => draftModel.Clear())), "Select a small GGUF MTP head made for this exact base model, or a DFlash 2 drafter for it (its GGUF architecture is dflash), which runs as llama.cpp's draft-dflash and drafts a whole block per pass; for DFlash 2, 7 draft tokens and N-gram on are the measured best on the 27B. The launcher's learned PLE metadata is separate from speculative MTP and is not treated as a draft head.", 170);
        mtpPanel.Controls.Add(mtpStack);
        Add(speculative, mtpPanel);
        var ngramStack = VStack();
        Row(ngramStack, "N-gram", Combo(ngram, new[] { "Off", "ngram-simple", "ngram-map-k", "ngram-map-k4v", "ngram-mod", "ngram-cache" }, width: 190), "N-gram speculation predicts from repeated prompt/output patterns without a learned draft model. It is independent of MTP. When both are enabled the launcher emits one combined speculative pipeline and the runtime reports each implementation's activity. This control does not enable or disable a model's learned PLE embedding table; that table is part of the model architecture.", 170);
        ngramPanel.Controls.Add(ngramStack);
        Add(speculative, ngramPanel);

        var sampling = Card(page, "Sampling");
        temperature.DecimalPlaces = 2;
        temperature.Increment = 0.05m;
        temperature.Minimum = 0;
        temperature.Maximum = 2;
        temperature.Width = 100;
        temperature.AccessibleName = "Temperature";
        Row(sampling, "Temperature", Flow(temperature, Button("Use Qwen default", () => { temperature.Value = thinking.Checked ? 1.00m : 0.70m; Changed(); })), "Controls response variety. Lower values are more predictable; higher values are more varied. Saved per model. Qwen models are tuned for 0.70 with thinking off and 1.00 with thinking on.", 170);

        var moe = Card(page, "Mixture-of-experts placement", "For large MoE models like Qwen3.8 Flash-Next on llama.cpp: decide which expert layers live in system RAM and which on the graphics card.");
        moeCard = moe;
        Add(moe, Flow(cpuMoe, Info("Hybrid placement permanently keeps the selected number of early expert layers in RAM and places the remaining expert layers on CUDA. Unlike a dynamic expert cache, permanent offload also reduces RAM traffic while processing large prompts.")));
        Row(moe, "CPU MoE layers", Num(cpuMoeLayers, 0, 256), "Flash-Next has 48 layers. On a 32 GB graphics card with 96 GB of RAM, 36 is a good starting point: 36 expert layers stay in RAM and 12 stay on the GPU. Lower values use more graphics memory; 0 keeps every expert layer on the CPU.", 170);
        Add(moe, Flow(moeCache, Info("The dynamic cache can improve short-token generation, but large prompt batches bypass it. Leave it off when you use permanent expert-layer offload, so graphics memory goes to that instead.")));
        Row(moe, "RAM working set GiB", Num(workingSetLimit, 0, 1024), "Hard ceiling for this server's resident Windows working set. On a 96 GB PC, 65 GiB leaves roughly 12–17 GiB for Windows and other apps while clean GGUF pages are evicted back to their SSD mapping. Zero disables the ceiling.", 170);

        var overrides = Card(page, "Specialist overrides", "Leave these empty unless a model needs them.");
        Row(overrides, "Vision projector", PathField(projector, Button("Browse…", () => BrowseFile(projector, "GGUF|*.gguf")), Button("Clear", () => projector.Clear())), "An mmproj GGUF that lets a vision model read images. Pick the one made for this exact model.", 170);
        Row(overrides, "Chat template file", PathField(template, Button("Browse…", () => BrowseFile(template, "Jinja template|*.jinja;*.jinja2|All files|*.*")), Button("Use embedded", () => template.Clear())), "Replaces the chat template stored in the GGUF. Leave empty to use the model's own template.", 170);
        Row(overrides, "Custom arguments", custom, "Advanced: numeric --seed, --temp, --top-p, --top-k, --min-p, --repeat-penalty, --presence-penalty and --frequency-penalty are accepted. Managed flags and duplicates are rejected, including sampling already managed by imported Qwen profiles.", 170);
    }
    void BuildNetwork()
    {
        var page = Page("Network", "Connect apps to your model: on this PC, from your local network, or from anywhere with Tailscale.", out networkPage);

        var local = Card(page, "This PC", "Works out of the box. Apps on this computer connect to the local address with the API key below.");
        localStatus.Font = new Font("Segoe UI Semibold", 9.75f);
        Add(local, localStatus);
        Row(local, "Server port", Num(port, 1024, 65535), null, 130);
        var showKey = Button("Show", () => { });
        showKey.Click += (_, _) => { key.UseSystemPasswordChar = !key.UseSystemPasswordChar; showKey.Text = key.UseSystemPasswordChar ? "Show" : "Hide"; };
        Row(local, "API key", PathField(key, showKey, Button("Copy", () => Clipboard.SetText(store.Value.ApiKey))), "Apps send this key as \"Authorization: Bearer <key>\". Keep it private.", 130);
        Add(local, Flow(Button("Copy local URL", () => { RequireLiveConnection(); Clipboard.SetText($"http://127.0.0.1:{store.Value.Port}/v1"); notice.Text = "Local URL copied."; }), Button("Copy model ID", () => { RequireLiveConnection(); Clipboard.SetText(runtime.LiveModelId); notice.Text = "Model ID copied."; }), Button("New key", () => { if (runtime.Active) throw new InvalidOperationException("Stop the server before changing its API key."); store.Value.ApiKey = Settings.NewKey(); key.Text = store.Value.ApiKey; store.Save(); notice.Text = "New key saved. Update the apps that use it."; })));
        Add(local, network);

        var lanCard = Card(page, "Local network (LAN)", "Lets phones, laptops and other devices on your home or office network use the model.");
        lanStatus.Font = new Font("Segoe UI Semibold", 9.75f);
        Add(lanCard, lanStatus);
        Add(lanCard, lan);
        Add(lanCard, Flow(Button("Copy LAN URL", () => { RequireLiveConnection(); if (!store.Value.Lan) throw new InvalidOperationException("LAN access is off on the running server. Turn it on, then Save and Restart."); Clipboard.SetText($"http://{Integration.LanAddress()}:{store.Value.Port}/v1"); notice.Text = "LAN URL copied."; }), Button("Repair firewall rule", async () => { Save(); if (!store.Value.Lan) throw new InvalidOperationException("Turn on local network access before applying a firewall rule."); await Integration.Firewall(engine.Executable(backend, Selected), store.Value.Port); notice.Text = "Firewall rule applied: only your local subnet can connect."; })));
        Add(lanCard, Secondary("When on, the server listens on all network adapters and Windows asks once for administrator permission to add a firewall rule that only allows your local subnet. Save and Restart the model after switching. PolyStrata and Basalt always stay on this PC; use Tailscale to reach them from other devices."));

        tailscaleShareRow = Flow(Button("Share model API", async () => await Run(async () =>
        {
            RequireLiveConnection();
            await ModelTailnet.Share(store.Value);
            store.Value.ModelTailscale = true;
            store.Save();
            var address = await Ollama.TailnetAddress();
            notice.Text = $"Model API shared: http://{address.Ip}:{store.Value.Port}/v1 (use the server API key).";
            tailscaleStatus.Text = $"Shared on your tailnet: http://{address.Ip}:{store.Value.Port}/v1";
            tailscaleStatus.ForeColor = positiveColor;
        })), Button("Stop sharing", async () => await Run(async () =>
        {
            await ModelTailnet.Stop(store.Value.ModelTailscalePort > 0 ? store.Value.ModelTailscalePort : store.Value.Port);
            store.Value.ModelTailscale = false;
            store.Save();
            notice.Text = "Model API sharing stopped.";
            RefreshTailscale();
        })));
        tips.SetToolTip(tailscaleShareRow.Controls[0], "Share (or repair) the model API on your tailnet. It keeps working while the launcher is closed.");
        const string tailscaleHelp = "Tailscale is a free app that links your own devices into a private, encrypted network (a \"tailnet\"). Install it on this PC and on your phone or laptop and sign in to the same account. Then you can use this AI model when you're away from this computer: apps connect to this PC's Tailscale address instead of your home network. Nothing is opened to the public internet, and the server API key is still required. The download opens tailscale.com; after installing and signing in, come back here and choose Share model API.";
        var tailscaleCard = Card(page, "Tailscale: use your model from anywhere", "Reach the model from your phone or laptop when you're away from this computer, privately, through your Tailscale network.", tailscaleHelp);
        tailscaleStatus.Font = new Font("Segoe UI Semibold", 9.75f);
        Add(tailscaleCard, tailscaleStatus);
        var download = Button("Download Tailscale ↗", () => Process.Start(new ProcessStartInfo(TailscaleDownloadUrl) { UseShellExecute = true }));
        ((ThemedButton)download).Kind = ButtonKind.Primary;
        var installInfo = Info(tailscaleHelp);
        installInfo.Margin = new Padding(2, 9, 2, 2);
        tailscaleInstallRow = Flow(download, installInfo, Button("I've installed it", () => { RefreshTailscale(); if (!tailscaleInstalled) notice.Text = "Tailscale wasn't found yet. Finish the installer, sign in, then try again."; }));
        Add(tailscaleCard, tailscaleInstallRow);
        Add(tailscaleCard, tailscaleShareRow);

        var ollamaCard = Card(page, "Ollama-compatible API", $"For apps that only speak Ollama's API. It runs on port {Ollama.Port} and answers with the loaded model; the server's own port and key stay as they are.");
        Add(ollamaCard, ollama);
        Add(ollamaCard, ollamaTailnet);
        Add(ollamaCard, ollamaRequireKey);
        Row(ollamaCard, "Ollama API key", ollamaKey, null, 130);
        Add(ollamaCard, ollamaDebug);
        Add(ollamaCard, ollamaStatus);
        Add(ollamaCard, Flow(Button("Copy Ollama URL", () => Clipboard.SetText(Ollama.LocalUrl)), Button("Copy Tailscale URL", async () =>
        {
            if (!store.Value.OllamaEnabled || !store.Value.OllamaTailscale)
                throw new InvalidOperationException("Turn on the Ollama API and Tailscale sharing first.");
            tailnet ??= await Ollama.TailnetAddress();
            if (tailnet.Problem != null)
                throw new InvalidOperationException(tailnet.Problem);
            Clipboard.SetText(tailnet.Url);
        }), Button("Copy key", () => { if (!store.Value.OllamaRequireKey) throw new InvalidOperationException("Turn on \"Require an API key\" first."); Clipboard.SetText(store.Value.OllamaApiKey); }), Button("New key", () =>
        {
            if (!store.Value.OllamaRequireKey)
                throw new InvalidOperationException("Turn on \"Require an API key\" first.");
            store.Value.OllamaApiKey = ollamaKey.Text = Ollama.NewKey();
            store.Save();
            notice.Text = "New Ollama API key saved and in effect now. Update the apps that use it.";
        }), Button("Open log", () => { string log = Ollama.LogFile(store.Root); if (!File.Exists(log)) throw new FileNotFoundException("The Ollama API has not written a log yet."); Process.Start(new ProcessStartInfo(log) { UseShellExecute = true }); })));
        Add(ollamaCard, Secondary($"It follows model, port and key changes by itself, keeps running while this window is closed, and starts when you sign in to Windows. Tailscale sharing makes it reachable from your tailnet devices only (no firewall change). Like Ollama it asks for no key unless you require one; apps send it as \"Authorization: Bearer <key>\", or in a URL as http://ollama:<key>@host:{Ollama.Port}. Needs Python 3.9 or later."));
        ollamaRequireKey.CheckedChanged += (_, _) =>
        {
            if (ollamaSyncing)
                return;
            if (ollamaRequireKey.Checked && store.Value.OllamaApiKey.Length == 0)
                store.Value.OllamaApiKey = Ollama.NewKey();
            store.Value.OllamaRequireKey = ollamaRequireKey.Checked;
            store.Save();
            ShowOllamaChoice(store.Value.OllamaEnabled, store.Value.OllamaTailscale);
            notice.Text = ollamaRequireKey.Checked ? "The Ollama API now requires its key. Copy it into the apps that use it." : "The Ollama API no longer requires a key.";
            _ = RefreshOllama();
        };
        ollamaDebug.CheckedChanged += (_, _) =>
        {
            if (ollamaSyncing)
                return;
            store.Value.OllamaDebugLog = ollamaDebug.Checked;
            store.Save();
            notice.Text = ollamaDebug.Checked ? "Ollama debug log on: one line of request details per request (never chat text). Turn it off when done." : "Ollama debug log off: only start, stop and errors are logged.";
            _ = RefreshOllama();
        };
        ollama.CheckedChanged += async (_, _) => await SetOllama(ollama.Checked, store.Value.OllamaTailscale);
        ollamaQuick.CheckedChanged += async (_, _) => await SetOllama(ollamaQuick.Checked, store.Value.OllamaTailscale);
        ollamaTailnet.CheckedChanged += async (_, _) => await SetOllama(store.Value.OllamaEnabled || ollamaTailnet.Checked, ollamaTailnet.Checked);
        ollamaTimer.Tick += async (_, _) => { RefreshTailscale(); await RefreshOllama(); };

        var qwen = Card(page, "Qwen Code integration", "Shown because this PC has an imported Qwen launcher configuration. Import leaves Qwen Code files untouched; Sync runs the original launcher's integration for the selected imported profile.");
        qwenCard = qwen;
        Add(qwen, Flow(Button("Sync Qwen Code settings", async () =>
        {
            if (Selected == null)
                return;
            RequireLiveConnection();
            await Integration.SyncQwen(store, Selected, engine.Executable(backend, Selected), runtime.LiveModelId, runtime.LiveContext);
            notice.Text = "Qwen Code synchronization completed using the live model ID and context.";
        }), Button("Open Qwen Code", () => { if (!File.Exists(store.Value.LegacyDesktop)) throw new FileNotFoundException("No imported Qwen Code installation was found."); Process.Start(new ProcessStartInfo(store.Value.LegacyDesktop) { UseShellExecute = true }); })));
    }
    void RequireLiveConnection()
    {
        if (Selected == null || unsaved || restartRequired || !(runtime.Active || runtime.ExistingServer)
            || runtime.LiveProfile != Selected || runtime.LiveModelId.Length == 0 || runtime.LiveContext is null)
            throw new InvalidOperationException("Start the selected model with these settings first. The copied details come from the running server, so they always match what apps will connect to.");
    }
    void BuildDiagnostics()
    {
        logsPage = new Panel { Text = "Logs", AccessibleName = "Logs", AccessibleRole = AccessibleRole.PropertyPage, Padding = Padding.Empty };
        tabs.AddPage(logsPage);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(26, 20, 22, 16) };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        logsPage.Controls.Add(layout);
        logs.Font = new Font("Cascadia Mono", 9f);
        layout.Controls.Add(PageHeader("Logs", "Live output from the engine. Useful when a model doesn't start.", null), 0, 0);
        layout.Controls.Add(Flow(Button("Copy report", () => { Clipboard.SetText(Report()); notice.Text = "Diagnostic report copied."; }), Button("Save report…", () => { using var d = new SaveFileDialog { Filter = "Diagnostic report|*.json", FileName = "launcher-diagnostics.json" }; if (d.ShowDialog(this) == DialogResult.OK) File.WriteAllText(d.FileName, Report()); }), Button("Open logs folder", () => Process.Start(new ProcessStartInfo(Path.Combine(store.Root, "logs")) { UseShellExecute = true })), Info("Reports exclude runtime output, API keys, paths, model names and conversations. Logs here are local and may include engine metadata. Review before sharing.")), 0, 1);
        var logCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 6, 10), Margin = new Padding(0, 6, 0, 0), AutoSize = false };
        logCard.Add(logs, Fit.Fill, fillHeight: true);
        layout.Controls.Add(logCard, 0, 2);
    }
    void BuildSettings()
    {
        var page = Page("Settings", "Appearance, defaults for new models, and information about this app.", out settingsPage);
        var appearance = Card(page, "Appearance");
        Row(appearance, "Theme", Combo(theme, new[] { "Dark", "Light", "System" }, width: 140), null, 170);
        theme.AccessibleName = "Theme";

        var defaults = Card(page, "New models", "Used when you add a model. Existing models keep their own settings.");
        Row(defaults, "Models folder", PathField(modelFolder, Button("Choose…", () => { using var d = new FolderBrowserDialog { InitialDirectory = store.Value.ModelsDirectory }; if (d.ShowDialog(this) == DialogResult.OK) { modelFolder.Text = d.SelectedPath; Save(); } })), "Where Add model… opens. Models can live anywhere; nothing is copied or moved.", 170);
        Combo(defaultEngine, new[] { EngineManager.BundledRuntime }, width: 260);
        defaultEngine.DisplayText = EngineName;
        Row(defaults, "Engine", defaultEngine, "The engine new models start with: the built-in llama.cpp or one of your custom llama.cpp builds. Basalt and PolyStrata need per-model setup, so choose them on the Model page.", 170);
        Combo(defaultMemorySafety, MemoryPolicies.Names, width: 142);
        Row(defaults, "Memory safety", defaultMemorySafety, "Balanced asks before a launch that likely won't fit in memory. Strict blocks it, Relaxed keeps a smaller reserve, and Off never checks. This only sets the default for models added later.", 170);
        tips.SetToolTip(defaultMemorySafety, "This is only the default for models added in the future. It never rewrites existing profiles.");

        var setup = Card(page, "Setup");
        Add(setup, Flow(Button("Run first-time setup again…", () => _ = Run(() => RunEngineSetup(true, null))), Button("Import Qwen launcher profiles…", async () => { if (runtime.Active) throw new InvalidOperationException("Stop the server before importing profiles."); using var d = new OpenFileDialog { Filter = "Qwen launcher configuration (schema 3)|*.json" }; if (d.ShowDialog(this) == DialogResult.OK) { store.Import(d.FileName); await ReloadModels(); } })));

        var about = Card(page, "About");
        var name = Label($"llama.cpp Launcher {AppVersion}");
        name.Font = new Font("Segoe UI Semibold", 10f);
        Add(about, name);
        Add(about, Secondary("A Windows launcher for local GGUF models. Not affiliated with the llama.cpp project. Models and settings stay on this PC; nothing is sent anywhere unless you share it."));
        Add(about, Flow(Button("Third-party licenses", () => { var path = Path.Combine(AppContext.BaseDirectory, "licenses", "THIRD-PARTY-NOTICES.txt"); if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }), Button("Create desktop shortcut", () => { Integration.DesktopShortcut(); notice.Text = "Desktop shortcut created."; }), Button("Open settings folder", () => Process.Start(new ProcessStartInfo(store.Root) { UseShellExecute = true }))));
    }
    // Friendly names for engine IDs. Profiles store the ID; lists show these names.
    string EngineName(string id) => id switch
    {
        EngineManager.BundledRuntime => "llama.cpp (built-in)",
        EngineManager.FlashNextRuntime => "llama.cpp TurboQuant fork (experimental)",
        PolyStrata.Runtime => "PolyStrata (experimental)",
        Basalt.Runtime => "Basalt (NVIDIA RTX 50-series)",
        _ when CustomEngine.IsCustom(id) => CustomEngine.NameOf(id) + " (custom llama.cpp)",
        _ => id
    };
    void RefreshLogs()
    {
        var current = runtime.LogText;
        if (logs.Text != current)
        {
            logs.Text = current;
            logs.SelectionStart = logs.TextLength;
            logs.ScrollToCaret();
        }
    }
    // Tailscale installs to Program Files or PATH; the download button disappears once it is found.
    void RefreshTailscale()
    {
        bool installed = Ollama.Tailscale() != null;
        tailscaleInstalled = installed;
        tailscaleInstallRow.Visible = !installed;
        tailscaleShareRow.Visible = installed;
        ollamaTailnet.Enabled = installed || ollamaTailnet.Checked;
        if (!installed)
        {
            tailscaleStatus.Text = "Tailscale isn't installed on this PC yet.";
            tailscaleStatus.ForeColor = secondaryColor;
        }
        else if (store.Value.ModelTailscale)
        {
            if (!tailscaleStatus.Text.StartsWith("Shared on your tailnet", StringComparison.Ordinal))
            {
                tailscaleStatus.Text = "Sharing is on. Apps on your Tailscale devices connect to this PC's Tailscale address with the server API key.";
                _ = ShowTailnetAddress();
            }
            tailscaleStatus.ForeColor = positiveColor;
        }
        else
        {
            tailscaleStatus.Text = "Tailscale is installed. Start a model, then choose Share model API to reach it from your other devices.";
            tailscaleStatus.ForeColor = foregroundColor;
        }
    }
    async Task ShowTailnetAddress()
    {
        try
        {
            var address = await Ollama.TailnetAddress();
            if (address.Problem == null && store.Value.ModelTailscale)
                tailscaleStatus.Text = $"Shared on your tailnet: http://{address.Ip}:{store.Value.Port}/v1 (use the server API key).";
            else if (address.Problem != null)
            {
                tailscaleStatus.Text = address.Problem + " Open Tailscale and sign in, then choose Share model API.";
                tailscaleStatus.ForeColor = cautionColor;
            }
        }
        catch (Exception) { }
    }
    void RefreshEngineStatus()
    {
        string root = store.Value.BasaltRoot;
        bool blackwell = EngineDialog.HasBlackwell(hardware);
        if (string.IsNullOrWhiteSpace(root))
        {
            basaltStatus.Text = "Not set up." + (blackwell ? " Your RTX 50-series graphics card can run it." : hardware.Gpus.Count > 0 ? " No RTX 50-series graphics card was found on this PC, so Basalt won't run here." : "");
            basaltStatus.ForeColor = secondaryColor;
        }
        else if (!File.Exists(Basalt.Engine(root)) || !File.Exists(Basalt.Python(root)))
        {
            basaltStatus.Text = "This folder doesn't contain bin\\basalt.exe and its .venv Python environment.";
            basaltStatus.ForeColor = cautionColor;
        }
        else
        {
            basaltStatus.Text = "✓ Ready." + (blackwell ? " RTX 50-series graphics card found." : " Note: no RTX 50-series graphics card was found on this PC.");
            basaltStatus.ForeColor = blackwell ? positiveColor : cautionColor;
        }
        if (!showPoly.Checked)
            polyStatus.Text = "Off. Turn it on to choose PolyStrata on the Model page.";
        else
            polyStatus.Text = PolyStrata.InstallProblem(store.Value.PolyStrataRoot) ?? "✓ Ready.";
        // The download buttons are for getting an engine; once it is installed they make way for updates.
        bool basaltInstalled = !string.IsNullOrWhiteSpace(root) && File.Exists(Basalt.Engine(root)) && File.Exists(Basalt.Python(root));
        getBasalt.Visible = !basaltInstalled;
        getPoly.Visible = PolyStrata.InstallProblem(store.Value.PolyStrataRoot) != null;
        turboCard.Visible = Directory.Exists(engine.ExperimentalRoot) || store.Value.ShowTurboQuantControls;
        RefreshCustomEngines();
    }
    string customEngineSignature = "";
    async Task CheckBasaltUpdate()
    {
        string root = store.Value.BasaltRoot;
        if (string.IsNullOrWhiteSpace(root) || !File.Exists(Basalt.Engine(root)))
            throw new InvalidOperationException("Choose the Basalt folder first.");
        basaltVersion.Text = "Basalt version: checking…";
        var installed = await Task.Run(() => Basalt.InstalledVersion(root));
        basaltRelease = await Basalt.LatestRelease();
        string current = installed == null ? "unknown" : installed.Version + (installed.Custom ? " (custom build)" : "");
        basaltVersion.Text = $"Basalt version: {current} · latest release {basaltRelease.Tag}";
        bool newer = installed == null || Basalt.IsNewer(basaltRelease.Tag, installed.Version);
        basaltDownload.Visible = newer;
        notice.Text = !newer ? "Basalt is up to date."
            : installed?.Custom == true ? $"Basalt {basaltRelease.Tag} is available. This PC runs a custom build based on {installed.Version}; it is left as it is. Download update opens the release page."
            : $"Basalt {basaltRelease.Tag} is available. Download update opens the release page; stop the server before installing it.";
    }
    void RefreshCustomEngines()
    {
        // Rebuilt only when the list (or a folder's state) changed; this runs on every Engines page visit.
        string signature = string.Join("|", store.Value.CustomEngines.Select(e => e.Name + "=" + e.Folder + "=" + CustomEngine.HasServer(e.Folder)));
        if (signature == customEngineSignature && customEngineList.Controls.Count > 0)
            return;
        customEngineSignature = signature;
        customEngineList.SuspendLayout();
        customEngineList.Clear();
        if (store.Value.CustomEngines.Count == 0)
            Add(customEngineList, Secondary("No custom engines yet. Choose Add custom engine… and pick a folder with llama-server.exe."));
        foreach (var entry in store.Value.CustomEngines.ToArray())
        {
            var row = HRow(new Padding(0, 2, 0, 2));
            var text = VStack();
            var title = Label(entry.Name);
            title.Font = new Font("Segoe UI Semibold", 9.75f);
            title.Margin = new Padding(0, 2, 0, 0);
            Add(text, title);
            bool ok = CustomEngine.HasServer(entry.Folder);
            var path = Secondary((ok ? "" : "Missing llama-server.exe · ") + entry.Folder);
            path.Margin = new Padding(0, 0, 0, 2);
            path.ForeColor = ok ? secondaryColor : cautionColor;
            Add(text, path);
            row.Add(text, Fit.Fill);
            row.Add(Button("Remove", () => RemoveCustomEngine(entry)), Fit.Auto);
            Add(customEngineList, row);
        }
        customEngineList.ResumeLayout(true);
        if (IsHandleCreated) ApplyTheme(customEngineList);
    }
    void RemoveCustomEngine(CustomEngine entry)
    {
        string id = CustomEngine.IdFor(entry.Name);
        if (runtime.Active && runtime.LiveProfile?.RuntimeId == id)
            throw new InvalidOperationException("Stop the server that uses this engine first.");
        var users = store.Value.Models.Where(p => p.RuntimeId == id).ToList();
        if (MessageBox.Show(this, $"Remove the custom engine \"{entry.Name}\"?" + (users.Count > 0 ? $"\n\n{users.Count} model(s) use it and will switch to the built-in llama.cpp." : "") + "\n\nIts folder and files are not deleted.", "Remove engine", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        foreach (var p in users)
            p.RuntimeId = EngineManager.BundledRuntime;
        store.Value.CustomEngines.Remove(entry);
        if (store.Value.DefaultRuntime == id)
            store.Value.DefaultRuntime = EngineManager.BundledRuntime;
        store.Save();
        SyncRuntimeChoices();
        if (Selected != null) { loading = true; runtimeChoice.SelectedItem = Selected.RuntimeId; loading = false; }
        RefreshCustomEngines();
        UpdateConditionalControls();
        notice.Text = $"Removed {entry.Name}. Its folder was left as it is.";
    }
    void AddEngine(string? kind) => _ = Run(() => RunEngineSetup(false, kind));
    async Task RunEngineSetup(bool firstRun, string? preselect)
    {
        using var dialog = new EngineDialog(pal, hardware, store.Value, firstRun, preselect);
        bool chose = dialog.ShowDialog(this) == DialogResult.OK && dialog.Choice != null;
        if (firstRun)
        {
            store.Value.WelcomeDone = true;
            if (!chose)
            {
                store.Save();
                notice.Text = "Setup skipped. The built-in llama.cpp picks the best backend automatically; add engines any time on the Engines page.";
                return;
            }
        }
        if (!chose)
            return;
        var choice = dialog.Choice!;
        if (firstRun && choice.ModelsDirectory.Length > 0 && Directory.Exists(choice.ModelsDirectory))
            store.Value.ModelsDirectory = modelFolder.Text = choice.ModelsDirectory;
        string message;
        switch (choice.Kind)
        {
            case "CUDA" or "Vulkan" or "CPU" or "Auto":
                store.Value.Backend = choice.Kind;
                message = choice.Kind == "CPU" ? "Engine set: llama.cpp on the processor." : $"Engine set: llama.cpp with {choice.Kind}.";
                break;
            case "Basalt":
                store.Value.BasaltRoot = basaltRoot.Text = choice.Folder;
                message = choice.Folder.Length == 0 ? "Basalt can be set up later on the Engines page." : "Basalt added. On the Model page, choose Basalt as a Flash-Next model's engine and pick its .basalt pack.";
                break;
            case "PolyStrata":
                store.Value.PolyStrataRoot = polyRoot.Text = choice.Folder;
                store.Value.PolyStrataEnabled = true;
                loading = true;
                showPoly.Checked = true;
                loading = false;
                message = "PolyStrata enabled. Choose it as the engine for a supported MoE model on the Model page.";
                break;
            default:
                var entry = new CustomEngine { Name = choice.Name, Folder = choice.Folder };
                store.Value.CustomEngines.Add(entry);
                if (firstRun || store.Value.Models.Count == 0)
                    store.Value.DefaultRuntime = CustomEngine.IdFor(entry.Name);
                message = $"Added {entry.Name}. Choose it in a model's Engine list" + (store.Value.DefaultRuntime == CustomEngine.IdFor(entry.Name) ? "; new models use it automatically." : ".");
                break;
        }
        store.Save();
        if (firstRun && choice.Kind is "CUDA" or "Vulkan" or "CPU")
        {
            loading = true;
            backendChoice.SelectedItem = store.Value.Backend;
            loading = false;
            backend = await ResolveBackend();
            probe = await engine.Inspect(backend, Selected);
            UpdateHardwareText();
            version.Text = VersionText();
        }
        SyncRuntimeChoices();
        SyncDefaultEngine();
        RefreshEngineStatus();
        UpdateConditionalControls();
        notice.Text = message;
    }
    // A chosen backend that cannot run here falls back to automatic detection instead of failing to open.
    async Task<string> ResolveBackend()
    {
        try { return await engine.Resolve(store.Value.Backend, hardware); }
        catch (Exception ex) when (store.Value.Backend != "Auto")
        {
            notice.Text = $"{store.Value.Backend} isn't available on this PC ({runtime.Redact(ex.Message).Split('\n')[0]}). Using automatic detection instead.";
            return await engine.Resolve("Auto", hardware);
        }
    }
    void SyncDefaultEngine()
    {
        bool was = loading;
        loading = true;
        defaultEngine.Items.Clear();
        defaultEngine.Items.Add(EngineManager.BundledRuntime);
        foreach (var e in store.Value.CustomEngines)
            defaultEngine.Items.Add(CustomEngine.IdFor(e.Name));
        defaultEngine.SelectedItem = defaultEngine.Items.Contains(store.Value.DefaultRuntime) ? store.Value.DefaultRuntime : EngineManager.BundledRuntime;
        loading = was;
    }
    async Task OpenUpdates()
    {
        suppressAutoUpdateCheck = true;
        try { tabs.SelectedTab = enginesPage; }
        finally { suppressAutoUpdateCheck = false; }
        await CheckForUpdates();
    }
    async Task CheckForUpdates()
    {
        if (updateCheckRunning) return;
        updateCheckRunning = true;
        installUpdateButton.Enabled = false;
        releaseNotesButton.Enabled = false;
        updateStatus.Text = "Checking official llama.cpp Windows x64 builds…";
        try
        {
            checkedEngineUpdate = await engine.CheckUpdates(backend);
            var check = checkedEngineUpdate;
            releaseNotesButton.Enabled = true;
            if (!EngineManager.TryBuildNumber(check.InstalledVersion, out int installedBuild))
            {
                updateStatus.Text = $"Found upstream build {check.Release.Tag}, but could not identify the installed bundled build. No changes were made.";
                return;
            }
            if (!check.UpdateAvailable)
            {
                updateStatus.Text = installedBuild >= (EngineManager.TryBuildNumber(check.Release.Tag, out int latestBuild) ? latestBuild : 0)
                    ? $"Bundled engine b{installedBuild} is current. Latest complete official Windows x64 build: {check.Release.Tag}."
                    : $"Installed bundled engine: b{installedBuild}. Latest complete official Windows x64 build: {check.Release.Tag}.";
                return;
            }
            long mib = (check.Release.DownloadBytes + 1024 * 1024 - 1) / (1024 * 1024);
            bool serverStopped = !runtime.Active && !runtime.ExistingServer;
            updateStatus.Text = $"Official upstream build {check.Release.Tag} is available (bundled engine b{installedBuild}). The Windows x64 CPU, CUDA 13, CUDA runtime libraries, and Vulkan files total about {mib:N0} MiB. "
                + (serverStopped ? "Choose Download and install update to stage it." : "Stop the live server before installing.");
            installUpdateButton.Enabled = serverStopped;
        }
        catch (Exception ex)
        {
            checkedEngineUpdate = null;
            updateStatus.Text = "Update check failed: " + runtime.Redact(ex.Message);
        }
        finally { updateCheckRunning = false; }
    }
    async Task InstallOnlineUpdate()
    {
        if (runtime.Active || runtime.ExistingServer)
            throw new InvalidOperationException("Stop the live server before installing an engine update.");
        var check = checkedEngineUpdate ?? throw new InvalidOperationException("Check for updates before installing.");
        if (!check.UpdateAvailable)
            throw new InvalidOperationException("There is no newer checked engine build to install.");
        long mib = (check.Release.DownloadBytes + 1024 * 1024 - 1) / (1024 * 1024);
        string selectedNote = Selected?.RuntimeId == EngineManager.FlashNextRuntime
            ? "\nYour selected model uses the separate experimental TurboQuant / Flash-Next runtime, which this update will leave unchanged."
            : "";
        var answer = MessageBox.Show(this,
            $"Download and stage official llama.cpp build {check.Release.Tag}?\n\nAbout {mib:N0} MiB will be downloaded for the Windows x64 CPU, CUDA 13, CUDA runtime, and Vulkan engines. Downloads are SHA-256 checked. The current engine and your model settings remain available if you roll back; starting a model is still required to confirm inference.{selectedNote}",
            "Install llama.cpp engine update", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (answer != DialogResult.Yes) return;
        installUpdateButton.Enabled = false;
        updateStatus.Text = "Starting verified engine download…";
        var progress = new Progress<EngineUpdateProgress>(p =>
        {
            if (p.Stage == "Downloading")
                updateStatus.Text = $"Downloading {p.AssetName}: {p.AssetDownloaded / 1024d / 1024d:0}/{p.AssetSize / 1024d / 1024d:0} MiB · total {p.TotalDownloaded / 1024d / 1024d:0}/{p.TotalSize / 1024d / 1024d:0} MiB.";
            else
                updateStatus.Text = p.Stage + "…";
        });
        try
        {
            await engine.InstallUpdate(check, backend, progress);
            checkedEngineUpdate = null;
            if (Selected?.RuntimeId != EngineManager.FlashNextRuntime)
                probe = await engine.Inspect(backend, Selected);
            version.Text = VersionText();
            RefreshEstimates();
            updateStatus.Text = $"Build {check.Release.Tag} is staged as the bundled engine. Start your model to confirm inference; Roll Back engine restores the previous build." + selectedNote;
        }
        catch (Exception ex)
        {
            updateStatus.Text = "The update was not installed: " + runtime.Redact(ex.Message);
            throw;
        }
        finally
        {
            installUpdateButton.Enabled = checkedEngineUpdate?.UpdateAvailable == true && !runtime.Active && !runtime.ExistingServer;
        }
    }
    void UpdateHardwareText()
    {
        Gpu? activeGpu = backend == "CUDA"
            ? hardware.Gpus.FirstOrDefault(g => g.Vendor == "NVIDIA")
            : backend == "Vulkan" ? hardware.Gpus.FirstOrDefault() : null;
        hardwareText.Text = activeGpu == null
            ? $"{backend} · {hardware.Cpu} · {hardware.PhysicalCores}C/{hardware.LogicalCores}T · {hardware.RamGiB:0} GB RAM ({hardware.AvailableRamGiB:0} available)"
            : $"{backend} · {activeGpu.Name} · {activeGpu.TotalGiB:0.#} GB dedicated VRAM ({activeGpu.AvailableGiB:0.#} available{(activeGpu.Estimated ? ", estimated" : "")}) · {hardware.PhysicalCores}C/{hardware.LogicalCores}T · {hardware.RamGiB:0} GB RAM ({hardware.AvailableRamGiB:0} available)";
        tips.SetToolTip(hardwareText, hardwareText.Text);
        if (hardwareText.Tag is Label details)
            details.Text = string.Join("\n", hardware.Gpus.Select(g => $"{g.Name} · {g.Vendor} · {g.TotalGiB:0.#} GB dedicated VRAM{(g.Estimated ? " (availability estimated)" : $" · {g.AvailableGiB:0.#} GB available")}")) +
                $"\n{hardware.Cpu} · {hardware.PhysicalCores} physical cores / {hardware.LogicalCores} logical processors\nSystem RAM · {hardware.RamGiB:0.#} GB installed · {hardware.AvailableRamGiB:0.#} GB currently available";
    }
    string VersionText()
    {
        // llama-server prints "version: 0.1.2-dev (build 10549, commit b2e5e9b28)"; older builds print "version: 10549 (...)".
        string build = probe == null ? "unavailable"
            : Regex.Match(probe.Version, @"build\s+(\d+)") is { Success: true } b ? "b" + b.Groups[1].Value
            : Regex.Match(probe.Version, @"version:\s*(\d+)\s*\(") is { Success: true } m ? "b" + m.Groups[1].Value
            : probe.Version.Trim().Split('\n')[0];
        return $"Engine build {build} · {backend} backend · selected model uses {EngineName(Selected?.RuntimeId ?? EngineManager.BundledRuntime)}";
    }
    string Report() => runtime.Report(hardware, info, Selected, backend, probe?.Version ?? "Unknown");
    async Task Initialize()
    {
        await Run(async () =>
        {
            hardware = await Task.Run(Hardware.Detect);
            UpdateHardwareText();
            RefreshEngineStatus();
            // First launch: choose the engine before the backend is resolved, so the choice is used right away.
            if (!store.Value.WelcomeDone)
                await RunEngineSetup(true, null);
            backend = await ResolveBackend();
            UpdateHardwareText();
            probe = await engine.Inspect(backend);
            version.Text = VersionText();
            await ReloadModels();
            if (!runtime.ExistingServer && string.IsNullOrWhiteSpace(notice.Text)) notice.Text = store.Notice;
            var recovery = store.Value.StartupRecovery; if (!runtime.ExistingServer && !runtime.Active && Selected != null && store.Value.StartupFailed && recovery?.ProfileId == Selected.Id) { string cause = string.IsNullOrWhiteSpace(recovery.FirstActionableCause) ? "No actionable stderr line was captured." : recovery.FirstActionableCause; notice.Text = "Previous startup failed for this model. Safe settings keep its runtime and GPU backend while leaving the saved profile intact."; var choice = MessageBox.Show(this, $"Previous startup failed for this model.\n\nFirst cause: {cause}\nRuntime: {recovery.RuntimeId}\nBackend: {recovery.Backend}\n\nYes: retry with temporary safe settings\nNo: keep settings and retry using Start\nCancel: stay stopped", "Startup recovery", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning); if (choice == DialogResult.Yes) await Start(true); } });
        loading = false;
        UpdateStatus();
        RefreshTailscale();
        ShowOllamaChoice(store.Value.OllamaEnabled, store.Value.OllamaTailscale);
        if (store.Value.ModelTailscale)
            await Run(async () => { await ModelTailnet.Share(store.Value); store.Save(); });
        if (store.Value.OllamaEnabled)
            try { await Ollama.Apply(store.Value, store.Root, true, store.Value.OllamaTailscale); } // also repairs the sign-in entry after an update
            catch (Exception ex) { notice.Text = "Ollama API: " + ex.Message; }
        await RefreshOllama();
        ollamaTimer.Start();
        await CleanVisionCache();                       // also what a crashed or killed server left
    }
    // PolyStrata's encoded pictures of servers that have ended (PolyStrata.CleanVisionCache), at Stop and on opening.
    // A stopped server's processes take a moment to end, and its directory is kept until they have: after a Stop
    // this tries again for up to 10 seconds.
    async Task CleanVisionCache(bool afterStop = false)
    {
        try
        {
            string root = store.Value.PolyStrataRoot;
            for (int i = 0; i < (afterStop ? 20 : 1); i++)
            {
                if (await Task.Run(() => PolyStrata.CleanVisionCache(root)) == 0)
                    return;
                await Task.Delay(500);
            }
        }
        catch (Exception) { } // best effort: it never holds up Stop
    }
    // The Ollama API switches: running and starting at sign-in while on, shared on the tailnet while both are on.
    async Task SetOllama(bool on, bool share)
    {
        if (ollamaSyncing)
            return;
        ShowOllamaChoice(on, share);
        ollama.Enabled = ollamaQuick.Enabled = ollamaTailnet.Enabled = false;
        ollamaStatus.Text = on ? "Ollama API: starting…" : "Ollama API: stopping…";
        try
        {
            await Ollama.Apply(store.Value, store.Root, on, share);
            store.Value.OllamaEnabled = on;
            store.Value.OllamaTailscale = share;
            store.Save();
            tailnet = null;
        }
        catch (Exception ex)
        {
            ShowOllamaChoice(store.Value.OllamaEnabled, store.Value.OllamaTailscale);
            ShowError(ex);
        }
        finally { ollama.Enabled = ollamaQuick.Enabled = ollamaTailnet.Enabled = true; }
        await RefreshOllama();
    }
    void ShowOllamaChoice(bool on, bool share)
    {
        ollamaSyncing = true;
        ollama.Checked = ollamaQuick.Checked = on;
        ollamaTailnet.Checked = share;
        ollamaRequireKey.Checked = store.Value.OllamaRequireKey;
        ollamaDebug.Checked = store.Value.OllamaDebugLog;
        ollamaKey.Text = store.Value.OllamaRequireKey ? store.Value.OllamaApiKey : "";
        ollamaSyncing = false;
    }
    async Task RefreshOllama()
    {
        if (ollamaRefreshing || closing)
            return;
        ollamaRefreshing = true;
        try
        {
            var s = store.Value;
            var state = await Ollama.Probe();
            if (s.OllamaEnabled && state == Ollama.Status.Off && DateTime.UtcNow - ollamaRestarted > TimeSpan.FromSeconds(30))
            {
                ollamaRestarted = DateTime.UtcNow;     // on, but it was stopped or ended: start it again
                try { state = await Ollama.Start(s, store.Root); } catch (Exception) { }
            }
            if (s.OllamaEnabled && s.OllamaTailscale && tailnet == null)
                tailnet = await Ollama.TailnetAddress();
            string model = runtime.Active || runtime.ExistingServer ? $"the server on port {s.Port}" : $"port {s.Port} (start a model to use it)";
            ollamaStatus.Text = state switch
            {
                Ollama.Status.PortTaken => $"Ollama API: port {Ollama.Port} is used by another program (Ollama itself?). Quit it to use this one.",
                Ollama.Status.Running when !s.OllamaEnabled => $"Ollama API: running, started outside this launcher. Turn it on here to manage it.",
                Ollama.Status.Running => $"Ollama API: on at {Ollama.LocalUrl}, answering through {model}" + (s.OllamaRequireKey ? ", API key required" : "") + (s.OllamaDebugLog ? ", DEBUG LOG ON" : "")
                    + (!s.OllamaTailscale ? "" : tailnet?.Problem is string problem ? "\nTailscale: " + problem : $"\nTailscale: {tailnet!.Url}" + (tailnet.Name.Length > 0 ? $" or http://{tailnet.Name}:{Ollama.Port}" : "")),
                _ => s.OllamaEnabled ? "Ollama API: on, but not answering. Open the Ollama log to see why." : "Ollama API: off",
            };
        }
        catch (Exception ex) { ollamaStatus.Text = "Ollama API: " + ex.Message; }
        finally { ollamaRefreshing = false; }
    }
    async Task ReloadModels()
    {
        var pruned = store.PruneMissingModels(runtime?.LiveProfile?.Id);
        loading = true;
        models.Items.Clear();
        foreach (var p in store.Value.Models)
            models.Items.Add(p);
        models.SelectedItem = store.Value.Models.FirstOrDefault(p => p.Id == store.Value.ActiveId) ?? store.Value.Models.FirstOrDefault();
        theme.SelectedItem = store.Value.Theme;
        backendChoice.SelectedItem = store.Value.Backend;
        showTurbo.Checked = store.Value.ShowTurboQuantControls;
        showPoly.Checked = store.Value.PolyStrataEnabled;
        polyRoot.Text = store.Value.PolyStrataRoot;
        basaltRoot.Text = store.Value.BasaltRoot;
        SyncRuntimeChoices();
        defaultMemorySafety.SelectedItem = store.Value.DefaultMemorySafety;
        port.Value = store.Value.Port;
        lan.Checked = store.Value.Lan;
        key.Text = store.Value.ApiKey;
        modelFolder.Text = store.Value.ModelsDirectory;
        autoCheck.Checked = store.Value.CheckUpdatesOnOpen;
        SyncDefaultEngine();
        // The Qwen Code bridge only applies to PCs with an imported Qwen launcher configuration.
        qwenCard.Visible = File.Exists(store.Value.LegacyCommon) || File.Exists(store.Value.LegacyDesktop);
        loading = false;
        RefreshEngineStatus();
        ShowEmptyState();
        await SelectModel();
        if (pruned.Count > 0)
            notice.Text = $"Removed {pruned.Count} model(s) whose GGUF file no longer exists: {string.Join(", ", pruned.Select(p => p.Name))}. "
                + "Their settings are kept in removed-models.json; adding the same file again starts a fresh profile.";
    }
    async Task SelectModel()
    {
        ShowEmptyState();
        if (Selected == null)
        {
            info = null;
            modelTitle.Text = "Add a GGUF to begin";
            RefreshEstimates();
            UpdateConditionalControls();
            UpdateStatus();
            return;
        }
        store.Value.ActiveId = Selected.Id;
        info = null;
        LoadProfile();
        try
        {
            var p = Selected;
            var parsed = await Task.Run(() => GgufInspector.Inspect(p.Path));
            if (Selected != p)
                return;
            info = parsed;
            bool repaired = engine.EnsureCompatibleProfile(p, parsed);
            backend = await engine.Resolve(store.Value.Backend, hardware, p);
            probe = await engine.Inspect(backend, p);
            UpdateHardwareText();
            version.Text = VersionText();
            LoadProfile();
            store.Save();
            if (repaired)
                notice.Text = "Flash-Next profile repaired: compatible CUDA runtime selected and the verified local MTP head attached when available.";
            if (await runtime.ReconcileExisting(store.Value.Models))
            {
                if (runtime.LiveProfile is Profile live && live != Selected)
                {
                    models.SelectedItem = live;
                    return;
                }
                notice.Text = runtime.Active && runtime.State.Contains("Live", StringComparison.Ordinal)
                    ? "Live server reconnected. The model stayed loaded while the launcher was closed."
                    : runtime.Error;
            }
            RefreshEstimates();
            UpdateStatus();
        }
        catch (Exception ex) { modelTitle.Text = Selected.Name; modelDetails.Text = "Model unavailable: " + runtime.Redact(ex.Message); compatibility.Text = "! Select an accessible, valid GGUF model."; }
    }
    // Without models, the Model card explains how to add one instead of showing empty settings.
    void ShowEmptyState()
    {
        bool none = store.Value.Models.Count == 0;
        emptyModels.Visible = none;
        modelDetailsRow.Visible = !none;
    }
    void LoadProfile()
    {
        ShowEmptyState();
        if (Selected == null)
            return;
        restorePreset.Visible = Selected.KnownGood != null;
        loading = true;
        var p = Selected;
        var o = p.Options;
        context.Maximum = info?.NativeContext > 0 ? Math.Min(1048576, Math.Max(info.NativeContext, o.I("ContextSize"))) : 1048576;
        context.Value = Math.Clamp(o.I("ContextSize", 4096), 256, context.Maximum);
        llamaParallel.Value = Math.Clamp(o.I("ParallelSlots", 1), 1, 128);
        polyParallel.Value = Math.Clamp(o.I("PolyParallelSlots", PolyStrata.ReadParallel(p.PolyStrataConfig)), 1, 8);
        EnsureComboValue(cacheK, o.S("CacheK"));
        EnsureComboValue(cacheV, o.S("CacheV"));
        gpu.Text = o.S("GpuLayers");
        if (gpu.IsHandleCreated) { gpu.SelectionStart = gpu.Text.Length; gpu.SelectionLength = 0; }
        threads.Value = Math.Clamp(o.I("Threads", 8), 1, 1024);
        batch.Value = Math.Clamp(o.I("BatchSize", 512), 1, 65536);
        ubatch.Value = Math.Clamp(o.I("UBatchSize", 128), 1, 65536);
        draft.Value = Math.Clamp(o.I("MtpDraftMax", 3), 1, 16);
        draftPMin.Value = Math.Clamp((decimal)o.D("MtpDraftPMin", 0.75), draftPMin.Minimum, draftPMin.Maximum);
        draftSampling.Checked = o.S("MtpDraftSampling") == "probabilistic";
        thinking.Checked = o.B("Thinking");
        temperature.Value = Math.Clamp((decimal)o.D("Temperature", 0.7), 0, 2);
        flash.Checked = o.B("FlashAttention");
        ramKv.Checked = o.B("ContextCacheInRam");
        cpuMoe.Checked = o.B("CpuMoe");
        cpuMoeLayers.Value = Math.Clamp(o.I("CpuMoeLayers", 36), (int)cpuMoeLayers.Minimum, (int)cpuMoeLayers.Maximum);
        moeCache.Checked = o.B("MoeCacheEnabled");
        workingSetLimit.Value = Math.Clamp(o.I("WorkingSetLimitGiB", 65), (int)workingSetLimit.Minimum, (int)workingSetLimit.Maximum);
        effort.SelectedItem = o.S("ReasoningEffort", "medium");
        acceleration.SelectedItem = p.Acceleration;
        ngram.SelectedItem = p.Ngram;
        draftModel.Text = p.DraftModel;
        basaltPack.Text = p.BasaltPack;
        runtimeChoice.SelectedItem = p.RuntimeId;
        turboEnabled.Checked = p.TurboQuantEnabled;
        turboPreset.SelectedItem = p.TurboQuantPreset;
        turboK.SelectedItem = p.TurboQuantK;
        turboV.SelectedItem = p.TurboQuantV;
        showTurbo.Checked = store.Value.ShowTurboQuantControls;
        MemoryPolicies.Normalize(p);
        memorySafety.SelectedItem = p.MemorySafety;
        customVramReserve.Value = Math.Clamp((decimal)p.CustomVramReserveGiB, customVramReserve.Minimum, customVramReserve.Maximum);
        customRamReserve.Value = Math.Clamp((decimal)p.CustomRamReserveGiB, customRamReserve.Minimum, customRamReserve.Maximum);
        customAction.SelectedItem = p.CustomOverBudgetAction;
        custom.Text = p.CustomArguments;
        projector.Text = o.S("Mmproj");
        template.Text = o.S("ChatTemplate");
        modelTitle.Text = p.Name;
        modelDetails.Text = info?.Summary ?? "Inspecting GGUF header…";
        thinking.Enabled = p.Legacy || info?.Reasoning == true;
        effort.Enabled = thinking.Enabled;
        SyncContextSlider();
        SyncCacheSlider(cacheK, cacheKSlider);
        SyncCacheSlider(cacheV, cacheVSlider);
        customMemoryPanel.Visible = p.MemorySafety == "Custom";
        UpdateConditionalControls();
        loading = false;
        RefreshEstimates();
    }
    void CaptureProfile()
    {
        if (loading)
            return;
        var s = store.Value;
        s.Port = (int)port.Value;
        s.Lan = lan.Checked;
        s.ModelsDirectory = modelFolder.Text;
        if (Selected == null)
            return;
        var p = Selected;
        var o = p.Options;
        if (p.Legacy && o.I("ContextSize") != (int)context.Value)
            o["MaxOutputTokens"] = Math.Min(32768, Math.Max(128, (int)context.Value / 2));
        o["ContextSize"] = (int)context.Value;
        o["ParallelSlots"] = (int)llamaParallel.Value;
        o["PolyParallelSlots"] = (int)polyParallel.Value;
        o["CacheK"] = cacheK.Text;
        o["CacheV"] = cacheV.Text;
        o["GpuLayers"] = gpu.Text;
        o["Threads"] = (int)threads.Value;
        o["ThreadsBatch"] = (int)threads.Value;
        o["BatchSize"] = (int)batch.Value;
        o["UBatchSize"] = (int)ubatch.Value;
        o["MtpDraftMax"] = (int)draft.Value;
        o["MtpDraftPMin"] = (double)draftPMin.Value;
        o["MtpDraftSampling"] = draftSampling.Checked ? "probabilistic" : "greedy";
        p.SetThinking(thinking.Checked);
        o["Temperature"] = (double)temperature.Value;
        o["FlashAttention"] = flash.Checked;
        o["ContextCacheInRam"] = ramKv.Checked;
        o["CpuMoe"] = cpuMoe.Checked;
        o["CpuMoeLayers"] = (int)cpuMoeLayers.Value;
        o["MoeCacheEnabled"] = moeCache.Checked;
        o["WorkingSetLimitGiB"] = (int)workingSetLimit.Value;
        o["ReasoningEffort"] = effort.Text;
        o["Mmproj"] = projector.Text;
        o["ChatTemplate"] = template.Text;
        p.Acceleration = acceleration.Text;
        p.Ngram = ngram.Text;
        p.DraftModel = draftModel.Text;
        p.BasaltPack = basaltPack.Text.Trim();
        s.BasaltRoot = basaltRoot.Text.Trim();
        p.RuntimeId = runtimeChoice.Text;
        p.TurboQuantEnabled = turboEnabled.Checked;
        p.TurboQuantPreset = turboPreset.Text;
        p.TurboQuantK = turboK.Text;
        p.TurboQuantV = turboV.Text;
        p.CustomArguments = custom.Text;
        p.MemorySafety = memorySafety.Text;
        p.CustomVramReserveGiB = (double)customVramReserve.Value;
        p.CustomRamReserveGiB = (double)customRamReserve.Value;
        p.CustomOverBudgetAction = customAction.Text;
    }
    void Save()
    {
        CaptureProfile();
        store.Save();
        unsaved = false;
        restartRequired = runtime.Active;
        notice.Text = runtime.Active ? "Settings saved. The running server still uses the previous values; Restart to apply." : "Settings saved.";
        UpdateStatus();
    }
    void Changed()
    {
        if (loading)
            return;
        CaptureProfile();
        unsaved = true;
        if (runtime.Active)
            restartRequired = true;
        UpdateConditionalControls();
        RefreshEstimates();
    }
    void CacheChanged(ComboBox combo, CompactTrackBar slider)
    {
        if (loading) return;
        bool wasTurbo = turboEnabled.Checked;
        if (wasTurbo)
        {
            loading = true;
            try { turboEnabled.Checked = false; }
            finally { loading = false; }
        }
        SyncCacheSlider(combo, slider);
        Changed();
        if (wasTurbo)
            notice.Text = "TurboQuant turned off; ordinary K/V cache values are selected. " +
                (runtime.Active ? "Save and Restart to apply." : "Save to keep these settings.");
    }
    void MemorySafetyChanged(object? sender, EventArgs e)
    {
        if (loading || Selected == null || string.IsNullOrWhiteSpace(memorySafety.Text))
            return;
        customMemoryPanel.Visible = memorySafety.Text == "Custom";
        Changed();
    }
    void DefaultMemorySafetyChanged(object? sender, EventArgs e)
    {
        if (loading || string.IsNullOrWhiteSpace(defaultMemorySafety.Text))
            return;
        store.Value.DefaultMemorySafety = defaultMemorySafety.Text;
        store.Save();
        notice.Text = "Default updated for newly added models. Existing profiles were not changed.";
    }
    void ShowTurboChanged(object? sender, EventArgs e)
    {
        if (loading)
            return;
        if (!showTurbo.Checked && store.Value.Models.Any(p => p.TurboQuantEnabled))
        {
            var answer = MessageBox.Show(this,
                "TurboQuant is enabled for one or more models. Hiding the controls will turn TurboQuant off for those models. Their ordinary K/V cache settings will be preserved and become effective again.\n\nContinue?",
                "Hide TurboQuant controls",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (answer != DialogResult.Yes)
            {
                loading = true;
                showTurbo.Checked = true;
                loading = false;
                return;
            }
            foreach (var profile in store.Value.Models)
                profile.TurboQuantEnabled = false;
            if (Selected != null)
                turboEnabled.Checked = false;
        }
        store.Value.ShowTurboQuantControls = showTurbo.Checked;
        store.Save();
        UpdateConditionalControls();
        RefreshEstimates();
        notice.Text = showTurbo.Checked ? "TurboQuant controls are now available per model on the Model page." : "TurboQuant controls hidden; ordinary K/V settings remain unchanged.";
    }
    // The Engine list: built-in llama.cpp, the experimental fork when installed, Basalt, PolyStrata while
    // its switch is on, and every custom build. A model's current engine always stays listed.
    void SyncRuntimeChoices()
    {
        var wanted = new List<string> { EngineManager.BundledRuntime };
        if (Directory.Exists(engine.ExperimentalRoot) || Selected?.RuntimeId == EngineManager.FlashNextRuntime)
            wanted.Add(EngineManager.FlashNextRuntime);
        wanted.Add(Basalt.Runtime);
        if (showPoly.Checked || Selected?.RuntimeId == PolyStrata.Runtime)
            wanted.Add(PolyStrata.Runtime);
        wanted.AddRange(store.Value.CustomEngines.Select(e => CustomEngine.IdFor(e.Name)));
        if (Selected != null && !wanted.Contains(Selected.RuntimeId))
            wanted.Add(Selected.RuntimeId);
        if (runtimeChoice.Items.Cast<object>().Select(x => x.ToString()).SequenceEqual(wanted))
            return;
        bool was = loading;
        loading = true;
        object? current = runtimeChoice.SelectedItem;
        runtimeChoice.Items.Clear();
        runtimeChoice.Items.AddRange(wanted.Cast<object>().ToArray());
        runtimeChoice.SelectedItem = current is string id && wanted.Contains(id) ? id : Selected?.RuntimeId;
        loading = was;
    }
    void ShowPolyChanged(object? sender, EventArgs e)
    {
        if (loading)
            return;
        if (!showPoly.Checked && runtime.Active && Selected?.RuntimeId == PolyStrata.Runtime)
        {
            loading = true;
            showPoly.Checked = true;
            loading = false;
            notice.Text = "Stop the PolyStrata server before turning PolyStrata off.";
            return;
        }
        if (!showPoly.Checked)
            foreach (var p in store.Value.Models.Where(p => p.RuntimeId == PolyStrata.Runtime))
                p.RuntimeId = EngineManager.BundledRuntime;
        if (showPoly.Checked && string.IsNullOrWhiteSpace(store.Value.PolyStrataRoot) && Directory.Exists(@"C:\AI\PolyStrata"))
            polyRoot.Text = store.Value.PolyStrataRoot = @"C:\AI\PolyStrata";
        store.Value.PolyStrataEnabled = showPoly.Checked;
        store.Save();
        loading = true;
        SyncRuntimeChoices();
        if (Selected != null)
            runtimeChoice.SelectedItem = Selected.RuntimeId;
        loading = false;
        UpdateConditionalControls();
        RefreshEngineStatus();
        notice.Text = showPoly.Checked
            ? PolyStrata.InstallProblem(store.Value.PolyStrataRoot) ?? "PolyStrata can now be chosen as the engine on the Model page for the MoE models it supports."
            : "PolyStrata turned off. Models that used it are back on the built-in llama.cpp.";
    }
    async Task ChangeRuntime()
    {
        if (Selected == null || string.IsNullOrWhiteSpace(runtimeChoice.Text))
            return;
        if (runtime.Active)
            throw new InvalidOperationException("Stop the server before changing this model's runtime.");
        var p = Selected;
        // A Flash-Next build pinned to the experimental llama.cpp runtime may still move to PolyStrata; only the
        // bundled llama.cpp choice is redirected to the runtime that can load it.
        if (CustomEngine.IsCustom(runtimeChoice.Text))
        {
            string? folder = store.Value.CustomEngines.FirstOrDefault(e => CustomEngine.IdFor(e.Name) == runtimeChoice.Text)?.Folder;
            if (!CustomEngine.HasServer(folder))
            {
                loading = true; runtimeChoice.SelectedItem = p.RuntimeId; loading = false;
                notice.Text = $"{CustomEngine.NameOf(runtimeChoice.Text)}: llama-server.exe was not found in {folder}. Check the folder on the Engines page.";
                return;
            }
        }
        // A user's own build is their explicit choice, so it is not redirected either.
        if (info != null && engine.RequiresExperimentalRuntime(info) && !CustomEngine.IsCustom(runtimeChoice.Text)
            && runtimeChoice.Text != EngineManager.FlashNextRuntime && runtimeChoice.Text != PolyStrata.Runtime && runtimeChoice.Text != Basalt.Runtime)
        {
            p.RuntimeId = EngineManager.FlashNextRuntime;
            loading = true;
            runtimeChoice.SelectedItem = p.RuntimeId;
            loading = false;
            notice.Text = info.UsesValidatedFlashNextPreset
                ? "This Flash-Next build keeps its measured experimental runtime preset. The compatible CUDA runtime remains selected."
                : "The installed bundled engine does not recognize qwen4exp. Update the engine, or keep the compatible CUDA runtime selected.";
            UpdateConditionalControls();
            UpdateStatus();
            return;
        }
        if ((runtimeChoice.Text == "Bundled" || CustomEngine.IsCustom(runtimeChoice.Text)) && p.TurboQuantEnabled)
        {
            var answer = MessageBox.Show(this,
                "This model currently uses TurboQuant, which requires the experimental native runtime. Disable TurboQuant and restore its ordinary K/V cache settings?",
                "Change model runtime",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (answer != DialogResult.Yes)
            {
                loading = true;
                runtimeChoice.SelectedItem = p.RuntimeId;
                loading = false;
                return;
            }
            p.TurboQuantEnabled = false;
            turboEnabled.Checked = false;
        }
        if (runtimeChoice.Text == PolyStrata.Runtime)
        {
            string? problem = PolyStrata.InstallProblem(store.Value.PolyStrataRoot)
                ?? (info != null && !PolyStrata.Supports(info)
                    ? $"PolyStrata does not run the {info.Architecture} architecture. Dense models such as Qwen3.8-27B and NVFP4 files stay on llama.cpp."
                    : null);
            string? config = problem == null ? PolyStrata.MatchConfig(store.Value.PolyStrataRoot, p.Path) : null;
            if (problem == null && config == null)
                problem = "PolyStrata has no run config for this model yet. Run its setup for this GGUF (python setup.py --gguf-dir <folder> --no-start), then choose the runtime again.";
            if (problem != null)
            {
                loading = true;
                runtimeChoice.SelectedItem = p.RuntimeId;
                loading = false;
                notice.Text = problem;
                return;
            }
            p.PolyStrataConfig = config!;
            if (p.TurboQuantEnabled)
            {
                p.TurboQuantEnabled = false;
                turboEnabled.Checked = false;
            }
        }
        if (runtimeChoice.Text == Basalt.Runtime)
        {
            string root = basaltRoot.Text.Trim(), pack = basaltPack.Text.Trim();
            string? problem = Basalt.Problem(root, pack, info);
            if (problem != null)
            {
                loading = true; runtimeChoice.SelectedItem = p.RuntimeId; loading = false;
                notice.Text = problem; return;
            }
            store.Value.BasaltRoot = root;
            p.BasaltPack = pack;
            p.TurboQuantEnabled = false;
            turboEnabled.Checked = false;
        }
        p.RuntimeId = runtimeChoice.Text;
        backend = await engine.Resolve(store.Value.Backend, hardware, p);
        probe = await engine.Inspect(backend, p);
        unsaved = true;
        UpdateHardwareText();
        version.Text = VersionText();
        UpdateConditionalControls();
        RefreshEstimates();
        UpdateStatus();
    }
    void ShowPolicyNotice(Control source, string policy, bool futureProfiles)
    {
        if (policy == "Balanced" || !Visible)
            return;
        string scope = futureProfiles ? "New models will use this default. " : "This model will use this policy. ";
        string message = policy switch
        {
            "Relaxed" => scope + "Relaxed keeps a smaller reserve than the recommended Balanced policy.",
            "Strict" => scope + "Strict keeps more memory in reserve and can block a launch that Balanced would allow.",
            "Off" => scope + "Off disables launcher memory admission checks. The estimate remains visible, but allocation can still fail or become slow.",
            "Custom" => scope + "Custom uses the reserves and over-budget action you set below.",
            _ => ""
        };
        if (message.Length == 0)
            return;
        policyPopover?.Close();
        var screen = Screen.FromControl(source).WorkingArea;
        var anchor = source.PointToScreen(new Point(0, source.Height));
        policyPopover = new HelpPopover(message, Font, surfaceColor, foregroundColor, borderColor, screen, anchor, 2200);
        policyPopover.FormClosed += (_, _) => policyPopover = null;
        policyPopover.Show(this);
    }
    void UpdateConditionalControls()
    {
        mtpPanel.Visible = true;
        ngramPanel.Visible = true;
        bool mtpActive = acceleration.Text != "Off";
        draft.Enabled = mtpActive;
        draftPMin.Enabled = mtpActive;
        draftSampling.Enabled = mtpActive;
        draftModel.Enabled = mtpActive;
        ngram.Enabled = true;
        bool turboVisible = showTurbo.Checked;
        bool flashNext = info?.Architecture.Equals("qwen4exp", StringComparison.OrdinalIgnoreCase) == true;
        turboPanel.Visible = turboVisible;
        turboEnabled.Enabled = runtimeChoice.Text == EngineManager.FlashNextRuntime;
        // Presets are configuration choices, so users can inspect/stage them
        // before enabling TurboQuant. Only their application depends on the toggle.
        turboPreset.Enabled = turboVisible;
        turboCustomPanel.Visible = turboVisible && turboPreset.Text == "Custom";
        turboK.Enabled = turboCustomPanel.Visible && !flashNext;
        turboV.Enabled = turboCustomPanel.Visible;
        cacheK.Enabled = cacheV.Enabled = true;
        cacheKSlider.Enabled = cacheVSlider.Enabled = true;
        customMemoryPanel.Visible = memorySafety.Text == "Custom";
        cpuMoeLayers.Enabled = workingSetLimit.Enabled = moeCache.Enabled = cpuMoe.Checked && flashNext;
        // PolyStrata uses its own speculation, expert placement and RAM budget; only context and KV cache apply.
        if (runtimeChoice.Text == PolyStrata.Runtime || runtimeChoice.Text == Basalt.Runtime)
        {
            acceleration.Enabled = draft.Enabled = draftPMin.Enabled = draftSampling.Enabled = draftModel.Enabled = ngram.Enabled = false;
            turboEnabled.Enabled = turboPreset.Enabled = turboK.Enabled = turboV.Enabled = false;
            cpuMoe.Enabled = cpuMoeLayers.Enabled = workingSetLimit.Enabled = moeCache.Enabled = false;
        }
        else
        {
            acceleration.Enabled = true;
            cpuMoe.Enabled = true;
        }
        string id = Selected?.RuntimeId ?? runtimeChoice.Text;
        bool external = id == PolyStrata.Runtime || id == Basalt.Runtime;
        // Only the slot count of the engine in use is shown, under one name.
        llamaSlotsRow.Visible = !external;
        polySlotsRow.Visible = external;
        basaltPackRow.Visible = id == Basalt.Runtime;
        moeCard.Enabled = !external;
        engineHint.Text = Selected == null ? "" : id switch
        {
            EngineManager.BundledRuntime => $"Official llama.cpp build, running on {backend}. Works with every GGUF model.",
            EngineManager.FlashNextRuntime => "A pinned llama.cpp fork with TurboQuant KV-cache compression, used for Qwen3.8 Flash-Next.",
            Basalt.Runtime => "For NVIDIA RTX 50-series (Blackwell) graphics cards only. Runs this model from its .basalt pack below; set up the Basalt folder on the Engines page.",
            PolyStrata.Runtime => "Experimental engine for large MoE models. It manages its own expert placement and memory.",
            _ when CustomEngine.IsCustom(id) => "Your llama.cpp build in " + (store.Value.CustomEngines.FirstOrDefault(e => CustomEngine.IdFor(e.Name) == id)?.Folder ?? "an unknown folder") + ".",
            _ => ""
        };
    }
    void SyncContextSlider()
    {
        int current = (int)context.Value;
        int maximum = (int)context.Maximum;
        int[] landmarks = { 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536, 98304, 131072, 196608, 262144, 524288, 1048576 };
        contextStops = landmarks.Where(x => x <= maximum).Append(current).Distinct().OrderBy(x => x).ToList();
        bool wasLoading = loading;
        loading = true;
        contextSlider.Maximum = Math.Max(1, contextStops.Count - 1);
        contextSlider.Value = Math.Max(0, contextStops.IndexOf(current));
        loading = wasLoading;
    }
    void SyncCacheSlider(ComboBox combo, CompactTrackBar sliderControl)
    {
        int index = Array.FindIndex(cacheTypes, x => x.Equals(combo.Text, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return;
        bool wasLoading = loading;
        loading = true;
        sliderControl.Value = index;
        loading = wasLoading;
    }
    static void EnsureComboValue(ComboBox combo, string value)
    {
        if (combo.Items.Cast<object>().All(x => !string.Equals(combo.GetItemText(x), value, StringComparison.OrdinalIgnoreCase)))
            combo.Items.Add(value);
        combo.SelectedItem = combo.Items.Cast<object>().FirstOrDefault(x => string.Equals(combo.GetItemText(x), value, StringComparison.OrdinalIgnoreCase));
    }
    void RefreshEstimates()
    {
        if (Selected == null || info == null)
        {
            memoryFit.Text = Selected == null ? "Add a model to see whether it fits in memory." : "Reading the model…";
            memoryFit.ForeColor = secondaryColor;
            memory.Text = "";
            vramBar.Set("Graphics memory (VRAM)", "–", 0, Tone.Neutral);
            ramBar.Set("System memory (RAM)", "–", 0, Tone.Neutral);
            compatibilitySummary.Text = "No model selected.";
            accelerationSummary.Text = "";
            placement.Text = "";
            contextHint.Text = "";
            return;
        }
        var e = MemoryEstimator.Calculate(info, Selected, hardware, backend);
        bool runningCurrent = runtime.Active && runtime.LiveProfile?.Id == Selected.Id && runtime.LiveContext is > 0;
        bool tight = e.State == "Tight";
        var policy = MemoryPolicies.Resolve(Selected);
        Tone fitTone = runningCurrent ? Tone.Positive : e.OverBudget ? Tone.Danger : tight ? Tone.Caution : Tone.Positive;
        memoryFit.ForeColor = pal.ToneColor(fitTone);
        memoryFit.Text = runningCurrent ? "✓ Running. Its memory is already allocated."
            : e.OverBudget ? "✕ Likely to exceed available memory. Lower the context or cache precision, or use Auto Optimize."
            : tight ? "! Tight fit. It should run, with little headroom."
            : "✓ Fits in memory" + (e.Approximate ? " (approximate estimate)" : "") + ".";
        // Bars show the estimate against what is free right now; a running model is already counted as used.
        if (!e.UsesGpu)
            vramBar.Set("Graphics memory (VRAM)", "Not used: the model runs on the CPU", 0, Tone.Neutral);
        else if (runningCurrent && hardware.Gpus.FirstOrDefault() is Gpu g && g.TotalGiB > 0)
            vramBar.Set("Graphics memory (VRAM)", $"{g.TotalGiB - g.AvailableGiB:0.0} of {g.TotalGiB:0.#} GB in use", (g.TotalGiB - g.AvailableGiB) / g.TotalGiB, Tone.Positive);
        else
            vramBar.Set("Graphics memory (VRAM)", $"needs {e.Vram:0.0} GB · {e.AvailableVram:0.0} GB free", e.AvailableVram > 0 ? e.Vram / e.AvailableVram : 1,
                e.Vram > e.AvailableVram ? Tone.Danger : e.VramMargin < 0 ? Tone.Caution : Tone.Positive);
        ramBar.Set("System memory (RAM)", runningCurrent ? $"{hardware.RamGiB - hardware.AvailableRamGiB:0.0} of {hardware.RamGiB:0} GB in use" : $"needs {e.Ram:0.0} GB · {e.AvailableRam:0.0} GB free",
            runningCurrent ? (hardware.RamGiB > 0 ? (hardware.RamGiB - hardware.AvailableRamGiB) / hardware.RamGiB : 0) : e.AvailableRam > 0 ? e.Ram / e.AvailableRam : 1,
            runningCurrent ? Tone.Positive : e.Ram > e.AvailableRam ? Tone.Danger : e.RamMargin < 0 ? Tone.Caution : Tone.Positive);
        memory.ForeColor = secondaryColor;
        memory.Text = runningCurrent
            ? $"Live context {runtime.LiveContext:N0}. Estimated allocation: model {e.ModelVram:0.0} + KV cache {e.KvVram:0.0} + runtime {e.RuntimeVram:0.0} GB. Free memory is measured after loading."
            : (e.UsesGpu ? $"Estimated VRAM: model {e.ModelVram:0.0} + KV cache {e.KvVram:0.0} + runtime {e.RuntimeVram:0.0} GB. Margin after the {e.VramReserve:0.0} GB reserve: {e.VramMargin:0.0} GB. " : "")
              + $"RAM margin after the {e.RamReserve:0.0} GB reserve: {e.RamMargin:0.0} GB. Memory safety: {policy.Name}"
              + (policy.Name == "Off" ? " (launcher checks are off; this estimate is informational)." : ".");
        contextHint.Text = runningCurrent
            ? $"Live context {runtime.LiveContext:N0} · configured context {context.Value:N0} · native limit {(info.NativeContext > 0 ? info.NativeContext.ToString("N0") : "Unknown")}."
            : $"Context {context.Value:N0} · memory recommendation ~{e.Recommended:N0} · native limit {(info.NativeContext > 0 ? info.NativeContext.ToString("N0") : "Unknown")}. Exact values are preserved; advice never changes them automatically.";
        string gpuLayers = Selected.Options.S("GpuLayers");
        if (info.Layers <= 0)
            placement.Text = "CPU placement: Unknown until model layout is available";
        else if (Selected.Options.B("CpuMoe") && backend != "CPU")
        {
            int cpuExperts = Math.Clamp(Selected.Options.I("CpuMoeLayers", info.Layers), 0, info.Layers);
            placement.Text = info.Architecture.Equals("qwen4exp", StringComparison.OrdinalIgnoreCase) && cpuExperts > 0 && cpuExperts < info.Layers
                ? $"CUDA placement: {cpuExperts} expert layers CPU / {info.Layers - cpuExperts} GPU; RAM working set capped at {Selected.Options.I("WorkingSetLimitGiB", 65)} GiB"
                : "CUDA placement: model compute + hot experts on GPU; full MoE expert store memory-mapped in RAM";
        }
        else if (gpuLayers.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            placement.Text = "CPU placement: none requested; all layers on GPU";
        else if (int.TryParse(gpuLayers, out int onGpu))
            placement.Text = $"CPU placement: about {Math.Max(0, info.Layers - onGpu)} of {info.Layers} layers" + (info.FullAttentionInterval > 0 ? " (hybrid estimate)" : "");
        else
            placement.Text = "CPU placement: Unknown";
        bool mtpActive = Arguments.MtpActive(Selected, info);
        string mtpState = Selected.RuntimeId == Basalt.Runtime ? "MTP on (Basalt pack)" : Selected.Acceleration == "Auto" ? (mtpActive ? "MTP on (Auto)" : "MTP off (Auto)") : "MTP " + Selected.Acceleration;
        string ngramState = Selected.RuntimeId == Basalt.Runtime ? "Basalt suffix drafting" : Selected.Ngram == "Off" ? "N-gram off" : "N-gram " + Selected.Ngram;
        accelerationSummary.Text = $"Acceleration: {mtpState} · {ngramState}";
        var effective = Arguments.EffectiveCache(Selected, info);
        bool unsupportedFlashNextKey = effective.TurboQuant
            && info.Architecture.Equals("qwen4exp", StringComparison.OrdinalIgnoreCase)
            && effective.K.StartsWith("Turbo", StringComparison.OrdinalIgnoreCase);
        effectiveCache.Text = unsupportedFlashNextKey
            ? "Unavailable: Flash-Next indexer K requires Q8. Choose Quality-first (Q8 K / Turbo4 V)."
            : effective.TurboQuant
            ? $"Effective cache: K {effective.K} · V {effective.V} · {effective.Preset}. Ordinary K/V values are preserved."
            : $"Effective cache: K {effective.K} · V {effective.V} · TurboQuant off";
        runtimeSummary.Text = "Runtime: " + Selected.RuntimeId;
        string runtimeEvidence = runningCurrent && !restartRequired ? "✓ Current model is running on this backend" : "• Runtime/model combination not yet confirmed in this session";
        bool turboCodec = probe?.Help.Contains("turbo4", StringComparison.OrdinalIgnoreCase) == true;
        string pleEvidence = info.HasPle
            ? $"Learned PLE detected{(info.PleParameters > 0 ? $"; {info.PleParameters / 1e9:0.##}B table parameters" : "")}; independent of speculative n-gram/MTP. "
                + (new EngineCapabilities(probe?.Help ?? "").HasFlag("--tensor-read-lazy")
                    ? "Lazy tensor reads requested; architecture placement still needs allocation verification."
                    : new EngineCapabilities(probe?.Help ?? "").HasFlag("--lazy-mode")
                        ? "Upstream --lazy-mode auto reads tables over 4 GiB from disk on demand; smaller tables stay resident. Allocation still needs verification."
                        : "Table loading is controlled by this runtime; SSD/lazy placement is unverified.")
            : "No learned PLE evidence detected; future metadata layouts may require an inspector update.";
        compatibility.Text = $"✓ GGUF v{info.Version} parsed\n• Architecture metadata: {info.Architecture}\n{pleEvidence}\n✓ {backend} runtime probe passed · {Selected.RuntimeId}\n{(info.HasTemplate || template.Text.Length > 0 ? "✓ Chat template detected" : "? Chat template support unknown")}\n{(info.Mtp == "Supported" || File.Exists(Selected.DraftModel) ? "✓" : "?")} MTP: {info.Mtp}{(File.Exists(Selected.DraftModel) ? " · external draft head selected" : "")}{(Selected.Legacy && Selected.KnownGood?.B("MtpEnabled") == true ? " · imported tested preset available" : "")}\n{(Selected.Ngram == "Off" ? "• N-gram disabled" : "• N-gram selected: " + Selected.Ngram)}\n{(turboCodec ? "✓ Native TurboQuant codecs present in runtime" : "• Native TurboQuant codecs not present in selected runtime")}\n{runtimeEvidence}\n{(runningCurrent ? "✓ Running model already allocated" : (e.OverBudget ? "!" : e.Approximate ? "?" : "✓") + " " + e.State + " (estimate)")}";
        if (Selected.RuntimeId == Basalt.Runtime)
        {
            string? basaltProblem = Basalt.Problem(store.Value.BasaltRoot, Selected.BasaltPack, info);
            compatibility.Text = basaltProblem == null
                ? $"✓ GGUF architecture: {info.Architecture}\n✓ Basalt pack has vision and MTP sections\n• Basalt manages its own expert placement and memory\n{runtimeEvidence}"
                : "! Basalt: " + basaltProblem;
            compatibilitySummary.Text = basaltProblem ?? (runningCurrent ? "Basalt is running." : "Basalt pack is ready to start.");
            placement.Text = "Basalt manages GPU and RAM expert placement.";
        }
        else
        compatibilitySummary.Text = unsupportedFlashNextKey
            ? "Action needed: Flash-Next TurboQuant keys are unsupported; use Q8 keys."
            : runningCurrent ? "The selected model is running; its memory is already allocated."
            : e.OverBudget ? "Action needed: estimated memory exceeds the selected policy budget." : e.Approximate ? "Compatibility is partly estimated; expand for evidence." : "No compatibility blocker detected.";
        tips.SetToolTip(compatibility, info.MtpEvidence);
        UpdateConditionalControls();
    }
    async Task Start(bool safe)
    {
        if (Selected == null || info == null || probe == null)
            throw new InvalidOperationException("Select a valid model and wait for hardware detection.");
        CaptureProfile();
        hardware = await Task.Run(Hardware.Detect);
        UpdateHardwareText();
        bool repaired = engine.EnsureCompatibleProfile(Selected, info);
        if (repaired)
        {
            backend = await engine.Resolve(store.Value.Backend, hardware, Selected);
            probe = await engine.Inspect(backend, Selected);
            LoadProfile();
            notice.Text = "Flash-Next runtime and MTP-head selection were repaired before launch.";
        }
        store.Save();
        unsaved = false;
        bool poly = Selected.RuntimeId == PolyStrata.Runtime || Selected.RuntimeId == Basalt.Runtime;
        if (!safe && !poly) // PolyStrata budgets its own RAM/VRAM; the llama.cpp estimate does not describe it
        {
            var estimate = MemoryEstimator.Calculate(info, Selected, hardware, backend);
            var admission = MemoryPolicies.Admission(Selected, estimate);
            if (admission == MemoryAdmission.Block)
                throw new InvalidOperationException("Strict memory safety blocked this launch because the estimate exceeds the selected budget. Change the configuration or policy; no values were changed automatically.");
            if (admission == MemoryAdmission.Confirm)
            {
                var answer = MessageBox.Show(this,
                    "The current estimate exceeds the selected memory budget. The backend may fail to allocate memory or become very slow.\n\nAttempt this exact configuration without changing context, cache precision, or offload?",
                    "Memory estimate is over budget",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes)
                {
                    notice.Text = "Launch canceled. The selected settings were preserved.";
                    return;
                }
            }
        }
        busy = true;
        UpdateStatus();
        try
        {
            if (!poly && store.Value.Lan && !Integration.FirewallMatches(engine.Executable(backend, Selected), store.Value.Port))
            {
                notice.Text = "Applying the LocalSubnet firewall rule for this engine. Windows may request administrator permission.";
                await Integration.Firewall(engine.Executable(backend, Selected), store.Value.Port);
            }
            bool qwenSync = !safe && File.Exists(store.Value.LegacyCommon) && File.Exists(store.Value.LegacySettings);
            bool qwenChanged = qwenSync && store.Value.QwenSyncSignature != Integration.Fingerprint(store.Value, Selected);
            DateTime? qwenWritten = null;
            if (qwenSync)
            {
                // Qwen Code reads its settings when it opens, and a large model can take a minute to load: tell it the
                // model now, from the profile's own model ID and context, so a window opened meanwhile lists the right
                // one. The verified values replace these once the server answers; a failure here is retried there.
                try
                {
                    await Integration.SyncQwen(store, Selected, engine.Executable(backend, Selected));
                    qwenWritten = DateTime.Now;
                }
                catch (Exception) { }
            }
            await runtime.Start(Selected, info, backend, engine, probe, safe);
            string syncWarning = "", desktopNote = "";
            if (store.Value.ModelTailscale)
                try { await ModelTailnet.Share(store.Value); store.Save(); }
                catch (Exception ex) { syncWarning = "Server is running. Tailscale sharing needs repair: " + runtime.Redact(ex.Message); }
            if (qwenSync)
            {
                try
                {
                    if (runtime.LiveModelId.Length == 0 || runtime.LiveContext is null)
                        throw new InvalidOperationException("The live model ID or effective context could not be verified through the server API.");
                    await Integration.SyncQwen(store, Selected, engine.Executable(backend, Selected), runtime.LiveModelId, runtime.LiveContext);
                    qwenWritten ??= DateTime.Now;
                    if (qwenChanged && Integration.DesktopOpenedBefore(store.Value.LegacyDesktop, qwenWritten.Value))
                        desktopNote = $"Server live. Qwen Code Desktop was already open, so it still lists the previous model: close and reopen it to use {Selected.Name}.";
                }
                catch (Exception ex) { syncWarning = "Server is running. " + runtime.Redact(ex.Message) + " Retry Sync Qwen Code settings on the Network page."; }
            }
            unsaved = false;
            restartRequired = false;
            notice.Text = safe ? "Server live with temporary safe settings; closing the launcher keeps it running." : "Server live. Closing the launcher keeps this model running; reopening reconnects to it.";
            if (runtime.Error.Length > 0)
                notice.Text = runtime.Error;
            else if (desktopNote.Length > 0)
                notice.Text = desktopNote;
            if (syncWarning.Length > 0)
                notice.Text = syncWarning;
            RefreshEstimates();
        }
        finally { busy = false; UpdateStatus(); }
    }
    void UpdateStatus()
    {
        string state = runtime.State;
        bool running = runtime.Active && state.StartsWith("Running", StringComparison.Ordinal);
        statusPill.Text = running ? (state.Contains("Live", StringComparison.Ordinal) ? "Running · reconnected" : "Running")
            : busy && state == "Stopped" ? "Starting…" : state;
        statusPill.Tone = running ? Tone.Positive
            : state.StartsWith("Loading", StringComparison.Ordinal) || state.StartsWith("Stopping", StringComparison.Ordinal) || busy || state.StartsWith("Existing", StringComparison.Ordinal) ? Tone.Caution
            : state.StartsWith("Stopped", StringComparison.Ordinal) ? Tone.Neutral : Tone.Danger;
        tips.SetToolTip(statusPill, running ? $"{runtime.LiveProfile?.Name} is loaded on port {store.Value.Port}. Closing the launcher keeps it running." : "Server status");
        changesLabel.Text = unsaved ? "● Unsaved changes" : restartRequired ? "● Restart to apply changes" : "";
        changesLabel.ForeColor = cautionColor;
        start.Enabled = !busy && !runtime.Active && !runtime.ExistingServer && info != null;
        restart.Enabled = !busy && runtime.Active && info != null;
        stop.Enabled = runtime.Active || !busy && runtime.CanStopExisting;
        save.Enabled = !busy && unsaved;
        models.Enabled = !busy && !runtime.Active;
        foreach (var b in mutationButtons)
            b.Enabled = !busy && !runtime.Active;
        foreach (var page in new[] { modelPage, advancedPage, networkPage })
            page.Enabled = !busy;
        backendChoice.Enabled = !runtime.Active && !busy;
        runtimeChoice.Enabled = Selected != null && !runtime.Active && !busy;
        bool live = runtime.Active || runtime.ExistingServer;
        string lanAddress = lan.Checked ? Integration.LanAddress() : "";
        network.Text = $"Listening on {(lan.Checked ? "0.0.0.0 (all adapters)" : "127.0.0.1 (this PC only)")}, port {port.Value} · server: {runtime.State}"
            + (runtime.LiveModelId.Length > 0 ? $" · model ID: {runtime.LiveModelId}" : "")
            + (runtime.LiveContext is int liveContext ? $" · context per slot: {liveContext:N0}" : "");
        localStatus.Text = live
            ? $"✓ Running. Apps on this PC connect to http://127.0.0.1:{port.Value}/v1"
            : $"✓ Ready. Once a model is started, apps on this PC connect to http://127.0.0.1:{port.Value}/v1";
        localStatus.ForeColor = positiveColor;
        bool externalEngine = Selected?.RuntimeId is PolyStrata.Runtime or Basalt.Runtime;
        if (!lan.Checked)
        {
            lanStatus.Text = "Off. Localhost works, but other devices on your network can't connect until you turn this on.";
            lanStatus.ForeColor = cautionColor;
        }
        else if (externalEngine)
        {
            lanStatus.Text = "On, but the selected engine only listens on this PC. Use Tailscale below to reach it from other devices.";
            lanStatus.ForeColor = cautionColor;
        }
        else
        {
            lanStatus.Text = $"On. Devices on your network connect to http://{lanAddress}:{port.Value}/v1"
                + (live && (unsaved || restartRequired) ? " after you Save and Restart the model." : live ? "" : " once the model starts.");
            lanStatus.ForeColor = live && (unsaved || restartRequired) ? cautionColor : positiveColor;
        }
    }
    async Task Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    void ShowError(Exception ex)
    {
        notice.Text = runtime.Redact(ex.Message);
        if (Visible)
            MessageBox.Show(this, notice.Text, "llama.cpp Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
    void BrowseModel()
    {
        using var d = new OpenFileDialog { Filter = "GGUF model|*.gguf", Multiselect = true, InitialDirectory = store.Value.ModelsDirectory };
        if (d.ShowDialog(this) == DialogResult.OK)
            _ = Run(async () => { foreach (var path in d.FileNames) await AddModel(path); });
    }
    async Task AddModel(string path)
    {
        if (runtime.Active)
            throw new InvalidOperationException("Stop the server before adding models.");
        var m = await Task.Run(() => GgufInspector.Inspect(path));
        var existing = store.Value.Models.FirstOrDefault(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            models.SelectedItem = existing;
            return;
        }
        string preferred = store.Value.DefaultRuntime;
        bool customDefault = CustomEngine.IsCustom(preferred) && CustomEngine.HasServer(store.Value.CustomEngines.FirstOrDefault(e => CustomEngine.IdFor(e.Name) == preferred)?.Folder);
        var p = new Profile { Path = path, Name = m.Name, MemorySafety = store.Value.DefaultMemorySafety, RuntimeId = customDefault ? preferred : engine.RequiresExperimentalRuntime(m) ? EngineManager.FlashNextRuntime : EngineManager.BundledRuntime };
        p.Alias = "local-" + p.Id[..12];
        p.Options["NativeContextLimit"] = m.NativeContext > 0 ? m.NativeContext : 262144;
        engine.EnsureCompatibleProfile(p, m);
        MemoryEstimator.Optimize(p, m, hardware, backend);
        store.Value.Models.Add(p);
        store.Value.ActiveId = p.Id;
        await ReloadModels();
        Save();
    }
    void RemoveModel()
    {
        if (Selected == null || runtime.Active)
            return;
        store.Value.Models.Remove(Selected);
        store.Value.ActiveId = store.Value.Models.FirstOrDefault()?.Id ?? "";
        store.Save();
        _ = ReloadModels();
        notice.Text = "Removed from the launcher. The GGUF file remains in its original location.";
    }
    void BrowseFile(TextBox box, string filter)
    {
        using var d = new OpenFileDialog { Filter = filter };
        if (d.ShowDialog(this) == DialogResult.OK)
        {
            box.Text = d.FileName;
            Changed();
        }
    }
    void ApplyTheme()
    {
        bool dark = store.Value.Theme == "Dark" || (store.Value.Theme == "System" && (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0);
        pal = Palette.Create(dark, SystemInformation.HighContrast);
        windowColor = pal.Chrome;
        surfaceColor = pal.Card;
        inputColor = pal.Input;
        borderColor = pal.Border;
        foregroundColor = pal.Fore;
        secondaryColor = pal.Muted;
        accentColor = pal.Accent;
        positiveColor = pal.Positive;
        cautionColor = pal.Caution;
        dangerColor = pal.Danger;
        BackColor = pal.Chrome;
        ForeColor = pal.Fore;
        foreach (Control child in Controls)
            ApplyTheme(child, pal.Chrome);
        tips.BackColor = surfaceColor;
        tips.ForeColor = foregroundColor;
        foreach (var bar in new[] { contextSlider, cacheKSlider, cacheVSlider })
        {
            bar.TrackColor = pal.Border;
            bar.AccentColor = pal.Accent;
            bar.Invalidate();
        }
        NativeTheme.Apply(this, dark);
        tabs.Invalidate();
        if (info != null && Selected != null)
            RefreshEstimates();
        if (start != null && nav.Count > 0)
        {
            UpdateStatus();
            RefreshTailscale();
            RefreshEngineStatus();
        }
    }
    // Re-themes controls created after startup (the custom engine list).
    void ApplyTheme(Control subtree)
    {
        Color background = pal.Page;
        for (var parent = subtree.Parent; parent != null; parent = parent.Parent)
            if (parent is CardPanel) { background = pal.Card; break; }
        ApplyTheme(subtree, background);
        NativeTheme.ApplyTo(subtree, pal.Dark);
    }
    void ApplyTheme(Control c, Color background)
    {
        Color own = c switch
        {
            _ when c == logs => pal.Card,
            CardPanel => pal.Card,
            PageHost or ThemedScrollHost => pal.Page,
            _ when c.Parent is PageHost => pal.Page,
            TextBox or ComboBox or NumericUpDown => pal.Input,
            _ when c.Tag as string == "Chrome" => pal.Chrome,
            _ => background
        };
        c.BackColor = own;
        c.ForeColor = c is Label label && label.Font.Size <= 9.1f ? pal.Muted : pal.Fore;
        switch (c)
        {
            case CardPanel card: card.Palette = pal; break;
            case NavButton item: item.Palette = pal; break;
            case StatusPill pill: pill.Palette = pal; break;
            case UsageBar usage: usage.Palette = pal; break;
            case ToggleSwitch toggle: toggle.Palette = pal; break;
            case ThemedButton button:
                button.Palette = pal;
                button.DisabledBackColor = Palette.Blend(pal.Border, background, pal.Dark ? 0.35 : 0.45);
                button.DisabledForeColor = pal.Muted;
                button.DisabledBorderColor = pal.Border;
                break;
            case InfoButton info:
                info.ForeColor = pal.Muted;
                info.HoverColor = pal.Accent;
                break;
            case ThemedComboBox combo:
                combo.ChromeBackColor = pal.Input;
                combo.ChromeForeColor = pal.Muted;
                combo.ChromeBorderColor = pal.Border;
                combo.ChromeHoverBackColor = pal.HighContrast ? SystemColors.Highlight : pal.Hover;
                combo.ChromeHoverForeColor = pal.HighContrast ? SystemColors.HighlightText : pal.Fore;
                combo.ChromeHoverBorderColor = pal.HighContrast ? SystemColors.Highlight : pal.Accent;
                break;
            case ThemedNumericUpDown number:
                number.ChromeBackColor = pal.Input;
                number.ChromeForeColor = pal.Muted;
                number.ChromeBorderColor = pal.Border;
                break;
            case ThemedScrollHost scroll:
                scroll.TrackBackColor = pal.Page;
                scroll.ThumbColor = pal.Dark ? Color.FromArgb(78, 82, 93) : Color.FromArgb(186, 191, 200);
                scroll.ThumbHoverColor = pal.Dark ? Color.FromArgb(108, 113, 126) : Color.FromArgb(146, 152, 163);
                break;
            case LinkLabel link:
                link.LinkColor = link.ActiveLinkColor = link.VisitedLinkColor = pal.Accent;
                break;
            case TextBox { Multiline: false } box:
                box.BorderStyle = BorderStyle.FixedSingle;
                break;
        }
        c.Invalidate();
        foreach (Control child in c.Controls)
            ApplyTheme(child, own);
    }
    void RestoreWindowPlacement()
    {
        var primary = Screen.PrimaryScreen!.WorkingArea;
        bool saved = store.Value.WindowX != int.MinValue && store.Value.WindowY != int.MinValue;
        // A first launch opens at a comfortable size for this monitor; later launches reuse the saved pixels.
        double scale = DeviceDpi / 96.0;
        int width = saved ? store.Value.WindowWidth : (int)(1180 * scale);
        int height = saved ? store.Value.WindowHeight : (int)(840 * scale);
        width = Math.Clamp(width, Math.Min(MinimumSize.Width, primary.Width), primary.Width);
        height = Math.Clamp(height, Math.Min(MinimumSize.Height, primary.Height), primary.Height);
        Rectangle desired = !saved
            ? new Rectangle(primary.Left + (primary.Width - width) / 2, primary.Top + (primary.Height - height) / 2, width, height)
            : new Rectangle(store.Value.WindowX, store.Value.WindowY, width, height);
        var screen = Screen.AllScreens.FirstOrDefault(s => s.WorkingArea.IntersectsWith(desired)) ?? Screen.PrimaryScreen!;
        var work = screen.WorkingArea;
        desired.Width = Math.Min(desired.Width, work.Width);
        desired.Height = Math.Min(desired.Height, work.Height);
        desired.X = Math.Clamp(desired.X, work.Left, work.Right - desired.Width);
        desired.Y = Math.Clamp(desired.Y, work.Top, work.Bottom - desired.Height);
        StartPosition = FormStartPosition.Manual;
        Bounds = desired;
        if (store.Value.WindowMaximized)
            WindowState = FormWindowState.Maximized;
    }
    void RememberWindowPlacement()
    {
        Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        store.Value.WindowX = bounds.X;
        store.Value.WindowY = bounds.Y;
        store.Value.WindowWidth = bounds.Width;
        store.Value.WindowHeight = bounds.Height;
        store.Value.WindowMaximized = WindowState == FormWindowState.Maximized;
    }
    async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (closing)
            return;
        e.Cancel = true;
        closing = true;
        timer.Stop();
        ollamaTimer.Stop();                             // the Ollama API itself keeps running
        try
        {
            RememberWindowPlacement();
            CaptureProfile();
            store.Save();
            if (runtime.Active && runtime.State.StartsWith("Running", StringComparison.Ordinal))
                runtime.Detach();
            else if (runtime.Active)
            {
                await runtime.Stop();
                await CleanVisionCache(afterStop: true);
            }
        }
        catch (Exception ex)
        {
            closing = false;
            ShowError(ex);
            return;
        }
        runtime.Dispose();
        BeginInvoke(Close);
    }
    internal void DropdownsOnlyForTest(string directory, int dpi)
    {
        loading = true;
        foreach (var p in store.Value.Models) models.Items.Add(p);
        models.SelectedItem = store.Value.Models.FirstOrDefault(p => p.Id == store.Value.ActiveId);
        LoadProfile();
        loading = false;
        Show();
        VerifyDropdownsForTest(directory, dpi);
    }
    void VerifyDropdownsForTest(string directory, int dpi)
    {
        var all = new[] { models, theme, cacheK, cacheV, effort, gpu, memorySafety, customAction,
            turboPreset, turboK, turboV, acceleration, ngram, runtimeChoice, backendChoice, defaultMemorySafety };
        string savedTheme = store.Value.Theme;
        bool wasLoading = loading;
        bool timerEnabled = timer.Enabled;
        timer.Stop();
        loading = true;
        try
        {
            foreach (string palette in new[] { "Dark", "Light", "System" })
            {
                store.Value.Theme = palette;
                theme.SelectedItem = palette;
                ApplyTheme();
                foreach (var combo in all)
                {
                    var parent = combo.Parent!;
                    int index = parent.Controls.GetChildIndex(combo);
                    var cell = parent is TableLayoutPanel table ? table.GetCellPosition(combo) : default;
                    var bounds = combo.Bounds;
                    var anchor = combo.Anchor;
                    var dock = combo.Dock;
                    bool enabled = combo.Enabled;
                    var deviceDpi = typeof(Control).GetField("_deviceDpi", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    int originalDpi = combo.DeviceDpi;
                    using var host = new DropdownRenderingTests.TestHost { Text = "Dropdown regression test", ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.None,
                        StartPosition = FormStartPosition.Manual, Location = new Point(20, 20), ClientSize = new Size(combo.Width + 40, combo.Height + 50), BackColor = windowColor };
                    parent.SuspendLayout();
                    try
                    {
                        combo.Dock = DockStyle.None;
                        combo.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                        host.Controls.Add(combo);
                        combo.Bounds = new Rectangle(new Point(20, 15), bounds.Size);
                        combo.Enabled = true;
                        host.Show();
                        NativeTheme.Apply(host, inputColor.GetBrightness() < .5f);
                        deviceDpi.SetValue(combo, dpi);
                        combo.Width++;
                        combo.Width--;
                        Application.DoEvents();
                        DropdownRenderingTests.Verify(combo, palette + "-" + Array.IndexOf(all, combo), directory);
                    }
                    finally
                    {
                        combo.DroppedDown = false;
                        deviceDpi.SetValue(combo, originalDpi);
                        if (parent is TableLayoutPanel originalTable) originalTable.Controls.Add(combo, cell.Column, cell.Row);
                        else parent.Controls.Add(combo);
                        parent.Controls.SetChildIndex(combo, index);
                        combo.Dock = dock;
                        combo.Anchor = anchor;
                        combo.Bounds = bounds;
                        combo.Enabled = enabled;
                        parent.ResumeLayout();
                    }
                }
            }
        }
        finally
        {
            store.Value.Theme = savedTheme;
            theme.SelectedItem = savedTheme;
            ApplyTheme();
            loading = wasLoading;
            if (timerEnabled) timer.Start();
        }
    }
    void VerifyCacheInteractionForTest()
    {
        if (Selected == null || info == null) throw new InvalidOperationException("Cache interaction test needs a model.");
        var p = Selected;
        var savedOptions = p.Options.Clone();
        bool savedTurbo = p.TurboQuantEnabled, savedShow = store.Value.ShowTurboQuantControls;
        string savedRuntime = p.RuntimeId, savedNotice = notice.Text;
        bool savedUnsaved = unsaved, savedRestart = restartRequired;
        void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException("Cache interaction: " + message); }
        void EnableTurbo()
        {
            loading = true;
            showTurbo.Checked = true;
            runtimeChoice.SelectedItem = EngineManager.FlashNextRuntime;
            turboEnabled.Checked = true;
            p.RuntimeId = EngineManager.FlashNextRuntime;
            p.TurboQuantEnabled = true;
            loading = false;
            UpdateConditionalControls();
        }
        void Invoke(Control control, string method, object argument) => control.GetType().GetMethod(method,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(control, new[] { argument });
        try
        {
            foreach (var (combo, slider, option) in new[] { (cacheK, cacheKSlider, "CacheK"), (cacheV, cacheVSlider, "CacheV") })
            {
                for (int i = 0; i < cacheTypes.Length; i++)
                {
                    EnableTurbo();
                    Assert(combo.Enabled && slider.Enabled, option + " must remain editable with TurboQuant enabled");
                    combo.DroppedDown = true;
                    Application.DoEvents();
                    Assert(combo.DroppedDown, option + " dropdown must open");
                    combo.SelectedItem = cacheTypes[i];
                    Invoke(combo, "OnSelectionChangeCommitted", EventArgs.Empty);
                    combo.DroppedDown = false;
                    Assert(slider.Value == i && p.Options.S(option) == cacheTypes[i], option + " dropdown must update slider and profile");
                    Assert(!p.TurboQuantEnabled && !Arguments.EffectiveCache(p, info).TurboQuant, "ordinary dropdown selection must become effective");
                    EnableTurbo();
                    int x = 7 + (int)Math.Round((slider.Width - 15d) * i / (cacheTypes.Length - 1));
                    Invoke(slider, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, x, slider.Height / 2, 0));
                    Invoke(slider, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, x, slider.Height / 2, 0));
                    Assert(combo.Text == cacheTypes[i] && p.Options.S(option) == cacheTypes[i] && !p.TurboQuantEnabled,
                        option + " mouse selection must work even at its existing value");
                }
                foreach (var (keyCode, expected) in new[] { (Keys.Home, 0), (Keys.Right, 1), (Keys.End, cacheTypes.Length - 1), (Keys.Left, cacheTypes.Length - 2) })
                {
                    EnableTurbo();
                    bool inputKey = (bool)slider.GetType().GetMethod("IsInputKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(slider, new object[] { keyCode })!;
                    Assert(inputKey, "slider must receive navigation key " + keyCode);
                    Invoke(slider, "OnKeyDown", new KeyEventArgs(keyCode));
                    Assert(combo.Text == cacheTypes[expected] && !p.TurboQuantEnabled, option + " keyboard selection must become effective");
                }
            }
            store.Save();
            var reloaded = new ConfigStore(store.Root, false).Value.Models.Single(x => x.Id == p.Id);
            Assert(reloaded.Options.S("CacheK") == cacheK.Text && reloaded.Options.S("CacheV") == cacheV.Text && !reloaded.TurboQuantEnabled,
                "edited cache values and ordinary mode must survive reload");
        }
        finally
        {
            p.Options = savedOptions;
            p.RuntimeId = savedRuntime;
            p.TurboQuantEnabled = savedTurbo;
            store.Value.ShowTurboQuantControls = savedShow;
            LoadProfile();
            notice.Text = savedNotice;
            unsaved = savedUnsaved;
            restartRequired = savedRestart;
            store.Save();
        }
    }
    // Test harness uses actual control layout and rendering at each scale, without changing OS settings.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        int step = keyData switch
        {
            Keys.Control | Keys.Tab or Keys.Control | Keys.PageDown => 1,
            Keys.Control | Keys.Shift | Keys.Tab or Keys.Control | Keys.PageUp => -1,
            _ => 0
        };
        if (step != 0 && tabs.TabPages.Count > 0)
        {
            tabs.SelectedIndex = (tabs.SelectedIndex + step + tabs.TabPages.Count) % tabs.TabPages.Count;
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    void EndLiveResize()
    {
        ThemedScrollHost.LiveResize = false;
        if (tabs.SelectedTab?.Controls.Count > 0 && tabs.SelectedTab.Controls[0] is ThemedScrollHost host)
            host.Refit();
    }
    // Times what users feel: switching pages and dragging the window edge. Each step waits until every
    // pending layout and paint has been processed, like a real frame.
    internal string PerformanceForTest()
    {
        hardware = Hardware.Detect();
        backend = "CUDA";
        UpdateHardwareText();
        loading = true;
        foreach (var p in store.Value.Models)
            models.Items.Add(p);
        models.SelectedItem = store.Value.Models.FirstOrDefault(p => p.Id == store.Value.ActiveId);
        if (Selected != null)
            info = GgufInspector.Inspect(Selected.Path);
        LoadProfile();
        loading = false;
        Show();
        Size = new Size(1180, 840);
        UpdateStatus();
        Application.DoEvents();
        var report = new System.Text.StringBuilder();
        double layoutTotal = 0, paintTotal = 0;
        double Frame(Action step)
        {
            var watch = Stopwatch.StartNew();
            step();
            double layout = watch.Elapsed.TotalMilliseconds;
            // Paint only what Windows invalidated, as during a real drag or page switch.
            Application.DoEvents();
            layoutTotal += layout;
            paintTotal += watch.Elapsed.TotalMilliseconds - layout;
            return watch.Elapsed.TotalMilliseconds;
        }
        foreach (string pass in new[] { "first visit", "second visit" })
        {
            var times = new List<string>();
            foreach (Panel page in tabs.TabPages)
                times.Add($"{page.Text} {Frame(() => tabs.SelectedTab = page):0} ms");
            report.AppendLine($"Page switch ({pass}): " + string.Join(", ", times) + $"  [layout {layoutTotal:0} ms, paint {paintTotal:0} ms]");
            layoutTotal = paintTotal = 0;
        }
        // Switching after the window changed size makes every page lay out again.
        Size = new Size(1040, 800);
        Application.DoEvents();
        var afterResize = new List<string>();
        foreach (Panel page in tabs.TabPages)
            afterResize.Add($"{page.Text} {Frame(() => tabs.SelectedTab = page):0} ms");
        report.AppendLine("Page switch (after resize): " + string.Join(", ", afterResize));
        foreach (Panel page in new[] { modelPage, networkPage })
        {
            tabs.SelectedTab = page;
            Application.DoEvents();
            var steps = new List<double>();
            for (int w = 1000; w <= 1500; w += 12) steps.Add(Frame(() => Size = new Size(w, 820)));
            for (int w = 1500; w >= 1000; w -= 12) steps.Add(Frame(() => Size = new Size(w, 820)));
            steps.Sort();
            report.AppendLine($"Resize on {page.Text}: {steps.Count} steps, median {steps[steps.Count / 2]:0.0} ms, 90th {steps[steps.Count * 9 / 10]:0.0} ms, worst {steps[^1]:0.0} ms (~{1000 / Math.Max(1, steps[steps.Count / 2]):0} fps)  [layout {layoutTotal / steps.Count:0.0} ms, paint {paintTotal / steps.Count:0.0} ms per step]");
            layoutTotal = paintTotal = 0;
            // The same drag as the user does it (ResizeBegin..ResizeEnd): content reflows at most every 80 ms.
            ThemedScrollHost.LiveResize = true;
            var live = new List<double>();
            for (int w = 1000; w <= 1500; w += 12) live.Add(Frame(() => Size = new Size(w, 820)));
            for (int w = 1500; w >= 1000; w -= 12) live.Add(Frame(() => Size = new Size(w, 820)));
            double release = Frame(EndLiveResize);
            live.Sort();
            report.AppendLine($"Live drag on {page.Text}: median {live[live.Count / 2]:0.0} ms (~{1000 / Math.Max(1, live[live.Count / 2]):0} fps), 90th {live[live.Count * 9 / 10]:0.0} ms; reflow on release {release:0} ms");
            layoutTotal = paintTotal = 0;
        }
        return report.ToString();
    }
    internal void RenderForTest(string dir, float scale)
    {
        hardware = Hardware.Detect();
        backend = "CUDA";
        UpdateHardwareText();
        probe = Task.Run(() => engine.Inspect("CUDA", store.Value.Models.FirstOrDefault(p => p.Id == store.Value.ActiveId))).GetAwaiter().GetResult();
        version.Text = VersionText();
        loading = true;
        foreach (var p in store.Value.Models)
            models.Items.Add(p);
        models.SelectedItem = store.Value.Models.FirstOrDefault(p => p.Id == store.Value.ActiveId);
        theme.SelectedItem = store.Value.Theme;
        backendChoice.SelectedItem = store.Value.Backend;
        port.Value = store.Value.Port;
        lan.Checked = store.Value.Lan;
        key.Text = store.Value.ApiKey;
        modelFolder.Text = store.Value.ModelsDirectory;
        if (Selected != null)
            info = GgufInspector.Inspect(Selected.Path);
        LoadProfile();
        loading = false;
        Show();
        UpdateStatus();
        PerformLayout();
        Scale(new SizeF(scale, scale));
        float fontScale = scale * 96f / DeviceDpi;
        // Set each control from its original point size once. Inherited fonts must be
        // captured first so changing the parent cannot multiply the scale twice.
        var fonts = new Dictionary<Control, Font>();
        void CaptureFonts(Control c)
        {
            fonts[c] = c.Font;
            foreach (Control child in c.Controls)
                CaptureFonts(child);
        }
        CaptureFonts(this);
        foreach (var pair in fonts)
            pair.Key.Font = new Font(pair.Value.FontFamily, pair.Value.Size * fontScale, pair.Value.Style);
        Size = new Size((int)(1000 * scale), (int)(780 * scale));
        VerifyDropdownsForTest(Path.Combine(dir, "dropdowns-" + scale), (int)Math.Round(96 * scale));
        VerifyCacheInteractionForTest();
        foreach (Panel page in tabs.TabPages)
        {
            tabs.SelectedTab = page;
            PerformLayout();
            Application.DoEvents();
            VerifyLayout(this);
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            bmp.Save(Path.Combine(dir, $"ui-{scale:0.00}-{tabs.SelectedIndex}.png"));
        }
        if (scale == 1)
        {
            tabs.SelectedIndex = 0;
            foreach (var size in new[] { new Size(1000, 640), new Size(1100, 780), new Size(1500, 900) })
            {
                Size = size;
                PerformLayout();
                Application.DoEvents();
                VerifyLayout(this);
                if (size.Width >= 1000)
                {
                    VerifyNoHorizontalPageScroll(this);
                    VerifyHorizontalContainment(tabs.SelectedTab!);
                }
                using var bmp = new Bitmap(Width, Height);
                DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                bmp.Save(Path.Combine(dir, $"ui-resize-{size.Width}.png"));
            }
            foreach (var name in new[] { "Light", "System", "Dark" })
            {
                theme.SelectedItem = name;
                PerformLayout();
                Application.DoEvents();
                VerifyLayout(this);
                using var bmp = new Bitmap(Width, Height);
                DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                bmp.Save(Path.Combine(dir, $"ui-theme-{name}.png"));
            }
            Size = new Size(1000, 780);
            tabs.SelectedIndex = 0;
            PerformLayout();
            Application.DoEvents();
            void CaptureState(string name)
            {
                PerformLayout();
                Application.DoEvents();
                VerifyLayout(this);
                VerifyNoHorizontalPageScroll(this);
                VerifyHorizontalContainment(tabs.SelectedTab!);
                using var stateBitmap = new Bitmap(Width, Height);
                DrawToBitmap(stateBitmap, new Rectangle(0, 0, Width, Height));
                stateBitmap.Save(Path.Combine(dir, name));
            }
            loading = true;
            var renderProfile = Selected;
            string savedMemorySafety = renderProfile?.MemorySafety ?? "Balanced";
            string savedAcceleration = renderProfile?.Acceleration ?? "Off";
            string savedNgram = renderProfile?.Ngram ?? "Off";
            string savedRuntime = renderProfile?.RuntimeId ?? "Bundled";
            bool savedTurboEnabled = renderProfile?.TurboQuantEnabled ?? false;
            string savedTurboPreset = renderProfile?.TurboQuantPreset ?? "Quality-first";
            bool savedShowTurbo = store.Value.ShowTurboQuantControls;
            memorySafety.SelectedItem = "Custom";
            if (renderProfile != null) renderProfile.MemorySafety = "Custom";
            UpdateConditionalControls();
            RefreshEstimates();
            CaptureState("ui-custom-memory.png");
            tabs.SelectedTab = advancedPage;
            acceleration.SelectedItem = "Off";
            ngram.SelectedItem = "ngram-mod";
            if (renderProfile != null) { renderProfile.Acceleration = "Off"; renderProfile.Ngram = "ngram-mod"; }
            UpdateConditionalControls();
            RefreshEstimates();
            CaptureState("ui-advanced-ngram.png");
            acceleration.SelectedItem = "On";
            ngram.SelectedItem = "Off";
            if (renderProfile != null) { renderProfile.Acceleration = "On"; renderProfile.Ngram = "Off"; }
            UpdateConditionalControls();
            RefreshEstimates();
            CaptureState("ui-advanced-mtp.png");
            acceleration.SelectedItem = "On";
            ngram.SelectedItem = "ngram-mod";
            if (renderProfile != null) { renderProfile.Acceleration = "On"; renderProfile.Ngram = "ngram-mod"; }
            UpdateConditionalControls();
            RefreshEstimates();
            CaptureState("ui-advanced-combined.png");
            tabs.SelectedIndex = 0;
            showTurbo.Checked = true;
            store.Value.ShowTurboQuantControls = true;
            runtimeChoice.SelectedItem = "TurboQuant / Flash-Next (Experimental)";
            turboEnabled.Checked = true;
            turboPreset.SelectedItem = "Quality-first";
            memorySafety.SelectedItem = "Balanced";
            if (renderProfile != null)
            {
                renderProfile.RuntimeId = "TurboQuant / Flash-Next (Experimental)";
                renderProfile.TurboQuantEnabled = true;
                renderProfile.TurboQuantPreset = "Quality-first";
                renderProfile.MemorySafety = "Balanced";
            }
            UpdateConditionalControls();
            RefreshEstimates();
            CaptureState("ui-turboquant-quality-first.png");
            turboEnabled.Checked = false;
            turboPreset.SelectedItem = "Memory saver";
            if (renderProfile != null) { renderProfile.TurboQuantEnabled = false; renderProfile.TurboQuantPreset = "Memory saver"; }
            UpdateConditionalControls();
            if (!turboPreset.Enabled)
                throw new InvalidOperationException("TurboQuant preset must remain selectable while its per-model toggle is off.");
            RefreshEstimates();
            CaptureState("ui-turboquant-preset-staged.png");
            turboEnabled.Checked = true;
            turboPreset.SelectedItem = "Custom";
            if (renderProfile != null) { renderProfile.TurboQuantEnabled = true; renderProfile.TurboQuantPreset = "Custom"; }
            UpdateConditionalControls();
            RefreshEstimates();
            CaptureState("ui-turboquant-custom.png");
            tabs.SelectedIndex = 0;
            if (renderProfile != null)
            {
                renderProfile.MemorySafety = savedMemorySafety;
                renderProfile.Acceleration = savedAcceleration;
                renderProfile.Ngram = savedNgram;
                renderProfile.RuntimeId = savedRuntime;
                renderProfile.TurboQuantEnabled = savedTurboEnabled;
                renderProfile.TurboQuantPreset = savedTurboPreset;
            }
            store.Value.ShowTurboQuantControls = savedShowTurbo;
            memorySafety.SelectedItem = savedMemorySafety;
            acceleration.SelectedItem = savedAcceleration;
            ngram.SelectedItem = savedNgram;
            showTurbo.Checked = savedShowTurbo;
            runtimeChoice.SelectedItem = savedRuntime;
            turboEnabled.Checked = savedTurboEnabled;
            turboPreset.SelectedItem = savedTurboPreset;
            UpdateConditionalControls();
            RefreshEstimates();
            loading = false;
            PerformLayout();
            Application.DoEvents();
            if (helpButtons.Count > 0)
            {
                ShowHelp(helpButtons[0], helpButtons[0].LongHelp);
                Application.DoEvents();
                if (helpPopover != null)
                {
                    using var popupBitmap = new Bitmap(helpPopover.Width, helpPopover.Height);
                    helpPopover.DrawToBitmap(popupBitmap, new Rectangle(Point.Empty, helpPopover.Size));
                    popupBitmap.Save(Path.Combine(dir, "ui-help-popup.png"));
                    helpPopover.Close();
                }
            }
            ShowPolicyNotice(memorySafety, "Off", false);
            Application.DoEvents();
            if (policyPopover != null)
            {
                using var policyBitmap = new Bitmap(policyPopover.Width, policyPopover.Height);
                policyPopover.DrawToBitmap(policyBitmap, new Rectangle(Point.Empty, policyPopover.Size));
                policyBitmap.Save(Path.Combine(dir, "ui-policy-notice.png"));
                policyPopover.Close();
            }
        }
    }
    static void VerifyLayout(Control parent)
    {
        if (parent is TableLayoutPanel or FlowLayoutPanel or Box)
        {
            var children = parent.Controls.Cast<Control>().Where(c => c.Visible).ToArray();
            for (int i = 0; i < children.Length; i++)
                for (int j = i + 1; j < children.Length; j++)
                    if (children[i].Bounds.IntersectsWith(children[j].Bounds))
                        throw new InvalidOperationException($"Layout overlap: {children[i].GetType().Name} '{children[i].Text}' {children[i].Bounds} / {children[j].GetType().Name} '{children[j].Text}' {children[j].Bounds}; parent {parent.GetType().Name} {parent.ClientSize}.");
        }
        foreach (Control child in parent.Controls)
            if (child.Visible)
                VerifyLayout(child);
    }
    static void VerifyNoHorizontalPageScroll(Control parent)
    {
        if (parent is Panel { AutoScroll: true } panel && panel.HorizontalScroll.Visible)
            throw new InvalidOperationException("Unexpected horizontal page scrolling at ordinary window width.");
        foreach (Control child in parent.Controls)
            if (child.Visible)
                VerifyNoHorizontalPageScroll(child);
    }
    static void VerifyHorizontalContainment(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (!child.Visible)
                continue;
            if (child.Left < -1 || child.Right > parent.ClientSize.Width + 1)
                throw new InvalidOperationException($"Horizontal clipping: {child.GetType().Name} '{child.Text}' accessible '{child.AccessibleName}' bounds {child.Bounds} in {parent.GetType().Name} '{parent.Text}' accessible '{parent.AccessibleName}' client {parent.ClientSize}; children: {string.Join(" | ", child.Controls.Cast<Control>().Select(c => $"{c.GetType().Name} '{c.Text}' {c.Bounds}"))}.");
            VerifyHorizontalContainment(child);
        }
    }
}
