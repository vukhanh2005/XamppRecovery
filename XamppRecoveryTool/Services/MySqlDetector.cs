namespace XamppRecoveryTool.Services;

public sealed class MySqlDetector(ProcessService processes, PortChecker ports)
{
    public async Task<string> QueryAsync(XamppInstallation x, Credentials credentials, string sql)
    {
        var owners = ports.Listeners(x.Port);
        var state = await processes.SnapshotAsync();
        if (owners.Count == 0 || owners.Any(o => !state.Processes.Any(p => p.Id == o.ProcessId && processes.Owns(x, p))))
            throw new IOException("Connection refused: listener ownership is not verified for this XAMPP.");
        var args = ProcessService.ClientArgs(x, credentials, "--default-character-set=utf8mb4", "--batch", "--skip-column-names");
        // UTF-8 stdin avoids legacy Windows client conversion of non-ASCII argv text.
        var result = await processes.RunAsync(x.Bin("mysql"), args, credentials, TimeSpan.FromSeconds(20), inputText: sql + "\n");
        if (result.ExitCode != 0) throw new IOException("MySQL query failed: " + ProcessService.Clip(result.Error));
        return result.Output.Trim();
    }

    public async Task VerifyConnectionAsync(XamppInstallation x, Credentials credentials)
    {
        string data = await QueryAsync(x, credentials, "SELECT @@datadir;");
        if (!Safety.SamePath(data.Replace("\\\\", "\\"), x.Data)) throw new IOException("Connected server has an unexpected datadir.");
    }

    public async Task WaitHealthyAsync(XamppInstallation x, Credentials credentials, int processId)
    {
        Exception? last = null;
        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(1000);
            var state = await processes.SnapshotAsync();
            if (!state.Processes.Any(p => p.Id == processId && processes.Owns(x, p)))
                throw new IOException("mysqld exited before it became healthy.");
            if (!ports.Listeners(x.Port).Any(p => p.ProcessId == processId)) continue;
            try { await VerifyConnectionAsync(x, credentials); return; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { last = ex; }
        }
        throw new IOException("MySQL did not pass process, LISTENING and authenticated connection checks.", last);
    }

    public async Task<List<string>> DatabasesAsync(XamppInstallation x, Credentials credentials)
        => Lines(await QueryAsync(x, credentials, "SHOW DATABASES;"))
            .Where(n => n is not ("information_schema" or "performance_schema")).Order(StringComparer.Ordinal).ToList();

    public async Task<List<string>> TablesAsync(XamppInstallation x, Credentials credentials)
        => Lines(await QueryAsync(x, credentials, "SELECT TABLE_SCHEMA,TABLE_NAME,TABLE_TYPE FROM information_schema.TABLES WHERE TABLE_SCHEMA NOT IN ('information_schema','performance_schema') ORDER BY TABLE_SCHEMA,TABLE_NAME;"))
            .Order(StringComparer.Ordinal).ToList();

    private static IEnumerable<string> Lines(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}
