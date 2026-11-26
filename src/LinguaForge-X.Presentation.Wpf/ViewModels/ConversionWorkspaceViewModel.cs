using System.Collections.ObjectModel;
using System.IO;
using LinguaForge_X.Application.Abstractions;
using LinguaForge_X.Domain.Models;
using Microsoft.Win32;

namespace LinguaForge_X.Presentation.Wpf.ViewModels;

/// <summary>
/// Manages video import, language/voice selection, two-phase subtitle &amp; dubbing preparation/export,
/// segment review, and task history restoration.
/// </summary>
public sealed class ConversionWorkspaceViewModel : ObservableObject
{
    private readonly WorkspaceCoordinator _workspace;
    private readonly IMediaService _media;
    private readonly IConversionService _conversion;
    private readonly IProjectStore _projects;
    private readonly ISynthesisService _synthesis;
    private readonly Func<bool> _isTrainingWebUiRunning;
    private readonly Action<string> _setOnlineSource;
    private readonly Action<int> _selectTab;
    private bool _changingChoices;
    private IReadOnlyList<LanguageOption> _translationLanguages = [];
    private IReadOnlyList<VoiceOption> _allVoices = [];
    private ConversionProject? _selectedHistory;
    private string? _previewPath;

    public ConversionWorkspaceViewModel(WorkspaceCoordinator workspace, IMediaService media,
        IConversionService conversion, IProjectStore projects, ISynthesisService synthesis,
        Func<bool> isTrainingWebUiRunning, Action<string> setOnlineSource, Action<int> selectTab)
    {
        _workspace = workspace;
        _media = media;
        _conversion = conversion;
        _projects = projects;
        _synthesis = synthesis;
        _isTrainingWebUiRunning = isTrainingWebUiRunning;
        _setOnlineSource = setOnlineSource;
        _selectTab = selectTab;

        ImportCommand = new(_ => ImportAsync(), _ => _workspace.CanConfigure);
        PrepareCommand = new(_ => PrepareAsync(), _ => _workspace.CanConfigure && _workspace.Video is not null);
        ExportCommand = new(_ => ExportAsync(),
            _ => !_workspace.IsBusy && !_isTrainingWebUiRunning() && _workspace.Project?.PreparationComplete == true && Segments.Count > 0);
        PreviewVoiceCommand = new(_ => PreviewVoiceAsync(),
            _ => !_workspace.IsBusy && !_isTrainingWebUiRunning() && !string.IsNullOrWhiteSpace(_workspace.Options.VoiceId));
        SaveEditsCommand = new(_ => _workspace.RunAsync("保存校对结果", async ct =>
        {
            if (_workspace.Project is null) return;
            _workspace.Project.Segments = Segments.ToList();
            await _projects.SaveAsync(_workspace.Project, ct);
            _workspace.Status = "校对结果已保存，可在任务历史中继续。";
            await RefreshHistoryCoreAsync(ct);
        }), _ => !_workspace.IsBusy && _workspace.HasProject);
        RefreshHistoryCommand = new(_ => _workspace.RunAsync("加载任务历史", RefreshHistoryCoreAsync), _ => !_workspace.IsBusy);
        RestoreCommand = new(_ => RestoreAsync(), _ => !_workspace.IsBusy && SelectedHistory is not null);
        ResetCommand = new(_ => ResetProject(), _ => !_workspace.IsBusy && _workspace.HasProject);
        ChooseOutputCommand = new(_ => _workspace.ExecuteUiAction(ChooseOutput), _ => !_workspace.IsBusy);
        PlayResultCommand = new(_ => _workspace.OpenExternal(_workspace.Project?.ResultPath),
            _ => !_workspace.IsBusy && _workspace.Project?.ResultPath is not null);
        OpenOutputFolderCommand = new(_ => _workspace.ExecuteUiAction(() => _workspace.OpenExternal(Path.GetDirectoryName(_workspace.Options.OutputPath))),
            _ => !_workspace.IsBusy && !string.IsNullOrWhiteSpace(_workspace.Options.OutputPath));
    }

    public ConversionProject? SelectedHistory
    {
        get => _selectedHistory;
        set { Set(ref _selectedHistory, value); _workspace.RaiseStateChanged(); }
    }

    public string? PreviewPath { get => _previewPath; private set => Set(ref _previewPath, value); }
    public string SourceSummary => string.IsNullOrWhiteSpace(_workspace.Options.SourceUrl) ? "" : $"在线来源：{_workspace.Options.SourceTitle}\n{_workspace.Options.SourceUrl}";
    public string VideoSummary => _workspace.Video is null ? "尚未导入视频" : $"{Path.GetFileName(_workspace.Video.FilePath)}  ·  {TimeSpan.FromSeconds(_workspace.Video.DurationSeconds):hh\\:mm\\:ss}  ·  {_workspace.Video.Width} × {_workspace.Video.Height}  ·  {_workspace.Video.AudioTracks.Count} 条音轨";
    public string ProjectSummary => _workspace.Project is null ? "先选择输出选项，再生成可校对的字幕与配音稿。" :
        $"当前任务：{_workspace.Project.Options.SourceLanguage} → 字幕 {_workspace.Project.Options.SubtitleLanguage} / 配音 {_workspace.Project.Options.SpeechLanguage}。音色、输出画质与导出编码可直接修改；更改语言或生成模式，请先点击「重新设置」。";

    public string SpeechLanguage
    {
        get => _workspace.Options.SpeechLanguage;
        set
        {
            if (_changingChoices || _workspace.HasProject || _workspace.Options.SpeechLanguage == value || string.IsNullOrWhiteSpace(value)) return;
            _workspace.Options.SpeechLanguage = value;
            OnPropertyChanged();
            FilterVoices();
        }
    }

    public string SourceLanguage
    {
        get => _workspace.Options.SourceLanguage;
        set
        {
            if (_changingChoices || _workspace.HasProject || _workspace.Options.SourceLanguage == value || string.IsNullOrWhiteSpace(value)) return;
            _workspace.Options.SourceLanguage = value;
            OnPropertyChanged();
            RebuildTargetLanguages();
        }
    }

    public string SubtitleLanguage
    {
        get => _workspace.Options.SubtitleLanguage;
        set
        {
            if (_changingChoices || _workspace.HasProject || string.IsNullOrWhiteSpace(value)) return;
            _workspace.Options.SubtitleLanguage = value;
            OnPropertyChanged();
        }
    }

    public string VoiceId
    {
        get => _workspace.Options.VoiceId;
        set
        {
            if (_changingChoices || _workspace.IsBusy || string.IsNullOrWhiteSpace(value)) return;
            _workspace.Options.VoiceId = value;
            OnPropertyChanged();
            _workspace.RaiseStateChanged();
        }
    }

    public int AudioStreamIndex
    {
        get => _workspace.Options.AudioStreamIndex;
        set
        {
            if (_changingChoices || _workspace.HasProject) return;
            _workspace.Options.AudioStreamIndex = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<AudioTrack> AudioTracks { get; } = [];
    public ObservableCollection<LanguageOption> SourceLanguages { get; } = [];
    public ObservableCollection<LanguageOption> SubtitleLanguages { get; } = [];
    public ObservableCollection<LanguageOption> SpeechLanguages { get; } = [];
    public ObservableCollection<VoiceOption> Voices { get; } = [];
    public ObservableCollection<TranscriptSegment> Segments { get; } = [];
    public ObservableCollection<ConversionProject> History { get; } = [];

    public IReadOnlyList<KeyValuePair<OriginalAudioMode, string>> AudioModes { get; } =
        [new(OriginalAudioMode.Replace, "完全替换原音轨（原音乐也会移除）"), new(OriginalAudioMode.QuietMix, "降低原音后混合（仍会有英文人声）")];
    public IReadOnlyList<KeyValuePair<VideoEncodingMode, string>> VideoEncodingModes { get; } =
        [new(VideoEncodingMode.Auto, "自动加速（推荐）"), new(VideoEncodingMode.CpuFast, "CPU 快速"), new(VideoEncodingMode.CpuQuality, "CPU 画质与体积优先")];
    public IReadOnlyList<KeyValuePair<OutputVideoQuality, string>> OutputQualities { get; } =
        [new(OutputVideoQuality.Original, "保持源分辨率（推荐）"), new(OutputVideoQuality.UltraHd2160, "4K（最高2160p）"),
         new(OutputVideoQuality.FullHd1080, "全高清（最高1080p）"), new(OutputVideoQuality.Hd720, "高清（最高720p）"), new(OutputVideoQuality.Compact480, "标清（最高480p）")];

    public AsyncCommand ImportCommand { get; }
    public AsyncCommand PrepareCommand { get; }
    public AsyncCommand ExportCommand { get; }
    public AsyncCommand PreviewVoiceCommand { get; }
    public AsyncCommand SaveEditsCommand { get; }
    public AsyncCommand RefreshHistoryCommand { get; }
    public AsyncCommand RestoreCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand ChooseOutputCommand { get; }
    public RelayCommand PlayResultCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }

    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog { Title = "选择本地视频", Filter = "视频文件|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.wmv;*.ts|所有文件|*.*" };
        if (dialog.ShowDialog() != true) return;
        await _workspace.RunAsync("读取视频", async ct =>
        {
            await _workspace.CloseVerificationIfOpenAsync();
            var media = WorkspaceCoordinator.Clone(_workspace.Settings.Media);
            await _media.CheckAvailableAsync(media, ct);
            var video = await _media.ProbeAsync(dialog.FileName, media, ct);
            ct.ThrowIfCancellationRequested();
            ApplyImportedVideo(video, media, null);
            _workspace.Status = "视频已读取。选择语言和输出模式，然后生成字幕与配音稿。";
        });
    }

    public void ApplyImportedVideo(VideoInfo video, MediaSettings media, OnlineVideoInfo? source)
    {
        if (video.AudioTracks.Count == 0) throw new InvalidOperationException("这个视频没有音轨，无法转写或配音。");
        var directory = media.OutputDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            var videosDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            directory = source is not null && !string.IsNullOrWhiteSpace(videosDirectory)
                ? Path.Combine(videosDirectory, "LinguaForge-X") : Path.GetDirectoryName(video.FilePath)!;
        }
        var title = source?.Title ?? Path.GetFileNameWithoutExtension(video.FilePath);
        var filename = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(filename)) filename = "在线视频";
        if (filename.Length > 100) filename = filename[..100];
        var outputPath = Path.Combine(directory, filename + "_中文.mp4");
        _workspace.Video = video;
        _workspace.Options.InputPath = video.FilePath;
        _workspace.Options.AudioStreamIndex = video.AudioTracks[0].StreamIndex;
        _workspace.Options.OutputPath = outputPath;
        _workspace.Options.SourceUrl = source?.WebpageUrl ?? "";
        _workspace.Options.SourceTitle = source?.Title ?? "";
        if (source is null) _setOnlineSource("");
        ReplaceItems(AudioTracks, video.AudioTracks);
        PreviewPath = video.FilePath;
        OnPropertyChanged(nameof(SourceSummary));
        NotifySelections();
        OnPropertyChanged(nameof(VideoSummary));
        _workspace.RaiseStateChanged();
    }

    public void ApplyCapabilities(ServiceCapabilities capabilities)
    {
        _allVoices = capabilities.Voices;
        _translationLanguages = capabilities.TranslationLanguages;
        _changingChoices = true;
        var sources = capabilities.SourceLanguages.ToList();
        if (_workspace.HasProject && !sources.Any(x => x.Code == _workspace.Options.SourceLanguage))
            sources.Add(new(_workspace.Options.SourceLanguage, _workspace.Options.SourceLanguage + "（任务原选项）"));
        ReplaceItems(SourceLanguages, sources);
        if (!_workspace.HasProject) EnsureSelection(SourceLanguages, _workspace.Options.SourceLanguage, x => _workspace.Options.SourceLanguage = x);
        _changingChoices = false;
        RebuildTargetLanguages();
        NotifySelections();
    }

    public void SetVoicesCatalog(IReadOnlyList<VoiceOption> voices)
    {
        _allVoices = voices;
        RebuildTargetLanguages();
    }

    public void RebuildTargetLanguages()
    {
        _changingChoices = true;
        var subtitles = new[] { new LanguageOption("source", "保持原语言") }.Concat(_translationLanguages).DistinctBy(x => x.Code).ToList();
        if (_workspace.HasProject && !subtitles.Any(x => x.Code == _workspace.Options.SubtitleLanguage))
            subtitles.Add(new(_workspace.Options.SubtitleLanguage, _workspace.Options.SubtitleLanguage + "（任务原选项）"));
        ReplaceItems(SubtitleLanguages, subtitles);
        var locales = _allVoices.SelectMany(x => x.SupportedLocales.Append(x.Locale)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var speechLanguages = _translationLanguages.Concat(SourceLanguages.Where(x => x.Code == _workspace.Options.SourceLanguage))
            .Where(x => x.Code != "auto" && locales.Any(l => LocaleMatches(x.Code, l))).DistinctBy(x => x.Code).ToList();
        if (_workspace.HasProject && !speechLanguages.Any(x => x.Code == _workspace.Options.SpeechLanguage))
            speechLanguages.Add(new(_workspace.Options.SpeechLanguage, _workspace.Options.SpeechLanguage + "（任务原选项）"));
        ReplaceItems(SpeechLanguages, speechLanguages);
        if (!_workspace.HasProject)
        {
            EnsureSelection(SubtitleLanguages, _workspace.Options.SubtitleLanguage, x => _workspace.Options.SubtitleLanguage = x);
            EnsureSelection(SpeechLanguages, _workspace.Options.SpeechLanguage, x => _workspace.Options.SpeechLanguage = x);
        }
        _changingChoices = false;
        FilterVoices();
        NotifySelections();
    }

    private void FilterVoices()
    {
        var voiceId = _workspace.Options.VoiceId;
        _changingChoices = true;
        var voices = _allVoices.Where(v => v.SupportedLocales.Append(v.Locale).Any(l => LocaleMatches(l, _workspace.Options.SpeechLanguage))).ToList();
        if (_workspace.HasProject && !string.IsNullOrWhiteSpace(voiceId) && !voices.Any(v => v.Id == voiceId))
            voices.Add(new(voiceId, voiceId + "（任务原音色，当前目录未验证）", _workspace.Options.SpeechLanguage, [_workspace.Options.SpeechLanguage]));
        ReplaceItems(Voices, voices);
        if (!_workspace.HasProject) _workspace.Options.VoiceId = Voices.FirstOrDefault(v => v.Id == voiceId)?.Id ?? Voices.FirstOrDefault()?.Id ?? "";
        _changingChoices = false;
        OnPropertyChanged(nameof(VoiceId));
        _workspace.RaiseStateChanged();
    }

    private Task PrepareAsync() => _workspace.RunAsync("生成字幕与配音稿", async ct =>
    {
        await _workspace.CloseVerificationIfOpenAsync();
        if (!_workspace.Options.GenerateSubtitles && !_workspace.Options.GenerateSpeech)
            throw new InvalidOperationException("请至少选择添加字幕或生成配音。");
        if (_workspace.Options.GenerateSpeech && string.IsNullOrWhiteSpace(_workspace.Options.VoiceId))
            throw new InvalidOperationException("当前语言没有可用音色。请配置配音服务并刷新能力目录。");
        var project = await _conversion.PrepareAsync(WorkspaceCoordinator.Clone(_workspace.Options), WorkspaceCoordinator.Clone(_workspace.Settings), _workspace.CreateProgress(), ct);
        SetProject(project);
        await RefreshHistoryCoreAsync(ct);
        _workspace.Status = $"已生成 {Segments.Count} 段。请校对时间与文本，再导出视频。";
    });

    private Task ExportAsync() => _workspace.RunAsync("合成并导出视频", async ct =>
    {
        if (_workspace.Project is null) return;
        _workspace.Project.Segments = Segments.ToList();
        if (_workspace.Project.Segments.Any(s => !double.IsFinite(s.StartSeconds) || !double.IsFinite(s.EndSeconds) || s.StartSeconds < 0 || s.EndSeconds <= s.StartSeconds || s.EndSeconds > _workspace.Project.Video.DurationSeconds + 0.5))
            throw new InvalidOperationException("请检查字幕时间：开始时间须 ≥ 0，结束时间须晚于开始时间且不能超出视频时长。");
        if (string.IsNullOrWhiteSpace(_workspace.Project.Options.OutputPath))
            throw new InvalidOperationException("请选择导出视频的保存位置。");
        if (File.Exists(_workspace.Project.Options.OutputPath))
            throw new InvalidOperationException("目标文件已存在，请选择新的输出文件名，以保留现有视频。");
        var exporting = _workspace.Project;
        await _projects.SaveAsync(exporting, ct);
        ConversionResult result;
        try { result = await _conversion.ExportAsync(exporting, WorkspaceCoordinator.Clone(_workspace.Settings), _workspace.CreateProgress(), ct); }
        finally
        {
            try
            {
                await RefreshHistoryCoreAsync(CancellationToken.None);
                if (History.FirstOrDefault(item => item.Id == exporting.Id) is { } persisted) SetProject(persisted);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        _workspace.Project!.ResultPath = result.VideoPath;
        _workspace.Project.Status = "已完成";
        PreviewPath = result.VideoPath;
        _workspace.Status = "导出完成：" + result.VideoPath + (result.SubtitlePath is null ? "" : "；字幕：" + result.SubtitlePath);
    });

    private Task RestoreAsync() => _workspace.RunAsync("恢复任务", async ct =>
    {
        if (SelectedHistory is null) return;
        await _workspace.CloseVerificationIfOpenAsync();
        var project = WorkspaceCoordinator.Clone(SelectedHistory);
        if (!File.Exists(project.Options.InputPath))
            throw new FileNotFoundException("原视频已移动或删除，无法恢复：" + project.Options.InputPath);
        if (project.PreparationComplete) SetProject(project);
        else
        {
            _workspace.Project = null;
            _workspace.Video = project.Video;
            _workspace.Options = WorkspaceCoordinator.Clone(project.Options);
            _setOnlineSource(project.Options.SourceUrl);
            ReplaceItems(AudioTracks, project.Video.AudioTracks);
            Segments.Clear();
            PreviewPath = project.Options.InputPath;
            RebuildTargetLanguages();
            OnPropertyChanged(nameof(VideoSummary));
            NotifySelections();
        }
        _selectTab(0);
        _workspace.Status = project.PreparationComplete
            ? "已恢复任务和生成时的输出选项。可继续校对并重新导出。"
            : "此任务尚未完成转写与翻译。请点击「生成字幕与配音稿」继续，已完成的片段将尝试复用缓存。";
    });

    public void SetProject(ConversionProject project)
    {
        _workspace.Video = project.Video;
        _workspace.Project = project;
        _workspace.Options = project.Options;
        _setOnlineSource(project.Options.SourceUrl);
        if (!SourceLanguages.Any(x => x.Code == project.Options.SourceLanguage))
            SourceLanguages.Add(new(project.Options.SourceLanguage, project.Options.SourceLanguage + "（任务原选项）"));
        ReplaceItems(AudioTracks, project.Video.AudioTracks);
        RebuildTargetLanguages();
        NotifySelections();
        ReplaceItems(Segments, project.Segments);
        PreviewPath = File.Exists(project.ResultPath) ? project.ResultPath : project.Options.InputPath;
        OnPropertyChanged(nameof(VideoSummary));
        OnPropertyChanged(nameof(ProjectSummary));
        _workspace.RaiseStateChanged();
    }

    private void ResetProject()
    {
        if (_workspace.Project is not null) _workspace.Options = WorkspaceCoordinator.Clone(_workspace.Project.Options);
        _workspace.Project = null;
        Segments.Clear();
        RebuildTargetLanguages();
        OnPropertyChanged(nameof(ProjectSummary));
        _workspace.Status = "已回到设置步骤。现有任务仍保留在历史中；新的生成将创建新任务。";
    }

    private void ChooseOutput()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出视频到本地",
            Filter = "MP4 视频|*.mp4",
            DefaultExt = ".mp4",
            AddExtension = true,
            FileName = string.IsNullOrWhiteSpace(_workspace.Options.OutputPath) ? "转换后视频.mp4" : Path.GetFileName(_workspace.Options.OutputPath),
            OverwritePrompt = false
        };
        var directory = Path.GetDirectoryName(_workspace.Options.OutputPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)) dialog.InitialDirectory = directory;
        if (dialog.ShowDialog() == true)
        {
            _workspace.Options.OutputPath = dialog.FileName;
            _workspace.RaiseStateChanged();
        }
    }

    private Task PreviewVoiceAsync() => _workspace.RunAsync("生成音色试听", async ct =>
    {
        var language = _workspace.Options.SpeechLanguage;
        var sample = language.Split('-')[0].ToLowerInvariant() switch
        {
            "zh" => "你好，这是视频配音试听。欢迎使用 LinguaForge。",
            "ja" => "こんにちは。これは動画の音声サンプルです。",
            "ko" => "안녕하세요. 동영상 음성 미리 듣기입니다.",
            "fr" => "Bonjour, voici un aperçu de la voix de votre vidéo.",
            "de" => "Hallo, dies ist eine Vorschau der Videostimme.",
            "es" => "Hola, esta es una muestra de la voz del vídeo.",
            _ => "Hello. This is a preview of your video narration."
        };
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinguaForge-X", "preview");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".wav");
        await _synthesis.SynthesizeAsync(sample, language, _workspace.Options.VoiceId, path, WorkspaceCoordinator.Clone(_workspace.Settings.Synthesis), ct);
        _workspace.RaiseVoicePreviewReady(path);
        _workspace.Status = "音色试听已生成并开始播放。部分语言使用英语示例，实际配音以目标语言文本为准。";
    });

    public async Task RefreshHistoryCoreAsync(CancellationToken ct) =>
        ReplaceItems(History, (await _projects.ListAsync(ct)).OrderByDescending(x => x.CreatedAt));

    public void NotifySelections()
    {
        OnPropertyChanged(nameof(SourceLanguage));
        OnPropertyChanged(nameof(SubtitleLanguage));
        OnPropertyChanged(nameof(SpeechLanguage));
        OnPropertyChanged(nameof(VoiceId));
        OnPropertyChanged(nameof(AudioStreamIndex));
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(VideoSummary));
        OnPropertyChanged(nameof(ProjectSummary));
    }

    public void RefreshCommands()
    {
        foreach (var command in new[] { ImportCommand, PrepareCommand, ExportCommand, PreviewVoiceCommand, SaveEditsCommand, RefreshHistoryCommand, RestoreCommand })
            command?.Refresh();
        foreach (var command in new[] { ResetCommand, ChooseOutputCommand, PlayResultCommand, OpenOutputFolderCommand })
            command?.Refresh();
    }

    private static bool LocaleMatches(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase) ||
        first is "*" || second is "*" ||
        (!first.Contains('-') || !second.Contains('-')) && string.Equals(first.Split('-')[0], second.Split('-')[0], StringComparison.OrdinalIgnoreCase);

    private static void EnsureSelection(IEnumerable<LanguageOption> options, string selected, Action<string> assign)
    {
        if (!options.Any(x => x.Code == selected) && options.FirstOrDefault() is { } first) assign(first.Code);
    }

    private static void ReplaceItems<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        var snapshot = items.ToList();
        collection.Clear();
        foreach (var item in snapshot) collection.Add(item);
    }
}
