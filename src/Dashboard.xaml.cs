using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Media = System.Windows.Media;

namespace Holdtalk;

public partial class Dashboard : Window
{
    private readonly Settings settings;
    private List<Transcript> transcripts = [];
    private bool closeForExit;
    public event Action? PauseRequested, ArchiveRequested, ConfigRequested, PermissionsRequested, QuitRequested;
    public event Action<string>? CopyRequested;
    public event Action<string, string>? PreferencesRequested;
    public static readonly DependencyProperty CanCopyProperty = DependencyProperty.Register(
        nameof(CanCopy), typeof(bool), typeof(Dashboard), new PropertyMetadata(true));
    public bool CanCopy { get => (bool)GetValue(CanCopyProperty); set => SetValue(CanCopyProperty, value); }

    public Dashboard(Settings settings)
    {
        this.settings = settings;
        InitializeComponent();
        Language = XmlLanguage.GetLanguage("pt-BR");
        Width = Math.Min(1040, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(760, SystemParameters.WorkArea.Height - 32);
        MinWidth = Math.Min(820, Width);
        MinHeight = Math.Min(580, Height);
        LanguageChoice.ItemsSource = Settings.SupportedLanguages
            .Select(code => new KeyValuePair<string, string>(code, FormatLanguage(code))).ToList();
        LanguageChoice.SelectedValue = settings.Language;
        DictationKey.Text = FormatKey(settings.DictationKey);
        CleanupKey.Text = FormatKey(settings.CleanupKey);
        ModelSummary.Text = FormatModel(settings.ModelPath);
        ShortcutDetails.Text = $"Ditado: segure {FormatKey(settings.DictationKey)}.\nLimpeza opcional: segure {FormatKey(settings.CleanupKey)}.";
        RuntimeDetails.Text = $"Modelo: {FormatModel(settings.ModelPath)}\nProcessamento: Vulkan / CPU";
        LanguageSummary.Text = FormatLanguage(settings.Language);
        Closing += OnClosing;
        UpdateHistory([]);
    }

    public static string FormatKey(string key) => key switch
    {
        "RControlKey" => "Ctrl direito", "LControlKey" => "Ctrl esquerdo",
        "RShiftKey" => "Shift direito", "LShiftKey" => "Shift esquerdo", _ => key
    };

    public static string FormatModel(string path) => Path.GetFileNameWithoutExtension(path)
        .Replace("-Q8_0", " · Q8", StringComparison.OrdinalIgnoreCase)
        .Replace("-Q4_K_M", " · Q4", StringComparison.OrdinalIgnoreCase);
    private static string FormatLanguage(string language) => language switch
    {
        "pt" => "Português", "en" => "Inglês", "fr" => "Francês", "de" => "Alemão",
        "es" => "Espanhol", "it" => "Italiano", "nl" => "Holandês", "pl" => "Polonês",
        "el" => "Grego", "ar" => "Árabe", "ja" => "Japonês", "zh" => "Chinês",
        "vi" => "Vietnamita", "ko" => "Coreano", _ => language
    };

    public void SetMicrophones(IEnumerable<string> microphones, string selected)
    {
        MicrophoneChoice.ItemsSource = new[] { "Padrão do Windows" }.Concat(microphones).ToList();
        MicrophoneChoice.SelectedItem = string.IsNullOrWhiteSpace(settings.Microphone) ? "Padrão do Windows" : settings.Microphone;
        HeaderMicrophone.Text = selected;
        HeaderMicrophone.ToolTip = selected;
    }

    public void UpdateState(string state, bool paused, bool ready, string microphone)
    {
        bool processing = state is "Transcribing" or "Cleaning" or "Pasting";
        var presentation = state switch
        {
            "Recording" => ("Gravando", "Estou ouvindo você.", "Solte o atalho quando terminar de falar.", "#FFB4B4"),
            "Transcribing" => ("Transcrevendo", "Transformando voz em texto.", "Mantenha o campo de destino em foco.", "#A7B8FF"),
            "Cleaning" => ("Limpando texto", "Só os últimos ajustes.", "Revisando pontuação e palavras de preenchimento.", "#A7B8FF"),
            "Pasting" => ("Colando", "Seu texto está chegando.", "Restaurando a área de transferência em seguida.", "#ACFFD5"),
            "Not ready" => ("Precisa de atenção", "Vamos preparar seu ditado.", "Confira a mensagem e as configurações do microfone.", "#FFB4B4"),
            "Starting" => ("Preparando", "Conectando seu microfone.", "Verificando os recursos de ditado.", "#A7B8FF"),
            _ when paused => ("Pausado", "Uma pausa para sua voz.", "Ative os atalhos quando quiser voltar a ditar.", "#BFC5CB"),
            _ => ("Pronto para ditar", "Pronto quando você estiver.", "Segure o atalho, fale e solte para transcrever.", "#ACFFD5")
        };
        StatusLabel.Text = presentation.Item1;
        StatusHeadline.Text = presentation.Item2;
        StatusHint.Text = presentation.Item3;
        var stateColor = (Media.Color)Media.ColorConverter.ConvertFromString(presentation.Item4);
        var stateBrush = new Media.SolidColorBrush(stateColor);
        StatusDot.Fill = StateIcon.Foreground = MicrophoneOrb.BorderBrush = stateBrush;
        StatusPill.Background = new Media.SolidColorBrush(Media.Color.FromArgb(24, stateColor.R, stateColor.G, stateColor.B));
        StateIcon.Content = FindResource(processing ? "MxTranscribing" : "MxMicrophone");
        WorkProgress.Visibility = processing ? Visibility.Visible : Visibility.Collapsed;
        FooterStatus.Text = paused ? "Atalhos pausados. Reative para voltar a ditar." : "Fechar a janela mantém o app na bandeja.";
        PauseButton.Content = paused ? "Ativar atalhos" : "Pausar atalhos";
        PauseButton.IsEnabled = ready;
        CanCopy = state != "Pasting";
        SaveSettingsButton.IsEnabled = (state is "Idle" or "Not ready") && MicrophoneChoice.Items.Count > 1;
        MicrophoneChoice.IsEnabled = LanguageChoice.IsEnabled = SaveSettingsButton.IsEnabled;
        if (!string.IsNullOrEmpty(microphone)) HeaderMicrophone.Text = microphone;
        if (state == "Recording") Notice.Visibility = Visibility.Collapsed;
    }

    public void UpdateHistory(IEnumerable<Transcript> entries)
    {
        transcripts = entries.ToList();
        LatestList.ItemsSource = transcripts.Take(1).ToList();
        HomeEmpty.Visibility = transcripts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshHistory();
    }

    private void RefreshHistory()
    {
        if (HistoryList is null) return;
        string search = HistorySearch.Text.Trim();
        var filtered = transcripts.Where(entry => entry.Text.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        HistoryList.ItemsSource = filtered;
        string total = transcripts.Count == 1 ? "1 transcrição" : $"{transcripts.Count} transcrições";
        HistoryCount.Text = search.Length == 0 ? $"{total} no histórico" : $"{filtered.Count} de {total}";
        HistoryEmpty.Text = transcripts.Count == 0 ? "Seu histórico começa no próximo ditado." : "Nenhuma transcrição encontrada para essa busca.";
        HistoryEmpty.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowNotice(string text, bool error)
    {
        NoticeText.Text = text;
        Notice.Background = new Media.SolidColorBrush((Media.Color)Media.ColorConverter.ConvertFromString(error ? "#49343A" : "#263E34"));
        Notice.BorderBrush = new Media.SolidColorBrush((Media.Color)Media.ColorConverter.ConvertFromString(error ? "#FFB4B4" : "#ACFFD5"));
        NoticeText.Foreground = new Media.SolidColorBrush((Media.Color)Media.ColorConverter.ConvertFromString(error ? "#FFE2E2" : "#D7FFE9"));
        Notice.Visibility = Visibility.Visible;
    }

    public void PreferencesSaved(string microphone, string language)
    {
        HeaderMicrophone.Text = microphone;
        LanguageSummary.Text = FormatLanguage(language);
        ShowNotice("Configurações salvas. O próximo ditado já usa esses ajustes.", false);
    }

    public void ShowPage(string page)
    {
        if (page == "history") HistoryNav.IsChecked = true;
        else if (page == "settings") SettingsNav.IsChecked = true;
        else HomeNav.IsChecked = true;
    }

    private void Navigate(object sender, RoutedEventArgs args)
    {
        if (HomePage is null) return;
        string page = (string)((System.Windows.Controls.RadioButton)sender).Tag;
        HomePage.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyTranscript(object sender, RoutedEventArgs args)
    {
        if (CanCopy && ((System.Windows.Controls.Button)sender).Tag is Transcript entry) CopyRequested?.Invoke(entry.Text);
    }
    private void SearchHistory(object sender, TextChangedEventArgs args) => RefreshHistory();
    private void ClearHistorySearch(object sender, RoutedEventArgs args) { HistorySearch.Clear(); HistorySearch.Focus(); }
    private void ConfigureMicrophone(object sender, RoutedEventArgs args) { ShowPage("settings"); MicrophoneChoice.Focus(); }
    private void DismissNotice(object sender, RoutedEventArgs args) => Notice.Visibility = Visibility.Collapsed;
    private void AdaptDensity(object sender, SizeChangedEventArgs args)
    {
        if (HomeIntroduction is not null) HomeIntroduction.Visibility = args.NewSize.Height < 680 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ShowHistory(object sender, RoutedEventArgs args) => ShowPage("history");
    private void PauseHotkeys(object sender, RoutedEventArgs args) => PauseRequested?.Invoke();
    private void OpenArchive(object sender, RoutedEventArgs args) => ArchiveRequested?.Invoke();
    private void OpenConfig(object sender, RoutedEventArgs args) => ConfigRequested?.Invoke();
    private void OpenPermissions(object sender, RoutedEventArgs args) => PermissionsRequested?.Invoke();
    private void QuitApp(object sender, RoutedEventArgs args) => QuitRequested?.Invoke();
    private void SaveSettings(object sender, RoutedEventArgs args)
    {
        if (MicrophoneChoice.SelectedItem is not string microphone || LanguageChoice.SelectedValue is not string language) return;
        PreferencesRequested?.Invoke(microphone == "Padrão do Windows" ? "" : microphone, language);
    }
    private void MinimizeWindow(object sender, RoutedEventArgs args) => WindowState = WindowState.Minimized;
    private void MaximizeWindow(object sender, RoutedEventArgs args) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void HideWindow(object sender, RoutedEventArgs args) => Hide();
    private void OnClosing(object? sender, CancelEventArgs args) { if (!closeForExit) { args.Cancel = true; Hide(); } }
    public void CloseForExit() { closeForExit = true; Close(); }
}
