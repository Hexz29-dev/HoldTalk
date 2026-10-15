using System.Diagnostics;

namespace Holdtalk;

public sealed class TrayApp : ApplicationContext
{
    private readonly Settings settings;
    private readonly string directory;
    private readonly SpeechEngine speech;
    private readonly CleanupClient cleanup;
    private readonly TranscriptLog log;
    private readonly List<Transcript> recent;
    private readonly Control dispatcher = new();
    private readonly NotifyIcon tray = new();
    private readonly ContextMenuStrip menu = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private readonly KeyboardHook hook;
    private readonly Dashboard dashboard;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, Icon> icons = [];
    private ChildProcess? recording;
    private string? wav;
    private string microphone = "";
    private string[] microphones = [];
    private Keys activeKey;
    private DateTime started;
    private string state = "Starting";
    private bool ready, paused, exiting;
    private Task work = Task.CompletedTask;

    public TrayApp(Settings settings, string directory)
    {
        this.settings = settings;
        this.directory = directory;
        speech = new(settings, directory);
        cleanup = new(Settings.ReadEnv(Path.Combine(directory, ".env")));
        log = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dictation"));
        recent = log.Recent();
        dashboard = new(settings);
        dashboard.UpdateHistory(recent);
        dashboard.PauseRequested += TogglePause;
        dashboard.CopyRequested += CopyText;
        dashboard.ArchiveRequested += () => OpenPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dictation"), true);
        dashboard.ConfigRequested += () => OpenPath(Path.Combine(directory, "config.json"));
        dashboard.PermissionsRequested += () => OpenPath("ms-settings:privacy-microphone");
        dashboard.PreferencesRequested += SavePreferences;
        dashboard.QuitRequested += async () => await QuitAsync();
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(dashboard);
        dispatcher.CreateControl();
        _ = dispatcher.Handle;
        icons["Idle"] = MakeIcon(Color.FromArgb(49, 94, 251), (System.Windows.Media.Geometry)dashboard.FindResource("MxMicrophone"));
        icons["Recording"] = MakeIcon(Color.Firebrick, (System.Windows.Media.Geometry)dashboard.FindResource("MxRecording"));
        icons["Transcribing"] = MakeIcon(Color.FromArgb(165, 98, 7), (System.Windows.Media.Geometry)dashboard.FindResource("MxTranscribing"));
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.MouseClick += (_, args) => { if (args.Button == MouseButtons.Left) ShowDashboard(); };
        menu.Opening += (_, _) => BuildMenu();
        var keys = new HoldKeys(Settings.ParseKey(settings.DictationKey), Settings.ParseKey(settings.CleanupKey));
        keys.Changed += (key, down) => dispatcher.BeginInvoke(() =>
        {
            if (exiting) return;
            if (down) Start(key);
            else if (state == "Recording" && activeKey == key) work = StopAsync();
        });
        hook = new(keys, () => ready && !paused && !exiting && state == "Idle");
        timer.Tick += (_, _) =>
        {
            if (state == "Recording" && recording is not null &&
                (recording.Process.HasExited || (DateTime.UtcNow - started).TotalSeconds >= settings.MaxRecordingSeconds))
                work = StopAsync();
        };
        timer.Start();
        SetState("Starting");
        dispatcher.BeginInvoke(ShowDashboard);
        dispatcher.BeginInvoke(async () => await InitializeAsync());
    }

    private async Task InitializeAsync()
    {
        try
        {
            speech.CheckFiles();
            microphones = await speech.ListMicrophonesAsync();
            microphone = string.IsNullOrWhiteSpace(settings.Microphone) ? WindowsMicrophone.DefaultName() : settings.Microphone;
            dashboard.SetMicrophones(microphones, microphone);
            if (string.IsNullOrEmpty(microphone)) throw new IOException("No microphone found. Enable desktop microphone access in Windows Settings and restart HoldTalk.");
            if (!microphones.Contains(microphone, StringComparer.Ordinal))
                throw new IOException($"Microphone '{microphone}' is not a DirectShow input. Set microphone in config.json to one of: {string.Join(", ", microphones)}");
            await speech.PrepareAsync(lifetime.Token);
            if (exiting) return;
            ready = true;
            SetState("Idle");
            Notify($"Ready. Microphone: {microphone}. Hold {settings.DictationKey} to dictate; hold {settings.CleanupKey} for cleanup.");
        }
        catch (Exception exception) { if (!exiting) { SetState("Not ready"); Notify(exception.Message, true); } }
    }

    private void Start(Keys key)
    {
        if (!ready || paused || state != "Idle") return;
        try
        {
            wav = Path.Combine(Path.GetTempPath(), $"holdtalk-{Guid.NewGuid():N}.wav");
            recording = speech.Record(wav, microphone);
            activeKey = key;
            started = DateTime.UtcNow;
            SetState("Recording");
        }
        catch (Exception exception) { DeleteAudio(); SetState("Idle"); Notify(exception.Message, true); }
    }

    private async Task StopAsync()
    {
        if (state != "Recording" || recording is null || wav is null) return;
        SetState("Transcribing");
        bool fallback = false;
        try
        {
            if (!recording.Process.HasExited)
            {
                try { await recording.Process.StandardInput.WriteLineAsync("q"); await recording.Process.StandardInput.FlushAsync(); }
                catch (IOException) when (recording.Process.HasExited) { }
            }
            await recording.FinishAsync(10, lifetime.Token);
            recording.Dispose();
            recording = null;
            if (!File.Exists(wav) || new FileInfo(wav).Length < 3244) return;
            string text = await speech.TranscribeAsync(wav, lifetime.Token);
            DeleteAudio();
            if (string.IsNullOrWhiteSpace(text)) return;
            if (activeKey == Settings.ParseKey(settings.CleanupKey))
            {
                SetState("Cleaning");
                (text, fallback) = await cleanup.CleanAsync(text, lifetime.Token);
            }
            var transcript = new Transcript(DateTimeOffset.Now, text);
            recent.Insert(0, transcript);
            if (recent.Count > 10) recent.RemoveAt(10);
            dashboard.UpdateHistory(recent);
            try { log.Append(transcript); }
            catch (IOException) { Notify("Could not write ~/Dictation. The transcript is still in tray history.", true); }
            catch (UnauthorizedAccessException) { Notify("No permission to write ~/Dictation. The transcript is still in tray history.", true); }
            if (exiting) return;
            if (menu.Visible) throw new InvalidOperationException("Tray menu is open. Focus your text field, then copy the transcript from history.");
            SetState("Pasting");
            await ClipboardPaste.PasteAsync(text, settings.ClipboardRestoreDelayMs);
            if (fallback) Notify("Cleanup unavailable; pasted the original transcript.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) { if (!exiting) Notify(exception.Message, true); }
        finally
        {
            recording?.Dispose();
            recording = null;
            DeleteAudio();
            if (!exiting) SetState("Idle");
        }
    }

    private void DeleteAudio()
    {
        if (wav is null) return;
        foreach (string file in new[] { wav, wav + ".txt" })
        {
            try { File.Delete(file); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Notify($"Could not delete temporary file: {file}", true); }
        }
        wav = null;
    }

    private void SetState(string value)
    {
        state = value;
        tray.Text = $"HoldTalk — {(paused && value == "Idle" ? "Paused" : value)}";
        tray.Icon = icons[value == "Recording" ? "Recording" : value is "Transcribing" or "Cleaning" or "Pasting" ? "Transcribing" : "Idle"];
        dashboard.UpdateState(value, paused, ready, microphone);
    }

    private void Notify(string message, bool error = false)
    {
        tray.ShowBalloonTip(5000, error ? "HoldTalk — attention" : "HoldTalk", message, error ? ToolTipIcon.Warning : ToolTipIcon.Info);
        if (error) dashboard.ShowNotice(message, true);
    }

    private void BuildMenu()
    {
        while (menu.Items.Count > 0) { var item = menu.Items[0]; menu.Items.RemoveAt(0); item.Dispose(); }
        menu.Items.Add("Abrir HoldTalk", null, (_, _) => ShowDashboard());
        menu.Items.Add(new ToolStripMenuItem($"HoldTalk · {state}") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem($"Hold {settings.DictationKey} · Cleanup: {settings.CleanupKey}") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem($"Microphone: {microphone.Replace("&", "&&")}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        if (recent.Count == 0) menu.Items.Add(new ToolStripMenuItem("Your last 10 transcripts appear here") { Enabled = false });
        foreach (var transcript in recent)
        {
            string preview = SpeechEngine.Normalize(transcript.Text);
            if (preview.Length > 65) preview = preview[..65] + "…";
            var item = new ToolStripMenuItem($"{transcript.Time:HH:mm}  {preview.Replace("&", "&&")}") { ToolTipText = transcript.Text };
            var copy = new ToolStripMenuItem("Copy transcript") { Enabled = state != "Pasting" };
            copy.Click += (_, _) =>
            {
                CopyText(transcript.Text);
            };
            item.DropDownItems.Add(copy);
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        var pause = new ToolStripMenuItem("Pause hotkeys") { Checked = paused };
        pause.Click += (_, _) => TogglePause();
        menu.Items.Add(pause);
        AddOpen("Open transcript folder", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dictation"), true);
        AddOpen("Edit config (restart to apply)", Path.Combine(directory, "config.json"));
        AddOpen("Microphone permissions", "ms-settings:privacy-microphone");
        menu.Items.Add("Quit", null, async (_, _) => await QuitAsync());
    }

    private void AddOpen(string label, string path, bool create = false)
    {
        menu.Items.Add(label, null, (_, _) =>
        {
            OpenPath(path, create);
        });
    }

    private void ShowDashboard()
    {
        if (exiting) return;
        dashboard.Show();
        if (dashboard.WindowState == System.Windows.WindowState.Minimized) dashboard.WindowState = System.Windows.WindowState.Normal;
        dashboard.Activate();
    }

    private void TogglePause()
    {
        paused = !paused;
        SetState(state);
        if (state == "Recording") work = StopAsync();
    }

    private void CopyText(string text)
    {
        if (state == "Pasting") return;
        try { ClipboardSnapshot.Retry(() => { Clipboard.SetText(text); return true; }); dashboard.ShowNotice("Transcrição copiada.", false); }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    private void OpenPath(string path, bool create = false)
    {
        try { if (create) Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception exception) { Notify(exception.Message, true); }
    }

    private void SavePreferences(string selectedMicrophone, string language)
    {
        if (state is not ("Idle" or "Not ready")) return;
        var previous = (settings.Microphone, settings.Language);
        try
        {
            string resolvedMicrophone = string.IsNullOrWhiteSpace(selectedMicrophone) ? WindowsMicrophone.DefaultName() : selectedMicrophone;
            speech.CheckFiles();
            if (!microphones.Contains(resolvedMicrophone, StringComparer.Ordinal))
                throw new InvalidOperationException("Escolha um microfone disponível na lista.");
            settings.Microphone = selectedMicrophone;
            settings.Language = language;
            settings.Save(directory);
            microphone = resolvedMicrophone;
            ready = true;
            dashboard.PreferencesSaved(microphone, language);
            SetState("Idle");
        }
        catch (Exception exception)
        {
            (settings.Microphone, settings.Language) = previous;
            Notify(exception.Message, true);
        }
    }

    private async Task QuitAsync()
    {
        if (exiting) return;
        exiting = true;
        lifetime.Cancel();
        if (state == "Recording") await StopAsync();
        else await work;
        ExitThread();
    }

    private static Icon MakeIcon(Color color, System.Windows.Media.Geometry shape)
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(color.R, color.G, color.B));
            drawing.DrawEllipse(fill, null, new(16, 16), 15, 15);
            var pen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.White, 1.8)
            {
                StartLineCap = System.Windows.Media.PenLineCap.Round,
                EndLineCap = System.Windows.Media.PenLineCap.Round,
                LineJoin = System.Windows.Media.PenLineJoin.Round,
            };
            drawing.PushTransform(new System.Windows.Media.TranslateTransform(6, 6));
            drawing.PushTransform(new System.Windows.Media.ScaleTransform(20.0 / 24, 20.0 / 24));
            drawing.DrawGeometry(null, pen, shape);
            drawing.Pop(); drawing.Pop();
        }
        var rendered = new System.Windows.Media.Imaging.RenderTargetBitmap(32, 32, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rendered.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rendered));
        using var stream = new MemoryStream();
        encoder.Save(stream); stream.Position = 0;
        using var bitmap = new Bitmap(stream);
        nint handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { Native.DestroyIcon(handle); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose(); hook.Dispose(); recording?.Dispose(); DeleteAudio();
            tray.Visible = false; tray.Dispose(); menu.Dispose();
            speech.Dispose(); cleanup.Dispose(); dispatcher.Dispose(); lifetime.Dispose();
            dashboard.CloseForExit();
            foreach (var icon in icons.Values) icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
