using System.Net;
using System.Net.Sockets;

namespace XamppRecoveryTool.Services;

public sealed class RecoveryService(Logger log, ProcessService processes, MySqlDetector mysql, BackupService backups, MySqlLogAnalyzer analyzer)
{
    public string? LastWarning { get; private set; }

    public async Task RecoverAsync(XamppInstallation x, Credentials credentials)
    {
        using var operationLock = Safety.Acquire(x);
        await ResumeInterruptedAsync(x, credentials);
        await analyzer.DiagnoseAsync(x);
        BackupResult backup = await backups.CreateAsync(x, credentials);
        var plan = await PrepareAsync(x, backup, "Recovery");
        var privateServer = x with { Port = FreePort() };
        Credentials activeCredentials = credentials;
        try
        {
            await ActivateAsync(x, plan);
            log.Write("Repair strategy: Level 2 - test a verified complete copy; engine files remain together.");
            // PID files are runtime markers, not database contents. Remove only in the candidate,
            // after verified backup and confirmed shutdown; original files stay in data_broken.
            foreach (string file in Directory.EnumerateFiles(x.Data, "*.pid")) File.Delete(file);
            string errorLog = Path.Combine(x.Data, "mysql_error.log");
            long logOffset = File.Exists(errorLog) ? new FileInfo(errorLog).Length : 0;
            try
            {
                int id = await processes.StartAsync(privateServer, true);
                await mysql.WaitHealthyAsync(privateServer, credentials, id);
                await processes.StopAsync(privateServer, credentials);
            }
            catch (Exception ex)
            {
                log.Write("Level 2 startup failed: " + ex.Message);
                analyzer.Tail(x);
                await processes.StopAsync(privateServer, credentials);
                if (await TryRepairProxyPrivilegesAsync(x, plan, logOffset))
                {
                    int repairedId = await processes.StartAsync(privateServer, true);
                    await mysql.WaitHealthyAsync(privateServer, credentials, repairedId);
                    string check = await mysql.QueryAsync(privateServer, credentials, "CHECK TABLE mysql.proxies_priv EXTENDED;");
                    log.Write(check);
                    if (!check.EndsWith("\tstatus\tOK", StringComparison.Ordinal))
                        throw new IOException("Repaired privilege table did not pass CHECK TABLE.");
                    // Export user data even when another damaged system table blocks a full dump.
                    // Keep those system tables untouched and report the unresolved fault explicitly.
                    var repairedBackup = await backups.CreateAsync(privateServer, credentials);
                    if (repairedBackup.Manifest.SqlStatus != "SUCCESS")
                        throw new IOException("Privilege repair succeeded, but user SQL backup failed. Rolling back for inspection.");
                    LastWarning = repairedBackup.Manifest.SqlWarning == null ? null :
                        "MySQL startup restored; system-table errors remain. " + repairedBackup.Manifest.SqlWarning +
                        "\nUser SQL backup: " + Path.Combine(repairedBackup.Folder, repairedBackup.Manifest.SqlFile) +
                        "\nDamaged system tables were preserved. Do not reset grants or replace the mysql database without reviewing permissions.";
                    if (LastWarning != null) log.Write("WARNING: " + LastWarning);
                }
                else
                {
                    await backups.VerifySqlAsync(backup);
                    log.Write("Repair strategy: Level 3 - rebuild from a coherent system template and import the complete SQL snapshot.");
                    await RebuildAsync(x, plan);
                    activeCredentials = new("root", "");
                    int id = await processes.StartAsync(privateServer, true);
                    await mysql.WaitHealthyAsync(privateServer, activeCredentials, id);
                    var result = await processes.RunAsync(x.Bin("mysql"), ProcessService.ClientArgs(privateServer, activeCredentials, "--binary-mode"),
                        activeCredentials, TimeSpan.FromMinutes(30), inputFile: Path.Combine(backup.Folder, "all-databases.sql"));
                    activeCredentials = credentials;
                    if (result.ExitCode != 0) throw new IOException("SQL restore failed: " + ProcessService.Clip(result.Error));
                    // mysql grants are part of all-databases.sql. Reload them before reconnecting with
                    // the original credentials, so a successful rebuild also preserves user accounts.
                    try { await mysql.QueryAsync(privateServer, new("root", ""), "FLUSH PRIVILEGES;"); }
                    catch (IOException) { await mysql.QueryAsync(privateServer, credentials, "FLUSH PRIVILEGES;"); }
                    if (!(await mysql.DatabasesAsync(privateServer, credentials)).SequenceEqual(backup.Manifest.Databases) ||
                        !(await mysql.TablesAsync(privateServer, credentials)).SequenceEqual(backup.Manifest.Tables))
                        throw new IOException("Restored database/table inventory differs from the SQL snapshot.");
                    await processes.StopAsync(privateServer, credentials);
                }
            }
            await FinalStartAsync(x, credentials, plan);
        }
        catch (Exception ex)
        {
            log.Write("MySQL still cannot start: " + ex.Message);
            analyzer.Tail(x);
            await RollbackAsync(x, plan, activeCredentials, privateServer);
            throw new IOException("MySQL still cannot start. Original data restored; all backups and failed candidates retained.\nCause: " + ex.Message, ex);
        }
    }

    private async Task<bool> TryRepairProxyPrivilegesAsync(XamppInstallation x, RecoveryPlan plan, long logOffset)
    {
        string errorLog = Path.Combine(x.Data, "mysql_error.log");
        if (!File.Exists(errorLog)) return false;
        Safety.NoLinks(errorLog);
        using var stream = new FileStream(errorLog, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Only the just-failed startup may select this strategy; old log errors cannot.
        if (stream.Length < logOffset) return false;
        stream.Position = Math.Max(logOffset, stream.Length - 65536);
        using var reader = new StreamReader(stream);
        string recent = await reader.ReadToEndAsync();
        if (!recent.Contains("Can't open and lock privilege tables: Incorrect file format 'proxies_priv'", StringComparison.Ordinal)) return false;
        reader.Dispose(); stream.Dispose();

        await processes.AssertStoppedAsync(x);
        await backups.VerifyAsync(x, plan.BackupFolder);
        foreach (string extension in new[] { ".frm", ".MAD", ".MAI" })
        {
            string file = Path.Combine(x.Data, "mysql", "proxies_priv" + extension);
            Safety.NoLinks(file);
            if (!File.Exists(file) || new FileInfo(file).Length == 0)
                throw new IOException("Cannot repair proxies_priv: schema or Aria data/index file is missing or empty.");
        }
        string checker = x.Bin("aria_chk");
        Safety.NoLinks(checker);
        if (!File.Exists(checker)) throw new IOException("aria_chk.exe is required to verify the targeted Aria repair.");
        var before = await Safety.StampAsync(x.Data);
        string schemaPath = Path.Combine(x.Data, "mysql", "proxies_priv.frm");
        byte[] schemaBefore = await File.ReadAllBytesAsync(schemaPath);
        await SavePlanAsync(x, plan, "RepairingProxyPrivileges");
        log.Write("Repair strategy: Level 2 - repair mysql.proxies_priv Aria index from its own .frm and .MAD; preserve accounts and InnoDB.");
        // USE_FRM reconstructs the damaged Aria index using this table's own schema/data.
        // No template files or default grants are substituted. Bootstrap accepts SQL on
        // stdin only; networking and InnoDB are disabled, so no unauthenticated listener
        // is exposed and no InnoDB tablespace/redo is rebuilt during this repair.
        var repair = await processes.RunAsync(x.Bin("mysqld"),
            ["--defaults-file=" + x.Config, "--bootstrap", "--standalone", "--skip-grant-tables", "--skip-networking",
             "--skip-innodb", "--default-storage-engine=Aria", "--event-scheduler=OFF", "--skip-slave-start", "--skip-log-bin",
             "--basedir=" + x.Mysql, "--datadir=" + x.Data, "--aria-log-dir-path=" + x.Data,
             "--log-error=" + errorLog, "--console"],
            null, TimeSpan.FromMinutes(2), inputText: "REPAIR TABLE mysql.proxies_priv USE_FRM;\n", terminateOnFailure: false);
        log.Write("Offline privilege repair: " + repair.Output + "\n" + repair.Error);
        if (repair.ExitCode != 0) throw new IOException("Offline privilege repair failed; rollback required.");
        await processes.AssertStoppedAsync(x);
        var after = await Safety.StampAsync(x.Data);
        byte[] schemaAfter = await File.ReadAllBytesAsync(schemaPath);
        if (!schemaBefore.SequenceEqual(schemaAfter))
        {
            // MariaDB upgrades the creator-version field (4 bytes at .frm offset 51)
            // when repairing older tables. Permit only that exact version refresh;
            // all remaining schema bytes must remain identical.
            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(x.Bin("mysqld"));
            uint serverVersion = (uint)(version.FileMajorPart * 10000 + version.FileMinorPart * 100 + version.FileBuildPart);
            if (schemaBefore.Length < 55 || schemaBefore.Length != schemaAfter.Length ||
                !schemaBefore.AsSpan(0, 51).SequenceEqual(schemaAfter.AsSpan(0, 51)) ||
                !schemaBefore.AsSpan(55).SequenceEqual(schemaAfter.AsSpan(55)) ||
                BitConverter.ToUInt32(schemaBefore, 51) > serverVersion ||
                BitConverter.ToUInt32(schemaAfter, 51) != serverVersion)
                throw new IOException("Privilege table schema changed beyond the MariaDB creator-version header; rollback required.");
            log.Write("proxies_priv.frm: server-version header refreshed; table schema bytes unchanged.");
        }
        static bool Protected(FileStamp file)
        {
            string path = file.Path.Replace('\\', '/');
            if (path.Equals("mysql/proxies_priv.MAI", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("mysql/proxies_priv.MAD", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("mysql/proxies_priv.frm", StringComparison.OrdinalIgnoreCase)) return false;
            return path.Contains('/') || (!path.StartsWith("aria_log", StringComparison.OrdinalIgnoreCase) &&
                !path.Equals("mysql_error.log", StringComparison.OrdinalIgnoreCase));
        }
        Safety.Match(new(before.Directories, before.Files.Where(Protected).ToList()), new(after.Directories, after.Files.Where(Protected).ToList()));
        var check = await processes.RunAsync(checker,
            ["--no-defaults", "--datadir=" + x.Data, "--check", "--read-only", Path.Combine(x.Data, "mysql", "proxies_priv.MAI")],
            null, TimeSpan.FromMinutes(2));
        log.Write("Aria verification: " + ProcessService.Clip(check.Output + "\n" + check.Error));
        if (check.ExitCode != 0 || check.Error.Contains("error:", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Aria verification failed after privilege repair.");
        log.Write("Privilege repair verified. User database files, account tables and InnoDB files unchanged by offline repair (SHA-256).");
        await SavePlanAsync(x, plan, "CandidateActive");
        return true;
    }

    public async Task<RecoveryPlan> PrepareAsync(XamppInstallation x, BackupResult backup, string operation)
    {
        LastWarning = backup.Manifest.SqlWarning;
        await processes.AssertStoppedAsync(x);
        await backups.VerifyAsync(x, backup.Folder);
        var plan = new RecoveryPlan
        {
            Root = x.Root, BackupFolder = backup.Folder, Operation = operation,
            Original = Safety.UniqueDirectory(x.Mysql, "data_broken_"),
            Candidate = Safety.UniqueDirectory(x.Mysql, "data_recovery_")
        };
        await SavePlanAsync(x, plan, "Preparing");
        await Safety.CopyVerifiedAsync(Path.Combine(backup.Folder, "data"), plan.Candidate, backup.Manifest.Physical);
        await SavePlanAsync(x, plan, "Prepared");
        return plan;
    }

    public async Task ActivateAsync(XamppInstallation x, RecoveryPlan plan)
    {
        await processes.AssertStoppedAsync(x);
        var originalBackup = await backups.VerifyAsync(x, plan.BackupFolder);
        Safety.Match(originalBackup.Manifest.Physical, await Safety.StampAsync(x.Data));
        Safety.NoLinks(x.Data); Safety.NoLinks(plan.Original); Safety.NoLinks(plan.Candidate);
        await SavePlanAsync(x, plan, "MovingOriginal");
        Directory.Move(x.Data, plan.Original);
        await SavePlanAsync(x, plan, "OriginalMoved");
        Directory.Move(plan.Candidate, x.Data);
        await SavePlanAsync(x, plan, "CandidateActive");
    }

    public async Task FinalStartAsync(XamppInstallation x, Credentials credentials, RecoveryPlan plan)
    {
        await SavePlanAsync(x, plan, "Starting");
        int id = await processes.StartAsync(x);
        await mysql.WaitHealthyAsync(x, credentials, id);
        await SavePlanAsync(x, plan, "Committed");
        log.Write(LastWarning == null ? "MySQL recovered successfully. Process, port and authenticated connection verified." :
            "MySQL started with warnings. Process, port and authenticated connection verified; system-table review still required.");
        log.Write("Original data retained: " + plan.Original);
    }

    private async Task RebuildAsync(XamppInstallation x, RecoveryPlan plan)
    {
        if (!Directory.Exists(x.Template)) throw new IOException("mysql\\backup template is missing. Original data will be restored.");
        var tree = await Safety.StampAsync(x.Template);
        if (tree.Files.Count == 0 || !Directory.Exists(Path.Combine(x.Template, "mysql"))) throw new IOException("Invalid system template.");
        foreach (string dir in Directory.EnumerateDirectories(x.Template))
            if (!Safety.SystemDatabases.Contains(Path.GetFileName(dir)))
                throw new IOException("Template contains a user database; cannot assume it is clean: " + Path.GetFileName(dir));
        string rebuilt = Safety.UniqueDirectory(x.Mysql, "data_rebuild_");
        // Keep the template's ibdata1, redo and aria_log* as one coherent set. Never splice
        // old .ibd / shared tablespaces into a new dictionary. User data is restored only
        // through SQL; failed templates are rolled back instead of deleting engine files.
        await Safety.CopyVerifiedAsync(x.Template, rebuilt, tree);
        await processes.AssertStoppedAsync(x);
        await SavePlanAsync(x, plan, "Rebuilding");
        Directory.Move(x.Data, Safety.UniqueDirectory(x.Mysql, "data_failed_"));
        Directory.Move(rebuilt, x.Data);
        await SavePlanAsync(x, plan, "CandidateActive");
    }

    public async Task RollbackAsync(XamppInstallation x, RecoveryPlan plan, Credentials credentials, XamppInstallation? privateServer = null)
    {
        log.Write("Rolling back recovery transaction...");
        try
        {
            bool neverActivated = plan.Stage is "Preparing" or "Prepared" or "MovingOriginal";
            if (privateServer != null) await processes.StopAsync(privateServer, credentials);
            await processes.StopAsync(x, credentials);
            await SavePlanAsync(x, plan, "RollingBack");
            if (Directory.Exists(plan.Original))
            {
                await processes.AssertStoppedAsync(x);
                Safety.NoLinks(plan.Original); Safety.NoLinks(x.Data);
                if (Directory.Exists(x.Data)) Directory.Move(x.Data, Safety.UniqueDirectory(x.Mysql, "data_failed_"));
                Directory.Move(plan.Original, x.Data);
            }
            else if (!Directory.Exists(x.Data) || !neverActivated)
            {
                var source = await backups.VerifyAsync(x, plan.BackupFolder);
                string stage = Safety.UniqueDirectory(x.Mysql, "data_rollback_");
                await Safety.CopyVerifiedAsync(Path.Combine(source.Folder, "data"), stage, source.Manifest.Physical);
                if (Directory.Exists(x.Data)) Directory.Move(x.Data, Safety.UniqueDirectory(x.Mysql, "data_failed_"));
                Directory.Move(stage, x.Data);
            }
            await SavePlanAsync(x, plan, "RolledBack");
            log.Write("Rollback complete. Original data is in mysql\\data. MySQL remains stopped for inspection.");
        }
        catch (Exception ex)
        {
            log.Write("ROLLBACK PENDING: " + ex.Message);
            log.Write("Keep data_broken and backup folders. Retry Restore Previous Backup after resolving permissions/shutdown. Journal: " + x.Journal);
            throw new IOException("Rollback could not finish safely. Original data and backup remain intact; recovery-plan.json records the pending transaction.", ex);
        }
    }

    public async Task ResumeInterruptedAsync(XamppInstallation x, Credentials credentials)
    {
        if (!File.Exists(x.Journal)) return;
        var plan = await Safety.LoadAsync<RecoveryPlan>(x.Journal);
        if (plan.Stage is "Committed" or "RolledBack") return;
        if (!Safety.SamePath(x.Root, plan.Root) || !SafeSibling(x, plan.Original, "data_broken_") || !SafeSibling(x, plan.Candidate, "data_recovery_"))
            throw new IOException("Recovery journal has invalid paths; manual inspection required.");
        log.Write("Interrupted transaction detected: " + plan.Stage + ". Restoring original state before proceeding.");
        // An interrupted private-port server is stopped through its verified listener port.
        var state = await processes.SnapshotAsync();
        var owned = state.Processes.Where(p => processes.Owns(x, p)).ToList();
        foreach (var process in owned)
        {
            string? arg = ProcessService.SplitArguments(process.CommandLine!).LastOrDefault(a => a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase));
            if (arg != null && int.TryParse(arg[7..], out int port) && port != x.Port)
                await processes.StopAsync(x with { Port = port }, credentials);
        }
        await RollbackAsync(x, plan, credentials);
    }

    private static bool SafeSibling(XamppInstallation x, string path, string prefix) =>
        Path.IsPathFullyQualified(path) && Safety.SamePath(Path.GetDirectoryName(path)!, x.Mysql) && Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal);

    private async Task SavePlanAsync(XamppInstallation x, RecoveryPlan plan, string stage)
    {
        plan.Stage = stage;
        await Safety.SaveAsync(x.Journal, plan);
        try
        {
            Directory.CreateDirectory(Path.Combine(x.Backups, "plans"));
            await Safety.SaveAsync(Path.Combine(x.Backups, "plans", Path.GetFileName(plan.Original) + ".json"), plan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write("Plan archive unavailable; authoritative recovery-plan.json is saved. " + ex.Message);
        }
        log.Write("Recovery plan: " + stage);
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
