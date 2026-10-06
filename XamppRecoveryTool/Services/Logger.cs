using System.Text;

namespace XamppRecoveryTool.Services;

public sealed class Logger : IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter writer;
    public string FilePath { get; }
    public event Action<string>? Message;

    public Logger(string? directory = null)
    {
        directory ??= Path.Combine(AppContext.BaseDirectory, "logs");
        try { (FilePath, writer) = Open(directory); }
        catch (UnauthorizedAccessException)
        {
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XamppMySqlRecoveryTool", "logs");
            (FilePath, writer) = Open(directory);
        }
    }

    private static (string Path, StreamWriter Writer) Open(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, $"recovery_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid():N}.log");
        return (path, new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true });
    }

    public void Write(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (gate) writer.WriteLine(line);
        Message?.Invoke(line);
    }

    public void Dispose() { lock (gate) writer.Dispose(); }
}
