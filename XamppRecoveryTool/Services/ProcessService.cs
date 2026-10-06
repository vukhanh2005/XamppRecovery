using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace XamppRecoveryTool.Services;

public sealed record CommandResult(int ExitCode, string Output, string Error);

public sealed class ProcessService(Logger log, PortChecker ports)
{
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public async Task<MachineState> SnapshotAsync() => await Task.Run(() =>
    {
        var processes = new List<ProcessInfo>();
        var services = new List<ServiceInfo>();
        object? locator = null, connection = null;
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!);
            connection = ((dynamic)locator!).ConnectServer(".", @"root\cimv2");
            Read("SELECT ProcessId,Name,ExecutablePath,CommandLine FROM Win32_Process WHERE Name='mysqld.exe' OR Name='mariadbd.exe' OR Name='httpd.exe'", item =>
            {
                processes.Add(new(Convert.ToInt32(Property(item, "ProcessId")), TextOrNull(Property(item, "Name")) ?? "unknown",
                    TextOrNull(Property(item, "ExecutablePath", true)), TextOrNull(Property(item, "CommandLine", true))));
            });
            Read("SELECT Name,State,ProcessId,PathName FROM Win32_Service WHERE PathName LIKE '%mysqld%' OR PathName LIKE '%mariadbd%'", item =>
            {
                string path = TextOrNull(Property(item, "PathName", true)) ?? "";
                if (path.Contains("mysqld", StringComparison.OrdinalIgnoreCase) || path.Contains("mariadbd", StringComparison.OrdinalIgnoreCase))
                    services.Add(new(TextOrNull(Property(item, "Name"))!, TextOrNull(Property(item, "State"))!, Convert.ToInt32(Property(item, "ProcessId")), path));
            });
            void Read(string query, Action<dynamic> read)
            {
                object collection = ((dynamic)connection).ExecQuery(query);
                try
                {
                    foreach (object item in (System.Collections.IEnumerable)collection)
                    {
                        try { read(item); }
                        finally { Marshal.FinalReleaseComObject(item); }
                    }
                }
                finally { Marshal.FinalReleaseComObject(collection); }
            }
        }
        catch (Exception ex) { throw new IOException("Cannot verify process/service ownership. Try Run as administrator. " + ex.Message, ex); }
        finally
        {
            if (connection != null) Marshal.FinalReleaseComObject(connection);
            if (locator != null) Marshal.FinalReleaseComObject(locator);
        }
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!processes.Any(p => p.Id == process.Id)) processes.Add(new(process.Id, process.ProcessName + ".exe", null, null));
                }
                catch (InvalidOperationException) { }
            }
        }
        return new MachineState(processes, services);
    });

    private static string? TextOrNull(object? value) => value is null or DBNull ? null : Convert.ToString(value);

    private static object? Property(object item, string name, bool optional = false)
    {
        object? properties = null, property = null;
        try
        {
            properties = ((dynamic)item).Properties_;
            property = ((dynamic)properties).Item(name);
            return ((dynamic)property).Value;
        }
        catch (COMException) when (optional) { return null; }
        finally
        {
            if (property != null) Marshal.FinalReleaseComObject(property);
            if (properties != null) Marshal.FinalReleaseComObject(properties);
        }
    }

    public static bool IsServer(ProcessInfo p) => p.Name.Equals("mysqld.exe", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("mariadbd.exe", StringComparison.OrdinalIgnoreCase);

    public bool Owns(XamppInstallation x, ProcessInfo p)
    {
        if (p.Path == null || p.CommandLine == null || !Safety.SamePath(p.Path, x.Bin("mysqld"))) return false;
        string[] args = SplitArguments(p.CommandLine);
        bool explicitConfiguration = false;
        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i].Replace('_', '-');
            string key = arg.Split('=', 2)[0].ToLowerInvariant();
            if (key is "--no-defaults" or "--innodb-data-file-path" or "--innodb-directories" or "--innodb-undo-directory") return false;
            if (key is not ("--datadir" or "--defaults-file" or "--defaults-extra-file" or "--innodb-data-home-dir" or "--innodb-log-group-home-dir" or "--aria-log-dir-path")) continue;
            string value = args[i].Contains('=') ? args[i].Split('=', 2)[1] : i + 1 < args.Length ? args[++i] : "";
            if (key == "--defaults-extra-file") return false;
            string expected = key == "--defaults-file" ? x.Config : x.Data;
            if (!Path.IsPathFullyQualified(value) || !Safety.SamePath(value, expected)) return false;
            if (key == "--defaults-file") explicitConfiguration = true;
        }
        return explicitConfiguration;
    }

    public static string[] SplitArguments(string commandLine)
    {
        IntPtr pointer = CommandLineToArgvW(commandLine, out int count);
        if (pointer == IntPtr.Zero) throw new IOException("Cannot parse process command line.");
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(pointer); }
    }

    public async Task AssertStoppedAsync(XamppInstallation x)
    {
        var state = await SnapshotAsync();
        foreach (var process in state.Processes.Where(IsServer))
            if (process.Path == null || Safety.SamePath(process.Path, x.Bin("mysqld")))
                throw new IOException($"MySQL process PID {process.Id} remains running or cannot be identified. No file changes allowed.");
        if (ports.Listeners(x.Port).Count != 0) throw new IOException($"Port {x.Port} is occupied. No data change allowed.");
    }

    public async Task StopAsync(XamppInstallation x, Credentials credentials)
    {
        log.Write("Stopping MySQL gracefully...");
        var state = await SnapshotAsync();
        var related = state.Processes.Where(IsServer).Where(p => p.Path == null || Safety.SamePath(p.Path, x.Bin("mysqld"))).ToList();
        if (related.Any(p => !Owns(x, p)))
            throw new IOException("A MySQL process cannot be safely assigned to this data directory. Stop it manually or retry as administrator.");
        if (related.Count > 1) throw new IOException("Multiple instances use this server binary. Stop them manually before recovery.");
        if (related.Count == 1)
        {
            string? portArg = SplitArguments(related[0].CommandLine!).LastOrDefault(a => a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase));
            if (portArg != null && int.TryParse(portArg[7..], out int actualPort)) x = x with { Port = actualPort };
        }
        foreach (var owner in ports.Listeners(x.Port))
            if (!related.Any(p => p.Id == owner.ProcessId)) throw new IOException($"Port conflict: PID {owner.ProcessId}. Unrelated processes will not be stopped.");
        foreach (var service in state.Services.Where(s => related.Any(p => p.Id == s.ProcessId)))
        {
            log.Write("Requesting Windows service stop: " + service.Name);
            var stopped = await RunAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["stop", service.Name], null, TimeSpan.FromSeconds(20));
            log.Write($"Service stop exit code: {stopped.ExitCode}");
        }
        if (related.Count > 0 && ports.Listeners(x.Port).Count > 0)
        {
            var result = await RunAsync(x.Bin("mysqladmin"), ClientArgs(x, credentials, "shutdown"), credentials, TimeSpan.FromSeconds(30));
            if (result.ExitCode != 0) log.Write("mysqladmin shutdown failed: " + Clip(result.Error));
        }
        for (int i = 0; i < 30; i++)
        {
            bool alive = related.Any(p => IsAlive(p.Id));
            if (!alive) { await AssertStoppedAsync(x); return; }
            await Task.Delay(1000);
        }
        throw new IOException("MySQL did not stop safely within 30 seconds. No force-kill performed. Resolve shutdown before retrying.");
    }

    private static bool IsAlive(int id)
    {
        try { using var p = Process.GetProcessById(id); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    public async Task<int> StartAsync(XamppInstallation x, bool isolated = false)
    {
        await AssertStoppedAsync(x);
        log.Write(isolated ? "Starting isolated local recovery server (events and replication disabled)..." : "Starting MySQL...");
        var arguments = new List<string>();
        foreach (string arg in new[] { "--defaults-file=" + x.Config, "--standalone", "--basedir=" + x.Mysql, "--datadir=" + x.Data,
            "--port=" + x.Port, "--innodb-data-home-dir=" + x.Data, "--innodb-log-group-home-dir=" + x.Data,
            "--aria-log-dir-path=" + x.Data, "--log-error=" + Path.Combine(x.Data, "mysql_error.log"), "--pid-file=" + Path.Combine(x.Data, "mysql.pid") })
            arguments.Add(arg);
        if (isolated)
            foreach (string arg in new[] { "--bind-address=127.0.0.1", "--event-scheduler=OFF", "--skip-slave-start", "--skip-log-bin" }) arguments.Add(arg);
        return StartDetached(x.Bin("mysqld"), arguments, x.Root);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, Width, Height, CharsX, CharsY, Fill, Flags;
        public short Show, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref SecurityAttributes security, uint disposition, uint attributes, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private static int StartDetached(string executable, List<string> arguments, string directory)
    {
        // A WinForms parent may have invalid console handles. Inherit real NUL handles,
        // not pipes: mysqld must survive closing the UI. Its --log-error remains on disk.
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
        using var sink = CreateFileW("NUL", 0xC0000000, 3, ref security, 3, 0, IntPtr.Zero);
        if (sink.IsInvalid) throw new IOException("Cannot create server standard handles.", new System.ComponentModel.Win32Exception());
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 0x100,
            Input = sink.DangerousGetHandle(), Output = sink.DangerousGetHandle(), Error = sink.DangerousGetHandle() };
        // Windows file paths cannot contain double quotes. These arguments all come from
        // validated paths, numeric ports and fixed options; no shell parses this command.
        var all = new[] { executable }.Concat(arguments);
        if (all.Any(a => a.Contains('"') || a.EndsWith('\\'))) throw new IOException("Unsupported server argument.");
        var command = new StringBuilder(string.Join(" ", all.Select(a => "\"" + a + "\"")));
        if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08000000, IntPtr.Zero, directory, ref startup, out var process))
            throw new IOException("Cannot start mysqld.", new System.ComponentModel.Win32Exception());
        CloseHandle(process.Thread); CloseHandle(process.Process);
        return process.ProcessId;
    }

    public static List<string> ClientArgs(XamppInstallation x, Credentials credentials, params string[] extra)
        => ["--no-defaults", "--protocol=TCP", "--host=127.0.0.1", "--port=" + x.Port, "--user=" + credentials.User, .. extra];

    public static string Clip(string value) => value.Length <= 1200 ? value.Trim() : value[..1200].Trim() + "...";

    public async Task<CommandResult> RunAsync(string executable, IEnumerable<string> args, Credentials? credentials,
        TimeSpan timeout, string? outputFile = null, string? inputFile = null, string? inputText = null, bool terminateOnFailure = true)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = inputFile != null || inputText != null,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = inputFile != null || inputText != null ? new UTF8Encoding(false) : null
        };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        // Child-only environment avoids passwords in command lines, logs and temporary option files.
        info.Environment.Remove("MYSQL_PWD");
        if (credentials != null) info.Environment["MYSQL_PWD"] = credentials.Password;
        using var process = Process.Start(info) ?? throw new IOException("Cannot start " + Path.GetFileName(executable));
        using var cancellation = new CancellationTokenSource(timeout);
        var token = cancellation.Token;
        Task<string> errorTask = process.StandardError.ReadToEndAsync(token);
        Task<string> outputTask = ReadOutput();
        Task inputTask = WriteInput();
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(token), outputTask, errorTask, inputTask);
            return new(process.ExitCode, await outputTask, await errorTask);
        }
        catch
        {
            // Offline bootstrap is also mysqld: never force-stop it. Pending rollback must
            // wait for safe shutdown, just as it does for a regular server instance.
            if (terminateOnFailure && !process.HasExited) process.Kill();
            cancellation.Cancel();
            if (terminateOnFailure) await process.WaitForExitAsync();
            try { await Task.WhenAll(outputTask, errorTask, inputTask); } catch { }
            throw;
        }
        async Task<string> ReadOutput()
        {
            if (outputFile == null) return await process.StandardOutput.ReadToEndAsync(token);
            await using var stream = new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            await process.StandardOutput.BaseStream.CopyToAsync(stream, token);
            await stream.FlushAsync(token); stream.Flush(true);
            return "";
        }
        async Task WriteInput()
        {
            if (inputText != null)
            {
                await process.StandardInput.WriteAsync(inputText.AsMemory(), token);
                process.StandardInput.Close();
                return;
            }
            if (inputFile == null) return;
            await using var stream = File.OpenRead(inputFile);
            await stream.CopyToAsync(process.StandardInput.BaseStream, token);
            process.StandardInput.Close();
        }
    }
}
