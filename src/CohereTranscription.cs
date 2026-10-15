using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Holdtalk;

// Run in a child process so cancellation/timeouts also stop native inference and GPU work.
internal static class CohereTranscription
{
    public static async Task RunWorkerAsync(string library, string model)
    {
        NativeLibrary.SetDllImportResolver(typeof(CohereTranscription).Assembly,
            (name, _, _) => name == "transcribe" ? NativeLibrary.Load(Path.GetFullPath(library)) : IntPtr.Zero);
        if (Marshal.PtrToStringUTF8(transcribe_version()) != "0.4.0")
            throw new InvalidDataException("This app requires transcribe.cpp 0.4.0; run scripts/setup.ps1.");
        Check(transcribe_init_backends_default());
        Check(transcribe_open(model, IntPtr.Zero, IntPtr.Zero, out var session));
        try
        {
            string backend = Marshal.PtrToStringUTF8(transcribe_model_backend(transcribe_get_model(session))) ?? "unknown";
            Console.Error.WriteLine("Cohere backend: " + backend);
            // Allocate GPU buffers and compile kernels before enabling dictation.
            Transcribe(session, new float[16000], "pt", allowEmpty: true);
            await ReplyAsync(new(backend, null));
            while (await Console.In.ReadLineAsync() is string line)
            {
                SpeechResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<SpeechRequest>(line) ?? throw new InvalidDataException("Missing transcription request.");
                    var read = Stopwatch.StartNew();
                    float[] pcm = ReadAudio(request.Wav);
                    bool noAudio = !pcm.Any(sample => sample != 0);
                    double readMs = read.Elapsed.TotalMilliseconds;
                    if (noAudio) response = new("", null, NoAudio: true);
                    else
                    {
                        string text = Transcribe(session, pcm, request.Language);
                        transcribe_timings_init(out var timings);
                        if (timings.StructSize != (ulong)Marshal.SizeOf<NativeTimings>())
                            throw new InvalidDataException("Unexpected transcribe.cpp timing layout.");
                        Check(transcribe_get_timings(session, ref timings));
                        response = new(text, null, Timings: new(readMs, timings.MelMs, timings.EncodeMs, timings.DecodeMs));
                    }
                }
                catch (Exception exception) { response = new("", exception.Message); }
                await ReplyAsync(response);
            }
        }
        finally { transcribe_session_free(session); }
    }

    private static async Task ReplyAsync(SpeechResponse response)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response));
        await Console.Out.FlushAsync();
    }

    private static string Transcribe(IntPtr session, float[] pcm, string language, bool allowEmpty = false)
    {
        if (!Settings.SupportedLanguages.Contains(language)) throw new InvalidDataException("Cohere requires an explicit supported language.");
        if (pcm.Length is 0 or > 16000 * 400) throw new InvalidDataException("Audio must be between 0 and 400 seconds.");
        IntPtr languagePointer = Marshal.StringToCoTaskMemUTF8(language);
        try
        {
            transcribe_run_params_init(out var parameters);
            if (parameters.StructSize != (ulong)Marshal.SizeOf<RunParameters>())
                throw new InvalidDataException("Unexpected transcribe.cpp parameter layout.");
            parameters.Language = languagePointer;
            Check(transcribe_run(session, pcm, pcm.Length, ref parameters));
            string text = Marshal.PtrToStringUTF8(transcribe_full_text(session)) ?? "";
            if (!allowEmpty && string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Cohere returned an empty transcript.");
            return text;
        }
        finally { Marshal.FreeCoTaskMem(languagePointer); }
    }

    // Capture already produces this format; reading it directly avoids two FFmpeg startups per dictation.
    public static float[] ReadAudio(string wav)
    {
        using var reader = new BinaryReader(File.OpenRead(wav), Encoding.ASCII);
        var stream = reader.BaseStream;
        if (stream.Length < 12 || reader.ReadUInt32() != 0x46464952)
            throw new InvalidDataException("Invalid RIFF WAV recording.");
        uint riffSize = reader.ReadUInt32();
        if (riffSize + 8L > stream.Length || reader.ReadUInt32() != 0x45564157)
            throw new InvalidDataException("Incomplete WAV recording.");
        long end = riffSize + 8L;
        bool validFormat = false;
        while (stream.Position + 8 <= end)
        {
            uint chunk = reader.ReadUInt32(), size = reader.ReadUInt32();
            long next = stream.Position + size + (size & 1);
            if (next > end) throw new InvalidDataException("Incomplete WAV chunk.");
            if (chunk == 0x20746D66)
            {
                if (size < 16) throw new InvalidDataException("Invalid WAV format.");
                ushort format = reader.ReadUInt16(), channels = reader.ReadUInt16();
                uint rate = reader.ReadUInt32(), bytesPerSecond = reader.ReadUInt32();
                ushort alignment = reader.ReadUInt16(), bits = reader.ReadUInt16();
                validFormat = format == 1 && channels == 1 && rate == 16000 && bytesPerSecond == 32000 && alignment == 2 && bits == 16;
            }
            else if (chunk == 0x61746164)
            {
                if (!validFormat || size % 2 != 0 || size > 16000 * 400 * 2)
                    throw new InvalidDataException("Recording must be mono 16 kHz 16-bit PCM, at most 400 seconds.");
                byte[] bytes = reader.ReadBytes((int)size);
                if (bytes.Length != size) throw new InvalidDataException("Incomplete WAV audio data.");
                var samples = MemoryMarshal.Cast<byte, short>(bytes);
                var pcm = new float[samples.Length];
                for (int i = 0; i < pcm.Length; i++) pcm[i] = samples[i] / 32768f;
                return pcm;
            }
            stream.Position = next;
        }
        throw new InvalidDataException("WAV recording has no audio data.");
    }

    private static void Check(int status)
    {
        if (status != 0) throw new IOException("Cohere transcription failed: " + Marshal.PtrToStringUTF8(transcribe_status_string(status)));
    }

    // Layout from the pinned transcribe.cpp v0.4.0 include/transcribe.h.
    [StructLayout(LayoutKind.Sequential)]
    private struct RunParameters
    {
        public ulong StructSize;
        public int Task, Timestamps, Pnc, Itn, Diarize;
        public IntPtr Language, TargetLanguage;
        public byte KeepSpecialTags;
        public IntPtr Family;
        public int SpecKDrafts;
        public IntPtr Vocabulary;
        public int VocabularyCount;
        public IntPtr Prompt, Prefix;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTimings
    {
        public ulong StructSize;
        public float LoadMs, MelMs, EncodeMs, DecodeMs;
    }

    [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr transcribe_version();
    [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] private static extern int transcribe_init_backends_default();
    [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] private static extern int transcribe_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr loadParameters, IntPtr sessionParameters, out IntPtr session);
    [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] private static extern void transcribe_session_free(IntPtr session);
    [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] private static extern void transcribe_run_params_init(out RunParameters parameters);
    [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] private static extern int transcribe_run(IntPtr session, float[] pcm, int samples, ref RunParameters parameters);
    [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr transcribe_full_text(IntPtr session);
    [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr transcribe_status_string(int status);
    [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr transcribe_get_model(IntPtr session);
    [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr transcribe_model_backend(IntPtr model);
    [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] private static extern void transcribe_timings_init(out NativeTimings timings);
    [DllImport("transcribe", CallingConvention = CallingConvention.Cdecl)] private static extern int transcribe_get_timings(IntPtr session, ref NativeTimings timings);
}

internal sealed record SpeechRequest(string Wav, string Language);
internal sealed record SpeechResponse(string Text, string? Error, bool NoAudio = false, TranscriptionTimings? Timings = null);
public sealed record TranscriptionTimings(double ReadMs, float MelMs, float EncodeMs, float DecodeMs);
