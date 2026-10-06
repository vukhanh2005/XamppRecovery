using System.Security.Cryptography;
using System.Text.Json;

namespace XamppRecoveryTool.Services;

public static class Safety
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
        { "mysql", "information_schema", "performance_schema", "phpmyadmin", "sys" };

    public static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'),
        Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public static string Child(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || !full.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Unsafe path outside expected directory: " + relative);
        return full;
    }

    public static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Junctions, symlinks and reparse points are unsupported: " + current);
    }

    public static (List<string> Directories, List<string> Files) Inventory(string root)
    {
        NoLinks(root);
        var dirs = new List<string>();
        var files = new List<string>();
        void Walk(string dir)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked data is unsupported: " + entry);
                string relative = Path.GetRelativePath(root, entry);
                if ((attributes & FileAttributes.Directory) != 0) { dirs.Add(relative); Walk(entry); }
                else
                {
                    if (Path.GetExtension(entry).Equals(".isl", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("External InnoDB tablespace detected: " + entry);
                    files.Add(relative);
                }
            }
        }
        Walk(root);
        dirs.Sort(StringComparer.OrdinalIgnoreCase);
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return (dirs, files);
    }

    public static async Task<string> HashAsync(string path, CancellationToken token = default)
    {
        NoLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    public static async Task<TreeStamp> StampAsync(string root, CancellationToken token = default)
    {
        var inventory = Inventory(root);
        var stamps = new List<FileStamp>();
        foreach (string file in inventory.Files)
        {
            string path = Child(root, file);
            stamps.Add(new(file, new FileInfo(path).Length, await HashAsync(path, token)));
        }
        return new(inventory.Directories, stamps);
    }

    public static void Match(TreeStamp expected, TreeStamp actual)
    {
        if (!expected.Directories.SequenceEqual(actual.Directories, StringComparer.OrdinalIgnoreCase) ||
            !expected.Files.SequenceEqual(actual.Files))
            throw new IOException("SHA-256 verification failed: file contents or directory structure changed.");
    }

    public static async Task CopyVerifiedAsync(string source, string target, TreeStamp expected, CancellationToken token = default)
    {
        NoLinks(source); NoLinks(target);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Destination already exists: " + target);
        Directory.CreateDirectory(target);
        foreach (string dir in expected.Directories) Directory.CreateDirectory(Child(target, dir));
        foreach (var file in expected.Files)
        {
            token.ThrowIfCancellationRequested();
            string from = Child(source, file.Path), to = Child(target, file.Path);
            NoLinks(from); NoLinks(to);
            await using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
            await using var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            await input.CopyToAsync(output, token);
            await output.FlushAsync(token);
            output.Flush(true);
        }
        Match(expected, await StampAsync(target, token));
    }

    public static async Task SaveAsync<T>(string path, T value)
    {
        NoLinks(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
        {
            await JsonSerializer.SerializeAsync(file, value, Json);
            await file.FlushAsync();
            file.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    public static async Task<T> LoadAsync<T>(string path)
    {
        NoLinks(path);
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream) ?? throw new IOException("Invalid JSON: " + path);
    }

    public static string UniqueDirectory(string parent, string prefix = "")
    {
        NoLinks(parent);
        string path = Path.Combine(parent, prefix + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        if (Directory.Exists(path) || File.Exists(path)) path += "_" + Guid.NewGuid().ToString("N")[..8];
        return path;
    }

    public static FileStream Acquire(XamppInstallation x)
    {
        NoLinks(x.Mysql);
        return new FileStream(Path.Combine(x.Mysql, ".recovery.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
}
