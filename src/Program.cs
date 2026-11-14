namespace LlamaLauncher;
internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--firewall")
        {
            try
            {
                Integration.ConfigureFirewall(args[1], int.Parse(args[2]));
                return 0;
            }
            catch { return 1; }
        }
        ApplicationConfiguration.Initialize();
        if (args.Length is 2 or 3 && args[0] == "--perf-test")
            return SelfTests.Performance(args[1], args.Length == 3 ? args[2] : null);
        if (args.Length == 2 && args[0] == "--dropdown-test")
            return SelfTests.Dropdowns(args[1]);
        if (args.Length == 3 && args[0] == "--ownership-host")
            return SelfTests.OwnershipHost(args[1], args[2]).GetAwaiter().GetResult();
        if (args.Length == 3 && args[0] == "--detach-host")
            return SelfTests.DetachHost(args[1], args[2]).GetAwaiter().GetResult();
        if (args.Length == 3 && args[0] == "--validate-profile")
            return SelfTests.ValidateProfile(args[1], args[2]).GetAwaiter().GetResult();
        if (args.Length == 3 && args[0] == "--validate-detach")
            return SelfTests.ValidateDetach(args[1], args[2]).GetAwaiter().GetResult();
        if (args.Length == 3 && args[0] == "--repair-flashnext")
            return SelfTests.RepairFlashNext(args[1], args[2]).GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] == "--self-test")
            return SelfTests.Run(args.Skip(1).ToArray()).GetAwaiter().GetResult();
        string? data = null;
        bool import = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length)
                data = args[++i];
            if (args[i] == "--no-import")
                import = false;
            if (args[i] == "--import-legacy")
                import = true;
        }
        string instanceScope = data == null ? "default" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(data))))[..12];
        using var singleton = new Mutex(true, "Local\\LlamaLauncher-" + System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value + "-" + instanceScope, out bool first);
        if (!first)
        {
            MessageBox.Show("llama.cpp Launcher is already open.");
            return 0;
        }
        try
        {
            var store = new ConfigStore(data, import);
            if (args.Contains("--initialize-only"))
            {
                store.Save();
                return 0;
            }
            var engine = new EngineManager(AppContext.BaseDirectory, store.Root);
            Application.Run(new MainForm(store, engine));
            return 0;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Launcher could not start", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
}
