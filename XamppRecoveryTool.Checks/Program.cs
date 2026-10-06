using System.Net;
using System.Net.Sockets;
using System.Text;
using XamppRecoveryTool;
using XamppRecoveryTool.Services;

internal static class Program
{
    private static int passed;
    private static string root = "";

    [STAThread]
    private static int Main(string[] args)
    {
        root = Path.Combine(Path.GetFullPath("test-artifacts"), DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        try
        {
            RunAsync(args).GetAwaiter().GetResult();
            ApplicationConfiguration.Initialize();
            using var log = new Logger(Path.Combine(root, "ui-logs"));
            using var form = new MainForm(log, Path.Combine(root, "fixture"));
            form.Show(); Application.DoEvents();
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, form.ClientRectangle with { Width = form.Width, Height = form.Height });
            image.Save(Path.Combine(root, "ui-preview.png"));
            form.Close();
            Console.WriteLine($"PASS: {passed} checks. Artifacts: {root}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Console.Error.WriteLine("Artifacts retained: " + root); return 1; }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
        passed++; Console.WriteLine("PASS: " + name);
    }

    private static async Task Reject(Func<Task> action, string name)
    {
        try { await action(); }
        catch (IOException) { Check(true, name); return; }
        throw new Exception("FAILED: expected refusal: " + name);
    }

    private static async Task RunAsync(string[] args)
    {
        string fixture = Path.Combine(root, "fixture");
        string data = Path.Combine(fixture, "mysql", "data");
        Directory.CreateDirectory(Path.Combine(data, "user_db", "empty"));
        Directory.CreateDirectory(Path.Combine(fixture, "mysql", "bin"));
        foreach (string exe in new[] { "mysqld", "mysql", "mysqladmin", "mysqldump" })
            await File.WriteAllTextAsync(Path.Combine(fixture, "mysql", "bin", exe + ".exe"), "fixture - not executable");
        await File.WriteAllBytesAsync(Path.Combine(data, "ibdata1"), Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray());
        await File.WriteAllTextAsync(Path.Combine(data, "user_db", "table.ibd"), "user table content");
        await File.WriteAllTextAsync(Path.Combine(data, "mysql_error.log"), "old example error: address already in use\n");
        string config = Path.Combine(fixture, "mysql", "bin", "my.ini");
        int freePort = RecoveryService.FreePort();
        await File.WriteAllTextAsync(config, $"[mysqld]\nport={freePort}\ndatadir=\"{data.Replace('\\', '/')}\"\n");
        var detector = new XamppDetector();
        var x = detector.Detect(fixture);
        Check(x.Port == freePort, "configured port detection");
        var original = await Safety.StampAsync(data);
        using var logger = new Logger(Path.Combine(root, "logs"));
        logger.Message += Console.WriteLine;
        var ports = new PortChecker();
        var process = new ProcessService(logger, ports);
        var mysql = new MySqlDetector(process, ports);
        var backups = new BackupService(logger, process, mysql);
        var analyzer = new MySqlLogAnalyzer(logger, process, ports);
        var recovery = new RecoveryService(logger, process, mysql, backups, analyzer);
        var credentials = new Credentials("root", "");

        var state = await process.SnapshotAsync();
        Check(state.Processes.Any(p => p.Id == Environment.ProcessId), "native Windows WMI process inventory");
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Check(ports.Listeners(port).Any(p => p.ProcessId == Environment.ProcessId), "IPv4 port owner PID");
            await Reject(() => process.StopAsync(x with { Port = port }, credentials), "unrelated listener is never stopped");
        }
        using (var listener = new TcpListener(IPAddress.IPv6Loopback, 0))
        {
            listener.Start();
            Check(ports.Listeners(((IPEndPoint)listener.LocalEndpoint).Port).Any(p => p.ProcessId == Environment.ProcessId), "IPv6 port owner PID");
        }
        var owned = new ProcessInfo(42, "mysqld.exe", x.Bin("mysqld"), $"\"{x.Bin("mysqld")}\" --defaults-file=\"{x.Config}\"");
        Check(process.Owns(x, owned), "explicit configuration proves process ownership");
        Check(!process.Owns(x, owned with { CommandLine = owned.CommandLine + " --datadir=C:\\unrelated" }), "different datadir rejects ownership");
        Check(!process.Owns(x, owned with { CommandLine = null }), "hidden command line rejects ownership");
        await Reject(() => Task.Run(() => Safety.Child(data, "..\\escape")), "path traversal rejected");
        await File.AppendAllTextAsync(config, "innodb_data_home_dir=C:/external\n");
        await Reject(() => Task.Run(() => detector.Detect(fixture)), "external InnoDB storage rejected");
        await File.WriteAllTextAsync(config, $"[mysqld]\nport={freePort}\ndatadir=\"{data.Replace('\\', '/')}\"\n");
        await File.WriteAllTextAsync(Path.Combine(data, "outside.isl"), "C:/outside/table.ibd");
        await Reject(() => Safety.StampAsync(data), "external tablespace link rejected");
        File.Delete(Path.Combine(data, "outside.isl"));
        await analyzer.DiagnoseAsync(x);
        Safety.Match(original, await Safety.StampAsync(data));
        Check(true, "diagnostics do not modify database bytes");

        var backup = await backups.CreateAsync(x, credentials);
        Check(backup.Manifest.Complete && backup.Manifest.SqlStatus == "SKIPPED", "offline physical backup and SQL SKIPPED");
        Check(Directory.Exists(Path.Combine(backup.Folder, "data", "user_db", "empty")), "empty directory structure preserved");
        await backups.VerifyAsync(x, backup.Folder);
        Check(true, "physical backup inventory and SHA-256 verification");
        await Reject(() => backups.VerifySqlAsync(backup), "InnoDB rebuild refused without complete SQL");
        using (Safety.Acquire(x)) await Reject(() => Task.Run(() => { using var second = Safety.Acquire(x); }), "concurrent recovery lock");

        var plan = await recovery.PrepareAsync(x, backup, "Check");
        await recovery.ActivateAsync(x, plan);
        await File.WriteAllTextAsync(Path.Combine(x.Data, "ibdata1"), "failed candidate");
        await recovery.RollbackAsync(x, plan, credentials);
        Safety.Match(original, await Safety.StampAsync(x.Data));
        Check(Directory.EnumerateDirectories(x.Mysql, "data_failed_*").Any(), "rollback restores bytes and retains failed candidate");

        plan = await recovery.PrepareAsync(x, backup, "CrashCheck");
        Directory.Move(x.Data, plan.Original);
        plan.Stage = "OriginalMoved";
        await Safety.SaveAsync(x.Journal, plan);
        await recovery.ResumeInterruptedAsync(x, credentials);
        Safety.Match(original, await Safety.StampAsync(x.Data));
        Check(true, "journal resumes crash between two directory renames");

        string corrupt = Path.Combine(backup.Folder, "data", "ibdata1");
        await File.AppendAllTextAsync(corrupt, "tampered");
        await Reject(() => backups.VerifyAsync(x, backup.Folder), "tampered backup rejected before restore");
        Safety.Match(original, await Safety.StampAsync(x.Data));
        Check(true, "failed validation leaves current data unchanged");
        string emptyRoot = Path.Combine(root, "empty-source"); Directory.CreateDirectory(emptyRoot);
        var emptyX = x with { Root = emptyRoot };
        Directory.CreateDirectory(emptyX.Data); Directory.CreateDirectory(Path.Combine(emptyX.Mysql, "bin"));
        foreach (string exe in new[] { "mysqld", "mysql", "mysqladmin", "mysqldump" }) File.Copy(x.Bin(exe), emptyX.Bin(exe));
        await Reject(() => backups.CreateAsync(emptyX, credentials), "empty physical backup rejected");
        if (args.Length == 2 && args[0] == "--integration") await IntegrationAsync(args[1], logger);
    }

    private static async Task IntegrationAsync(string source, Logger logger)
    {
        string fixture = Path.Combine(root, "integration");
        string mysqlRoot = Path.Combine(fixture, "mysql");
        foreach (string directory in new[] { "bin", "share", "lib" })
        {
            string from = Path.Combine(Path.GetFullPath(source), "mysql", directory);
            if (Directory.Exists(from)) await Safety.CopyVerifiedAsync(from, Path.Combine(mysqlRoot, directory), await Safety.StampAsync(from));
        }
        var portChecker = new PortChecker();
        var process = new ProcessService(logger, portChecker);
        string install = Path.Combine(mysqlRoot, "bin", "mysql_install_db.exe");
        string data = Path.Combine(mysqlRoot, "data");
        int port = RecoveryService.FreePort();
        var initialization = await process.RunAsync(install, ["--datadir=" + data, "--port=" + port], null, TimeSpan.FromMinutes(2));
        Check(initialization.ExitCode == 0, "initialize isolated MariaDB fixture: " + initialization.Error);
        string config = Path.Combine(mysqlRoot, "bin", "my.ini");
        await File.WriteAllTextAsync(config, $"[mysqld]\nport={port}\nbasedir=\"{mysqlRoot.Replace('\\', '/')}\"\ndatadir=\"{data.Replace('\\', '/')}\"\nbind-address=127.0.0.1\ninnodb_buffer_pool_size=32M\n", new UTF8Encoding(false));
        var x = new XamppDetector().Detect(fixture);
        var credentials = new Credentials("root", "");
        var sql = new MySqlDetector(process, portChecker);
        var backups = new BackupService(logger, process, sql);
        var analyzer = new MySqlLogAnalyzer(logger, process, portChecker);
        var recovery = new RecoveryService(logger, process, sql, backups, analyzer);
        var restore = new RestoreService(logger, backups, recovery, process, sql);
        try
        {
            int id = await process.StartAsync(x, true);
            await sql.WaitHealthyAsync(x, credentials, id);
            await sql.QueryAsync(x, credentials, "DROP DATABASE IF EXISTS test;");
            await process.StopAsync(x, credentials);
            await Safety.CopyVerifiedAsync(x.Data, x.Template, await Safety.StampAsync(x.Data));
            id = await process.StartAsync(x, true);
            await sql.WaitHealthyAsync(x, credentials, id);
            await sql.QueryAsync(x, credentials, "CREATE DATABASE recovery_check; CREATE TABLE recovery_check.items (id INT PRIMARY KEY, payload VARCHAR(100)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4; INSERT INTO recovery_check.items VALUES (1,'original value'),(2,'Ti\u1ebfng Vi\u1ec7t');");
            var backup = await backups.CreateAsync(x, credentials);
            Check(backup.Manifest.SqlStatus == "SUCCESS", "real mysqldump with global read lock through shutdown");
            await backups.VerifySqlAsync(backup);
            await recovery.RecoverAsync(x, credentials);
            Check(await sql.QueryAsync(x, credentials, "SELECT payload FROM recovery_check.items WHERE id=1;") == "original value", "recovery preserves real InnoDB rows");
            await sql.QueryAsync(x, credentials, "UPDATE recovery_check.items SET payload='new value' WHERE id=1;");
            await restore.RestoreAsync(x, credentials, backup.Folder);
            Check(await sql.QueryAsync(x, credentials, "SELECT payload FROM recovery_check.items WHERE id=1;") == "original value", "restore returns previous InnoDB rows");
            Check(Directory.EnumerateDirectories(x.Backups).Count(d => File.Exists(Path.Combine(d, "manifest.json"))) >= 3, "restore retains pre-restore backup and earlier backups");
            await sql.QueryAsync(x, credentials, "SET PASSWORD = PASSWORD('fixture-only-password');");
            credentials = new("root", "fixture-only-password");
            bool injected = false;
            void InjectFailure(string line)
            {
                if (!injected && line.Contains("Repair strategy: Level 2", StringComparison.Ordinal))
                {
                    injected = true;
                    // Damage only the disposable candidate after backup; exercise Level 3 without
                    // touching the source XAMPP installation or this fixture's retained original.
                    File.WriteAllBytes(Path.Combine(x.Data, "ibdata1"), [1, 2, 3]);
                }
            }
            logger.Message += InjectFailure;
            try { await recovery.RecoverAsync(x, credentials); }
            finally { logger.Message -= InjectFailure; }
            Check(injected && await sql.QueryAsync(x, credentials, "SELECT payload FROM recovery_check.items WHERE id=1;") == "original value", "Level 3 SQL rebuild preserves InnoDB rows and password-protected root account");
            Check(await sql.QueryAsync(x, credentials, "SELECT payload FROM recovery_check.items WHERE id=2;") == "Ti\u1ebfng Vi\u1ec7t", "UTF-8 Vietnamese text survives SQL dump and rebuild");
            const string grantsQuery = "SELECT Host,User,Proxied_host,Proxied_user,With_grant,Grantor,Timestamp FROM mysql.proxies_priv ORDER BY Host,User,Proxied_host,Proxied_user;";
            string grantsBefore = await sql.QueryAsync(x, credentials, grantsQuery);
            await process.StopAsync(x, credentials);
            string oldSchema = Path.Combine(x.Data, "mysql", "proxies_priv.frm");
            byte[] oldSchemaBytes = await File.ReadAllBytesAsync(oldSchema);
            BitConverter.GetBytes(BitConverter.ToUInt32(oldSchemaBytes, 51) - 1).CopyTo(oldSchemaBytes, 51);
            await File.WriteAllBytesAsync(oldSchema, oldSchemaBytes);
            await using (var index = new FileStream(Path.Combine(x.Data, "mysql", "proxies_priv.MAI"), FileMode.Open, FileAccess.Write, FileShare.None))
                await index.WriteAsync(new byte[32]);
            bool targetedRepair = false, offlineBackup = false;
            void ObserveAriaRepair(string line)
            {
                if (line.Contains("repair mysql.proxies_priv Aria index", StringComparison.Ordinal)) targetedRepair = true;
                if (line.Contains("SQL backup: SKIPPED", StringComparison.Ordinal)) offlineBackup = true;
            }
            logger.Message += ObserveAriaRepair;
            try { await recovery.RecoverAsync(x, credentials); }
            finally { logger.Message -= ObserveAriaRepair; }
            Check(targetedRepair && offlineBackup, "corrupt proxies_priv index repaired without a pre-existing SQL dump");
            Check(await sql.QueryAsync(x, credentials, grantsQuery) == grantsBefore, "targeted Aria repair preserves every proxy privilege row");
            Check(await sql.QueryAsync(x, credentials, "SELECT payload FROM recovery_check.items WHERE id=2;") == "Ti\u1ebfng Vi\u1ec7t", "targeted privilege repair preserves InnoDB data and authenticated access");
            await process.StopAsync(x, credentials);
            string dbDataPath = Path.Combine(x.Data, "mysql", "db.MAD");
            byte[] dbData = await File.ReadAllBytesAsync(dbDataPath);
            dbData[^1] ^= 0xff;
            await File.WriteAllBytesAsync(dbDataPath, dbData);
            await using (var index = new FileStream(Path.Combine(x.Data, "mysql", "proxies_priv.MAI"), FileMode.Open, FileAccess.Write, FileShare.None))
                await index.WriteAsync(new byte[32]);
            await recovery.RecoverAsync(x, credentials);
            Check(recovery.LastWarning != null && await sql.QueryAsync(x, credentials, "SELECT payload FROM recovery_check.items WHERE id=2;") == "Ti\u1ebfng Vi\u1ec7t",
                "unrelated grant-table corruption is preserved and surfaced while user data remains accessible");
            string latest = Directory.EnumerateDirectories(x.Backups).Where(d => File.Exists(Path.Combine(d, "manifest.json"))).OrderDescending(StringComparer.Ordinal).First();
            var userBackup = await backups.VerifyAsync(x, latest);
            Check(userBackup.Manifest.SqlStatus == "SUCCESS" && userBackup.Manifest.SqlScope == "USER_DATABASES" &&
                await Safety.HashAsync(Path.Combine(latest, userBackup.Manifest.SqlFile)) == userBackup.Manifest.SqlHash,
                "system-table dump failure falls back to a verified user-only SQL backup");
            await Reject(() => backups.VerifySqlAsync(userBackup), "user-only SQL dump cannot authorize an InnoDB rebuild");
        }
        finally { await process.StopAsync(x, credentials); }
    }
}
