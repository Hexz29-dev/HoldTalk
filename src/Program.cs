namespace Holdtalk;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--speech-worker")
        {
            try
            {
                if (args.Length != 3) throw new ArgumentException("Expected native library and model paths.");
                CohereTranscription.RunWorkerAsync(args[1], args[2]).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
        }
        ApplicationConfiguration.Initialize();
        using var singleInstance = new Mutex(true, "Local\\Holdtalk", out bool created);
        if (!created) { MessageBox.Show("HoldTalk is already running in the system tray.", "HoldTalk"); return 0; }
        try
        {
            string directory = Settings.FindAppDirectory(AppContext.BaseDirectory);
            var settings = Settings.Load(directory);
            string model = Settings.ResolvePath(directory, settings.ModelPath);
            if (!File.Exists(model) && Path.GetFullPath(model).Equals(
                Path.GetFullPath(Settings.ResolvePath(directory, new Settings().ModelPath)), StringComparison.OrdinalIgnoreCase))
            {
                using var download = new ModelDownloadDialog(model);
                if (download.ShowDialog() != DialogResult.OK) return 0;
            }
            using var app = new TrayApp(settings, directory);
            Application.Run(app);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "HoldTalk could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        return 0;
    }
}
