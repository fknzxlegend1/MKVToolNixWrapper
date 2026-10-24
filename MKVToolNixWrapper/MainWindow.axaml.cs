using MKVToolNixWrapper.Dtos;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Platform.Storage;

namespace MKVToolNixWrapper;

public partial class MainWindow : Window
{
    private static string _mkvMergePath = OperatingSystem.IsWindows()
        ? @"C:\Program Files\MKVToolNix\mkvmerge.exe"
        : "mkvmerge";
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MKVToolNixWrapper",
        "settings.json");
    private static readonly JsonSerializerOptions MkvJsonSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };
    private readonly Lock _processIdTrackerLock = new();
    private int _fileListRefreshQueued;
    private WindowsTaskbarProgress? _taskbarProgress;

    public List<FileMeta> FileList { get; private set; } = [];
    private List<int> ProcessIdTracker { get; } = [];

    public List<TrackListMeta> TrackList { get; private set; } = [];

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => _taskbarProgress?.Dispose();
        Title = $"MKVToolNixWrapper v{Assembly.GetExecutingAssembly().GetName().Version}";
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        WriteOutputLine($"Welcome to MKVToolNixWrapper v{Assembly.GetExecutingAssembly().GetName().Version}");

        DataContext = this;
        TrackGrid.IsEnabled = false;
        AnalyseButton.IsEnabled = false;
        BatchButton.IsEnabled = false;

        try
        {
            _taskbarProgress = WindowsTaskbarProgress.TryCreate(this);
        }
        catch (Exception ex)
        {
            WriteOutputLine($"Windows taskbar progress unavailable: {ex.Message}");
        }

        await MkvMergeExistsCheck();

        StartPulsing(BrowseFolderButton, 2000);
        StartPulsing(SelectedFolderPathLabel, 2000);
        SetTaskbarProgress(WindowsTaskbarProgress.TaskbarProgressState.None);
    }

    private async Task MkvMergeExistsCheck()
    {
        var savedPath = LoadMkvMergePath();
        if (!string.IsNullOrWhiteSpace(savedPath))
        {
            _mkvMergePath = savedPath;
        }
        else if (!OperatingSystem.IsWindows())
        {
            _mkvMergePath = FindMkvMergeOnPath() ?? "mkvmerge";
        }

        if (File.Exists(_mkvMergePath))
        {
            WriteOutputLine($"Succesfully located MKVMerge at: \"{_mkvMergePath}\"");
        }
        else
        {
            await ShowMessageAsync(
                "Unable to locate mkvmerge. Install MKVToolNix, then select its executable.",
                "Failed to locate MKVToolNix");

            WriteOutputLine($"Failed to locate MKVMerge at: \"{_mkvMergePath}\" prompting user for location");

            if (!await SetMkvMergePath())
            {
                ToggleUI(false);
            }
        }
    }

    private async Task<bool> SetMkvMergePath()
    {
        var executableName = OperatingSystem.IsWindows() ? "mkvmerge.exe" : "mkvmerge";
        var selectedFiles = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Locate MKVToolNix mkvmerge",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("MKVToolNix executable")
                {
                    Patterns = [executableName]
                }
            ]
        });
        if (selectedFiles.Count == 0)
        {
            return false;
        }

        var selectedPath = selectedFiles[0].TryGetLocalPath();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (selectedPath != null && Path.GetFileName(selectedPath).Equals(executableName, comparison))
        {
            _mkvMergePath = selectedPath;
            try
            {
                SaveMkvMergePath(_mkvMergePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigurationErrorsException)
            {
                await ShowMessageAsync($"Could not save MKVToolNix path.\r\n{ex.Message}", "Settings error");
                return false;
            }

            WriteOutputLine($"Succesfully located MKVMerge at: \"{_mkvMergePath}\"");
            return true;
        }

        await ShowMessageAsync(
            $"Selected file must be named '{executableName}'.",
            "Invalid MKVToolNix executable");
        return false;
    }

    private static string? FindMkvMergeOnPath()
    {
        var pathEntries = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];
        foreach (var pathEntry in pathEntries)
        {
            var candidate = Path.Combine(pathEntry, "mkvmerge");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private string? LoadMkvMergePath()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var roaming = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal);
                var fileMap = new ExeConfigurationFileMap { ExeConfigFilename = roaming.FilePath };
                var config = ConfigurationManager.OpenMappedExeConfiguration(fileMap, ConfigurationUserLevel.None);
                return config.AppSettings.Settings["MkvMergePath"]?.Value;
            }
            catch (ConfigurationErrorsException ex)
            {
                WriteOutputLine($"Failed to read MKVToolNix settings: {ex.Message}");
                return null;
            }
        }

        if (!File.Exists(SettingsPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath))?.MkvMergePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            WriteOutputLine($"Failed to read MKVToolNix settings: {ex.Message}");
            return null;
        }
    }

    private static void SaveMkvMergePath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var roaming = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal);
            var fileMap = new ExeConfigurationFileMap { ExeConfigFilename = roaming.FilePath };
            var config = ConfigurationManager.OpenMappedExeConfiguration(fileMap, ConfigurationUserLevel.None);
            if (config.AppSettings.Settings["MkvMergePath"] == null)
            {
                config.AppSettings.Settings.Add("MkvMergePath", path);
            }
            else
            {
                config.AppSettings.Settings["MkvMergePath"].Value = path;
            }

            config.Save(ConfigurationSaveMode.Modified);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new UserSettings { MkvMergePath = path }));
    }

    private static void StartPulsing(Control element, int durationMs)
    {
        element.Transitions =
        [
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(500)
            }
        ];
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        var elapsed = 0;
        var fading = true;
        timer.Tick += (_, _) =>
        {
            elapsed += 500;
            element.Opacity = fading ? 0.2 : 1;
            fading = !fading;
            if (elapsed >= durationMs)
            {
                timer.Stop();
                element.Opacity = 1;
            }
        };
        timer.Start();
    }

    private sealed class UserSettings
    {
        public string? MkvMergePath { get; set; }
    }

    private async void BrowseFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            TrackGrid.IsEnabled = false;
            AnalyseButton.IsEnabled = false;
            BatchButton.IsEnabled = false;
            TrackGrid.ItemsSource = null;
            FileListBox.ItemsSource = null;

            var selectedFolders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select folder containing MKV files",
                AllowMultiple = false
            });
            if (selectedFolders.Count > 0 && selectedFolders[0].TryGetLocalPath() is { } selectedPath)
            {
                await BrowseFolderHandler(selectedPath);
            }
            else
            {
                SelectedFolderPathLabel.Text = "";
            }
        }
        catch (Exception ex)
        {
            WriteOutputLine($"Failed to load the selected folder: {ex}");
            await ShowMessageAsync($"Failed to load the selected folder.\r\n{ex.Message}", "Folder error");
            BrowseFolderButton.IsEnabled = true;
            SetBusyCursor(false);
        }
    }

    private async Task BrowseFolderHandler(string path)
    {
        try
        {
            WriteOutputLine($"Source folder selected: \"{path}\"");
            BrowseFolderButton.IsEnabled = false;

            SetBusyCursor(true);

            var mkvFiles = await Task.Run(() => GetMkvFilesInFolder(path));
            if (mkvFiles.Count == 0)
            {
                await ShowMessageAsync(
                    "Unable to locate .mkv files in the selected folder. The 'MasteredFiles' folder is excluded.",
                    "Directory error");

                WriteOutputLine($"Error: Unable to locate .mkv files in the selected folder: \"{path}\"");
                SelectedFolderPathLabel.Text = "";
            }
            else
            {
                SelectedFolderPathLabel.Text = path;

                WriteOutputLine($"Source folder successfully validated - MKV count: {mkvFiles.Count}");
                WriteOutputLine();

                AnalyseButton.IsEnabled = true;

                StartPulsing(AnalyseButton, 2000);

                FileList =
                [
                    .. mkvFiles.Select(x => new FileMeta
                        { FilePath = x, Include = true, Status = FileStatusEnum.Unprocessed })
                ];
                FileListBox.ItemsSource = FileList;
            }
        }
        finally
        {
            SetBusyCursor(false);
            BrowseFolderButton.IsEnabled = true;
        }
    }

    private async Task AnalyseMkvFiles()
    {
        await Task.Run(() =>
        {
            WriteOutputLine("**** ANALYSIS START ****");
            RunOnUiThread(() =>
                SetTaskbarProgress(WindowsTaskbarProgress.TaskbarProgressState.Indeterminate));

            TrackList = [];
            RunOnUiThread(() => ToggleUI(false));

            foreach (var file in FileList.Where(file => file.Status == FileStatusEnum.FailedAnalysis))
            {
                file.Status = FileStatusEnum.Unprocessed;
            }

            ForceSetControlItemsSourceBinding(FileListBox, FileList);

            RunOnUiThread(() => SetBusyCursor(true));
            RunOnUiThread(() => TrackGrid.ItemsSource = null);

            var allPassed = true;
            FileMeta? compareFileItem = null;
            List<MkvTrack> compareMkvSubTracks = [];
            List<MkvTrack> compareMkvAudioTracks = [];
            List<MkvTrack> compareMkvVideoTracks = [];
            foreach (var fileItem in FileList.Where(x => x.Include).ToList())
            {
                var mkvFile = QueryMkvFile(fileItem.FilePath);
                if (mkvFile == null)
                {
                    WriteOutputLine($"FAIL - \"{Path.GetFileName(fileItem.FilePath)}\" is not a valid mkv file");
                    fileItem.Status = FileStatusEnum.FailedAnalysis;
                    ForceSetControlItemsSourceBinding(FileListBox, FileList);
                    continue;
                }

                var tracks = mkvFile.Tracks;
                if (tracks == null)
                {
                    WriteOutputLine(
                        $"FAIL - \"{Path.GetFileName(fileItem.FilePath)}\" does not contain track information");
                    fileItem.Status = FileStatusEnum.FailedAnalysis;
                    allPassed = false;
                    ForceSetControlItemsSourceBinding(FileListBox, FileList);
                    continue;
                }

                var attachments = mkvFile.Attachments ?? [];
                var attachmentCount = attachments.Count;
                var coverArtCount = attachments.Count(x => CoverAttachmentRegex().IsMatch(x.FileName ?? string.Empty));
                if (attachmentCount > 0)
                    WriteOutputLine(
                        $"Attachment(s) Found: {attachmentCount} {(coverArtCount > 0 ? $"- Cover Art Found: {coverArtCount}" : "")}");

                if (compareFileItem == null)
                {
                    WriteOutputLine($"PASS - \"{Path.GetFileName(fileItem.FilePath)}\" marked as comparison point");
                    compareFileItem = fileItem;
                    compareMkvSubTracks =
                    [
                        .. tracks.Where(x => x.Type == "subtitles").OrderBy(y => y.Id)
                            .ThenBy(z => z.Properties?.TrackName)
                    ];
                    compareMkvAudioTracks =
                    [
                        .. tracks.Where(x => x.Type == "audio").OrderBy(y => y.Id)
                            .ThenBy(z => z.Properties?.TrackName)
                    ];
                    compareMkvVideoTracks =
                    [
                        .. tracks.Where(x => x.Type == "video").OrderBy(y => y.Id)
                            .ThenBy(z => z.Properties?.TrackName)
                    ];

                    TrackList =
                    [
                        .. tracks.OrderBy(y => y.Id).ThenBy(z => z.Properties?.TrackName)
                            .Select(x => new TrackListMeta
                            {
                                Id = x.Id,
                                Name = x.Properties?.TrackName ?? string.Empty,
                                Language = x.Properties?.Language ?? string.Empty,
                                Type = x.Type ?? string.Empty,
                                Codec = x.Properties?.CodecId ?? string.Empty,
                                Include = true,
                                Default = x.Properties?.DefaultTrack ?? false,
                                Forced = x.Properties?.ForcedTrack ?? false
                            })
                    ];
                    continue;
                }

                var curMkvVideoTracks = tracks.Where(x => x.Type == "video").OrderBy(y => y.Id)
                    .ThenBy(z => z.Properties?.TrackName).ToList();

                if (!CheckTracks("video", fileItem.FilePath, curMkvVideoTracks, compareFileItem.FilePath,
                        compareMkvVideoTracks))
                {
                    allPassed = false;
                    fileItem.Status = FileStatusEnum.FailedAnalysis;
                    ForceSetControlItemsSourceBinding(FileListBox, FileList);
                    continue;
                }

                var curMkvAudioTracks = tracks.Where(x => x.Type == "audio").OrderBy(y => y.Id)
                    .ThenBy(z => z.Properties?.TrackName).ToList();
                if (!CheckTracks("audio", fileItem.FilePath, curMkvAudioTracks, compareFileItem.FilePath,
                        compareMkvAudioTracks))
                {
                    allPassed = false;
                    fileItem.Status = FileStatusEnum.FailedAnalysis;
                    ForceSetControlItemsSourceBinding(FileListBox, FileList);
                    continue;
                }

                var curMkvSubTracks = tracks.Where(x => x.Type == "subtitles").OrderBy(y => y.Id)
                    .ThenBy(z => z.Properties?.TrackName).ToList();
                if (!CheckTracks("subtitles", fileItem.FilePath, curMkvSubTracks, compareFileItem.FilePath,
                        compareMkvSubTracks))
                {
                    allPassed = false;
                    fileItem.Status = FileStatusEnum.FailedAnalysis;
                    ForceSetControlItemsSourceBinding(FileListBox, FileList);
                    continue;
                }

                WriteOutputLine($"PASS - \"{Path.GetFileName(fileItem.FilePath)}\"");
                fileItem.Status = FileStatusEnum.PassedAnalysis;
                ForceSetControlItemsSourceBinding(FileListBox, FileList);
            }

            RunOnUiThread(() => SetBusyCursor(false));
            RunOnUiThread(() => ToggleUI(true));

            if (allPassed)
            {
                ForceSetControlItemsSourceBinding(TrackGrid, TrackList);
                RunOnUiThread(() => TrackGrid.IsEnabled = true);
                RunOnUiThread(() => BatchButton.IsEnabled = true);
                RunOnUiThread(() => StartPulsing(BatchButton, 2000));
                WriteOutputLine("Analysis Completed - Outcome: PASS");
                RunOnUiThread(() =>
                    SetTaskbarProgress(WindowsTaskbarProgress.TaskbarProgressState.Normal, 100, 100));
            }
            else
            {
                RunOnUiThread(() => BatchButton.IsEnabled = false);
                RunOnUiThread(() => TrackGrid.IsEnabled = false);
                WriteOutputLine("Analysis Completed - Outcome: FAIL");
                WriteOutputLine(
                    "Explanation: Unable to unlock batching as the selected files have differing sub/audio/video track setup, proceeding would result in missmatched tracks");
                WriteOutputLine(
                    "Resolution: Deselect the MKV's that have FAILED and process them on their own. Only once all selected files PASS is the batch button unlocked");
                PlayErrorSound();
                RunOnUiThread(() => SetTaskbarProgress(WindowsTaskbarProgress.TaskbarProgressState.Error));
            }

            WriteOutputLine("**** ANALYSIS END ****");
            WriteOutputLine();
        });
    }

    private bool CheckTracks(string trackType, string currentFilePath, List<MkvTrack> currentTracks,
        string compareFilePath, List<MkvTrack> compareTracks)
    {
        var currentCount = currentTracks.Count;
        var compareCount = compareTracks.Count;

        if (currentCount != compareCount)
        {
            WriteOutputLine(
                $"FAIL - \"{Path.GetFileName(currentFilePath)}\" due to differing {trackType} track count compared to \"{Path.GetFileName(compareFilePath)}\": {currentCount} vs {compareCount}");
            return false;
        }

        for (var i = 0; i < currentCount; i++)
        {
            if (currentTracks[i].Properties?.Language != compareTracks[i].Properties?.Language)
            {
                WriteOutputLine(
                    $"FAIL - \"{Path.GetFileName(currentFilePath)}\" due to {trackType} track {i} having a different lang flag compared to \"{Path.GetFileName(compareFilePath)}\": {currentTracks[i].Properties?.Language} vs {compareTracks[i].Properties?.Language}");
                return false;
            }

            if (currentTracks[i].Properties?.TrackName == compareTracks[i].Properties?.TrackName)
            {
                continue;
            }

            WriteOutputLine(
                $"FAIL - \"{Path.GetFileName(currentFilePath)}\" due to {trackType} track {i} having a different track name compared to \"{Path.GetFileName(compareFilePath)}\": {currentTracks[i].Properties?.TrackName} vs {compareTracks[i].Properties?.TrackName}");
            return false;
        }

        return true;
    }

    private static List<string> GetMkvFilesInFolder(string path)
    {
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(filePath => string.Equals(Path.GetExtension(filePath), ".mkv", StringComparison.OrdinalIgnoreCase))
            .Where(filePath =>
            {
                return !Path.GetFullPath(filePath)
                    .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                        StringSplitOptions.RemoveEmptyEntries)
                    .SkipLast(1)
                    .Any(segment => pathComparer.Equals(segment, "MasteredFiles"));
            })
            .ToList();
    }

    private MkvFile? QueryMkvFile(string filePath)
    {
        try
        {
            var jsonOut = QueryMkvFileToJson(filePath);
            return JsonSerializer.Deserialize<MkvFile>(jsonOut, MkvJsonSerializerOptions);
        }
        catch (Exception ex)
        {
            WriteOutputLine($"An exception occured deserializing the JSON output from MKVMerge: {ex}");
            return null;
        }
    }

    private string QueryMkvFileToJson(string filePath)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = _mkvMergePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.StartInfo.ArgumentList.Add("--identification-format");
            process.StartInfo.ArgumentList.Add("json");
            process.StartInfo.ArgumentList.Add("--identify");
            process.StartInfo.ArgumentList.Add(filePath);

            process.Start();
            lock (_processIdTrackerLock)
            {
                ProcessIdTracker.Add(process.Id);
            }

            var standardOuput = process.StandardOutput.ReadToEnd();
            var standardErrorOutput = process.StandardError.ReadToEnd();
            if (!string.IsNullOrEmpty(standardErrorOutput))
            {
                WriteOutputLine($"An error occured with mkvmerge.exe identify: {standardErrorOutput}");
            }

            process.WaitForExit();
            lock (_processIdTrackerLock)
            {
                ProcessIdTracker.Remove(process.Id);
            }

            return standardOuput;
        }
        catch (Exception ex)
        {
            WriteOutputLine($"An exception occured requesting JSON from MKVMerge: {ex}");
            return "";
        }
    }

    private void WriteOutputLine(string text = "", bool replaceLastLine = false)
    {
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                var outputText = OutputTextBox.Text ?? string.Empty;
                if (replaceLastLine)
                {
                    var secondToLastIndex = outputText.LastIndexOf("\r\n", outputText.Length - 3,
                        StringComparison.Ordinal);
                    if (secondToLastIndex >= 0)
                    {
                        outputText = outputText[..(secondToLastIndex + 2)];
                    }
                }

                OutputTextBox.Text = outputText
                                     + (string.IsNullOrEmpty(text) ? "\r\n" : $"{DateTime.Now} - {text}\r\n");
                OutputTextBox.CaretIndex = OutputTextBox.Text.Length;
            }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to write to output window: {ex.Message}");
        }
    }

    private async void AnalyseButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await AnalyseMkvFiles();
        }
        catch (Exception ex)
        {
            WriteOutputLine($"Analysis failed unexpectedly: {ex}");
            await ShowMessageAsync($"Analysis failed unexpectedly.\r\n{ex.Message}", "Analysis error");
            SetBusyCursor(false);
            SetTaskbarProgress(WindowsTaskbarProgress.TaskbarProgressState.None);
            ToggleUI(true);
        }
    }

    private async void BatchButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var includedFiles = FileList.Where(x => x.Include).ToList();
            var includedTracks = TrackList.Where(x => x.Include).ToList();
            var mergeArguments = new List<string>();

            var videoTracks = includedTracks.Where(x => x.Type == "video").ToList();
            if (videoTracks.Count > 0)
            {
                mergeArguments.Add("--video-tracks");
                mergeArguments.Add(string.Join(",", videoTracks.Select(x => x.Id.ToString(CultureInfo.InvariantCulture))));
            }
            else
            {
                mergeArguments.Add("--no-video");
            }

            var audioTracks = includedTracks.Where(x => x.Type == "audio").ToList();
            if (audioTracks.Count > 0)
            {
                mergeArguments.Add("--audio-tracks");
                mergeArguments.Add(string.Join(",", audioTracks.Select(x => x.Id.ToString(CultureInfo.InvariantCulture))));
            }
            else
            {
                mergeArguments.Add("--no-audio");
            }

            var subtitleTracks = includedTracks.Where(x => x.Type == "subtitles").ToList();
            if (subtitleTracks.Count > 0)
            {
                mergeArguments.Add("--subtitle-tracks");
                mergeArguments.Add(string.Join(",", subtitleTracks.Select(x => x.Id.ToString(CultureInfo.InvariantCulture))));
            }
            else
            {
                mergeArguments.Add("--no-subtitles");
            }

            foreach (var track in includedTracks)
            {
                var trackId = track.Id.ToString(CultureInfo.InvariantCulture);
                mergeArguments.Add("--track-name");
                mergeArguments.Add($"{trackId}:{track.Name}");
                mergeArguments.Add("--default-track");
                mergeArguments.Add($"{trackId}:{(track.Default ? "yes" : "no")}");
                mergeArguments.Add("--forced-track");
                mergeArguments.Add($"{trackId}:{(track.Forced ? "yes" : "no")}");
                mergeArguments.Add("--language");
                mergeArguments.Add($"{trackId}:{track.Language}");
            }

            if (AttachmentsCheckbox.IsChecked == true)
            {
                mergeArguments.Add("--no-attachments");
            }

            ToggleUI(false);
            WriteOutputLine("**** BATCH START ****");
            SetTaskbarProgress(WindowsTaskbarProgress.TaskbarProgressState.Indeterminate);
            SetBusyCursor(true);

            var failedCount = 0;
            try
            {
                await Parallel.ForEachAsync(includedFiles, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
                }, async (filePath, cancellationToken) =>
                {
                    var fileName = Path.GetFileName(filePath.FilePath);
                    var lastProgressLog = DateTime.MinValue;
                    try
                    {
                        var outputFilePath = Path.Combine(Path.GetDirectoryName(filePath.FilePath)!, "MasteredFiles",
                            Path.GetFileName(filePath.FilePath));
                        Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath)!);
                        WriteOutputLine($"[{fileName}] Writing \"{outputFilePath}\"");
                        filePath.Status = FileStatusEnum.WritingFile;
                        RequestFileListRefresh();

                        var standardErrorLines = new ConcurrentQueue<string>();
                        using var process = new Process();
                        process.StartInfo = new ProcessStartInfo
                        {
                            FileName = _mkvMergePath,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        process.StartInfo.ArgumentList.Add("-o");
                        process.StartInfo.ArgumentList.Add(outputFilePath);
                        foreach (var argument in mergeArguments)
                        {
                            process.StartInfo.ArgumentList.Add(argument);
                        }
                        process.StartInfo.ArgumentList.Add(filePath.FilePath);

                        process.OutputDataReceived += (_, outputDataReceivedEventArgs) =>
                        {
                            if (outputDataReceivedEventArgs.Data == null)
                                return;

                            if (outputDataReceivedEventArgs.Data.StartsWith("Progress:", StringComparison.Ordinal))
                            {
                                var now = DateTime.UtcNow;
                                if (now - lastProgressLog < TimeSpan.FromMilliseconds(250) &&
                                    !outputDataReceivedEventArgs.Data.Contains("100%"))
                                    return;

                                lastProgressLog = now;
                            }

                            WriteOutputLine($"[{fileName}] {outputDataReceivedEventArgs.Data}");
                        };
                        process.ErrorDataReceived += (_, errorDataReceivedEventArgs) =>
                        {
                            if (errorDataReceivedEventArgs.Data == null)
                                return;

                            standardErrorLines.Enqueue(errorDataReceivedEventArgs.Data);
                            WriteOutputLine($"[{fileName}] {errorDataReceivedEventArgs.Data}");
                        };

                        var processStarted = false;
                        try
                        {
                            process.Start();
                            processStarted = true;
                            lock (_processIdTrackerLock)
                            {
                                ProcessIdTracker.Add(process.Id);
                            }

                            process.BeginOutputReadLine();
                            process.BeginErrorReadLine();
                            await process.WaitForExitAsync(cancellationToken);
                        }
                        finally
                        {
                            if (processStarted)
                            {
                                lock (_processIdTrackerLock)
                                {
                                    ProcessIdTracker.Remove(process.Id);
                                }
                            }
                        }

                        var standardError = string.Join(Environment.NewLine, standardErrorLines);
                        if (string.IsNullOrEmpty(standardError) && File.Exists(outputFilePath) && process.ExitCode == 0)
                        {
                            filePath.Status = FileStatusEnum.WrittenFile;
                            WriteOutputLine($"[{fileName}] Writing complete \"{outputFilePath}\"");
                        }
                        else
                        {
                            filePath.Status = FileStatusEnum.Error;
                            Interlocked.Increment(ref failedCount);
                            WriteOutputLine(
                                $"[{fileName}] Writing error (exit code {process.ExitCode}). {standardError}");
                        }
                    }
                    catch (Exception ex)
                    {
                        filePath.Status = FileStatusEnum.Error;
                        Interlocked.Increment(ref failedCount);
                        WriteOutputLine($"[{fileName}] Exception invoking mkvmerge for \"{filePath.FilePath}\": {ex}");
                    }
                    finally
                    {
                        RequestFileListRefresh();
                    }
                });
            }
            finally
            {
                SetBusyCursor(false);
                SetTaskbarProgress(failedCount == 0
                    ? WindowsTaskbarProgress.TaskbarProgressState.Normal
                    : WindowsTaskbarProgress.TaskbarProgressState.Error, 100, 100);
                RequestFileListRefresh();
                ToggleUI(true);
            }

            if (failedCount > 0)
            {
                PlayErrorSound();
            }

            PlayNotificationSound();
            WriteOutputLine("**** BATCH END ****");
        }
        catch (Exception ex)
        {
            WriteOutputLine($"Batch processing failed unexpectedly: {ex}");
            await ShowMessageAsync($"Batch processing failed unexpectedly.\r\n{ex.Message}", "Batch error");
            SetBusyCursor(false);
            SetTaskbarProgress(WindowsTaskbarProgress.TaskbarProgressState.None);
            ToggleUI(true);
        }
    }

    private void ToggleUI(bool enable)
    {
        TrackGrid.IsEnabled = enable;
        BrowseFolderButton.IsEnabled = enable;
        AnalyseButton.IsEnabled = enable;
        BatchButton.IsEnabled = enable;
        InvertFileButton.IsEnabled = enable;
        SelectAllFileButton.IsEnabled = enable;
        SelectAllTrackButton.IsEnabled = enable;
        SelectNoneFileButton.IsEnabled = enable;
        SelectNoneTrackButton.IsEnabled = enable;
        DeselectFailsButton.IsEnabled = enable;
        SelectUnprocessedButton.IsEnabled = enable;
    }

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Column.Header?.ToString() != "Language") return;
        if (e.EditingElement is not TextBox textBox)
            return;

        if (IsValidLanguageCode(textBox.Text))
            return;

        TrackGrid.CancelEdit(DataGridEditingUnit.Cell);
        _ = ShowMessageAsync(
            $"The language code \"{textBox.Text}\" is invalid.\r\nPlease enter a valid ISO 639-2 language code.",
            "Invalid language code");
    }

    private static bool IsValidLanguageCode(string? code)
    {
        if (string.IsNullOrEmpty(code) || !LanguageCodeRegex().IsMatch(code))
            return false;

        try
        {
            CultureInfo.GetCultureInfoByIetfLanguageTag(code);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private void PlayNotificationSound()
    {
        if (OperatingSystem.IsWindows())
        {
            using var notificationSound = new SoundPlayer(
                GetType().Assembly.GetManifestResourceStream("MKVToolNixWrapper.Assets.SH3Menu.wav"));
            notificationSound.Play();
        }
    }

    private void PlayErrorSound()
    {
        if (OperatingSystem.IsWindows())
        {
            SystemSounds.Hand.Play();
        }
    }

    private async void HelpButton_Click(object? sender, RoutedEventArgs e)
    {
        var helpStream = GetType().Assembly.GetManifestResourceStream("MKVToolNixWrapper.Assets.Info.txt")
                         ?? throw new InvalidOperationException("Embedded help instructions are missing.");
        using var reader = new StreamReader(helpStream);
        var helpText = await reader.ReadToEndAsync();

        var helpWindow = new Window
        {
            Title = "MKVToolNix Wrapper Help",
            Width = 760,
            Height = 640,
            MinWidth = 500,
            MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var closeButton = new Button
        {
            Content = "Close",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            MinWidth = 80
        };
        closeButton.Click += (_, _) => helpWindow.Close();
        DockPanel.SetDock(closeButton, Avalonia.Controls.Dock.Bottom);
        helpWindow.Content = new DockPanel
        {
            Margin = new Thickness(12),
            Children =
            {
                closeButton,
                new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = helpText,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    }
                }
            }
        };
        await helpWindow.ShowDialog(this);
    }

    private async Task ShowMessageAsync(string message, string title)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            MinHeight = 150,
            MaxHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var closeButton = new Button
        {
            Content = "OK",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            MinWidth = 80
        };
        closeButton.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                closeButton
            }
        };
        await dialog.ShowDialog(this);
    }

    private void SetBusyCursor(bool isBusy)
    {
        Cursor = new Cursor(isBusy ? StandardCursorType.Wait : StandardCursorType.Arrow);
    }

    private void SetTaskbarProgress(WindowsTaskbarProgress.TaskbarProgressState state,
        ulong completed = 0, ulong total = 100)
    {
        _taskbarProgress?.SetState(state, completed, total);
    }

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Background).GetAwaiter().GetResult();
    }

    private void SelectAllTrackButton_Click(object? sender, RoutedEventArgs e)
    {
        TrackList.ForEach(item => item.Include = true);
        TrackGrid.ItemsSource = null;
        TrackGrid.ItemsSource = TrackList;
    }

    private void SelectNoneTrackButton_Click(object? sender, RoutedEventArgs e)
    {
        TrackList.ForEach(item => item.Include = false);
        TrackGrid.ItemsSource = null;
        TrackGrid.ItemsSource = TrackList;
    }

    private void SelectAllFileButton_Click(object? sender, RoutedEventArgs e)
    {
        FileList.ForEach(item => item.Include = true);
        FileListBox.ItemsSource = null;
        FileListBox.ItemsSource = FileList;
    }

    private void SelectNoneFileButton_Click(object? sender, RoutedEventArgs e)
    {
        FileList.ForEach(item => item.Include = false);
        FileListBox.ItemsSource = null;
        FileListBox.ItemsSource = FileList;
    }

    private void InvertFileButton_Click(object? sender, RoutedEventArgs e)
    {
        FileList.ForEach(item => item.Include = !item.Include);
        FileListBox.ItemsSource = null;
        FileListBox.ItemsSource = FileList;
    }

    private void FileListBoxCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        TrackGrid.ItemsSource = null;
        TrackGrid.IsEnabled = false;
        BatchButton.IsEnabled = false;
    }

    private void DeselectFailsButton_Click(object? sender, RoutedEventArgs e)
    {
        FileList.ForEach(x =>
        {
            if (x.Status == FileStatusEnum.FailedAnalysis)
            {
                x.Include = false;
            }
        });
        ForceSetControlItemsSourceBinding(FileListBox, FileList);
    }

    private void SelectUnprocessedButton_Click(object? sender, RoutedEventArgs e)
    {
        FileList.ForEach(x =>
        {
            if (x.Status == FileStatusEnum.Unprocessed)
            {
                x.Include = true;
            }
        });
        ForceSetControlItemsSourceBinding(FileListBox, FileList);
    }

    private void RequestFileListRefresh()
    {
        if (Interlocked.CompareExchange(ref _fileListRefreshQueued, 1, 0) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            FileListBox.ItemsSource = null;
            FileListBox.ItemsSource = FileList;
            Interlocked.Exchange(ref _fileListRefreshQueued, 0);
        }, DispatcherPriority.Background);
    }

    private void ForceSetControlItemsSourceBinding<T>(Control control, List<T> items)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyItemsSource(control, items);
            return;
        }

        Dispatcher.UIThread.Post(() => ApplyItemsSource(control, items), DispatcherPriority.Background);
    }

    private static void ApplyItemsSource<T>(Control control, List<T> items)
    {
        switch (control)
        {
            case ListBox listBox:
                listBox.ItemsSource = null;
                listBox.ItemsSource = items;
                break;
            case DataGrid dataGrid:
                dataGrid.ItemsSource = null;
                dataGrid.ItemsSource = items;
                break;
        }
    }

    [GeneratedRegex(@"cover.*\.(png|jpg)$", RegexOptions.IgnoreCase)]
    private static partial Regex CoverAttachmentRegex();

    [GeneratedRegex("^[a-z]{3}$")]
    private static partial Regex LanguageCodeRegex();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        int[] processIds;
        lock (_processIdTrackerLock)
        {
            processIds = [.. ProcessIdTracker];
        }

        foreach (var processId in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);

                if (process.HasExited)
                    continue;

                process.Kill();
                process.WaitForExit();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to kill process with ID {processId}: {ex.Message}");
            }
        }

        base.OnClosing(e);
    }
}