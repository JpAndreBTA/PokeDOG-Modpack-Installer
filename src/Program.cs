using System.Drawing;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Any(arg => arg.StartsWith("--", StringComparison.OrdinalIgnoreCase)))
        {
            return await RunCliAsync(args);
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerForm());
        return 0;
    }

    private static async Task<int> RunCliAsync(string[] args)
    {
        var options = InstallerOptions.Parse(args);
        var log = new ConsoleProgressLog();
        try
        {
            await InstallerEngine.RunAsync(options, log, CancellationToken.None);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}

internal sealed class InstallerForm : Form
{
    private readonly TextBox _targetBox = new();
    private readonly TextBox _manifestBox = new();
    private readonly TextBox _payloadBox = new();
    private readonly TextBox _logBox = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Button _installButton = new();
    private readonly Button _checkButton = new();
    private readonly Button _targetButton = new();
    private readonly Button _manifestButton = new();
    private readonly Button _payloadButton = new();
    private readonly CancellationTokenSource _disposeToken = new();

    public InstallerForm()
    {
        Text = "PokeDOG Modpack Installer";
        MinimumSize = new Size(860, 620);
        Size = new Size(980, 700);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(14, 18, 24);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);
        Icon = LoadIconSafe();

        var header = new Panel { Dock = DockStyle.Top, Height = 126, BackColor = Color.FromArgb(20, 28, 38) };
        Controls.Add(header);

        var logo = new PictureBox
        {
            Size = new Size(72, 72),
            Location = new Point(24, 26),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = Icon?.ToBitmap()
        };
        header.Controls.Add(logo);

        var title = new Label
        {
            Text = "PokeDOG Modpack Installer",
            Location = new Point(112, 24),
            Size = new Size(680, 36),
            Font = new Font("Segoe UI", 20f, FontStyle.Bold),
            ForeColor = Color.White
        };
        header.Controls.Add(title);

        var subtitle = new Label
        {
            Text = "Instala, repara e atualiza o modpack, o Client Guard e dependencias gerenciadas.",
            Location = new Point(116, 66),
            Size = new Size(760, 26),
            ForeColor = Color.FromArgb(185, 205, 220)
        };
        header.Controls.Add(subtitle);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 3,
            RowCount = 8,
            BackColor = BackColor
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        Controls.Add(body);

        AddRow(body, 0, "Destino", _targetBox, _targetButton, "Escolher");
        AddRow(body, 1, "Manifesto", _manifestBox, _manifestButton, "Abrir");
        AddRow(body, 2, "Payload", _payloadBox, _payloadButton, "Abrir");

        _targetBox.Text = DefaultTargetRoot();
        _manifestBox.Text = InstallerPaths.FindDefaultManifest();
        _payloadBox.Text = InstallerPaths.FindDefaultPayload();

        _checkButton.Text = "Verificar";
        _checkButton.Dock = DockStyle.Fill;
        _checkButton.BackColor = Color.FromArgb(47, 58, 75);
        _checkButton.ForeColor = Color.White;
        _checkButton.FlatStyle = FlatStyle.Flat;
        _checkButton.Click += async (_, _) => await RunInstallAsync(dryRun: true);
        body.Controls.Add(_checkButton, 1, 4);

        _installButton.Text = "Instalar / Atualizar";
        _installButton.Dock = DockStyle.Fill;
        _installButton.BackColor = Color.FromArgb(220, 58, 58);
        _installButton.ForeColor = Color.White;
        _installButton.FlatStyle = FlatStyle.Flat;
        _installButton.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _installButton.Click += async (_, _) => await RunInstallAsync(dryRun: false);
        body.Controls.Add(_installButton, 2, 4);

        _logBox.Dock = DockStyle.Fill;
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.BackColor = Color.FromArgb(9, 12, 17);
        _logBox.ForeColor = Color.FromArgb(218, 232, 238);
        _logBox.BorderStyle = BorderStyle.FixedSingle;
        body.Controls.Add(_logBox, 0, 6);
        body.SetColumnSpan(_logBox, 3);

        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Style = ProgressBarStyle.Continuous;
        body.Controls.Add(_progressBar, 0, 7);
        body.SetColumnSpan(_progressBar, 3);

        _targetButton.Click += (_, _) => PickFolder(_targetBox);
        _manifestButton.Click += (_, _) => PickFile(_manifestBox, "Manifestos JSON (*.json)|*.json|Todos (*.*)|*.*");
        _payloadButton.Click += (_, _) => PickFile(_payloadBox, "Payload ZIP (*.zip)|*.zip|Todos (*.*)|*.*");

        AppendLog("Pronto. Se o manifesto local nao existir, o instalador usa o manifesto embutido.");
        AppendLog("O Client Guard verifica atualizacao ao entrar no servidor: versao, manifesto e hashes precisam bater com a politica server-side.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposeToken.Cancel();
            _disposeToken.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task RunInstallAsync(bool dryRun)
    {
        SetBusy(true);
        _progressBar.Value = 0;
        _logBox.Clear();
        try
        {
            var options = new InstallerOptions(
                _manifestBox.Text.Trim(),
                _targetBox.Text.Trim(),
                dryRun,
                _payloadBox.Text.Trim());
            var log = new FormProgressLog(AppendLog, value =>
            {
                _progressBar.Value = Math.Max(0, Math.Min(100, value));
            });
            await InstallerEngine.RunAsync(options, log, _disposeToken.Token);
            AppendLog(dryRun ? "Verificacao concluida." : "Instalacao/atualizacao concluida.");
        }
        catch (Exception ex)
        {
            AppendLog("ERRO: " + ex.Message);
            MessageBox.Show(this, ex.Message, "PokeDOG Modpack Installer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _installButton.Enabled = !busy;
        _checkButton.Enabled = !busy;
        _targetButton.Enabled = !busy;
        _manifestButton.Enabled = !busy;
        _payloadButton.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void AppendLog(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(line));
            return;
        }
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
    }

    private static void AddRow(TableLayoutPanel body, int row, string labelText, TextBox box, Button button, string buttonText)
    {
        var label = new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(220, 230, 238)
        };
        body.Controls.Add(label, 0, row);

        box.Dock = DockStyle.Fill;
        box.BackColor = Color.FromArgb(26, 32, 42);
        box.ForeColor = Color.White;
        box.BorderStyle = BorderStyle.FixedSingle;
        body.Controls.Add(box, 1, row);

        button.Text = buttonText;
        button.Dock = DockStyle.Fill;
        button.BackColor = Color.FromArgb(47, 58, 75);
        button.ForeColor = Color.White;
        button.FlatStyle = FlatStyle.Flat;
        body.Controls.Add(button, 2, row);
    }

    private static void PickFolder(TextBox target)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Escolha a pasta do Minecraft/modpack",
            SelectedPath = Directory.Exists(target.Text) ? target.Text : DefaultTargetRoot()
        };
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            target.Text = dialog.SelectedPath;
        }
    }

    private static void PickFile(TextBox target, string filter)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = filter,
            FileName = target.Text
        };
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            target.Text = dialog.FileName;
        }
    }

    private static string DefaultTargetRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var minecraft = Path.Combine(appData, ".minecraft");
        return Directory.Exists(minecraft) ? minecraft : AppContext.BaseDirectory;
    }

    private static Icon? LoadIconSafe()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "assets", "pokedog.ico");
            if (File.Exists(path))
            {
                return new Icon(path);
            }
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("pokedog.ico");
            return stream == null ? null : new Icon(stream);
        }
        catch
        {
            return null;
        }
    }
}

internal static class InstallerEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task RunAsync(InstallerOptions options, IInstallerLog log, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var thisVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        var targetRoot = Path.GetFullPath(options.TargetRoot);
        var backupRoot = Path.Combine(targetRoot, ".pokedog-backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var manifest = await LoadManifestAsync(options.Manifest, http, log, cancellationToken);

        log.Write($"PokeDOG Modpack Installer {thisVersion}");
        log.Write($"Destino: {targetRoot}");
        log.Write($"Pack: {manifest.PackVersion} | Guard: {manifest.GuardVersion}");
        log.Write(options.DryRun ? "Modo verificacao: nada sera alterado." : "Modo atualizacao: backups serao criados antes de substituir arquivos.");

        if (!options.DryRun)
        {
            Directory.CreateDirectory(targetRoot);
        }

        if (await MaybeUpdateInstallerAsync(manifest, thisVersion, targetRoot, http, options.DryRun, log, cancellationToken))
        {
            return;
        }

        var payloadPresent = !string.IsNullOrWhiteSpace(options.PayloadZip) && File.Exists(options.PayloadZip);
        if (payloadPresent)
        {
            await ExtractPayloadAsync(options.PayloadZip, targetRoot, backupRoot, options.DryRun, log, cancellationToken);
        }
        else
        {
            log.Write("Payload local nao encontrado. O instalador tentara usar apenas URLs do manifesto.");
        }

        await ApplyManagedFilesAsync(manifest, targetRoot, backupRoot, http, options.DryRun, payloadPresent, log, cancellationToken);
        log.ReportProgress(100);
    }

    private static async Task<PokeDogManifest> LoadManifestAsync(string source, HttpClient http, IInstallerLog log, CancellationToken cancellationToken)
    {
        string json;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            log.Write($"Baixando manifesto: {source}");
            json = await http.GetStringAsync(uri, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(source) && File.Exists(source))
        {
            log.Write($"Manifesto local: {source}");
            json = await File.ReadAllTextAsync(source, cancellationToken);
        }
        else
        {
            log.Write("Manifesto local nao encontrado. Usando manifesto embutido do instalador.");
            await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("pokedog_manifest.default.json")
                ?? throw new FileNotFoundException("Manifesto local ausente e manifesto embutido nao encontrado.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            json = await reader.ReadToEndAsync(cancellationToken);
        }

        var manifest = JsonSerializer.Deserialize<PokeDogManifest>(json, JsonOptions);
        if (manifest == null)
        {
            throw new InvalidOperationException("Manifesto vazio ou invalido.");
        }
        return manifest;
    }

    private static async Task<bool> MaybeUpdateInstallerAsync(PokeDogManifest manifest, string thisVersion, string targetRoot, HttpClient http, bool dryRun, IInstallerLog log, CancellationToken cancellationToken)
    {
        if (manifest.Installer is not { Url.Length: > 0, Sha256.Length: > 0 } installer ||
            !IsNewerVersion(installer.Version, thisVersion))
        {
            return false;
        }

        log.Write($"Nova versao do instalador disponivel: {installer.Version}");
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
        {
            throw new InvalidOperationException("Nao foi possivel localizar o executavel atual para auto-update.");
        }
        var destination = currentExe + ".update";
        await DownloadAndVerifyAsync(installer.Url, installer.Sha256, destination, http, dryRun, log, cancellationToken);
        if (dryRun)
        {
            return false;
        }
        ScheduleSelfReplace(currentExe, destination, log);
        log.Write("Instalador atualizado. Reiniciando para aplicar a nova versao.");
        Environment.Exit(0);
        return true;
    }

    private static void ScheduleSelfReplace(string currentExe, string updateExe, IInstallerLog log)
    {
        var script = Path.Combine(Path.GetTempPath(), "pokedog-installer-self-update-" + Guid.NewGuid().ToString("N") + ".cmd");
        var content = $"""
@echo off
timeout /t 2 /nobreak >nul
copy /y "{updateExe}" "{currentExe}" >nul
del /f /q "{updateExe}" >nul 2>nul
start "" "{currentExe}"
del /f /q "%~f0" >nul 2>nul
""";
        File.WriteAllText(script, content, Encoding.ASCII);
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c \"" + script + "\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        log.Write("Auto-update agendado para substituir o executavel apos fechar.");
    }

    private static async Task ExtractPayloadAsync(string zipPath, string targetRoot, string backupRoot, bool dryRun, IInstallerLog log, CancellationToken cancellationToken)
    {
        log.Write($"Payload local: {zipPath}");
        using var archive = ZipFile.OpenRead(zipPath);
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        var done = 0;
        var plannedUpdates = 0;
        foreach (var entry in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(entry.FullName);
            var destination = Path.Combine(targetRoot, relativePath);
            var needsUpdate = !File.Exists(destination) || await Sha256FileAsync(destination, cancellationToken) != await Sha256ZipEntryAsync(entry, cancellationToken);
            if (!needsUpdate)
            {
                done++;
                log.ReportProgress(done * 80 / Math.Max(1, files.Count));
                continue;
            }

            plannedUpdates++;
            if (!dryRun || plannedUpdates <= 40)
            {
                log.Write((dryRun ? "VERIFICAR " : "ATUALIZAR ") + relativePath);
            }
            if (!dryRun)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    BackupFile(destination, backupRoot, targetRoot);
                }
                entry.ExtractToFile(destination, true);
            }
            done++;
            log.ReportProgress(done * 80 / Math.Max(1, files.Count));
        }
        if (dryRun && plannedUpdates > 40)
        {
            log.Write($"... mais {plannedUpdates - 40} arquivos seriam verificados/atualizados pelo payload.");
        }
        log.Write(dryRun ? $"Payload verificado: {plannedUpdates} arquivo(s) precisam instalar/atualizar." : $"Payload aplicado: {plannedUpdates} arquivo(s) instalados/atualizados.");
    }

    private static async Task ApplyManagedFilesAsync(PokeDogManifest manifest, string targetRoot, string backupRoot, HttpClient http, bool dryRun, bool payloadPresent, IInstallerLog log, CancellationToken cancellationToken)
    {
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(file.Path);
            var destination = Path.Combine(targetRoot, relativePath);

            if (file.Policy.Equals("delete", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(destination))
                {
                    log.Write($"REMOVER {relativePath}");
                    if (!dryRun)
                    {
                        BackupFile(destination, backupRoot, targetRoot);
                        File.Delete(destination);
                    }
                }
                continue;
            }

            if (file.Policy.Equals("preserve-if-exists", StringComparison.OrdinalIgnoreCase) && File.Exists(destination))
            {
                log.Write($"PRESERVAR {relativePath}");
                continue;
            }

            var currentHash = File.Exists(destination) ? await Sha256FileAsync(destination, cancellationToken) : "";
            if (currentHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                log.Write($"OK {relativePath}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(file.Url) || file.Url.Contains("example.invalid", StringComparison.OrdinalIgnoreCase))
            {
                if (dryRun && payloadPresent)
                {
                    log.Write($"OK {relativePath} sera fornecido pelo payload local.");
                    continue;
                }
                throw new InvalidOperationException($"{relativePath} precisa atualizar, mas nao ha URL real no manifesto. Coloque o cobbleverse_payload.zip junto do instalador ou publique o manifesto com URL HTTPS/GitHub.");
            }

            log.Write($"BAIXAR {relativePath}");
            if (dryRun)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination))
            {
                BackupFile(destination, backupRoot, targetRoot);
            }

            var temp = destination + ".pokedog-download";
            await DownloadAndVerifyAsync(file.Url, file.Sha256, temp, http, false, log, cancellationToken);
            File.Move(temp, destination, true);
        }
    }

    private static async Task DownloadAndVerifyAsync(string url, string sha256, string destination, HttpClient http, bool dryRun, IInstallerLog log, CancellationToken cancellationToken)
    {
        if (dryRun)
        {
            log.Write($"DRY DOWNLOAD {url}");
            return;
        }

        await using (var input = await http.GetStreamAsync(url, cancellationToken))
        await using (var output = File.Create(destination))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        var actual = await Sha256FileAsync(destination, cancellationToken);
        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destination);
            throw new InvalidOperationException($"Hash invalido para {url}. Esperado {sha256}, recebido {actual}.");
        }
    }

    private static void BackupFile(string file, string backupRoot, string targetRoot)
    {
        var rel = Path.GetRelativePath(targetRoot, file);
        var backup = Path.Combine(backupRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(file, backup, true);
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string> Sha256ZipEntryAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar).Trim();
        if (Path.IsPathRooted(normalized) || normalized.Split(Path.DirectorySeparatorChar).Any(part => part == ".."))
        {
            throw new InvalidOperationException($"Caminho inseguro no manifesto/payload: {path}");
        }
        return normalized;
    }

    private static bool IsNewerVersion(string candidate, string current)
    {
        return Version.TryParse(candidate, out var c) && Version.TryParse(current, out var now) && c > now;
    }
}

internal interface IInstallerLog
{
    void Write(string line);
    void ReportProgress(int percent);
}

internal sealed class ConsoleProgressLog : IInstallerLog
{
    public void Write(string line) => Console.WriteLine(line);
    public void ReportProgress(int percent) { }
}

internal sealed class FormProgressLog(Action<string> write, Action<int> progress) : IInstallerLog
{
    public void Write(string line) => write(line);
    public void ReportProgress(int percent) => progress(percent);
}

internal sealed record InstallerOptions(string Manifest, string TargetRoot, bool DryRun, string PayloadZip)
{
    public static InstallerOptions Parse(string[] args)
    {
        var manifest = InstallerPaths.FindDefaultManifest();
        var target = Directory.GetCurrentDirectory();
        var payload = InstallerPaths.FindDefaultPayload();
        var dryRun = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--manifest" when i + 1 < args.Length:
                    manifest = args[++i];
                    break;
                case "--target" when i + 1 < args.Length:
                    target = args[++i];
                    break;
                case "--payload" when i + 1 < args.Length:
                    payload = args[++i];
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
            }
        }
        return new InstallerOptions(manifest, target, dryRun, payload);
    }
}

internal sealed record PokeDogManifest(
    string PackVersion,
    string GuardVersion,
    InstallerUpdate? Installer,
    IReadOnlyList<ManifestFile> Files
);

internal sealed record InstallerUpdate(string Version, string Url, string Sha256);

internal sealed record ManifestFile(
    string Path,
    string Sha256,
    long Size,
    string Url,
    string Policy
);

internal static class InstallerPaths
{
    private const string RemoteManifestUrl = "https://github.com/JpAndreBTA/PokeDOG-Modpack-Installer/raw/refs/heads/main/pokedog_manifest.json";

    public static string FindDefaultManifest()
    {
        return RemoteManifestUrl;
    }

    public static string FindDefaultPayload()
    {
        return FirstExisting(
            Path.Combine(AppContext.BaseDirectory, "cobbleverse_payload.zip"),
            Path.Combine(AppContext.BaseDirectory, "PokeDOG", "cobbleverse_payload.zip"),
            Path.Combine(AppContext.BaseDirectory, "PokeDOG", "PokeDOG_Cliente", "cobbleverse_payload.zip"));
    }

    private static string FirstExisting(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }
        return paths[0];
    }

    private static string FirstExistingOrRemote(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }
        return RemoteManifestUrl;
    }
}
