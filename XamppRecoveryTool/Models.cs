namespace XamppRecoveryTool;

public sealed record XamppInstallation(string Root, string Config, int Port)
{
    public string Mysql => Path.Combine(Root, "mysql");
    public string Data => Path.Combine(Mysql, "data");
    public string Template => Path.Combine(Mysql, "backup");
    public string Backups => Path.Combine(Root, "xampp_mysql_backups");
    public string Journal => Path.Combine(Mysql, "recovery-plan.json");
    public string Bin(string name) => Path.Combine(Mysql, "bin", name + ".exe");
}

public sealed record Credentials(string User, string Password);
public sealed record ProcessInfo(int Id, string Name, string? Path, string? CommandLine);
public sealed record ServiceInfo(string Name, string State, int ProcessId, string Path);
public sealed record MachineState(List<ProcessInfo> Processes, List<ServiceInfo> Services);
public sealed record PortOwner(int ProcessId, string Address);
public sealed record FileStamp(string Path, long Length, string Sha256);
public sealed record TreeStamp(List<string> Directories, List<FileStamp> Files);
public sealed class BackupManifest
{
    public int Format { get; set; } = 1;
    public string Root { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public string BinaryHash { get; set; } = "";
    public string ConfigHash { get; set; } = "";
    public string SqlStatus { get; set; } = "SKIPPED";
    public string SqlScope { get; set; } = "ALL_DATABASES";
    public string SqlFile { get; set; } = "all-databases.sql";
    public string? SqlWarning { get; set; }
    public string? SqlHash { get; set; }
    public List<string> Databases { get; set; } = [];
    public List<string> Tables { get; set; } = [];
    public TreeStamp Physical { get; set; } = new([], []);
    public bool Complete { get; set; }
}
public sealed record BackupResult(string Folder, BackupManifest Manifest);
public sealed class RecoveryPlan
{
    public string Root { get; set; } = "";
    public string BackupFolder { get; set; } = "";
    public string Original { get; set; } = "";
    public string Candidate { get; set; } = "";
    public string Stage { get; set; } = "Prepared";
    public string Operation { get; set; } = "Recovery";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
}
