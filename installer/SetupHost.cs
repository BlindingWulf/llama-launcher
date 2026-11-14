// Windows 10 22H2 / Windows 11 include .NET Framework 4.8. This small host makes
// Setup.exe a single movable file without renaming the WiX Burn engine to a
// filename that triggers Windows installer-compatibility shims.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class SetupHost
{
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetDefaultDllDirectories(uint flags);
    static string Quote(string value)
    {
        var b=new StringBuilder("\"");int slashes=0;
        foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='"'){b.Append('\\',slashes*2+1);b.Append(c);}else{b.Append('\\',slashes);b.Append(c);}slashes=0;}
        b.Append('\\',slashes*2);b.Append('"');return b.ToString();
    }
    static void ExtractAndVerify(string payload)
    {
        using(var source=Assembly.GetExecutingAssembly().GetManifestResourceStream("Installer"))
        using(var dest=new FileStream(payload,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            if(source==null)throw new InvalidDataException("The embedded installer is missing.");
            source.CopyTo(dest);
        }
        using(var input=File.OpenRead(payload))using(var hash=SHA256.Create())
        {
            string actual=BitConverter.ToString(hash.ComputeHash(input)).Replace("-","");
            if(!actual.Equals(SetupHash.Value,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Installer integrity verification failed.");
        }
    }
    static string Arguments(string[] args)
    {
        var result=new StringBuilder();
        foreach(var arg in args){if(result.Length>0)result.Append(' ');result.Append(Quote(arg));}
        return result.ToString();
    }
    [STAThread] static int Main(string[] args)
    {
        bool quiet=Array.Exists(args,arg=>arg.Equals("/quiet",StringComparison.OrdinalIgnoreCase)||arg.Equals("/q",StringComparison.OrdinalIgnoreCase));
        try{return Run(args,quiet);}
        catch(Exception ex){if(!quiet)MessageBox.Show(ex.Message,"Setup could not continue",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
    }
    static int Run(string[] args,bool quiet)
    {
        SetDefaultDllDirectories(0x800); // system32 only; no adjacent DLL search
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        string directory=Path.Combine(Path.GetTempPath(),"LlamaLauncherSetup-"+Guid.NewGuid().ToString("N"));
        var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        Directory.CreateDirectory(directory,security);string payload=Path.Combine(directory,"LlamaLauncherInstaller.exe");int result=1;
        if(quiet)
        {
            try
            {
                ExtractAndVerify(payload);
                using(var process=Process.Start(new ProcessStartInfo(payload,Arguments(args)){UseShellExecute=false,WorkingDirectory=directory}))
                {process.WaitForExit();return process.ExitCode;}
            }
            finally{try{File.Delete(payload);Directory.Delete(directory,false);}catch{}}
        }
        using(var window=new Form{Text="llama.cpp Launcher Setup",Size=new Size(490,170),StartPosition=FormStartPosition.CenterScreen,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false})
        {
            var label=new Label{Text="Preparing llama.cpp Launcher setup…",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,Font=new Font("Segoe UI",12)};window.Controls.Add(label);
            window.Shown+=async delegate{
                try{
                    await Task.Run(delegate{ExtractAndVerify(payload);});
                    window.Hide();
                    using(var process=Process.Start(new ProcessStartInfo(payload,Arguments(args)){UseShellExecute=false,WorkingDirectory=directory})){await Task.Run(delegate{process.WaitForExit();});result=process.ExitCode;}
                }catch(Exception ex){MessageBox.Show(ex.Message,"Setup could not continue",MessageBoxButtons.OK,MessageBoxIcon.Error);}
                finally{try{File.Delete(payload);Directory.Delete(directory,false);}catch{}window.Close();}
            };
            Application.Run(window);
        }
        return result;
    }
}
