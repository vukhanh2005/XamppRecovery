using System.Diagnostics;

namespace XamppRecoveryTool.Services;

public sealed class BackupService(Logger log, ProcessService processes, MySqlDetector mysql)
{
    public async Task<BackupResult> CreateAsync(XamppInstallation x, Credentials credentials)
    {
        Safety.NoLinks(x.Backups);
        Directory.CreateDirectory(x.Backups);
        string folder = Safety.UniqueDirectory(x.Backups);
        Directory.CreateDirectory(folder);
        var manifest = new BackupManifest
        {
            Root = x.Root,
            BinaryHash = await Safety.HashAsync(x.Bin("mysqld")),
            ConfigHash = await Safety.HashAsync(x.Config)
        };
        File.Copy(x.Config, Path.Combine(folder, "my.ini.snapshot"), false);
        await Safety.SaveAsync(Path.Combine(folder, "manifest.json"), manifest);
        ReadLock? readLock = null;
        try
        {
            var state = await processes.SnapshotAsync();
            if (state.Processes.Any(p => processes.Owns(x, p)))
            {
                try
                {
                    await mysql.VerifyConnectionAsync(x, credentials);
                    // Hold one global read lock through shutdown: rebuilding from SQL must not lose
                    // writes made between mysqldump finishing and the physical backup starting.
                    readLock = await ReadLock.AcquireAsync(x, credentials);
                    manifest.Databases = await mysql.DatabasesAsync(x, credentials);
                    manifest.Tables = await mysql.TablesAsync(x, credentials);
                    log.Write("Creating SQL backup of all databases, including accounts, routines, events and triggers...");
                    string sql = Path.Combine(folder, "all-databases.sql");
                    var result = await processes.RunAsync(x.Bin("mysqldump"),
                        ProcessService.ClientArgs(x, credentials, "--default-character-set=utf8mb4", "--all-databases", "--routines", "--events", "--triggers", "--hex-blob", "--quick", "--lock-all-tables"),
                        credentials, TimeSpan.FromMinutes(30), outputFile: sql);
                    if (result.ExitCode != 0 || new FileInfo(sql).Length == 0 || !DumpComplete(sql))
                    {
                        manifest.SqlWarning = "Full SQL dump failed: " + ProcessService.Clip(result.Error);
                        log.Write(manifest.SqlWarning);
                        var users = manifest.Databases.Where(name => name is not ("mysql" or "sys")).ToList();
                        if (users.Count == 0) throw new IOException(manifest.SqlWarning);
                        readLock.AssertHeld();
                        manifest.SqlScope = "USER_DATABASES";
                        manifest.SqlFile = "user-databases.sql";
                        sql = Path.Combine(folder, manifest.SqlFile);
                        log.Write("Retrying SQL backup for user databases and phpMyAdmin only; mysql/sys tables remain in physical backup.");
                        result = await processes.RunAsync(x.Bin("mysqldump"),
                            ProcessService.ClientArgs(x, credentials, ["--default-character-set=utf8mb4", "--routines", "--events", "--triggers", "--hex-blob", "--quick", "--lock-all-tables", "--databases", "--", .. users]),
                            credentials, TimeSpan.FromMinutes(30), outputFile: sql);
                        if (result.ExitCode != 0 || new FileInfo(sql).Length == 0 || !DumpComplete(sql))
                            throw new IOException("User SQL backup failed: " + ProcessService.Clip(result.Error));
                    }
                    readLock.AssertHeld();
                    manifest.SqlHash = await Safety.HashAsync(sql);
                    manifest.SqlStatus = "SUCCESS";
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
                {
                    manifest.SqlStatus = "FAILED";
                    manifest.SqlHash = null;
                    if (readLock != null) { await readLock.DisposeAsync(); readLock = null; }
                    log.Write("SQL export unavailable: " + ex.Message);
                }
            }
            if (readLock != null) readLock.AssertHeld();
            await processes.StopAsync(x, credentials);
        }
        finally
        {
            if (readLock != null) await readLock.DisposeAsync();
            log.Write("SQL backup: " + manifest.SqlStatus);
            if (manifest.SqlStatus == "SUCCESS") log.Write("SQL backup scope: " + manifest.SqlScope + " (" + manifest.SqlFile + ")");
            await Safety.SaveAsync(Path.Combine(folder, "manifest.json"), manifest);
        }
        try
        {
            await processes.AssertStoppedAsync(x);
            log.Write("Creating physical backup...");
            var tree = await Safety.StampAsync(x.Data);
            if (tree.Files.Count == 0 || tree.Files.Sum(f => f.Length) == 0) throw new IOException("Source data directory is empty; refusing recovery.");
            long required = checked(tree.Files.Sum(f => f.Length) * 3 + 64L * 1024 * 1024);
            var drive = new DriveInfo(Path.GetPathRoot(x.Root)!);
            if (drive.AvailableFreeSpace < required) throw new IOException("Insufficient disk space. Need at least three times the data size plus 64 MiB for backup and staging.");
            await Safety.CopyVerifiedAsync(x.Data, Path.Combine(folder, "data"), tree);
            await processes.AssertStoppedAsync(x);
            Safety.Match(tree, await Safety.StampAsync(x.Data));
            manifest.Physical = tree;
            manifest.Complete = true;
            await Safety.SaveAsync(Path.Combine(folder, "manifest.json"), manifest);
            log.Write("Backup verified: full file inventory, sizes and SHA-256 match source and destination.");
            log.Write("Physical backup: SUCCESS");
            log.Write("Backup folder: " + folder);
            return new(folder, manifest);
        }
        catch
        {
            log.Write("Physical backup: FAILED. Recovery stopped. Partial backup retained for inspection.");
            throw;
        }
    }

    public async Task<BackupResult> VerifyAsync(XamppInstallation x, string folder)
    {
        Safety.NoLinks(folder);
        if (!Safety.SamePath(Path.GetDirectoryName(Path.GetFullPath(folder))!, x.Backups))
            throw new IOException("Select a timestamp folder inside this XAMPP's xampp_mysql_backups directory.");
        var manifest = await Safety.LoadAsync<BackupManifest>(Path.Combine(folder, "manifest.json"));
        if (manifest.Format != 1 || !manifest.Complete || !Safety.SamePath(manifest.Root, x.Root) || manifest.Physical.Files.Count == 0 || manifest.Physical.Files.Sum(f => f.Length) <= 0)
            throw new IOException("Backup is incomplete, empty, unsupported or belongs to a different installation.");
        foreach (var file in manifest.Physical.Files) Safety.Child(Path.Combine(folder, "data"), file.Path);
        foreach (var dir in manifest.Physical.Directories) Safety.Child(Path.Combine(folder, "data"), dir);
        if (manifest.BinaryHash != await Safety.HashAsync(x.Bin("mysqld")) || manifest.ConfigHash != await Safety.HashAsync(x.Config))
            throw new IOException("Server binary or my.ini differs from this backup. Automatic cross-version/configuration restore is unsafe.");
        Safety.Match(manifest.Physical, await Safety.StampAsync(Path.Combine(folder, "data")));
        log.Write("Selected physical backup verified: " + folder);
        return new(folder, manifest);
    }

    public async Task VerifySqlAsync(BackupResult backup)
    {
        if (backup.Manifest.SqlStatus != "SUCCESS" || backup.Manifest.SqlScope != "ALL_DATABASES" ||
            backup.Manifest.SqlFile != "all-databases.sql" || backup.Manifest.SqlHash == null ||
            await Safety.HashAsync(Path.Combine(backup.Folder, "all-databases.sql")) != backup.Manifest.SqlHash)
            throw new IOException("Complete, consistent SQL backup is unavailable. InnoDB rebuild refused.");
    }

    private static bool DumpComplete(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 8192) file.Seek(-8192, SeekOrigin.End);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd().Contains("-- Dump completed on", StringComparison.Ordinal);
    }

    private sealed class ReadLock : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task<string> errors;
        private ReadLock(Process process) { this.process = process; errors = process.StandardError.ReadToEndAsync(); }

        public static async Task<ReadLock> AcquireAsync(XamppInstallation x, Credentials credentials)
        {
            var info = new ProcessStartInfo(x.Bin("mysql"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string arg in ProcessService.ClientArgs(x, credentials, "--batch", "--skip-column-names", "--unbuffered")) info.ArgumentList.Add(arg);
            info.Environment["MYSQL_PWD"] = credentials.Password;
            var guard = new ReadLock(Process.Start(info) ?? throw new IOException("Cannot acquire SQL read lock."));
            try
            {
                await guard.process.StandardInput.WriteLineAsync("FLUSH TABLES WITH READ LOCK; SELECT 'XAMPP_RECOVERY_LOCKED';");
                await guard.process.StandardInput.FlushAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                string? line = await guard.process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line != "XAMPP_RECOVERY_LOCKED") throw new IOException("Global read lock was not acquired. Check database privileges and active transactions.");
                guard.AssertHeld();
                return guard;
            }
            catch { await guard.DisposeAsync(); throw; }
        }

        public void AssertHeld()
        {
            if (process.HasExited) throw new IOException("SQL lock session was interrupted; rebuild is unsafe.");
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                try { process.StandardInput.Close(); }
                catch (IOException) { }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); }
            }
            await errors;
            process.Dispose();
        }
    }
}
