using System.Security.Principal;
using XamppRecoveryTool.Services;

namespace XamppRecoveryTool;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            using var logger = new Logger();
            using var form = new MainForm(logger, args.Length == 2 && args[0] == "--xampp" ? args[1] : @"C:\xampp");
            Application.Run(form);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "XAMPP MySQL Recovery Tool", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
