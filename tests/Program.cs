using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Holdtalk;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
        Console.WriteLine("PASS " + message);
    }
    private static void Throws(Action operation, string message)
    {
        try { operation(); }
        catch (InvalidDataException) { Check(true, message); return; }
        throw new Exception(message);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string temp = Path.Combine(Path.GetTempPath(), "holdtalk-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            if (args.Contains("--microphone-check"))
            {
                string directory = args[^1];
                using var engine = new SpeechEngine(Settings.Load(directory), directory);
                Console.WriteLine("Windows default recording device: " + WindowsMicrophone.DefaultName());
                foreach (string microphone in engine.ListMicrophonesAsync().GetAwaiter().GetResult())
                    Console.WriteLine("DirectShow microphone: " + microphone);
                return 0;
            }
            TestSettings(temp);
            TestAudio(temp);
            TestKeys();
            TestHistory(temp);
            TestCleanupAsync().GetAwaiter().GetResult();
            TestModelDownloadAsync(temp).GetAwaiter().GetResult();
            TestProcessesAsync().GetAwaiter().GetResult();
            if (args.Contains("--ui")) { TestDashboard(args[^1]); TestModelDownloadDialog(temp, args[^1]); }
            if (args.Contains("--windows")) TestWindows();
            if (args.Contains("--speech")) TestSpeechAsync(args[^1]).GetAwaiter().GetResult();
            if (args.Contains("--benchmark")) TestBenchmarkAsync(args[^1]).GetAwaiter().GetResult();
            if (args.Contains("--latency")) TestLatencyAsync(args[^1]).GetAwaiter().GetResult();
            Console.WriteLine($"{checks} checks passed.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { Directory.Delete(temp, true); }
    }

    private static void TestSettings(string temp)
    {
        new Settings().Validate();
        Check(Settings.ParseKey("rcontrolkey") == Keys.RControlKey, "default right Ctrl and case-insensitive config");
        Throws(() => new Settings { CleanupKey = "RControlKey" }.Validate(), "duplicate hotkeys rejected");
        Throws(() => new Settings { DictationKey = "Control" }.Validate(), "ambiguous modifier rejected");
        Throws(() => new Settings { ClipboardRestoreDelayMs = 0 }.Validate(), "unsafe restore delay rejected");
        Throws(() => new Settings { ModelPath = "" }.Validate(), "empty model rejected");
        Throws(() => new Settings { Language = "auto" }.Validate(), "Cohere rejects automatic language detection");
        Throws(() => new Settings { Language = "xx" }.Validate(), "unsupported Cohere language rejected");
        Throws(() => new Settings { TranscribeLibraryPath = "" }.Validate(), "empty native library path rejected");
        Throws(() => new Settings { MaxRecordingSeconds = 401 }.Validate(), "Cohere recording limit enforced");
        foreach (string language in Settings.SupportedLanguages) new Settings { Language = language, MaxRecordingSeconds = 400 }.Validate();
        Check(Settings.SupportedLanguages.Length == 14, "all 14 Cohere languages accept the maximum recording duration");
        File.WriteAllText(Path.Combine(temp, ".env"), "# comment\nLLM_API_KEY=\"a=b#c\"\nLLM_MODEL='local-model'\n");
        var env = Settings.ReadEnv(Path.Combine(temp, ".env"));
        Check(env["LLM_API_KEY"] == "a=b#c" && env["LLM_MODEL"] == "local-model", ".env preserves quoted keys and equals signs");
        Check(Settings.ReadEnv(Path.Combine(temp, "missing")).Count == 0, "missing .env is offline");
        Check(Settings.ResolvePath(temp, "model.bin") == Path.Combine(temp, "model.bin"), "paths resolve relative to app, not working directory");
        string checkout = Path.Combine(temp, "checkout");
        string build = Path.Combine(checkout, "src", "bin", "Release", "net10.0-windows");
        string prepared = Path.Combine(checkout, "dist", "Holdtalk");
        Directory.CreateDirectory(build);
        Directory.CreateDirectory(prepared);
        File.WriteAllText(Path.Combine(checkout, "src", "Holdtalk.csproj"), "<Project />");
        Check(Settings.FindAppDirectory(build) == build, "unprepared development build keeps its own directory");
        File.WriteAllText(Path.Combine(prepared, "config.json"), "{}");
        Check(Settings.FindAppDirectory(build) == prepared, "development build finds prepared config and dependencies");
        Check(Settings.FindAppDirectory(prepared) == prepared, "portable app keeps its own configuration");
        Check(Settings.FindAppDirectory(Path.Combine(temp, "other-app")) == Path.Combine(temp, "other-app"), "unrelated app never inherits checkout configuration");
        Check(SpeechEngine.Normalize(" hello\r\n world  ") == "hello world", "transcription output normalized");
        var savedSettings = new Settings { Microphone = "Physical microphone", Language = "pt", ModelPath = "models/custom.bin" };
        savedSettings.Save(temp);
        var reloaded = Settings.Load(temp);
        Check(reloaded.Microphone == "Physical microphone" && reloaded.Language == "pt" && reloaded.ModelPath == "models/custom.bin", "UI preferences persist without losing model path");
        savedSettings.ClipboardRestoreDelayMs = 0;
        Throws(() => savedSettings.Save(temp), "invalid settings cannot overwrite saved config");
        Check(Settings.Load(temp).ClipboardRestoreDelayMs == 500, "failed settings save preserves existing file");
    }

    private static void TestKeys()
    {
        var keys = new HoldKeys(Keys.RControlKey, Keys.F8);
        var events = new List<(Keys, bool)>();
        keys.Changed += (key, down) => events.Add((key, down));
        Check(!keys.Handle(Keys.LControlKey, true, true), "left Ctrl remains available");
        Check(keys.Handle(Keys.RControlKey, true, true), "hold starts and suppresses hotkey");
        Check(keys.Handle(Keys.RControlKey, true, false) && events.Count == 1, "auto-repeat suppressed without a second start");
        Check(keys.Handle(Keys.RControlKey, false, false) && events.Count == 2 && !events[^1].Item2, "release stops even while busy or paused");
        Check(!keys.Handle(Keys.F8, true, false) && !keys.Handle(Keys.F8, false, false), "paused hotkeys pass through");
        keys.Handle(Keys.F8, true, true); keys.Handle(Keys.F8, false, false);
        Check(events.Count == 4 && events[^2] == (Keys.F8, true), "cleanup is a separate hold/release key");
    }

    private static void TestHistory(string temp)
    {
        var log = new TranscriptLog(Path.Combine(temp, "archive"));
        Check(log.Recent().Count == 0, "no history before first use");
        var start = new DateTimeOffset(2026, 9, 30, 23, 59, 55, TimeSpan.FromHours(-3));
        for (int i = 0; i < 15; i++) log.Append(new(start.AddSeconds(i), $"Transcript {i}."));
        var recent = log.Recent();
        Check(recent.Count == 10 && recent[0].Text == "Transcript 14." && recent[^1].Text == "Transcript 5.", "last ten reload across monthly boundary");
        Check(File.ReadAllText(Path.Combine(temp, "archive", "2026-10.md")).Contains("2026-10-01 00:00:00 -03:00"), "timestamp and timezone archived");
    }

    private static async Task TestCleanupAsync()
    {
        const string original = "um hello there";
        var never = new FakeHandler((_, _) => throw new Exception("Network used without a key!"));
        using (var client = new CleanupClient([], never))
            Check(await client.CleanAsync(original) == (original, false), "no key makes zero network calls");
        var env = new Dictionary<string, string> { ["LLM_API_KEY"] = "test-key", ["LLM_ENDPOINT"] = "http://localhost:1234/v1/chat/completions", ["LLM_MODEL"] = "test" };
        using (var client = new CleanupClient(env, new FakeHandler(async (request, _) =>
        {
            Check(request.Headers.Authorization?.Parameter == "test-key", "cleanup authenticates only explicit request");
            Check((await request.Content!.ReadAsStringAsync()).Contains(original), "cleanup sends text, not audio");
            return Json("{\"choices\":[{\"message\":{\"content\":\"Hello there.\"}}]}");
        }))) Check(await client.CleanAsync(original) == ("Hello there.", false), "cleanup response pasted verbatim");
        foreach (var response in new[] { Json("not json"), Json("{\"choices\":[]}"), Json("{\"choices\":[{\"message\":{\"content\":\"\"}}]}"), new HttpResponseMessage(HttpStatusCode.Unauthorized) })
        {
            using var client = new CleanupClient(env, new FakeHandler((_, _) => Task.FromResult(response)));
            Check(await client.CleanAsync(original) == (original, true), "failed/empty/malformed cleanup preserves original");
        }
        env["LLM_ENDPOINT"] = "http://untrusted.example/v1/chat/completions";
        using (var client = new CleanupClient(env, never))
            Check(await client.CleanAsync(original) == (original, true), "remote plaintext endpoint rejected before sending key");
        env["LLM_ENDPOINT"] = "https://example.com/v1/chat/completions";
        using (var client = new CleanupClient(env, new FakeHandler((_, _) => throw new HttpRequestException("offline"))))
            Check(await client.CleanAsync(original) == (original, true), "offline cleanup falls back");
    }

    private static async Task TestModelDownloadAsync(string temp)
    {
        string path = Path.Combine(temp, "models", "cohere.gguf");
        var progress = new Progress<int>();
        using (var client = new HttpClient(new FakeHandler((request, _) =>
        {
            Check(request.RequestUri?.AbsoluteUri == ModelDownloadDialog.ModelUrl, "model download uses the pinned HTTPS revision");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("corrupted model") });
        })))
        {
            try { await ModelDownloadDialog.DownloadAsync(client, path, progress, CancellationToken.None); throw new Exception("Corrupted model accepted"); }
            catch (InvalidDataException) { Check(true, "model download rejects a mismatched SHA256"); }
        }
        Check(!File.Exists(path) && !File.Exists(path + ".part"), "corrupted model and partial download are removed");
        File.WriteAllText(path, "existing model");
        using (var client = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))))
        {
            try { await ModelDownloadDialog.DownloadAsync(client, path, progress, CancellationToken.None); throw new Exception("HTTP failure ignored"); }
            catch (HttpRequestException) { Check(true, "model download reports HTTP failure"); }
        }
        Check(File.ReadAllText(path) == "existing model", "failed download preserves an existing model");
        using var cancellation = new CancellationTokenSource();
        using (var client = new HttpClient(new FakeHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[200000]) }))))
        {
            try
            {
                await ModelDownloadDialog.DownloadAsync(client, path, new CallbackProgress(_ => cancellation.Cancel()), cancellation.Token);
                throw new Exception("Cancellation ignored");
            }
            catch (OperationCanceledException) { Check(true, "model download supports cancellation while writing a partial file"); }
        }
        Check(!File.Exists(path + ".part") && File.ReadAllText(path) == "existing model", "cancelled download leaves no partial file and preserves existing models");
    }

    private static async Task TestProcessesAsync()
    {
        using (var process = new ChildProcess("cmd.exe", ["/d", "/c", "echo %HOLDTALK_TEST_ENV%"],
            environment: new Dictionary<string, string> { ["HOLDTALK_TEST_ENV"] = "child-only" }))
            Check((await process.FinishAsync(5)).Trim() == "child-only" && Environment.GetEnvironmentVariable("HOLDTALK_TEST_ENV") is null,
                "native child environment is set at startup without changing the parent");
        using (var process = new ChildProcess("cmd.exe", ["/d", "/c", "echo ready"]))
            Check((await process.FinishAsync(5)).Contains("ready"), "child process output drained");
        using (var process = new ChildProcess("cmd.exe", ["/d", "/c", "exit 7"]))
        {
            try { await process.FinishAsync(5); throw new Exception("Exit ignored"); }
            catch (IOException) { Check(true, "nonzero child exit reported"); }
        }
        using (var process = new ChildProcess("powershell.exe", ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"]))
        {
            try { await process.FinishAsync(1); throw new Exception("Timeout ignored"); }
            catch (TimeoutException) { Check(process.Process.HasExited, "timed-out process killed"); }
        }
    }

    private static void TestWindows()
    {
        ApplicationConfiguration.Initialize();
        using var saved = ClipboardSnapshot.Capture();
        try
        {
            Clipboard.Clear();
            using (var snapshot = ClipboardSnapshot.Capture())
            { Clipboard.SetText("temporary"); snapshot.Restore(); Check(Clipboard.GetDataObject()!.GetFormats().Length == 0, "empty clipboard restored"); }
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, "original clipboard");
            data.SetData(DataFormats.Html, "<b>original clipboard</b>");
            data.SetFileDropList(new() { "C:\\Windows\\notepad.exe" });
            Clipboard.SetDataObject(data, true);
            using (var snapshot = ClipboardSnapshot.Capture())
            {
                Clipboard.SetText("temporary"); snapshot.Restore();
                Check(Clipboard.GetText() == "original clipboard", "text clipboard restored");
                Check(Clipboard.GetText(TextDataFormat.Html).Contains("<b>original clipboard</b>"), "HTML clipboard restored");
                Check(Clipboard.GetFileDropList().Contains("C:\\Windows\\notepad.exe"), "file-list clipboard restored");
            }
            using (var bitmap = new Bitmap(4, 4)) { bitmap.SetPixel(0, 0, Color.Red); Clipboard.SetImage(bitmap); }
            using (var snapshot = ClipboardSnapshot.Capture())
            { Clipboard.SetText("temporary"); snapshot.Restore(); using var image = Clipboard.GetImage(); Check(image is not null && new Bitmap(image).GetPixel(0, 0).R == 255, "image clipboard restored"); }
            using (var owner = new Control())
            using (var bitmap = new Bitmap(4, 4))
            {
                bitmap.SetPixel(0, 0, Color.Blue);
                nint handle = bitmap.GetHbitmap();
                if (!Native.OpenClipboard(owner.Handle)) { Native.DeleteObject(handle); throw new Exception("Clipboard busy in native image test"); }
                try
                {
                    if (!Native.EmptyClipboard() || Native.SetClipboardData(2, handle) == 0)
                    { Native.DeleteObject(handle); throw new Exception("Could not set native screenshot-style bitmap"); }
                }
                finally { Native.CloseClipboard(); }
                using var snapshot = ClipboardSnapshot.Capture();
                Clipboard.SetText("temporary"); snapshot.Restore();
                using var image = Clipboard.GetImage();
                Check(image is not null && new Bitmap(image).GetPixel(0, 0).B == 255, "native screenshot bitmap clipboard restored");
            }
            Clipboard.SetText("original clipboard");
            using var form = new Form { Text = "Holdtalk isolated paste test", Width = 400, Height = 120, StartPosition = FormStartPosition.CenterScreen, TopMost = true };
            var field = new TextBox { Dock = DockStyle.Fill, Multiline = true };
            form.Controls.Add(field);
            form.Shown += async (_, _) =>
            {
                try
                {
                    form.Activate(); form.BringToFront(); field.Focus();
                    uint foregroundThread = FocusTest.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
                    uint ownThread = FocusTest.GetCurrentThreadId();
                    if (foregroundThread != ownThread) FocusTest.AttachThreadInput(ownThread, foregroundThread, true);
                    try { FocusTest.SetForegroundWindow(form.Handle); field.Focus(); }
                    finally { if (foregroundThread != ownThread) FocusTest.AttachThreadInput(ownThread, foregroundThread, false); }
                    await Task.Delay(150);
                    Check(Native.GetForegroundWindow() == form.Handle, "test owns focus before simulating paste");
                    Check(Marshal.SizeOf<Native.Input>() == (IntPtr.Size == 8 ? 40 : 28), "SendInput struct matches Windows ABI");
                    await ClipboardPaste.PasteAsync("isolated dictation", 150);
                    Check(field.Text == "isolated dictation", "Ctrl+V reaches focused native text box");
                    Check(Clipboard.GetText() == "original clipboard", "paste restores previous clipboard");
                    var paste = ClipboardPaste.PasteAsync("second dictation", 300);
                    await Task.Delay(100); Clipboard.SetText("new user copy"); await paste;
                    Check(Clipboard.GetText() == "new user copy", "clipboard changed during paste is left intact");
                }
                catch (Exception exception) { form.Tag = exception; }
                finally { form.Close(); }
            };
            Application.Run(form);
            if (form.Tag is Exception failure) throw failure;
        }
        finally { saved.Restore(); }
    }

    private static async Task TestSpeechAsync(string directory)
    {
        var settings = Settings.Load(directory);
        using var engine = new SpeechEngine(settings, directory);
        engine.CheckFiles();
        bool portuguese = settings.Language.Equals("pt", StringComparison.OrdinalIgnoreCase);
        string sample = Path.GetFullPath(Path.Combine(directory, portuguese ? "../../tests/fixtures/portuguese.wav" : "../../.downloads/jfk.wav"));
        string wav = Path.Combine(Path.GetTempPath(), "holdtalk-speech-test-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            File.Copy(sample, wav);
            string result = await engine.TranscribeAsync(wav);
            Check(portuguese
                ? result.Contains("relatório", StringComparison.OrdinalIgnoreCase) && result.Contains("reunião", StringComparison.OrdinalIgnoreCase) && System.Text.RegularExpressions.Regex.IsMatch(result, @"(9|nove) horas")
                : result.Contains("country", StringComparison.OrdinalIgnoreCase) && result.Contains("ask", StringComparison.OrdinalIgnoreCase),
                "real Cohere configured model recognizes offline test phrase");
            string repeated = await engine.TranscribeAsync(wav);
            Check(repeated == result, "persistent worker reuses the model without changing recognition");
            using (var cancellation = new CancellationTokenSource(30))
            {
                try { await engine.TranscribeAsync(wav, cancellation.Token); throw new Exception("Native inference was not cancelled."); }
                catch (OperationCanceledException) { Check(engine.Backend == "", "cancellation stops and clears the native worker"); }
            }
            Check(await engine.TranscribeAsync(wav) == result, "dictation recovers after a cancelled native worker");
            Check(await engine.HasAudioAsync(wav), "speech is accepted by audio signal check");
            using (var quiet = new ChildProcess(engine.Ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-y", "-i", sample, "-af", "volume=0.001", wav]))
                await quiet.FinishAsync(10);
            Check(await engine.HasAudioAsync(wav), "very quiet speech is not discarded as digital silence");
            using (var silence = new ChildProcess(engine.Ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-t", "1", "-c:a", "pcm_s16le", wav]))
                await silence.FinishAsync(10);
            Check(!await engine.HasAudioAsync(wav), "digitally silent recording is detected");
            try { await engine.TranscribeAsync(wav); throw new Exception("Silence was transcribed"); }
            catch (InvalidOperationException exception) when (exception.Message.StartsWith("No audio signal"))
            { Check(true, "silence cannot become a hallucinated transcript"); }
            Check(engine.Backend != "", "silence rejection keeps the warmed worker resident");
            File.Copy(sample, wav, overwrite: true);
            Check(await engine.TranscribeAsync(wav) == result, "speech after silence reuses the worker without changing recognition");
            Check(engine.LastTimings is { ReadMs: >= 0, MelMs: > 0, EncodeMs: > 0, DecodeMs: > 0 }, "native stage timings are available after recognition");
            string[] microphones = await engine.ListMicrophonesAsync();
            string microphone = string.IsNullOrWhiteSpace(settings.Microphone) ? WindowsMicrophone.DefaultName() : settings.Microphone;
            Check(microphones.Contains(microphone), "configured microphone matches real DirectShow input");
            File.Delete(wav); File.Delete(wav + ".txt");
            using (var recording = engine.Record(wav, microphone))
            {
                await Task.Delay(1200);
                await recording.Process.StandardInput.WriteLineAsync("q");
                await recording.Process.StandardInput.FlushAsync();
                await recording.FinishAsync(10);
                Check(new FileInfo(wav).Length > 3244, "real microphone starts, stops on q, and flushes WAV");
            }
            using (var probe = new ChildProcess(Path.Combine(Path.GetDirectoryName(engine.Ffmpeg)!, "ffprobe.exe"),
                ["-v", "error", "-show_entries", "stream=codec_name,sample_rate,channels", "-of", "json", wav]))
            {
                using var audio = System.Text.Json.JsonDocument.Parse(await probe.FinishAsync(10));
                var stream = audio.RootElement.GetProperty("streams")[0];
                Check(stream.GetProperty("sample_rate").GetString() == "16000" &&
                    stream.GetProperty("channels").GetInt32() == 1 && stream.GetProperty("codec_name").GetString() == "pcm_s16le",
                    "hardware capture is mono 16 kHz signed 16-bit PCM");
            }
            File.Delete(wav);
            using (var recording = engine.Record(wav, microphone))
            {
                await recording.Process.StandardInput.WriteLineAsync("q");
                await recording.Process.StandardInput.FlushAsync();
                await recording.FinishAsync(10);
                Check(recording.Process.HasExited, "immediate key release shuts recorder down safely");
            }
        }
        finally { File.Delete(wav); File.Delete(wav + ".txt"); }
    }

    private static async Task TestBenchmarkAsync(string directory)
    {
        string previous = Environment.GetEnvironmentVariable("TRANSCRIBE_BACKENDS") ?? "";
        var settings = Settings.Load(directory);
        settings.Language = "en";
        string sample = Path.GetFullPath(Path.Combine(directory, "../../.downloads/jfk.wav"));
        try
        {
            foreach (string backend in new[] { "cpu", "vulkan" })
            {
                Environment.SetEnvironmentVariable("TRANSCRIBE_BACKENDS", backend);
                using var engine = new SpeechEngine(settings, directory);
                await engine.PrepareAsync();
                Check(engine.Backend.StartsWith(backend, StringComparison.OrdinalIgnoreCase), $"benchmark really uses {backend}");
                for (int run = 1; run <= 2; run++)
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    string transcript = await engine.TranscribeAsync(sample);
                    Check(transcript.Contains("country", StringComparison.OrdinalIgnoreCase) && transcript.Contains("ask", StringComparison.OrdinalIgnoreCase), $"{backend} warm benchmark retains recognition");
                    Console.WriteLine($"BENCHMARK {backend} run {run}: {timer.Elapsed.TotalMilliseconds:F0} ms for 11 seconds of audio.");
                }
            }
        }
        finally { Environment.SetEnvironmentVariable("TRANSCRIBE_BACKENDS", previous); }
    }

    private static async Task TestLatencyAsync(string directory)
    {
        var settings = Settings.Load(directory);
        using var engine = new SpeechEngine(settings, directory);
        string sample = Path.GetFullPath(Path.Combine(directory, settings.Language == "pt" ? "../../tests/fixtures/portuguese.wav" : "../../.downloads/jfk.wav"));
        string wav = Path.Combine(Path.GetTempPath(), "holdtalk-latency-" + Guid.NewGuid().ToString("N") + ".wav");
        var startup = System.Diagnostics.Stopwatch.StartNew();
        await engine.PrepareAsync();
        Console.WriteLine($"STARTUP: {startup.Elapsed.TotalMilliseconds:F0} ms; backend {engine.Backend}.");
        try
        {
            foreach (int seconds in new[] { 5, 10, 15, 30 })
            {
                var elapsed = new List<double>();
                string? firstText = null;
                using (var clip = new ChildProcess(engine.Ffmpeg,
                    ["-hide_banner", "-loglevel", "error", "-y", "-stream_loop", "-1", "-i", sample, "-t", seconds.ToString(), "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav]))
                    await clip.FinishAsync(10);
                for (int run = 0; run <= 5; run++)
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    string text = await engine.TranscribeAsync(wav);
                    double milliseconds = timer.Elapsed.TotalMilliseconds;
                    if (run > 0) elapsed.Add(milliseconds);
                    Check(!string.IsNullOrWhiteSpace(text), "warm Cohere returns a transcript");
                    firstText ??= text;
                    Check(text == firstText, "repeated recognition preserves the complete transcript");
                    var stages = engine.LastTimings ?? throw new Exception("Missing native stage timings.");
                    Console.WriteLine($"LATENCY {seconds}s run {run}: {milliseconds:F1} ms (read {stages.ReadMs:F1}, mel {stages.MelMs:F1}, encode {stages.EncodeMs:F1}, decode {stages.DecodeMs:F1}). {text}");
                    if (settings.Language == "pt")
                        Check(text.Contains("relatório", StringComparison.OrdinalIgnoreCase), "Portuguese dictation retains the key phrase");
                }
                elapsed.Sort();
                Console.WriteLine($"WARM {seconds}s: median {elapsed[elapsed.Count / 2]:F1} ms; sample p95 {elapsed[(int)Math.Ceiling(elapsed.Count * 0.95) - 1]:F1} ms (5 runs; excludes first use of this length).");
            }
        }
        finally { File.Delete(wav); }
    }

    private static void TestAudio(string temp)
    {
        using var engine = new SpeechEngine(new Settings(), temp);
        string wav = Path.Combine(temp, "signal.wav");
        void WriteWave(short sample, int rate = 16000, bool truncated = false)
        {
            using var writer = new BinaryWriter(File.Create(wav));
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(48u);
            writer.Write(Encoding.ASCII.GetBytes("WAVEJUNK")); writer.Write(1u); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write(Encoding.ASCII.GetBytes("fmt ")); writer.Write(16u);
            writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(rate); writer.Write(rate * 2);
            writer.Write((ushort)2); writer.Write((ushort)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(2u);
            if (!truncated) writer.Write(sample);
        }
        WriteWave(0);
        Check(!engine.HasAudioAsync(wav).GetAwaiter().GetResult(), "PCM parser skips padded ancillary chunks and rejects digital silence");
        WriteWave(1);
        Check(engine.HasAudioAsync(wav).GetAwaiter().GetResult(), "PCM parser preserves the quietest 16-bit signal");
        Check(CohereTranscription.ReadAudio(wav).SequenceEqual([1 / 32768f]), "bulk PCM reader converts the quietest sample exactly");
        WriteWave(-32768);
        Check(engine.HasAudioAsync(wav).GetAwaiter().GetResult(), "PCM parser accepts negative full-scale samples");
        Check(CohereTranscription.ReadAudio(wav).SequenceEqual([-1f]), "bulk PCM reader preserves negative full scale");
        WriteWave(32767);
        Check(CohereTranscription.ReadAudio(wav).SequenceEqual([32767 / 32768f]), "bulk PCM reader preserves positive full scale");
        WriteWave(1, 48000);
        Throws(() => engine.HasAudioAsync(wav).GetAwaiter().GetResult(), "unexpected recording sample rate rejected");
        WriteWave(1, truncated: true);
        Throws(() => engine.HasAudioAsync(wav).GetAwaiter().GetResult(), "truncated WAV cannot become a transcript");
        File.WriteAllText(wav, "not audio");
        Throws(() => engine.HasAudioAsync(wav).GetAwaiter().GetResult(), "invalid WAV header rejected");
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class CallbackProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private static void TestModelDownloadDialog(string temp, string outputDirectory)
    {
        string path = Path.Combine(temp, "declined-model.gguf");
        using var dialog = new ModelDownloadDialog(path);
        dialog.Shown += (_, _) =>
        {
            using var bitmap = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(bitmap, dialog.ClientRectangle with { Width = dialog.Width, Height = dialog.Height });
            bitmap.Save(Path.Combine(outputDirectory, "model-download.png"));
            ((Button)dialog.CancelButton!).PerformClick();
        };
        Check(dialog.ShowDialog() == DialogResult.Cancel && !File.Exists(path), "model download can be declined without creating a model");
    }

    private static void TestDashboard(string outputDirectory)
    {
        var settings = new Settings { Microphone = "Microphone (High Definition Audio Device)", Language = "pt", ModelPath = "models/cohere-transcribe-03-2026-Q4_K_M.gguf" };
        var window = new Dashboard(settings);
        try
        {
            window.Width = 1040; window.Height = 760;
            window.SetMicrophones([settings.Microphone, "Virtual Mic (AudioRelay)"], settings.Microphone);
            window.UpdateState("Idle", false, true, settings.Microphone);
            window.UpdateHistory([new(DateTimeOffset.Now, "A reunião começa às nove horas. Vou enviar o relatório para a equipe."), new(DateTimeOffset.Now.AddMinutes(-12), "Uma ideia de cada vez. Falar pode ser o jeito mais simples de começar.")]);
            T Find<T>(string name) where T : class => (T)window.FindName(name);
            Check(window.Icon is not null && window.FindResource("BrandLogoWhite") is System.Windows.Media.DrawingImage brand && !brand.Drawing.Bounds.IsEmpty, "brand wordmark and Windows icon load as native assets");
            string[] iconNames = ["Home", "History", "Settings", "Microphone", "Copy", "Minimize", "Maximize", "Close", "Recording", "Transcribing", "Dropdown"];
            Check(iconNames.All(name => window.FindResource("Mx" + name) is System.Windows.Media.Geometry geometry && !geometry.IsEmpty() && new System.Windows.Rect(0, 0, 24, 24).Contains(geometry.Bounds)), "mx-icons load as native vectors within their SVG viewport");
            var iconPreview = new System.Windows.Controls.StackPanel { Background = System.Windows.Media.Brushes.DarkSlateGray };
            foreach (int size in new[] { 16, 24, 32, 48 })
            {
                var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                foreach (string name in iconNames)
                {
                    var geometry = (System.Windows.Media.Geometry)window.FindResource("Mx" + name);
                    row.Children.Add(new System.Windows.Controls.ContentControl { Content = geometry, Style = (System.Windows.Style)window.FindResource("MxIcon"), Width = size, Height = size, Foreground = System.Windows.Media.Brushes.White, Margin = new System.Windows.Thickness(8) });
                }
                iconPreview.Children.Add(row);
            }
            iconPreview.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            iconPreview.Arrange(new System.Windows.Rect(iconPreview.DesiredSize));
            iconPreview.UpdateLayout();
            var iconBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)iconPreview.ActualWidth, (int)iconPreview.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            iconBitmap.Render(iconPreview);
            var iconEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            iconEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(iconBitmap));
            Directory.CreateDirectory(outputDirectory);
            using (var preview = File.Create(Path.Combine(outputDirectory, "icons.png"))) iconEncoder.Save(preview);
            Check(Find<System.Windows.Controls.TextBlock>("HeaderMicrophone").Text == settings.Microphone, "dashboard exposes selected microphone");
            window.ShowPage("history");
            Check(Find<System.Windows.Controls.Grid>("HistoryPage").Visibility == System.Windows.Visibility.Visible, "history navigation opens real page");
            Find<System.Windows.Controls.TextBox>("HistorySearch").Text = "relatório";
            Check(Find<System.Windows.Controls.ItemsControl>("HistoryList").Items.Count == 1, "history search filters real transcripts");
            Find<System.Windows.Controls.TextBox>("HistorySearch").Text = "missing word";
            Check(Find<System.Windows.Controls.TextBlock>("HistoryEmpty").Visibility == System.Windows.Visibility.Visible, "no-results state is visible");
            Find<System.Windows.Controls.Button>("ClearSearchButton").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(Find<System.Windows.Controls.ItemsControl>("HistoryList").Items.Count == 2 && Find<System.Windows.Controls.TextBox>("HistorySearch").Text.Length == 0, "clear search restores all transcripts");
            window.ShowPage("home");
            Find<System.Windows.Controls.Button>("HeaderMicrophoneButton").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(Find<System.Windows.Controls.ScrollViewer>("SettingsPage").Visibility == System.Windows.Visibility.Visible, "header microphone opens its settings");
            var details = Find<System.Windows.Controls.Expander>("ProcessingDetails");
            details.IsExpanded = true;
            Check(Find<System.Windows.Controls.TextBlock>("RuntimeDetails").Text.Length > 0, "processing details retain actual runtime information");
            details.IsExpanded = false;
            window.ShowNotice("Configurações salvas.", false);
            Find<System.Windows.Controls.Button>("DismissNoticeButton").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(Find<System.Windows.Controls.Border>("Notice").Visibility == System.Windows.Visibility.Collapsed, "status message can be dismissed");
            window.ShowPage("settings");
            (string, string)? requested = null;
            window.PreferencesRequested += (mic, language) => requested = (mic, language);
            Find<System.Windows.Controls.ComboBox>("LanguageChoice").SelectedValue = "en";
            Find<System.Windows.Controls.Button>("SaveSettingsButton").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(requested == (settings.Microphone, "en"), "settings controls dispatch microphone and language values");
            Find<System.Windows.Controls.ComboBox>("LanguageChoice").SelectedValue = settings.Language;
            window.UpdateState("Not ready", false, false, settings.Microphone);
            Check(Find<System.Windows.Controls.Button>("SaveSettingsButton").IsEnabled, "unavailable microphone can be corrected inside the UI");
            window.UpdateState("Pasting", false, true, settings.Microphone);
            Check(!window.CanCopy && !Find<System.Windows.Controls.Button>("SaveSettingsButton").IsEnabled, "pasting protects clipboard and preferences");
            window.UpdateState("Transcribing", false, true, settings.Microphone);
            Check(ReferenceEquals(Find<System.Windows.Controls.ContentControl>("StateIcon").Content, window.FindResource("MxTranscribing")), "processing state uses the mx-icons clock");
            Check(Find<System.Windows.Controls.ProgressBar>("WorkProgress").Visibility == System.Windows.Visibility.Visible, "transcription has visible loading feedback");
            window.UpdateState("Idle", true, true, settings.Microphone);
            Check((string)Find<System.Windows.Controls.Button>("PauseButton").Content == "Ativar atalhos", "pause control reflects actual state");
            window.UpdateState("Idle", false, true, settings.Microphone);
            window.ShowPage("home");
            Directory.CreateDirectory(outputDirectory);
            void Render(string name)
            {
                Find<System.Windows.Controls.TextBlock>("FooterStatus").Text = "Prévia visual · frases de teste";
                var content = (System.Windows.FrameworkElement)window.Content;
                content.Measure(new System.Windows.Size(window.Width, window.Height));
                content.Arrange(new System.Windows.Rect(0, 0, window.Width, window.Height));
                content.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(outputDirectory, name + ".png"));
                encoder.Save(file);
            }
            Render("home");
            window.UpdateState("Recording", false, true, settings.Microphone); Render("recording");
            window.UpdateState("Transcribing", false, true, settings.Microphone); Render("transcribing");
            window.UpdateState("Idle", false, true, settings.Microphone);
            window.ShowPage("history"); Render("history"); window.ShowPage("settings"); Render("settings");
            window.Width = 840; window.Height = 600; window.ShowPage("home"); Render("compact");
            Check(Find<System.Windows.Controls.StackPanel>("HomeIntroduction").Visibility == System.Windows.Visibility.Collapsed, "compact layout prioritizes dictation and transcripts");
            window.ShowPage("settings"); Render("settings-compact");
            window.Width = 1040; window.Height = 760; window.ShowPage("history");
            Find<System.Windows.Controls.TextBox>("HistorySearch").Text = "sem resultado"; Render("search-empty");
            Find<System.Windows.Controls.TextBox>("HistorySearch").Clear();
            window.UpdateHistory([new(DateTimeOffset.Now, string.Join(" ", Enumerable.Repeat("Esta é uma transcrição longa para conferir a leitura e a quebra de linha.", 12)))]); Render("history-long");
            window.UpdateHistory([]); window.ShowPage("home"); Render("empty");
            Check(File.Exists(Path.Combine(outputDirectory, "home.png")), "native UI renders to preview artifacts");
        }
        finally { window.CloseForExit(); }
    }
    private static class FocusTest
    {
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint handle, out uint process);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint handle);
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
