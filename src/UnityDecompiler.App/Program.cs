namespace UnityDecompiler.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // A game dragged onto UnityDecompiler.exe arrives as the first argument.
        Application.Run(new MainForm(args.FirstOrDefault()));
    }
}
