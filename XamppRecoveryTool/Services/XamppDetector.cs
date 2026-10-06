using System.Text.RegularExpressions;

namespace XamppRecoveryTool.Services;

public sealed class XamppDetector
{
    public XamppInstallation Detect(string root, bool allowMissingData = false)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || root.StartsWith(@"\\"))
            throw new IOException("Choose an absolute local XAMPP directory.");
        root = Path.GetFullPath(root).TrimEnd('\\');
        Safety.NoLinks(root);
        string mysql = Path.Combine(root, "mysql");
        foreach (string exe in new[] { "mysqld.exe", "mysql.exe", "mysqladmin.exe", "mysqldump.exe" })
            if (!File.Exists(Path.Combine(mysql, "bin", exe))) throw new IOException("Missing XAMPP executable: " + exe);
        if (!allowMissingData && !Directory.Exists(Path.Combine(mysql, "data")))
            throw new IOException("mysql\\data does not exist.");
        string config = new[] { Path.Combine(mysql, "bin", "my.ini"), Path.Combine(mysql, "my.ini") }
            .FirstOrDefault(File.Exists) ?? throw new IOException("my.ini was not found.");
        Safety.NoLinks(config);
        var options = ReadServerOptions(config);
        if (options.TryGetValue("basedir", out string? basedir) && !Safety.SamePath(Resolve(root, basedir), mysql))
            throw new IOException("Configured basedir does not match this XAMPP installation.");
        foreach (string key in new[] { "datadir", "innodb-data-home-dir", "innodb-log-group-home-dir", "aria-log-dir-path" })
            if (options.TryGetValue(key, out string? value) && !Safety.SamePath(Resolve(root, value), Path.Combine(mysql, "data")))
                throw new IOException($"Unsupported {key}: all database storage must be in mysql\\data.");
        foreach (string key in new[] { "innodb-directories", "innodb-undo-directory", "innodb-temp-data-file-path", "innodb-temp-tablespaces-dir", "init-file", "init-connect", "plugin-load", "plugin-load-add", "early-plugin-load", "wsrep-provider", "log-bin", "log-bin-index", "relay-log", "relay-log-index", "master-info-file", "relay-log-info-file", "skip-networking", "skip-grant-tables" })
            if (options.ContainsKey(key)) throw new IOException("Unsupported custom server option: " + key + ". Manual recovery required.");
        if (options.Keys.Any(k => k.StartsWith("replicate-", StringComparison.Ordinal) || k.StartsWith("wsrep-", StringComparison.Ordinal)))
            throw new IOException("Replication / cluster configuration requires manual recovery.");
        if (options.TryGetValue("innodb-data-file-path", out string? paths) && paths.Split(';').Any(p => !Regex.IsMatch(p, @"^[A-Za-z0-9_.-]+:\d+[KMG]?(?::autoextend(?::max:\d+[KMG]?)?)?$", RegexOptions.IgnoreCase)))
            throw new IOException("External or unsupported InnoDB system tablespace layout.");
        if (options.TryGetValue("innodb-force-recovery", out string? force) && force != "0")
            throw new IOException("Remove innodb_force_recovery only after expert review; automatic repair refused.");
        int port = 3306;
        if (options.TryGetValue("port", out string? raw) && !int.TryParse(raw, out port)) throw new IOException("Invalid MySQL port in my.ini.");
        if (port is < 1 or > 65535) throw new IOException("Invalid MySQL port in my.ini.");
        return new(root, config, port);
    }

    private static string Resolve(string root, string value) => Path.IsPathRooted(value) ? value : Path.Combine(root, value);

    public static Dictionary<string, string> ReadServerOptions(string config)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool server = false;
        foreach (string raw in File.ReadLines(config))
        {
            string line = raw.Trim();
            if (line.StartsWith('#') || line.StartsWith(';') || line.Length == 0) continue;
            if (line.StartsWith('!')) throw new IOException("Included configuration files require manual review.");
            if (line.StartsWith('['))
            {
                int end = line.IndexOf(']');
                if (end < 0) throw new IOException("Invalid my.ini section header.");
                string group = line[1..end].Trim().ToLowerInvariant();
                server = group is "mysqld" or "server" or "mariadb" or "mariadbd" || group.StartsWith("mysqld-") || group.StartsWith("mariadb-") || group.StartsWith("mariadbd-");
                continue;
            }
            if (!server) continue;
            string[] parts = line.Split('=', 2);
            string key = parts[0].Trim().Replace('_', '-').ToLowerInvariant();
            if (key.StartsWith("loose-")) key = key[6..];
            result[key] = parts.Length == 1 ? "1" : parts[1].Trim().Trim('"', '\'');
        }
        return result;
    }
}
