using MKVToolNixWrapper.Dtos;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;

namespace MKVToolNixWrapper;

public partial class MainWindow
{
    private static string _mkvMergePath = @"C:\Program Files\MKVToolNix\mkvmerge.exe";
    private static readonly JsonSerializerOptions MkvJsonSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };
    private readonly Lock _processIdTrackerLock = new();
    private int _fileListRefreshQueued;

    public List<FileMeta> FileList { get; private set; } = [];
    private List<int> ProcessIdTracker { get; } = [];

    public List<TrackListMeta> TrackList { get; private set; } = [];

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Title = $"MKVToolNixWrapper v{Assembly.GetExecutingAssembly().GetName().Version}";
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        WriteOutputLine($"Welcome to MKVToolNixWrapper v{Assembly.GetExecutingAssembly().GetName().Version}");

        DataContext = this;
        TrackGrid.IsEnabled = false;
        AnalyseButton.IsEnabled = false;
        BatchButton.IsEnabled = false;

        MkvMergeExistsCheck();

        StartPulsing(BrowseFolderButton, 2000);
        StartPulsing(SelectedFolderPathLabel, 2000);

        TaskbarItemInfo.ProgressValue = 100;
    }

    private void MkvMergeExistsCheck()
    {
        var roaming = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal);
        var fileMap = new ExeConfigurationFileMap { ExeConfigFilename = roaming.FilePath };
        var config = ConfigurationManager.OpenMappedExeConfiguration(fileMap, ConfigurationUserLevel.None);
        var appSettingsMkvMergePath = config?.AppSettings?.Settings["MkvMergePath"]?.Value;

        _mkvMergePath = string.IsNullOrEmpty(appSettingsMkvMergePath) ? _mkvMergePath : appSettingsMkvMergePath;

        if (File.Exists(_mkvMergePath))
        {
            WriteOutputLine($"Succesfully located MKVMerge at: \"{_mkvMergePath}\"");
        }
        else
        {
            MessageBox.Show(
                messageBoxText: "Unable to locate mkvmerge.exe\r\nPlease click OK and locate your mkvmerge.exe",
                caption: "Failed to locate MKVMerge",
                button: MessageBoxButton.OK,
                icon: MessageBoxImage.Error);

            WriteOutputLine($"Failed to locate MKVMerge at: \"{_mkvMergePath}\" prompting user for location");

            if (!SetMkvMergePath())
            {
                ToggleUI(false);
            }
        }
    }

    private bool SetMkvMergePath()
    {
        var openFileDlg = new System.Windows.Forms.OpenFileDialog
        {
            Title = "Locate your mkvmerge.exe",
            Filter = "Executable Files|*.exe",
            FileName = "mkvmerge.exe",
            CheckFileExists = true,
            CheckPathExists = true
        };

        var result = openFileDlg.ShowDialog();
        if (result != System.Windows.Forms.DialogResult.OK)
        {
            return false;
        }

        if (Path.GetFileName(openFileDlg.FileName).Equals("mkvmerge.exe", StringComparison.OrdinalIgnoreCase))
        {
            var roaming = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal);
            var fileMap = new ExeConfigurationFileMap
            {
                ExeConfigFilename = roaming.FilePath
            };
            var config = ConfigurationManager.OpenMappedExeConfiguration(fileMap, ConfigurationUserLevel.None);
            if (config.AppSettings.Settings["MkvMergePath"] == null)
                config.AppSettings.Settings.Add("MkvMergePath", openFileDlg.FileName);
            else
                config.AppSettings.Settings["MkvMergePath"].Value = openFileDlg.FileName;

            config.Save(ConfigurationSaveMode.Modified);

            _mkvMergePath = openFileDlg.FileName;
            WriteOutputLine($"Succesfully located MKVMerge at: \"{_mkvMergePath}\"");
            return true;
        }

        MessageBox.Show(
            messageBoxText:
            "You have selected an invalid executable\r\nEnsure the file is correctly named 'mkvmerge.exe'\r\nPlease restart the application and try again\r\n\r\nClick on 'Help' if you need more information on MkvMerge",
            caption: "Failed to locate MKVMerge",
            button: MessageBoxButton.OK,
            icon: MessageBoxImage.Error);
        return false;
    }

    private static void StartPulsing(UIElement element, int durationMs)
    {
        var storyboard = new Storyboard();
        var opacityAnimation1 = new DoubleAnimation
        {
            From = 1.0,
            To = 0.2,
            Duration = new Duration(TimeSpan.FromSeconds(0.5)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };

        storyboard.Children.Add(opacityAnimation1);
        Storyboard.SetTarget(opacityAnimation1, element);
        Storyboard.SetTargetProperty(opacityAnimation1, new PropertyPath(OpacityProperty));

        storyboard.Begin();
        var stopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs) };
        stopTimer.Tick += (_, _) =>
        {
            stopTimer.Stop();
            storyboard.Stop();
            element.Opacity = 1;
        };
        stopTimer.Start();
    }

    private async void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TrackGrid.IsEnabled = false;
            AnalyseButton.IsEnabled = false;
            BatchButton.IsEnabled = false;
            TrackGrid.ItemsSource = null;
            FileListBox.ItemsSource = null;

            using var openFileDlg = new System.Windows.Forms.FolderBrowserDialog();
            var result = openFileDlg.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK)
            {
                await BrowseFolderHandler(openFileDlg.SelectedPath);
            }
            else
            {
                SelectedFolderPathLabel.Content = "";
            }
        }
        catch (Exception ex)
        {
            WriteOutputLine($"Failed to load the selected folder: {ex}");
            MessageBox.Show(
                messageBoxText: $"Failed to load the selected folder.\r\n{ex.Message}",
                caption: "Folder error",
                button: MessageBoxButton.OK,
                icon: MessageBoxImage.Error);
            BrowseFolderButton.IsEnabled = true;
            Mouse.OverrideCursor = null;
        }
    }

    private async Task BrowseFolderHandler(string path)
    {
        try
        {
            WriteOutputLine($"Source folder selected: \"{path}\"");
            BrowseFolderButton.IsEnabled = false;

            Dispatcher.Invoke(() => Mouse.OverrideCursor = Cursors.Wait);

            var mkvFiles = await Task.Run(() => GetMkvFilesInFolder(path));
            if (mkvFiles.Count == 0)
            {
                MessageBox.Show(
                    messageBoxText:
                    "Unable to locate .mkv files in the selected folder, please select a valid folder.\r\n\r\nNote: 'MasteredFiles' folder is reserved and not included in the search!",
                    caption: "Directory error",
                    button: MessageBoxButton.OK,
                    icon: MessageBoxImage.Error);

                WriteOutputLine($"Error: Unable to locate .mkv files in the selected folder: \"{path}\"");
                SelectedFolderPathLabel.Content = "";
            }
            else
            {
                SelectedFolderPathLabel.Content = path;

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
            // Hide mouse spinner and enable browse button
            Dispatcher.Invoke(() => Mouse.OverrideCursor = null);
            BrowseFolderButton.IsEnabled = true;
        }
    }

    private async Task AnalyseMkvFiles()
    {
        await Task.Run(() =>
        {
            WriteOutputLine("**** ANALYSIS START ****");
            Dispatcher.Invoke(() => TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Indeterminate);

            TrackList = [];
            Dispatcher.Invoke(() => ToggleUI(false));

            foreach (var file in FileList.Where(file => file.Status == FileStatusEnum.FailedAnalysis))
            {
                file.Status = FileStatusEnum.Unprocessed;
            }

            ForceSetControlItemsSourceBinding(FileListBox, FileList);

            Dispatcher.Invoke(() => Mouse.OverrideCursor = Cursors.Wait);
            Dispatcher.Invoke(() => TrackGrid.ItemsSource = null);

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

            Dispatcher.Invoke(() => Mouse.OverrideCursor = null);
            Dispatcher.Invoke(() => ToggleUI(true));

            if (allPassed)
            {
                ForceSetControlItemsSourceBinding(TrackGrid, TrackList);
                Dispatcher.Invoke(() => TrackGrid.IsEnabled = true);
                Dispatcher.Invoke(() => BatchButton.IsEnabled = true);
                Dispatcher.Invoke(() => StartPulsing(BatchButton, 2000));
                WriteOutputLine("Analysis Completed - Outcome: PASS");
                Dispatcher.Invoke(() => TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Normal);
            }
            else
            {
                Dispatcher.Invoke(() => BatchButton.IsEnabled = false);
                Dispatcher.Invoke(() => TrackGrid.IsEnabled = false);
                WriteOutputLine("Analysis Completed - Outcome: FAIL");
                WriteOutputLine(
                    "Explanation: Unable to unlock batching as the selected files have differing sub/audio/video track setup, proceeding would result in missmatched tracks");
                WriteOutputLine(
                    "Resolution: Deselect the MKV's that have FAILED and process them on their own. Only once all selected files PASS is the batch button unlocked");
                SystemSounds.Hand.Play();
                Dispatcher.Invoke(() => TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Error);
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
        return
        [
            .. Directory.GetFiles(path, "*.mkv", SearchOption.AllDirectories)
                .Where(filePath => !filePath.Contains(@"\MasteredFiles\"))
        ];
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
                Arguments = $"--identification-format json --identify \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

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
            Dispatcher.BeginInvoke(() =>
            {
                if (replaceLastLine)
                {
                    var secondToLastIndex = OutputTextBox.Text.LastIndexOf("\r\n", OutputTextBox.Text.Length - 3,
                        StringComparison.Ordinal);
                    if (secondToLastIndex >= 0)
                    {
                        OutputTextBox.Text = OutputTextBox.Text[..(secondToLastIndex + 2)];
                    }
                }

                OutputTextBox.AppendText(string.IsNullOrEmpty(text) ? "\r\n" : $"{DateTime.Now} - {text}\r\n");
                OutputTextBox.ScrollToEnd();
            }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to write to output window: {ex.Message}");
        }
    }

    private async void AnalyseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await AnalyseMkvFiles();
        }
        catch (Exception ex)
        {
            WriteOutputLine($"Analysis failed unexpectedly: {ex}");
            MessageBox.Show(
                messageBoxText: $"Analysis failed unexpectedly.\r\n{ex.Message}",
                caption: "Analysis error",
                button: MessageBoxButton.OK,
                icon: MessageBoxImage.Error);
            Mouse.OverrideCursor = null;
            TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
            ToggleUI(true);
        }
    }

    private async void BatchButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var includedFiles = FileList.Where(x => x.Include).ToList();
            var includedTracks = TrackList.Where(x => x.Include).ToList();
            var mergeCommandString = "";

            var videoTracks = includedTracks.Where(x => x.Type == "video").ToList();
            mergeCommandString += videoTracks.Count > 0
                ? $"--video-tracks {string.Join(",", videoTracks.Select(x => x.Id))} "
                : "--no-video ";

            var audioTracks = includedTracks.Where(x => x.Type == "audio").ToList();
            mergeCommandString += audioTracks.Count > 0
                ? $"--audio-tracks {string.Join(",", audioTracks.Select(x => x.Id))} "
                : "--no-audio ";

            var subtitleTracks = includedTracks.Where(x => x.Type == "subtitles").ToList();
            mergeCommandString += subtitleTracks.Count > 0
                ? $"--subtitle-tracks {string.Join(",", subtitleTracks.Select(x => x.Id))} "
                : "--no-subtitles ";

            mergeCommandString +=
                string.Join(" ", includedTracks.Select(x => $"--track-name {x.Id}:\"{x.Name}\"")) + " ";
            mergeCommandString += string.Join(" ",
                includedTracks.Select(x => $"--default-track {x.Id}:{(x.Default ? "yes" : "no")}")) + " ";
            mergeCommandString += string.Join(" ",
                includedTracks.Select(x => $"--forced-track {x.Id}:{(x.Forced ? "yes" : "no")}")) + " ";
            mergeCommandString += string.Join(" ", includedTracks.Select(x => $"--language {x.Id}:{x.Language}")) + " ";

            if (AttachmentsCheckbox.IsChecked == true)
            {
                mergeCommandString += "--no-attachments ";
            }

            ToggleUI(false);
            WriteOutputLine("**** BATCH START ****");
            TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Indeterminate;
            Mouse.OverrideCursor = Cursors.Wait;

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
                        var mkvMergeArgument = $"-o \"{outputFilePath}\" {mergeCommandString} \"{filePath.FilePath}\"";

                        WriteOutputLine($"[{fileName}] Writing \"{outputFilePath}\"");
                        filePath.Status = FileStatusEnum.WritingFile;
                        RequestFileListRefresh();

                        var standardErrorLines = new ConcurrentQueue<string>();
                        using var process = new Process();
                        process.StartInfo = new ProcessStartInfo
                        {
                            FileName = _mkvMergePath,
                            Arguments = mkvMergeArgument,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

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
                Mouse.OverrideCursor = null;
                TaskbarItemInfo.ProgressState = failedCount == 0
                    ? TaskbarItemProgressState.Normal
                    : TaskbarItemProgressState.Error;
                RequestFileListRefresh();
                ToggleUI(true);
            }

            if (failedCount > 0)
            {
                SystemSounds.Hand.Play();
            }

            PlayNotificationSound();
            WriteOutputLine("**** BATCH END ****");
        }
        catch (Exception ex)
        {
            WriteOutputLine($"Batch processing failed unexpectedly: {ex}");
            MessageBox.Show(
                messageBoxText: $"Batch processing failed unexpectedly.\r\n{ex.Message}",
                caption: "Batch error",
                button: MessageBoxButton.OK,
                icon: MessageBoxImage.Error);
            Mouse.OverrideCursor = null;
            TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
            ToggleUI(true);
        }
    }

    // ReSharper disable once InconsistentNaming
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

    private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Column != LanguageCodeColumn) return;
        if (e.EditingElement is not TextBox textBox)
            return;

        if (IsValidLanguageCode(textBox.Text))
            return;

        e.Cancel = true;
        MessageBox.Show(
            messageBoxText:
            $"The language code \"{textBox.Text}\" is invalid.\r\nPlease enter a valid ISO 639-2 language code.",
            caption: "Invalid language code",
            button: MessageBoxButton.OK,
            icon: MessageBoxImage.Error);
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
        var notificationSound =
            new SoundPlayer(GetType().Assembly.GetManifestResourceStream("MKVToolNixWrapper.Assets.SH3Menu.wav"));
        notificationSound.Play();
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        PlayNotificationSound();

        using var fileStream = File.Create($"{Path.GetTempPath()}\\Info.txt");
        GetType().Assembly.GetManifestResourceStream("MKVToolNixWrapper.Assets.Info.txt")!.CopyTo(fileStream);

        var process = Process.Start("notepad", $"{Path.GetTempPath()}\\Info.txt");
        process.Dispose();
    }

    private void SelectAllTrackButton_Click(object sender, RoutedEventArgs e)
    {
        TrackList.ForEach(item => item.Include = true);
        TrackGrid.ItemsSource = null;
        TrackGrid.ItemsSource = TrackList;
    }

    private void SelectNoneTrackButton_Click(object sender, RoutedEventArgs e)
    {
        TrackList.ForEach(item => item.Include = false);
        TrackGrid.ItemsSource = null;
        TrackGrid.ItemsSource = TrackList;
    }

    private void SelectAllFileButton_Click(object sender, RoutedEventArgs e)
    {
        FileList.ForEach(item => item.Include = true);
        FileListBox.ItemsSource = null;
        FileListBox.ItemsSource = FileList;
    }

    private void SelectNoneFileButton_Click(object sender, RoutedEventArgs e)
    {
        FileList.ForEach(item => item.Include = false);
        FileListBox.ItemsSource = null;
        FileListBox.ItemsSource = FileList;
    }

    private void InvertFileButton_Click(object sender, RoutedEventArgs e)
    {
        FileList.ForEach(item => item.Include = !item.Include);
        FileListBox.ItemsSource = null;
        FileListBox.ItemsSource = FileList;
    }

    private void FileListBoxCheckBox_Click(object sender, RoutedEventArgs e)
    {
        TrackGrid.ItemsSource = null;
        TrackGrid.IsEnabled = false;
        BatchButton.IsEnabled = false;
    }

    private void DeselectFailsButton_Click(object sender, RoutedEventArgs e)
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

    private void SelectUnprocessedButton_Click(object sender, RoutedEventArgs e)
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

    // Hack to avoid using INotifyPropertyChanged as it's messy to implement and restricts us to only using ObservableCollection :/
    // Nulling the item source and re-assigning it our collection effectively re-syncs with the UI. Aware this is not great but the 'offical' solution is some what to be desired.
    private void RequestFileListRefresh()
    {
        if (Interlocked.CompareExchange(ref _fileListRefreshQueued, 1, 0) != 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            FileListBox.ItemsSource = null;
            FileListBox.ItemsSource = FileList;
            Interlocked.Exchange(ref _fileListRefreshQueued, 0);
        }, DispatcherPriority.Background);
    }

    private void ForceSetControlItemsSourceBinding<T>(Control control, List<T> items)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyItemsSource(control, items);
            return;
        }

        Dispatcher.BeginInvoke(() => ApplyItemsSource(control, items), DispatcherPriority.Background);
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

    protected override void OnClosing(CancelEventArgs e)
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