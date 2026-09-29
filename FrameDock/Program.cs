namespace FrameDock;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.FirstOrDefault(File.Exists), args.Contains("--tray")));
    }
}
