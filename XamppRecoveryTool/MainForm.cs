using System.Diagnostics;
using XamppRecoveryTool.Services;

namespace XamppRecoveryTool;

public sealed class MainForm : Form
{
    private readonly Logger log;
    private readonly XamppDetector detector = new();
    private readonly PortChecker ports = new();
    private readonly ProcessService processes;
    private readonly MySqlLogAnalyzer analyzer;
    private readonly RecoveryService recovery;
    private readonly RestoreService restore;
    private readonly TextBox path = new() { Dock = DockStyle.Fill, AccessibleName = "XAMPP directory" };
    private readonly TextBox username = new() { Text = "root", Dock = DockStyle.Fill, AccessibleName = "MySQL username" };
    private readonly TextBox password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill, AccessibleName = "MySQL password" };
    private readonly RichTextBox output = new() { ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(24, 35, 38), ForeColor = Color.FromArgb(209, 227, 213), Font = new Font("Cascadia Mono", 9), AccessibleName = "Recovery activity log", WordWrap = false };
    private readonly Label apache = Status("APACHE"), mysql = Status("MYSQL"), port = Status("MYSQL PORT"), data = Status("DATA DIRECTORY");
    private readonly Label result = new() { AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = Color.FromArgb(72, 87, 78), Text = "Ready. Diagnose first, or start a backed-up recovery.", AccessibleName = "Operation result" };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Height = 5, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 0, Visible = false };
    private readonly List<Control> actions = [];
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15000 };
    private bool busy, refreshing;

    public MainForm(Logger logger, string initialPath)
    {
        log = logger;
        processes = new(log, ports);
        var sql = new MySqlDetector(processes, ports);
        analyzer = new(log, processes, ports);
        var backups = new BackupService(log, processes, sql);
        recovery = new(log, processes, sql, backups, analyzer);
        restore = new(log, backups, recovery, processes, sql);
        Text = "XAMPP MySQL Recovery Tool";
        Icon = new Icon(typeof(MainForm), "Assets.app.ico");
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(244, 246, 239);
        ForeColor = Color.FromArgb(27, 49, 40);
        ClientSize = new Size(900, 820);
        MinimumSize = new Size(850, 800);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        path.Text = initialPath;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 10 };
        foreach (int height in new[] { 85, 55, 70, 80, 68, 52, 52, 10 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        Controls.Add(layout);
        var heading = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        heading.Controls.Add(new Label { Text = "XAMPP MySQL Recovery Tool", Font = new Font("Bahnschrift", 22, FontStyle.Bold), AutoSize = true });
        heading.Controls.Add(new Label { Text = "VERIFIED BACKUPS  /  GUARDED REPAIR  /  ROLLBACK", Font = new Font("Segoe UI", 9), AutoSize = true, ForeColor = Color.FromArgb(85, 106, 93) });
        layout.Controls.Add(heading, 0, 0);

        var location = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 8, 0, 0) };
        location.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95)); location.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); location.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        location.Controls.Add(new Label { Text = "XAMPP", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, 0, 0);
        location.Controls.Add(path, 1, 0);
        location.Controls.Add(Button("Browse", Browse), 2, 0);
        layout.Controls.Add(location, 0, 1);

        var status = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        foreach (var label in new[] { apache, mysql, port, data })
        {
            status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25)); status.Controls.Add(label);
        }
        layout.Controls.Add(status, 0, 2);
        var auth = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2 };
        auth.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); auth.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        auth.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105)); auth.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        auth.Controls.Add(new Label { Text = "MySQL user", AutoSize = true }, 0, 0); auth.Controls.Add(username, 1, 0);
        auth.Controls.Add(new Label { Text = "Password", AutoSize = true }, 2, 0); auth.Controls.Add(password, 3, 0);
        var note = new Label { Text = "Password stays in memory. Close database clients before recovery or restore.", AutoSize = true, Font = new Font("Segoe UI", 9) };
        auth.Controls.Add(note, 0, 1); auth.SetColumnSpan(note, 4);
        layout.Controls.Add(auth, 0, 3);

        var repair = Button("Backups and Reset XAMPP", Repair);
        repair.BackColor = Color.FromArgb(26, 94, 67); repair.ForeColor = Color.White; repair.Font = new Font("Segoe UI", 13, FontStyle.Bold);
        layout.Controls.Add(repair, 0, 4);
        var controls = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        foreach (int _ in Enumerable.Range(0, 3)) controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        controls.Controls.Add(Button("Restore Previous Backup", Restore));
        controls.Controls.Add(Button("Open Backup Folder", OpenBackups));
        controls.Controls.Add(Button("Open XAMPP", OpenXampp));
        layout.Controls.Add(controls, 0, 5);

        var utilities = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        utilities.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); utilities.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        utilities.Controls.Add(Button("Diagnose (read-only)", Diagnose));
        var admin = Button(Program.IsAdministrator() ? "Administrator session" : "Run as administrator", Elevate);
        if (Program.IsAdministrator()) { admin.Enabled = false; actions.Remove(admin); }
        utilities.Controls.Add(admin);
        layout.Controls.Add(utilities, 0, 6);
        layout.Controls.Add(progress, 0, 7);
        layout.Controls.Add(output, 0, 8);
        layout.Controls.Add(result, 0, 9);
        actions.AddRange([path, username, password]);
        log.Message += AppendLog;
        log.Write("Log file: " + log.FilePath);
        log.Write("Administrator: " + (Program.IsAdministrator() ? "yes" : "no (elevate only if access is denied)"));
        timer.Tick += async (_, _) => { if (!busy) await RefreshStatusAsync(); };
        Shown += async (_, _) => { await RefreshStatusAsync(); timer.Start(); };
        FormClosing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            MessageBox.Show(this, "An operation is running. Wait for completion or rollback before closing.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); log.Message -= AppendLog; };
    }

    private static Label Status(string title) => new() { Text = title + "\nChecking...", Dock = DockStyle.Fill, Padding = new Padding(10, 5, 5, 5), BackColor = Color.FromArgb(228, 234, 224), AutoEllipsis = true, AccessibleName = title + " status" };

    private Button Button(string title, EventHandler action)
    {
        var button = new Button { Text = title, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, BackColor = Color.White, Cursor = Cursors.Hand, Margin = new Padding(3, 3, 3, 6), AccessibleName = title };
        button.FlatAppearance.BorderColor = Color.FromArgb(195, 210, 196);
        button.Click += action; actions.Add(button); return button;
    }

    private void AppendLog(string line)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { BeginInvoke(() => AppendLog(line)); return; }
        if (output.TextLength > 250000) output.Text = output.Text[^150000..];
        output.AppendText(line + Environment.NewLine); output.SelectionStart = output.TextLength; output.ScrollToCaret();
    }

    private async Task RefreshStatusAsync()
    {
        if (refreshing || IsDisposed) return;
        refreshing = true;
        try
        {
            string root = path.Text.Trim();
            var snapshot = await Task.Run(async () =>
            {
                var x = detector.Detect(root, true);
                var state = await processes.SnapshotAsync();
                var owners = ports.Listeners(x.Port);
                return (Xampp: x, State: state, Owners: owners);
            });
            apache.Text = "APACHE\n" + (snapshot.State.Processes.Any(p => p.Name.Equals("httpd.exe", StringComparison.OrdinalIgnoreCase) && p.Path != null && Safety.SamePath(p.Path, Path.Combine(snapshot.Xampp.Root, "apache", "bin", "httpd.exe"))) ? "Running" : "Stopped / unverified");
            mysql.Text = "MYSQL\n" + (snapshot.State.Processes.Any(p => processes.Owns(snapshot.Xampp, p)) ? "Running" : "Stopped / unverified");
            port.Text = $"MYSQL PORT {snapshot.Xampp.Port}\n" + (snapshot.Owners.Count == 0 ? "Free" : "Listening, PID " + snapshot.Owners[0].ProcessId);
            data.Text = "DATA DIRECTORY\n" + (Directory.Exists(snapshot.Xampp.Data) ? "Present" : "Missing");
            if (File.Exists(snapshot.Xampp.Journal))
            {
                var plan = await Safety.LoadAsync<RecoveryPlan>(snapshot.Xampp.Journal);
                if (plan.Stage is not ("Committed" or "RolledBack")) result.Text = "Interrupted recovery found. Next recovery/restore will first roll back.";
            }
        }
        catch (Exception ex) { result.Text = ex.Message; mysql.Text = "MYSQL\nUnknown"; port.Text = "MYSQL PORT\nUnknown"; data.Text = "DATA DIRECTORY\nUnverified"; }
        finally { refreshing = false; }
    }

    private async Task RunOperationAsync(Func<XamppInstallation, Credentials, Task> action, bool writable)
    {
        if (busy) return;
        string root = path.Text.Trim();
        var credentials = new Credentials(username.Text.Trim(), password.Text);
        if (string.IsNullOrWhiteSpace(credentials.User)) { MessageBox.Show(this, "Enter a MySQL username.", Text); return; }
        busy = true; foreach (var control in actions) control.Enabled = false;
        progress.Visible = true; progress.MarqueeAnimationSpeed = 25;
        result.Text = writable ? "Working. Keep XAMPP and database clients stopped until completion." : "Reading diagnostics...";
        try
        {
            await Task.Run(async () =>
            {
                var x = detector.Detect(root, writable);
                await action(x, credentials);
            });
            bool warning = writable && recovery.LastWarning != null;
            result.Text = warning ? "MySQL started with warnings. System-table review required; see log." :
                writable ? "\u2713 MySQL recovered successfully" : "Diagnostics complete. Review log for findings.";
            if (writable) MessageBox.Show(this, warning ? recovery.LastWarning : "\u2713 MySQL recovered successfully\nBackups and original data have been retained.",
                Text, MessageBoxButtons.OK, warning ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            log.Write("STOPPED: " + ex.Message);
            if (ex.InnerException != null) log.Write("Detail: " + ex.InnerException.Message);
            result.Text = writable ? "\u2717 MySQL still cannot start / operation stopped. See log." : "Diagnostics stopped. See log.";
            MessageBox.Show(this, ex.Message + "\n\nLog: " + log.FilePath, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            busy = false; foreach (var control in actions) control.Enabled = true;
            progress.Visible = false; progress.MarqueeAnimationSpeed = 0;
            await RefreshStatusAsync();
        }
    }

    private async void Browse(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "Select XAMPP installation", UseDescriptionForTitle = true, InitialDirectory = path.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK) { path.Text = dialog.SelectedPath; await RefreshStatusAsync(); }
    }

    private async void Repair(object? sender, EventArgs e)
    {
        if (!Confirm("The tool will first create a complete backup of your MySQL data. No database will be deleted before the backup is verified. Continue?")) return;
        await RunOperationAsync(recovery.RecoverAsync, true);
    }

    private async void Restore(object? sender, EventArgs e)
    {
        using var picker = new FolderBrowserDialog { Description = "Select a backup timestamp folder containing manifest.json", UseDescriptionForTitle = true, InitialDirectory = Path.Combine(path.Text, "xampp_mysql_backups") };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        string folder = picker.SelectedPath;
        if (!Confirm("Restore this backup?\n" + folder + "\n\nCurrent data will first be backed up and verified. A failed restore will roll back. Close database clients before continuing.")) return;
        await RunOperationAsync((x, c) => restore.RestoreAsync(x, c, folder), true);
    }

    private bool Confirm(string message)
    {
        using var dialog = new Form { Text = "Confirm recovery", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, Font = Font, BackColor = BackColor };
        var layout = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 2 };
        var text = new Label { Text = message, AutoSize = true, MaximumSize = new Size(550, 0), Margin = new Padding(0, 0, 0, 20) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(112, 38) };
        var proceed = new Button { Text = "Continue", DialogResult = DialogResult.OK, Size = new Size(112, 38) };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([proceed, cancel]); layout.Controls.Add(text); layout.Controls.Add(buttons);
        dialog.Controls.Add(layout); dialog.CancelButton = cancel; dialog.AcceptButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK;
    }

    private async void Diagnose(object? sender, EventArgs e) => await RunOperationAsync((x, _) => analyzer.DiagnoseAsync(x), false);

    private void OpenBackups(object? sender, EventArgs e) => Shell(() =>
    {
        var x = detector.Detect(path.Text.Trim(), true);
        Safety.NoLinks(x.Backups); Directory.CreateDirectory(x.Backups);
        Process.Start(new ProcessStartInfo(x.Backups) { UseShellExecute = true });
    });

    private void OpenXampp(object? sender, EventArgs e) => Shell(() =>
    {
        string exe = Path.Combine(Path.GetFullPath(path.Text.Trim()), "xampp-control.exe");
        Safety.NoLinks(exe);
        if (!File.Exists(exe)) throw new IOException("xampp-control.exe was not found.");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
    });

    private void Elevate(object? sender, EventArgs e) => Shell(() =>
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        info.ArgumentList.Add("--xampp"); info.ArgumentList.Add(path.Text.Trim());
        if (Process.Start(info) != null) Close();
    });

    private void Shell(Action action)
    {
        try { action(); }
        catch (Exception ex) { log.Write(ex.Message); MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
