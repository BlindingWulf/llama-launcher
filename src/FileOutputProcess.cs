using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LlamaLauncher;
// Starts a server whose stdout and stderr go straight into a file instead of a pipe the launcher reads. A pipe breaks
// when the launcher closes with the server running, and PolyStrata's Python server then fails every request that writes
// a log line (it stays up but drops the connection). A file stays writable for as long as the server runs; the launcher
// tails it for the Logs view. The process starts suspended so it is in the job before it can start children, and only
// the output handle is inherited.
static class FileOutputProcess
{
    public static Process Start(ProcessStartInfo start, string outputPath, IntPtr job)
    {
        using SafeFileHandle output = File.OpenHandle(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        if (!SetHandleInformation(output, HandleFlagInherit, HandleFlagInherit))
            throw new System.ComponentModel.Win32Exception();
        var command = new StringBuilder(Quote(start.FileName));
        foreach (string arg in start.ArgumentList)
            command.Append(' ').Append(Quote(arg));
        var environment = new StringBuilder();
        foreach (var item in start.Environment.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            if (item.Value != null)
                environment.Append(item.Key).Append('=').Append(item.Value).Append('\0');
        environment.Append('\0');
        IntPtr block = Marshal.StringToHGlobalUni(environment.ToString());
        IntPtr handles = Marshal.AllocHGlobal(IntPtr.Size);
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        IntPtr attributes = Marshal.AllocHGlobal(size);
        bool listReady = false;
        try
        {
            Marshal.WriteIntPtr(handles, output.DangerousGetHandle());
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw new System.ComponentModel.Win32Exception();
            listReady = true;
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)AttributeHandleList, handles, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception();
            var info = new StartupInfoEx { Attributes = attributes };
            info.Startup.Size = Marshal.SizeOf<StartupInfoEx>();
            info.Startup.Flags = UseStdHandles;
            info.Startup.Output = info.Startup.Error = output.DangerousGetHandle();
            if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, true, Suspended | NoWindow | UnicodeEnvironment | ExtendedStartupInfo,
                    block, start.WorkingDirectory, ref info, out var created))
                throw new System.ComponentModel.Win32Exception();
            try
            {
                if (!AssignProcessToJobObject(job, created.Process))
                {
                    var error = new System.ComponentModel.Win32Exception();
                    TerminateProcess(created.Process, 1);
                    throw error;
                }
                var process = Process.GetProcessById(created.ProcessId);
                _ = process.Handle; // opened while suspended, so the exit code stays readable however soon it exits
                ResumeThread(created.Thread);
                return process;
            }
            finally
            {
                CloseHandle(created.Thread);
                CloseHandle(created.Process);
            }
        }
        finally
        {
            if (listReady)
                DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(block);
        }
    }
    // The quoting CommandLineToArgvW and the C runtime undo: backslashes double only before a quote or the closing quote.
    static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            return arg;
        var quoted = new StringBuilder("\"");
        for (int i = 0; i < arg.Length; i++)
        {
            int slashes = 0;
            while (i < arg.Length && arg[i] == '\\') { slashes++; i++; }
            if (i == arg.Length) { quoted.Append('\\', slashes * 2); break; }
            quoted.Append('\\', arg[i] == '"' ? slashes * 2 + 1 : slashes).Append(arg[i]);
        }
        return quoted.Append('"').ToString();
    }
    const uint HandleFlagInherit = 1, UseStdHandles = 0x100, AttributeHandleList = 0x20002;
    const uint Suspended = 0x4, UnicodeEnvironment = 0x400, ExtendedStartupInfo = 0x80000, NoWindow = 0x08000000;
    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfo
    {
        public int Size; public IntPtr Reserved, Desktop, Title; public int X, Y, Width, Height, Columns, Rows, Fill; public uint Flags;
        public short ShowWindow, Reserved2Size; public IntPtr Reserved2, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation created);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
