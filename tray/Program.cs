namespace FreeVram;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, @"Local\FreeVram.Tray", out bool first);
        if (!first) return; // already running

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
