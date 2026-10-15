using System.Security.Cryptography;

namespace Holdtalk;

public sealed class ModelDownloadDialog : Form
{
    public const string ModelUrl = "https://huggingface.co/handy-computer/cohere-transcribe-03-2026-gguf/resolve/ab667acedcb5d8d56837ef3ed3568c3ad50dde47/cohere-transcribe-03-2026-Q4_K_M.gguf";
    public const string ModelSha256 = "0ea56826d8bd5d74b7143a4a04e022dc1bb75452cfae49d98b6acb0c1d16a1fb";

    public ModelDownloadDialog(string path)
    {
        Text = "HoldTalk — Instalar modelo de voz";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(520, 220);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        var message = new Label
        {
            Text = "Para ditar, baixe o modelo Cohere (1,56 GB).\nO download requer internet. Depois, o ditado funciona offline.\nDeseja instalar o modelo agora?",
            Location = new(20, 20), Size = new(480, 90)
        };
        var progress = new ProgressBar { Location = new(20, 120), Size = new(480, 22), AccessibleName = "Progresso do download" };
        var install = new Button { Text = "&Baixar modelo", Location = new(210, 160), Size = new(140, 40), TabIndex = 0 };
        var cancel = new Button { Text = "&Agora não", Location = new(360, 160), Size = new(140, 40), DialogResult = DialogResult.Cancel, TabIndex = 1 };
        Controls.AddRange([message, progress, install, cancel]);
        AcceptButton = install;
        CancelButton = cancel;
        bool downloading = false;
        install.Click += async (_, _) =>
        {
            using var cancellation = new CancellationTokenSource();
            FormClosingEventHandler closing = (_, args) =>
            {
                if (!downloading) return;
                args.Cancel = true;
                cancellation.Cancel();
            };
            FormClosing += closing;
            downloading = true;
            install.Enabled = false;
            cancel.Text = "&Cancelar";
            message.Text = "Baixando modelo Cohere… 0%";
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromHours(2) };
                await DownloadAsync(client, path, new Progress<int>(value =>
                {
                    progress.Value = value;
                    message.Text = value < 100 ? $"Baixando modelo Cohere… {value}%" : "Verificando integridade do modelo…";
                }), cancellation.Token);
                downloading = false;
                DialogResult = DialogResult.OK;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                downloading = false;
                DialogResult = DialogResult.Cancel;
            }
            catch (Exception exception)
            {
                message.Text = $"Não foi possível instalar o modelo: {exception.Message}\nVerifique sua conexão e tente novamente.";
                progress.Value = 0;
            }
            finally
            {
                downloading = false;
                FormClosing -= closing;
                install.Enabled = true;
                cancel.Text = "&Agora não";
            }
        };
    }

    public static async Task DownloadAsync(HttpClient client, string path, IProgress<int> progress, CancellationToken cancellation)
    {
        string partial = path + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            using var response = await client.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int previous = -1, count;
                while ((count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                    total += count;
                    int percent = response.Content.Headers.ContentLength is > 0 and var length
                        ? (int)Math.Min(99, total * 100 / length) : 0;
                    if (percent != previous) { progress.Report(percent); previous = percent; }
                }
            }
            progress.Report(100);
            await using (var file = File.OpenRead(partial))
            {
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation).ConfigureAwait(false));
                if (!hash.Equals(ModelSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("O arquivo baixado falhou na verificação SHA256.");
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(partial, path);
        }
        finally { File.Delete(partial); }
    }
}
