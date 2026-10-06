namespace XamppRecoveryTool.Services;

public sealed class MySqlLogAnalyzer(Logger log, ProcessService processes, PortChecker ports)
{
    public async Task DiagnoseAsync(XamppInstallation x)
    {
        log.Write("Detecting XAMPP: " + x.Root);
        var state = await processes.SnapshotAsync();
        foreach (int port in new[] { 3306, x.Port }.Distinct())
        {
            log.Write($"Checking port {port}...");
            var owners = ports.Listeners(port);
            if (owners.Count == 0) log.Write($"Port {port}: FREE");
            foreach (var owner in owners)
            {
                var process = state.Processes.FirstOrDefault(p => p.Id == owner.ProcessId);
                log.Write($"Port {port}: LISTENING, PID {owner.ProcessId}, {process?.Name ?? "unknown"}, {process?.Path ?? "path unavailable"}");
            }
        }
        foreach (var process in state.Processes.Where(ProcessService.IsServer))
            log.Write($"MySQL process: PID {process.Id}, {process.Path ?? "unknown path"}, ownership={(processes.Owns(x, process) ? "selected XAMPP" : "unverified / other installation")}");
        foreach (var service in state.Services)
            log.Write($"Windows service: {service.Name}, {service.State}, PID {service.ProcessId}, {service.Path}");
        foreach (string name in new[] { "ibdata1", "ib_logfile0", "ib_logfile1", "aria_log_control" })
        {
            var file = new FileInfo(Path.Combine(x.Data, name));
            log.Write($"{name}: {(file.Exists ? file.Length + " bytes" : "MISSING (may depend on server version)")}");
        }
        // Read-only access check: no probe file is written into live database storage.
        var inventory = Safety.Inventory(x.Data);
        foreach (string relative in inventory.Files)
        {
            using var file = new FileStream(Safety.Child(x.Data, relative), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        log.Write($"Directory read access: OK ({inventory.Files.Count} files). Write/rename access is checked at each operation.");
        var users = Directory.EnumerateDirectories(x.Data).Select(Path.GetFileName).Where(n => n != null && !Safety.SystemDatabases.Contains(n));
        log.Write("User database directories: " + string.Join(", ", users));
        Tail(x);
        if (ports.Listeners(x.Port).Any(o => !state.Processes.Any(p => p.Id == o.ProcessId && processes.Owns(x, p))))
            throw new IOException($"Level 1: port {x.Port} is occupied by another or unverified process. Stop the named conflicting service in Windows Services, or change the XAMPP MySQL/client port together. Nothing was killed.");
    }

    public void Tail(XamppInstallation x)
    {
        if (!Directory.Exists(x.Data)) return;
        var paths = Directory.EnumerateFiles(x.Data, "*.err").Append(Path.Combine(x.Data, "mysql_error.log")).Where(File.Exists).Distinct();
        foreach (string path in paths)
        {
            Safety.NoLinks(path);
            log.Write("Reading " + Path.GetFileName(path) + " (last 40 lines, max 64 KiB)...");
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > 65536) file.Seek(-65536, SeekOrigin.End);
            using var reader = new StreamReader(file);
            string text = reader.ReadToEnd();
            foreach (string line in text.Split('\n').TakeLast(40)) if (!string.IsNullOrWhiteSpace(line)) log.Write("  " + line.TrimEnd());
            string lower = text.ToLowerInvariant();
            if (lower.Contains("address already in use") || lower.Contains("10048")) log.Write("Likely cause: port conflict.");
            if (lower.Contains("permission denied") || lower.Contains("operating system error number 5")) log.Write("Likely cause: filesystem permissions or security software.");
            if (lower.Contains("corrupt") || lower.Contains("crashed") || lower.Contains("is in the future") || lower.Contains("assertion failure")) log.Write("Possible storage/engine corruption. Old log lines are evidence, not proof of current failure.");
            if (lower.Contains("aria_log") || lower.Contains("aria:")) log.Write("Aria appears in logs. aria_log* will be retained as a coherent set; no blind deletion.");
        }
    }
}
