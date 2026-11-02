using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Typlo.Core.Updates;

namespace Typlo.App.Services;

// Failure-only, bounded metadata. Restart Manager users are candidates, not proof of share modes.
internal static unsafe class UpdateFileDiagnostics
{
    internal static void Record(UpdateFileEvent value)
    {
        Log.Info($"Update file phase={value.Phase} operation={value.Operation} outcome={value.Outcome} source={Quote(value.Source)} target={Quote(value.Target)}" +
            (value.Error is { } error ? $" hresult=0x{error.HResult:X8} exception={error}" : ""));
        if (!OperatingSystem.IsWindows() || value.Outcome != "failed") return;
        try
        {
            var files = new List<string>();
            foreach (var path in new[] { value.Source, value.Target }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Directory.Exists(path))
                {
                    Probe(path);
                    files.AddRange(Directory.EnumerateFiles(path, "*", new EnumerationOptions
                    { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(128 - files.Count));
                }
                else if (files.Count < 128) files.Add(path);
            }
            files = files.Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToList();
            var denied = files.Where(path => Probe(path) is 32 or 33).ToArray();
            var resources = denied.Length > 0 ? denied : files.Where(File.Exists).ToArray();
            Log.Info($"Update resource scan files={files.Count} share-denied={denied.Length} limit=128");
            if (resources.Length > 0) DescribeUsers(resources);
        }
        catch (Exception diagnosticError) { Log.Error($"Update resource diagnostics unavailable hresult=0x{diagnosticError.HResult:X8}", diagnosticError); }
    }
    private static string Quote(string? value) => value is null ? "-" : "\"" + value.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    private static int Probe(string path)
    {
        // Request DELETE access without deleting or setting DELETE_ON_CLOSE. This identifies
        // incompatible sharing without modifying the file or denying other readers/writers.
        using var handle = CreateFileW(path, 0x10000, 7, 0, 3, 0x02000000, 0);
        var error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        if (error != 0) Log.Info($"Update resource path={Quote(path)} delete-access-error={error}");
        return error;
    }
    private static void DescribeUsers(string[] files)
    {
        uint session;
        var key = stackalloc char[33];
        var result = RmStartSession(out session, 0, key);
        if (result != 0) throw new System.ComponentModel.Win32Exception((int)result, "RmStartSession");
        var names = stackalloc char*[files.Length]; var allocated = 0;
        try
        {
            for (var i = 0; i < files.Length; i++) { names[i] = (char*)Marshal.StringToCoTaskMemUni(files[i]); allocated++; }
            result = RmRegisterResources(session, (uint)files.Length, names, 0, 0, 0, 0);
            if (result != 0) throw new System.ComponentModel.Win32Exception((int)result, "RmRegisterResources");
            uint count = 0;
            result = RmGetList(session, out var needed, ref count, null, out _);
            for (var attempt = 0; result == 234 && attempt < 3 && needed is > 0 and <= 256; attempt++)
            {
                var users = new ProcessInfo[needed]; count = needed;
                fixed (ProcessInfo* pointer = users) result = RmGetList(session, out needed, ref count, pointer, out _);
                if (result != 0) continue;
                Log.Info($"Update resource users count={count}; candidates for [{string.Join(", ", files.Select(Quote))}]");
                for (var i = 0; i < count; i++)
                {
                    var user = users[i];
                    var started = ((long)user.StartHigh << 32) | user.StartLow;
                    var image = "unavailable";
                    try
                    {
                        using var process = Process.GetProcessById((int)user.Pid);
                        if (process.StartTime.ToFileTimeUtc() == started) image = process.MainModule?.FileName ?? "unavailable";
                    }
                    catch (Exception) { }
                    Log.Info($"Update resource user pid={user.Pid} start-filetime={started} name={Quote(new string(user.Name))} image={Quote(image)}");
                }
                return;
            }
            if (result == 0) Log.Info("Update resource users count=0 (snapshot; the original lock may already have gone)");
            else throw new System.ComponentModel.Win32Exception((int)result, "RmGetList (bounded query)");
        }
        finally
        {
            for (var i = 0; i < allocated; i++) Marshal.FreeCoTaskMem((nint)names[i]);
            RmEndSession(session);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public uint Pid, StartLow, StartHigh;
        public fixed char Name[256];
        public fixed char Service[64];
        public uint Type, Status, Session;
        public int Restartable;
    }
    [DllImport("rstrtmgr.dll")] private static extern uint RmStartSession(out uint session, uint flags, char* key);
    [DllImport("rstrtmgr.dll")] private static extern uint RmEndSession(uint session);
    [DllImport("rstrtmgr.dll")] private static extern uint RmRegisterResources(uint session, uint count, char** files, uint applications, nint applicationList, uint services, nint serviceList);
    [DllImport("rstrtmgr.dll")] private static extern uint RmGetList(uint session, out uint needed, ref uint count, ProcessInfo* processes, out uint reasons);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
}
