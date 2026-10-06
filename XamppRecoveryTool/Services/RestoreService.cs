namespace XamppRecoveryTool.Services;

public sealed class RestoreService(Logger log, BackupService backups, RecoveryService recovery, ProcessService processes, MySqlDetector mysql)
{
    public async Task RestoreAsync(XamppInstallation x, Credentials credentials, string folder)
    {
        using var operationLock = Safety.Acquire(x);
        await recovery.ResumeInterruptedAsync(x, credentials);
        var selected = await backups.VerifyAsync(x, folder);
        log.Write("Backing up current state before restore...");
        var current = await backups.CreateAsync(x, credentials);
        var plan = await recovery.PrepareAsync(x, selected, "Restore");
        // Rollback must return to the pre-restore state, not the selected historic backup.
        plan.BackupFolder = current.Folder;
        var privateServer = x with { Port = RecoveryService.FreePort() };
        try
        {
            await recovery.ActivateAsync(x, plan);
            int id = await processes.StartAsync(privateServer, true);
            await mysql.WaitHealthyAsync(privateServer, credentials, id);
            await processes.StopAsync(privateServer, credentials);
            await recovery.FinalStartAsync(x, credentials, plan);
            log.Write("Restore Previous Backup: SUCCESS");
        }
        catch
        {
            await recovery.RollbackAsync(x, plan, credentials, privateServer);
            throw;
        }
    }
}
