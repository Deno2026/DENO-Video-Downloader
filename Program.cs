using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DenoVideoDownloader;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // A normal WinExe launch has no console handle.
        }

        if (args.Length > 0 && args[0].Equals("--self-test", StringComparison.OrdinalIgnoreCase))
        {
            return SelfTest.Run();
        }

        if (args.Length > 1 && args[0].Equals("--headless-download", StringComparison.OrdinalIgnoreCase))
        {
            var outputDirectory = args.Length > 2 ? args[2] : AppPaths.DownloadsDirectory;
            return RunHeadlessDownload(args[1], outputDirectory).GetAwaiter().GetResult();
        }

        ApplicationConfiguration.Initialize();

        if (args.Length > 1 && args[0].Equals("--capture-ui", StringComparison.OrdinalIgnoreCase))
        {
            using var captureForm = new DownloaderForm();
            captureForm.Shown += async (_, _) =>
            {
                await Task.Delay(500);
                using var bitmap = new Bitmap(captureForm.Width, captureForm.Height);
                captureForm.DrawToBitmap(bitmap, new Rectangle(0, 0, captureForm.Width, captureForm.Height));
                bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                captureForm.Close();
            };
            Application.Run(captureForm);
            return 0;
        }

        Application.Run(new DownloaderForm());
        return 0;
    }

    private static async Task<int> RunHeadlessDownload(string url, string outputDirectory)
    {
        try
        {
            var progress = new Progress<DownloadUpdate>(update =>
            {
                var percent = update.Percent.HasValue
                    ? $" {update.Percent.Value.ToString("0.0", CultureInfo.InvariantCulture)}%"
                    : string.Empty;
                Console.WriteLine($"{update.Stage}{percent} {update.Detail}".Trim());
            });

            var service = new DownloadService();
            var result = await service.DownloadAsync(url, outputDirectory, progress, CancellationToken.None);
            Console.WriteLine($"SUCCESS:{result.FilePath ?? "(path unavailable)"}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}

internal static class AppPaths
{
    public static string DownloadsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public static string? FindYtDlp()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return FindFirstExisting(
            Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp.exe"),
            FindOnPath("yt-dlp.exe"),
            Path.Combine(profile, ".local", "bin", "yt-dlp.exe"),
            Path.Combine(local, "Microsoft", "WinGet", "Links", "yt-dlp.exe"));
    }

    public static string? FindFfmpeg()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var onPath = FindOnPath("ffmpeg.exe");
        if (onPath is not null)
        {
            return onPath;
        }

        var packageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WinGet",
            "Packages");

        if (!Directory.Exists(packageRoot))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(packageRoot, "ffmpeg.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public static string? FindNode() =>
        FindFirstExisting(
            Path.Combine(AppContext.BaseDirectory, "tools", "node.exe"),
            FindOnPath("node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"));

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var rawDirectory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries and continue with known locations.
            }
        }

        return null;
    }

    private static string? FindFirstExisting(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));
}

internal enum DownloadStage
{
    Preparing,
    Downloading,
    Merging,
    Completed
}

internal sealed record DownloadUpdate(
    DownloadStage Stage,
    double? Percent = null,
    string Detail = "",
    string? FilePath = null);

internal sealed record DownloadResult(string? FilePath);

internal sealed class DownloadService
{
    private static readonly Regex PercentRegex =
        new(@"(?<value>\d+(?:\.\d+)?)%", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private Process? _activeProcess;

    public async Task<DownloadResult> DownloadAsync(
        string url,
        string outputDirectory,
        IProgress<DownloadUpdate>? progress,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("올바른 http 또는 https 영상 링크를 입력해 주세요.");
        }

        var ytDlp = AppPaths.FindYtDlp()
            ?? throw new FileNotFoundException("yt-dlp를 찾지 못했습니다. 앱을 만든 환경의 도구 설치 상태를 확인해 주세요.");
        var ffmpeg = AppPaths.FindFfmpeg()
            ?? throw new FileNotFoundException("ffmpeg를 찾지 못했습니다. 고화질 MP4 병합에 필요한 도구입니다.");
        var node = AppPaths.FindNode();

        Directory.CreateDirectory(outputDirectory);
        progress?.Report(new DownloadUpdate(DownloadStage.Preparing, Detail: "영상 정보를 확인하는 중…"));

        var startInfo = new ProcessStartInfo
        {
            FileName = ytDlp,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = outputDirectory
        };

        AddArguments(startInfo, url, outputDirectory, ffmpeg, node);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _activeProcess = process;

        var errorLines = new ConcurrentQueue<string>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? completedFilePath = null;

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            var line = e.Data.Trim();
            if (line.StartsWith("__DENO_PROGRESS__:", StringComparison.Ordinal))
            {
                var payload = line["__DENO_PROGRESS__:".Length..];
                var parts = payload.Split('|');
                var match = PercentRegex.Match(parts.ElementAtOrDefault(0) ?? string.Empty);
                var percent = match.Success &&
                              double.TryParse(match.Groups["value"].Value, NumberStyles.Float,
                                  CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : (double?)null;
                var speed = CleanMetric(parts.ElementAtOrDefault(1));
                var eta = CleanMetric(parts.ElementAtOrDefault(2));
                var detail = BuildProgressDetail(speed, eta);
                progress?.Report(new DownloadUpdate(DownloadStage.Downloading, percent, detail));
                return;
            }

            if (line.StartsWith("__DENO_FILE__:", StringComparison.Ordinal))
            {
                completedFilePath = line["__DENO_FILE__:".Length..].Trim();
                return;
            }

            if (line.StartsWith("[Merger]", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(new DownloadUpdate(DownloadStage.Merging, Detail: "영상과 오디오를 합치는 중…"));
                return;
            }

            if (line.Contains("has already been downloaded", StringComparison.OrdinalIgnoreCase))
            {
                var marker = "has already been downloaded";
                var markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                var path = markerIndex > 0 ? line[..markerIndex].Replace("[download]", string.Empty).Trim() : null;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    completedFilePath = path;
                }

                progress?.Report(new DownloadUpdate(DownloadStage.Preparing, Detail: "이미 받은 파일을 확인하는 중…"));
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                errorLines.Enqueue(e.Data.Trim());
                while (errorLines.Count > 10)
                {
                    errorLines.TryDequeue(out var droppedLine);
                }
            }
        };

        process.Exited += (_, _) => completion.TrySetResult();

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("다운로드 도구를 시작하지 못했습니다.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // The process may have exited between the checks.
                }
            });

            await completion.Task.WaitAsync(cancellationToken);
            process.WaitForExit();

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (process.ExitCode != 0)
            {
                var error = string.Join(Environment.NewLine, errorLines);
                throw new InvalidOperationException(BuildFriendlyError(error, process.ExitCode));
            }

            if (!string.IsNullOrWhiteSpace(completedFilePath))
            {
                completedFilePath = Path.GetFullPath(completedFilePath);
            }

            progress?.Report(new DownloadUpdate(
                DownloadStage.Completed,
                100,
                "다운로드가 끝났습니다.",
                completedFilePath));

            return new DownloadResult(completedFilePath);
        }
        finally
        {
            _activeProcess = null;
        }
    }

    public void Cancel()
    {
        try
        {
            if (_activeProcess is { HasExited: false })
            {
                _activeProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Cancellation is best-effort; the token path remains authoritative.
        }
    }

    private static void AddArguments(
        ProcessStartInfo startInfo,
        string url,
        string outputDirectory,
        string ffmpeg,
        string? node)
    {
        var outputTemplate = Path.Combine(outputDirectory, "%(title)s [%(id)s].%(ext)s");
        var arguments = new List<string>
        {
            "--no-playlist",
            "--windows-filenames",
            "--no-overwrites",
            "--no-color",
            "--newline",
            "--progress",
            "--progress-template",
            "download:__DENO_PROGRESS__:%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
            "--print",
            "after_move:__DENO_FILE__:%(filepath)s",
            "--ffmpeg-location",
            Path.GetDirectoryName(ffmpeg) ?? ffmpeg,
            "-f",
            "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best",
            "--merge-output-format",
            "mp4",
            "-o",
            outputTemplate
        };

        if (!string.IsNullOrWhiteSpace(node))
        {
            arguments.Add("--js-runtimes");
            arguments.Add($"node:{node}");
        }

        arguments.Add(url);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private static string CleanMetric(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = value.Trim();
        if (cleaned.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("NA", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return cleaned;
    }

    private static string BuildProgressDetail(string speed, string eta)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(speed))
        {
            parts.Add(speed);
        }

        if (!string.IsNullOrWhiteSpace(eta))
        {
            parts.Add($"남은 시간 {eta}");
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "받는 중…";
    }

    private static string BuildFriendlyError(string error, int exitCode)
    {
        if (error.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase))
        {
            return "이 링크는 현재 다운로드 도구에서 지원하지 않습니다.";
        }

        if (error.Contains("Sign in", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("private video", StringComparison.OrdinalIgnoreCase))
        {
            return "로그인이 필요하거나 비공개 영상이라 다운로드할 수 없습니다.";
        }

        if (error.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase))
        {
            return "사이트가 요청을 잠시 제한했습니다. 잠시 뒤 다시 시도해 주세요.";
        }

        var lines = error
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
            .TakeLast(4)
            .ToArray();

        return lines.Length > 0
            ? string.Join(Environment.NewLine, lines)
            : $"다운로드 도구가 오류 코드 {exitCode}로 종료되었습니다.";
    }
}

internal static class SelfTest
{
    public static int Run()
    {
        var failures = new List<string>();
        var ytDlp = AppPaths.FindYtDlp();
        var ffmpeg = AppPaths.FindFfmpeg();
        var node = AppPaths.FindNode();

        if (string.IsNullOrWhiteSpace(ytDlp) || !File.Exists(ytDlp))
        {
            failures.Add("yt-dlp not found");
        }

        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
        {
            failures.Add("ffmpeg not found");
        }

        if (!Directory.Exists(AppPaths.DownloadsDirectory))
        {
            failures.Add("Downloads directory not found");
        }

        Console.WriteLine($"yt-dlp={ytDlp ?? "(missing)"}");
        Console.WriteLine($"ffmpeg={ffmpeg ?? "(missing)"}");
        Console.WriteLine($"node={node ?? "(optional, missing)"}");
        Console.WriteLine($"downloads={AppPaths.DownloadsDirectory}");

        if (failures.Count == 0)
        {
            Console.WriteLine("SELF_TEST_OK");
            return 0;
        }

        Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
        return 1;
    }
}

internal sealed class DownloaderForm : Form
{
    private static readonly Color Noir = ColorTranslator.FromHtml("#0F0E12");
    private static readonly Color Surface = ColorTranslator.FromHtml("#1B1320");
    private static readonly Color Raised = ColorTranslator.FromHtml("#2E1E38");
    private static readonly Color Neutral = ColorTranslator.FromHtml("#2A2A31");
    private static readonly Color Plum = ColorTranslator.FromHtml("#5A386B");
    private static readonly Color Accent = ColorTranslator.FromHtml("#F2FF59");
    private static readonly Color PrimaryText = ColorTranslator.FromHtml("#E8E6E1");
    private static readonly Color SecondaryText = ColorTranslator.FromHtml("#A3A6AD");

    private readonly TextBox _urlTextBox;
    private readonly RoundedButton _pasteButton;
    private readonly RoundedButton _downloadButton;
    private readonly RoundedButton _cancelButton;
    private readonly RoundedButton _openFolderButton;
    private readonly Label _statusTitle;
    private readonly Label _statusDetail;
    private readonly Label _statusDot;
    private readonly ProgressTrack _progressTrack;
    private readonly Label _savePathLabel;
    private readonly DownloadService _downloadService = new();
    private CancellationTokenSource? _downloadCancellation;
    private bool _isRunning;

    public DownloaderForm()
    {
        Text = "영상 다운로드";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 480);
        MinimumSize = new Size(680, 480);
        MaximumSize = new Size(680, 480);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        BackColor = Noir;
        ForeColor = PrimaryText;
        Font = CreateUiFont(10F, FontStyle.Regular);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        var eyebrow = new Label
        {
            AutoSize = true,
            Text = "DENO UTILITY",
            ForeColor = Accent,
            Font = CreateUiFont(8.5F, FontStyle.Bold),
            Location = new Point(34, 28)
        };

        var title = new Label
        {
            AutoSize = true,
            Text = "영상 다운로드",
            ForeColor = PrimaryText,
            Font = CreateUiFont(22F, FontStyle.Bold),
            Location = new Point(30, 49)
        };

        var subtitle = new Label
        {
            AutoSize = true,
            Text = "영상 링크 하나를 붙여넣으면 최고 화질 MP4로 저장합니다.",
            ForeColor = SecondaryText,
            Font = CreateUiFont(9.5F, FontStyle.Regular),
            Location = new Point(34, 91)
        };

        var inputLabel = new Label
        {
            AutoSize = true,
            Text = "영상 링크",
            ForeColor = PrimaryText,
            Font = CreateUiFont(9F, FontStyle.Bold),
            Location = new Point(34, 127)
        };

        var inputSurface = new RoundedPanel
        {
            BackColor = Surface,
            BorderColor = Neutral,
            CornerRadius = 10,
            Location = new Point(34, 151),
            Size = new Size(612, 55)
        };

        _urlTextBox = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Surface,
            ForeColor = PrimaryText,
            Font = CreateUiFont(10.5F, FontStyle.Regular),
            Location = new Point(14, 17),
            Size = new Size(477, 25),
            PlaceholderText = "https://youtu.be/…",
            TabIndex = 0
        };
        _urlTextBox.KeyDown += UrlTextBox_KeyDown;
        _urlTextBox.TextChanged += (_, _) => UpdateDownloadAvailability();

        _pasteButton = CreateButton("붙여넣기", Surface, PrimaryText, Plum);
        _pasteButton.Location = new Point(500, 9);
        _pasteButton.Size = new Size(102, 37);
        _pasteButton.TabIndex = 1;
        _pasteButton.Click += (_, _) => PasteFromClipboard();
        inputSurface.Controls.Add(_urlTextBox);
        inputSurface.Controls.Add(_pasteButton);

        _downloadButton = CreateButton("다운로드 시작", Accent, Noir, Accent);
        _downloadButton.Location = new Point(34, 222);
        _downloadButton.Size = new Size(494, 48);
        _downloadButton.Font = CreateUiFont(10.5F, FontStyle.Bold);
        _downloadButton.TabIndex = 2;
        _downloadButton.Click += async (_, _) => await StartDownloadAsync();

        _cancelButton = CreateButton("취소", Neutral, PrimaryText, Plum);
        _cancelButton.Location = new Point(539, 222);
        _cancelButton.Size = new Size(107, 48);
        _cancelButton.TabIndex = 3;
        _cancelButton.Enabled = false;
        _cancelButton.Click += (_, _) => CancelDownload();

        var statusSurface = new RoundedPanel
        {
            BackColor = Surface,
            BorderColor = Color.FromArgb(72, Plum),
            CornerRadius = 12,
            Location = new Point(34, 286),
            Size = new Size(612, 91)
        };

        _statusDot = new Label
        {
            AutoSize = true,
            Text = "●",
            ForeColor = SecondaryText,
            Font = CreateUiFont(10F, FontStyle.Bold),
            Location = new Point(16, 14)
        };

        _statusTitle = new Label
        {
            AutoSize = false,
            Text = "준비됨",
            ForeColor = PrimaryText,
            Font = CreateUiFont(9.5F, FontStyle.Bold),
            Location = new Point(38, 12),
            Size = new Size(545, 23)
        };

        _statusDetail = new Label
        {
            AutoSize = false,
            Text = "링크를 붙여넣어 주세요.",
            ForeColor = SecondaryText,
            Font = CreateUiFont(9F, FontStyle.Regular),
            Location = new Point(38, 35),
            Size = new Size(545, 22),
            AutoEllipsis = true
        };

        _progressTrack = new ProgressTrack
        {
            Location = new Point(17, 66),
            Size = new Size(578, 7),
            TrackColor = Neutral,
            ProgressColor = Accent,
            Value = 0
        };

        statusSurface.Controls.Add(_statusDot);
        statusSurface.Controls.Add(_statusTitle);
        statusSurface.Controls.Add(_statusDetail);
        statusSurface.Controls.Add(_progressTrack);

        _savePathLabel = new Label
        {
            AutoSize = false,
            Text = $"저장 위치  {AppPaths.DownloadsDirectory}",
            ForeColor = SecondaryText,
            Font = CreateUiFont(8.5F, FontStyle.Regular),
            Location = new Point(34, 394),
            Size = new Size(475, 25),
            AutoEllipsis = true
        };

        _openFolderButton = CreateButton("폴더 열기", Noir, PrimaryText, Neutral);
        _openFolderButton.Location = new Point(539, 389);
        _openFolderButton.Size = new Size(107, 30);
        _openFolderButton.TabIndex = 4;
        _openFolderButton.Click += (_, _) => OpenDownloadsFolder();

        Controls.Add(eyebrow);
        Controls.Add(title);
        Controls.Add(subtitle);
        Controls.Add(inputLabel);
        Controls.Add(inputSurface);
        Controls.Add(_downloadButton);
        Controls.Add(_cancelButton);
        Controls.Add(statusSurface);
        Controls.Add(_savePathLabel);
        Controls.Add(_openFolderButton);

        AcceptButton = _downloadButton;
        Shown += (_, _) =>
        {
            TryPrefillFromClipboard();
            _urlTextBox.Focus();
        };
        FormClosing += DownloaderForm_FormClosing;
        UpdateDownloadAvailability();
    }

    private async Task StartDownloadAsync()
    {
        if (_isRunning)
        {
            return;
        }

        var url = _urlTextBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            SetStatus("링크를 확인해 주세요", "http 또는 https로 시작하는 영상 링크가 필요합니다.", Color.OrangeRed, 0);
            _urlTextBox.Focus();
            return;
        }

        _isRunning = true;
        _downloadCancellation = new CancellationTokenSource();
        SetRunningState(true);
        SetStatus("영상 확인 중", "다운로드 가능한 영상인지 확인하고 있습니다.", Accent, 0);

        var progress = new Progress<DownloadUpdate>(update =>
        {
            switch (update.Stage)
            {
                case DownloadStage.Preparing:
                    SetStatus("영상 확인 중", update.Detail, Accent, null);
                    break;
                case DownloadStage.Downloading:
                    SetStatus(
                        "다운로드 중",
                        update.Detail,
                        Accent,
                        update.Percent.HasValue ? (int)Math.Round(update.Percent.Value) : null);
                    break;
                case DownloadStage.Merging:
                    SetStatus("마무리 중", update.Detail, Accent, null);
                    _progressTrack.IsIndeterminate = true;
                    break;
                case DownloadStage.Completed:
                    SetStatus("완료", update.Detail, Accent, 100);
                    break;
            }
        });

        try
        {
            var result = await _downloadService.DownloadAsync(
                url,
                AppPaths.DownloadsDirectory,
                progress,
                _downloadCancellation.Token);

            var detail = !string.IsNullOrWhiteSpace(result.FilePath)
                ? Path.GetFileName(result.FilePath)
                : "다운로드 폴더에 저장했습니다.";
            SetStatus("다운로드 완료", detail, Accent, 100);
            FlashWindow();
        }
        catch (OperationCanceledException)
        {
            SetStatus("취소됨", "다운로드를 중단했습니다. 다시 시도할 수 있습니다.", SecondaryText, 0);
        }
        catch (Exception ex)
        {
            SetStatus("다운로드 실패", ex.Message, Color.FromArgb(255, 118, 118), 0);
        }
        finally
        {
            _downloadCancellation.Dispose();
            _downloadCancellation = null;
            _isRunning = false;
            SetRunningState(false);
        }
    }

    private void CancelDownload()
    {
        if (!_isRunning)
        {
            return;
        }

        _statusDetail.Text = "안전하게 중단하는 중…";
        _cancelButton.Enabled = false;
        _downloadCancellation?.Cancel();
        _downloadService.Cancel();
    }

    private void SetRunningState(bool running)
    {
        _urlTextBox.Enabled = !running;
        _pasteButton.Enabled = !running;
        _downloadButton.Enabled = !running && HasValidUrl();
        _downloadButton.Text = running ? "다운로드 중…" : "다운로드 시작";
        _cancelButton.Enabled = running;
        _progressTrack.IsIndeterminate = false;
    }

    private void SetStatus(string title, string detail, Color dotColor, int? progress)
    {
        _statusTitle.Text = title;
        _statusDetail.Text = detail;
        _statusDot.ForeColor = dotColor;

        if (progress.HasValue)
        {
            _progressTrack.IsIndeterminate = false;
            _progressTrack.Value = Math.Clamp(progress.Value, 0, 100);
        }
    }

    private void PasteFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                _urlTextBox.Text = Clipboard.GetText().Trim();
                _urlTextBox.SelectionStart = _urlTextBox.TextLength;
                _urlTextBox.Focus();
            }
            else
            {
                SetStatus("붙여넣을 내용이 없습니다", "클립보드에 영상 링크를 복사한 뒤 다시 눌러 주세요.", SecondaryText, 0);
            }
        }
        catch
        {
            SetStatus("클립보드를 읽지 못했습니다", "Ctrl+V로 링크를 직접 붙여넣어 주세요.", Color.OrangeRed, 0);
        }
    }

    private void TryPrefillFromClipboard()
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                return;
            }

            var text = Clipboard.GetText().Trim();
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                _urlTextBox.Text = text;
                _urlTextBox.SelectionStart = _urlTextBox.TextLength;
            }
        }
        catch
        {
            // Clipboard access can be temporarily busy; the explicit paste action remains available.
        }
    }

    private void UrlTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && !_isRunning && HasValidUrl())
        {
            e.SuppressKeyPress = true;
            _ = StartDownloadAsync();
        }
    }

    private void UpdateDownloadAvailability()
    {
        if (!_isRunning)
        {
            var hasValidUrl = HasValidUrl();
            _downloadButton.Enabled = hasValidUrl;

            if (hasValidUrl)
            {
                SetStatus("준비됨", "다운로드를 시작할 수 있습니다.", Accent, 0);
            }
            else if (string.IsNullOrWhiteSpace(_urlTextBox.Text))
            {
                SetStatus("준비됨", "링크를 붙여넣어 주세요.", SecondaryText, 0);
            }
            else
            {
                SetStatus("링크 확인 필요", "http 또는 https로 시작하는 주소를 입력해 주세요.", Color.OrangeRed, 0);
            }
        }
    }

    private bool HasValidUrl()
    {
        return Uri.TryCreate(_urlTextBox.Text.Trim(), UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static void OpenDownloadsFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DownloadsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { AppPaths.DownloadsDirectory },
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"다운로드 폴더를 열지 못했습니다.{Environment.NewLine}{ex.Message}",
                "영상 다운로드",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void DownloaderForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_isRunning)
        {
            _downloadCancellation?.Cancel();
            _downloadService.Cancel();
        }
    }

    private void FlashWindow()
    {
        if (!Focused)
        {
            NativeMethods.FlashWindow(Handle, true);
        }
    }

    private static Font CreateUiFont(float size, FontStyle style)
    {
        foreach (var family in new[] { "Noto Sans KR", "Noto Sans CJK KR", "Segoe UI" })
        {
            try
            {
                using var candidate = new Font(family, size, style, GraphicsUnit.Point);
                if (candidate.Name.Equals(family, StringComparison.OrdinalIgnoreCase))
                {
                    return new Font(family, size, style, GraphicsUnit.Point);
                }
            }
            catch
            {
                // Continue to the Windows UI fallback.
            }
        }

        var fallbackFamily = SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif;
        return new Font(fallbackFamily, size, style, GraphicsUnit.Point);
    }

    private static RoundedButton CreateButton(string text, Color backColor, Color foreColor, Color borderColor)
    {
        return new RoundedButton
        {
            Text = text,
            BackColor = backColor,
            ForeColor = foreColor,
            BorderColor = borderColor,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            CornerRadius = 8,
            UseVisualStyleBackColor = false
        };
    }
}

internal sealed class RoundedPanel : Panel
{
    public int CornerRadius { get; set; } = 10;
    public Color BorderColor { get; set; } = Color.Transparent;

    public RoundedPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = DrawingHelpers.RoundedRectangle(ClientRectangle, CornerRadius);
        using var pen = new Pen(BorderColor, 1F);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        using var path = DrawingHelpers.RoundedRectangle(ClientRectangle, CornerRadius);
        Region = new Region(path);
    }
}

internal sealed class RoundedButton : Button
{
    public int CornerRadius { get; set; } = 8;
    public Color BorderColor { get; set; } = Color.Transparent;

    public RoundedButton()
    {
        FlatAppearance.BorderSize = 0;
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var fillColor = Enabled ? BackColor : Color.FromArgb(44, 44, 50);
        var textColor = Enabled ? ForeColor : Color.FromArgb(145, 145, 152);

        using var path = DrawingHelpers.RoundedRectangle(ClientRectangle, CornerRadius);
        using var brush = new SolidBrush(fillColor);
        using var pen = new Pen(BorderColor, 1F);
        pevent.Graphics.FillPath(brush, path);
        pevent.Graphics.DrawPath(pen, path);
        TextRenderer.DrawText(
            pevent.Graphics,
            Text,
            Font,
            ClientRectangle,
            textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var path = DrawingHelpers.RoundedRectangle(ClientRectangle, CornerRadius);
        Region = new Region(path);
    }
}

internal sealed class ProgressTrack : Control
{
    private readonly System.Windows.Forms.Timer _timer;
    private int _value;
    private int _indeterminateOffset;
    private bool _isIndeterminate;

    public Color TrackColor { get; set; } = Color.DimGray;
    public Color ProgressColor { get; set; } = Color.Yellow;

    public int Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0, 100);
            Invalidate();
        }
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set
        {
            _isIndeterminate = value;
            if (value)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
                _indeterminateOffset = 0;
            }
            Invalidate();
        }
    }

    public ProgressTrack()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        _timer = new System.Windows.Forms.Timer { Interval = 28 };
        _timer.Tick += (_, _) =>
        {
            _indeterminateOffset = (_indeterminateOffset + 8) % Math.Max(1, Width + 120);
            Invalidate();
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var trackPath = DrawingHelpers.RoundedRectangle(ClientRectangle, Height / 2);
        using var trackBrush = new SolidBrush(TrackColor);
        e.Graphics.FillPath(trackBrush, trackPath);

        Rectangle fillRectangle;
        if (_isIndeterminate)
        {
            var segmentWidth = Math.Max(60, Width / 4);
            var x = _indeterminateOffset - segmentWidth;
            fillRectangle = new Rectangle(x, 0, segmentWidth, Height);
        }
        else
        {
            var width = (int)Math.Round(Width * (_value / 100D));
            fillRectangle = new Rectangle(0, 0, width, Height);
        }

        if (fillRectangle.Width > 0)
        {
            var state = e.Graphics.Save();
            e.Graphics.SetClip(trackPath);
            using var fillBrush = new SolidBrush(ProgressColor);
            e.Graphics.FillRectangle(fillBrush, fillRectangle);
            e.Graphics.Restore(state);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class DrawingHelpers
{
    public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 1 || bounds.Height <= 1)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var safeRadius = Math.Max(1, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
        var diameter = safeRadius * 2;
        var adjusted = new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);

        path.AddArc(adjusted.Left, adjusted.Top, diameter, diameter, 180, 90);
        path.AddArc(adjusted.Right - diameter, adjusted.Top, diameter, diameter, 270, 90);
        path.AddArc(adjusted.Right - diameter, adjusted.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(adjusted.Left, adjusted.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool FlashWindow(IntPtr hWnd, bool bInvert);
}
