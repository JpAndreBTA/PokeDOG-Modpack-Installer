using System.Drawing;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(arg => arg.Equals("--list-instances", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var instance in MinecraftInstanceLocator.FindInstances())
            {
                Console.WriteLine(instance);
            }
            return 0;
        }

        if (args.Any(arg => arg.StartsWith("--", StringComparison.OrdinalIgnoreCase)))
        {
            return RunCliAsync(args).GetAwaiter().GetResult();
        }

        ApplicationConfiguration.Initialize();
        InstallerPaths.FlushTransientCaches();
        if (TryGuiStartupUpdate().GetAwaiter().GetResult())
        {
            return 0;
        }
        Application.Run(new WebInstallerForm());
        return 0;
    }

    private static async Task<bool> TryGuiStartupUpdate()
    {
        try
        {
            var log = new ConsoleProgressLog();
            var updated = await InstallerEngine.TrySelfUpdateBeforeInstallAsync(
                InstallerPaths.FindDefaultManifest(),
                log,
                CancellationToken.None);
            if (updated)
            {
                MessageBox.Show(
                    "O instalador foi atualizado. Ele sera reaberto automaticamente.",
                    "PokeDOG Modpack Installer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return true;
            }
        }
        catch
        {
        }

        return false;
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

internal sealed class WebInstallerForm : Form
{
    private readonly WebView2 _web = new();
    private readonly CancellationTokenSource _disposeToken = new();
    private readonly ConcurrentQueue<object> _pendingWebMessages = new();
    private readonly System.Windows.Forms.Timer _webFlushTimer = new() { Interval = 35 };
    private bool _busy;
    private bool _fallbackOpened;
    private bool _startupUpdateChecked;
    private bool _receivedWebHandshake;
    private bool _lastVerificationUpToDate;

    public WebInstallerForm()
    {
        Text = "PokeDOG Modpack Installer";
        MinimumSize = new Size(720, 660);
        Size = new Size(760, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(7, 9, 15);
        Icon = LoadIconSafe();

        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);
        _webFlushTimer.Tick += async (_, _) => await FlushPendingWebMessagesAsync();
        Load += async (_, _) => await InitializeWebAsync();
        FormClosed += (_, _) =>
        {
            _disposeToken.Cancel();
            _webFlushTimer.Stop();
        };
    }

    private async Task InitializeWebAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PokeDOG",
                "ModpackInstaller",
                "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _web.EnsureCoreWebView2Async(environment);
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _web.NavigateToString(BuildInstallerHtml(GetBackgroundUri()));
            _ = MonitorWebHandshakeAsync();
        }
        catch (Exception ex)
        {
            OpenCompatibilityMode(ex);
        }
    }

    private async Task MonitorWebHandshakeAsync()
    {
        try
        {
            await Task.Delay(4000, _disposeToken.Token);
            if (!_receivedWebHandshake && !_fallbackOpened && !IsDisposed)
            {
                OpenCompatibilityMode(new InvalidOperationException("A interface moderna nao respondeu ao handshake inicial."));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OpenCompatibilityMode(Exception ex)
    {
        OpenCompatibilityMode(ex, null, null);
    }

    private void OpenCompatibilityMode(Exception ex, string? targetRoot, bool? autoRunDryRun)
    {
        if (_fallbackOpened)
        {
            return;
        }
        _fallbackOpened = true;
        MessageBox.Show(
            "Nao foi possivel abrir a interface moderna do PokeDOG Installer.\n\n"
            + ex.Message
            + "\n\nAbrindo o modo de compatibilidade para permitir instalar/atualizar mesmo assim.",
            "PokeDOG Modpack Installer",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        var fallback = new InstallerForm(targetRoot, autoRunDryRun);
        fallback.FormClosed += (_, _) =>
        {
            if (!IsDisposed)
            {
                Close();
            }
        };
        Hide();
        fallback.Show(this);
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            _receivedWebHandshake = true;
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : "";
            switch (type)
            {
                case "ready":
                    var initialInstances = MinecraftInstanceLocator.FindInstanceEntries();
                    if (!_startupUpdateChecked)
                    {
                        _startupUpdateChecked = true;
                        await SendAsync(new { type = "toast", title = "Verificando", message = "Checando atualizacao do instalador..." });
                        if (await TryStartupSelfUpdateAsync())
                        {
                            return;
                        }
                    }
                    await SendAsync(new
                    {
                        type = "init",
                        folder = InstallerUserSettings.GetPreferredMinecraftFolder(),
                        version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                        payloadMb = 494.8,
                        instances = initialInstances
                    });
                    break;
                case "close":
                    Close();
                    break;
                case "chooseFolder":
                    ChooseFolder();
                    break;
                case "autoDetect":
                    var instanceEntries = MinecraftInstanceLocator.FindInstanceEntries();
                    var detectedFolder = instanceEntries.FirstOrDefault()?.Path ?? InstallerUserSettings.GetPreferredMinecraftFolder();
                    InstallerUserSettings.SaveLastTargetRoot(detectedFolder);
                    await SendAsync(new
                    {
                        type = "folder",
                        folder = detectedFolder,
                        detectedCount = instanceEntries.Count,
                        instances = instanceEntries
                    });
                    break;
                case "selectDetectedInstance":
                    var selectedPath = root.TryGetProperty("selectedPath", out var selectedPathElement) ? selectedPathElement.GetString() : "";
                    if (!string.IsNullOrWhiteSpace(selectedPath))
                    {
                        InstallerUserSettings.SaveLastTargetRoot(selectedPath);
                        await SendAsync(new
                        {
                            type = "folder",
                            folder = selectedPath,
                            detectedCount = MinecraftInstanceLocator.FindInstanceEntries().Count,
                            instances = MinecraftInstanceLocator.FindInstanceEntries()
                        });
                    }
                    break;
                case "verify":
                    await RunInstallerFromWebAsync(root, dryRun: true);
                    break;
                case "install":
                    await RunInstallerFromWebAsync(root, dryRun: false, forceRepair: false);
                    break;
                case "repair":
                    await RunInstallerFromWebAsync(root, dryRun: false, forceRepair: true);
                    break;
                case "openCompatibility":
                    OpenCompatibilityMode(new InvalidOperationException("Fallback solicitado pela interface."));
                    break;
            }
        }
        catch (Exception ex)
        {
            await SendAsync(new { type = "error", message = ex.Message });
        }
    }

    private void OpenCompatibilityFromWeb(JsonElement root, bool dryRun)
    {
        var target = root.TryGetProperty("folder", out var folderElement) ? folderElement.GetString() : GetDefaultMinecraftFolder();
        if (!string.IsNullOrWhiteSpace(target))
        {
            InstallerUserSettings.SaveLastTargetRoot(target);
        }
        OpenCompatibilityMode(
            new InvalidOperationException(dryRun
                ? "Verificacao sera executada no modo de compatibilidade."
                : "Instalacao sera executada no modo de compatibilidade."),
            target,
            dryRun);
    }

    private async Task<bool> TryStartupSelfUpdateAsync()
    {
        try
        {
            var log = new WebProgressLog(_ => { }, _ => { });
            var updated = await InstallerEngine.TrySelfUpdateBeforeInstallAsync(
                InstallerPaths.FindDefaultManifest(),
                log,
                _disposeToken.Token);
            if (!updated)
            {
                return false;
            }

            await SendAsync(new { type = "toast", title = "Atualizando", message = "Nova versao encontrada. O instalador vai reiniciar." });
            await Task.Delay(900, _disposeToken.Token);
            Close();
            return true;
        }
        catch
        {
            await SendAsync(new { type = "toast", title = "Aviso", message = "Nao foi possivel verificar atualizacao agora. Continuando..." });
            return false;
        }
    }

    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Selecione a pasta da instancia do Minecraft/modpack",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(GetDefaultMinecraftFolder()) ? GetDefaultMinecraftFolder() : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            InstallerUserSettings.SaveLastTargetRoot(dialog.SelectedPath);
            _ = SendAsync(new
            {
                type = "folder",
                folder = dialog.SelectedPath,
                detectedCount = MinecraftInstanceLocator.FindInstanceEntries().Count,
                instances = MinecraftInstanceLocator.FindInstanceEntries()
            });
        }
    }

    private async Task RunInstallerFromWebAsync(JsonElement root, bool dryRun, bool forceRepair = false)
    {
        if (_busy)
        {
            await SendAsync(new { type = "toast", title = "Processando", message = "Aguarde a tarefa atual finalizar." });
            return;
        }

        var target = root.TryGetProperty("folder", out var folderElement) ? folderElement.GetString() : GetDefaultMinecraftFolder();
        if (string.IsNullOrWhiteSpace(target))
        {
            await SendAsync(new { type = "error", message = "Selecione a pasta do Minecraft antes de continuar." });
            return;
        }

        InstallerUserSettings.SaveLastTargetRoot(target);

        _busy = true;
        await SendAsync(new { type = dryRun ? "verifyStarted" : "installStarted", forceRepair });
        try
        {
            var installerUpdated = false;
            var actionsNeeded = false;
            var options = new InstallerOptions(InstallerPaths.FindDefaultManifest(), target, dryRun, InstallerPaths.FindDefaultPayload(), forceRepair);
            var log = new WebProgressLog(line =>
            {
                if (line.Contains("Instalador atualizado", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Auto-update agendado", StringComparison.OrdinalIgnoreCase))
                {
                    installerUpdated = true;
                }
                if (dryRun && InstallerEngine.LogLineSuggestsRepairOrUpdate(line))
                {
                    actionsNeeded = true;
                }
                EnqueueWebMessage(new { type = "log", line });
            }, percent => EnqueueWebMessage(new { type = "progress", percent }));
            await Task.Run(async () => await InstallerEngine.RunAsync(options, log, _disposeToken.Token), _disposeToken.Token);
            await FlushPendingWebMessagesAsync();
            if (dryRun)
            {
                _lastVerificationUpToDate = !actionsNeeded;
            }
            await SendAsync(new { type = dryRun ? "verified" : "installed", installerUpdated, forceRepair, upToDate = dryRun ? !actionsNeeded : _lastVerificationUpToDate });
        }
        catch (OperationCanceledException)
        {
            await SendAsync(new { type = "error", message = "Operacao cancelada." });
        }
        catch (Exception ex)
        {
            await SendAsync(new { type = "error", message = ex.Message });
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SendAsync(object message)
    {
        if (IsDisposed || _web.CoreWebView2 == null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(message);
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => _ = _web.CoreWebView2.ExecuteScriptAsync($"window.pokedogFromHost({json});")));
            return;
        }

        await _web.CoreWebView2.ExecuteScriptAsync($"window.pokedogFromHost({json});");
    }

    private void EnqueueWebMessage(object message)
    {
        if (IsDisposed)
        {
            return;
        }

        _pendingWebMessages.Enqueue(message);
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() =>
            {
                if (!_webFlushTimer.Enabled)
                {
                    _webFlushTimer.Start();
                }
            }));
            return;
        }

        if (!_webFlushTimer.Enabled)
        {
            _webFlushTimer.Start();
        }
    }

    private async Task FlushPendingWebMessagesAsync()
    {
        if (IsDisposed || _web.CoreWebView2 == null)
        {
            return;
        }

        while (_pendingWebMessages.TryDequeue(out var message))
        {
            await SendAsync(message);
        }

        if (_pendingWebMessages.IsEmpty)
        {
            _webFlushTimer.Stop();
        }
    }

    private static string GetDefaultMinecraftFolder()
    {
        return InstallerUserSettings.GetPreferredMinecraftFolder();
    }

    private static string GetBackgroundUri()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "pokedog-installer-ui");
        var outputPath = Path.Combine(outputDir, "background.png");
        try
        {
            Directory.CreateDirectory(outputDir);
            if (!File.Exists(outputPath))
            {
                using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("pokedog_background_main.png")
                    ?? Assembly.GetExecutingAssembly().GetManifestResourceStream("pokedog_background.png");
                if (input != null)
                {
                    using var output = File.Create(outputPath);
                    input.CopyTo(output);
                }
            }
            return File.Exists(outputPath) ? new Uri(outputPath).AbsoluteUri : "";
        }
        catch
        {
            return "";
        }
    }

    private static Icon? LoadIconSafe()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("pokedog.ico");
            return stream == null ? null : new Icon(stream);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildInstallerHtml(string backgroundUri)
    {
        return """
<!DOCTYPE html>
<html lang="pt-BR">
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <title>PokeDOG Modpack Installer</title>
  <script src="https://cdn.tailwindcss.com"></script>
  <link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.4.0/css/all.min.css">
  <link rel="preconnect" href="https://fonts.googleapis.com">
  <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
  <link href="https://fonts.googleapis.com/css2?family=Press+Start+2P&family=Silkscreen:wght@400;700&family=VT323&family=Plus+Jakarta+Sans:wght@400;600;800&display=swap" rel="stylesheet">
  <script>
    tailwind.config = { theme: { extend: { fontFamily: { pixel: ['Press Start 2P','monospace'], silkscreen: ['Silkscreen','monospace'], terminal: ['VT323','monospace'], sans: ['Plus Jakarta Sans','sans-serif'] }, colors: { pokeRed: '#E13030', pokeYellow: '#FACC15', launcherDark: '#0e121d' } } } };
  </script>
  <style>
    * { box-sizing: border-box; }
    ::-webkit-scrollbar { width: 8px; }
    ::-webkit-scrollbar-track { background: #090b11; }
    ::-webkit-scrollbar-thumb { background: #FACC15; border: 2px solid #090b11; }
    body { background: #07090f; overflow: hidden; user-select: none; }
    .bg-world { background-image: url('__BG__'), radial-gradient(circle at top, #141926 0%, #05070a 100%); filter: brightness(.35) contrast(1.1) blur(3px); transform: scale(1.03); }
    .pixel-border { border: 0; }
    .pixel-shadow { box-shadow: none; }
    .step { transition: opacity .22s ease, transform .22s ease; }
    .hidden-step { display: none; }
    .folder-input { min-width: 0; overflow: hidden; text-overflow: clip; white-space: nowrap; }
    .terminal-line { overflow-wrap: anywhere; }
    .absolute { position: absolute; } .relative { position: relative; } .fixed { position: fixed; }
    .inset-0 { inset: 0; } .top-4 { top: 1rem; } .right-4 { right: 1rem; } .bottom-6 { bottom: 1.5rem; } .right-6 { right: 1.5rem; }
    .z-0 { z-index: 0; } .z-10 { z-index: 10; } .z-20 { z-index: 20; } .z-50 { z-index: 50; }
    .flex { display: flex; } .grid { display: grid; } .hidden { display: none; }
    .flex-col { flex-direction: column; } .flex-grow { flex: 1 1 0%; } .flex-none { flex: none; }
    .items-center { align-items: center; } .justify-center { justify-content: center; } .justify-between { justify-content: space-between; }
    .text-center { text-align: center; } .text-left { text-align: left; } .text-right { text-align: right; }
    .mx-auto { margin-left: auto; margin-right: auto; } .mt-0\.5 { margin-top: .125rem; } .mt-1 { margin-top: .25rem; } .mt-2 { margin-top: .5rem; } .mb-3 { margin-bottom: .75rem; } .mb-4 { margin-bottom: 1rem; }
    .p-3 { padding: .75rem; } .p-4 { padding: 1rem; } .p-5 { padding: 1.25rem; } .px-1 { padding-left: .25rem; padding-right: .25rem; } .px-2 { padding-left: .5rem; padding-right: .5rem; } .px-3 { padding-left: .75rem; padding-right: .75rem; } .px-4 { padding-left: 1rem; padding-right: 1rem; } .px-5 { padding-left: 1.25rem; padding-right: 1.25rem; } .px-6 { padding-left: 1.5rem; padding-right: 1.5rem; } .py-0\.5 { padding-top: .125rem; padding-bottom: .125rem; } .py-2 { padding-top: .5rem; padding-bottom: .5rem; } .py-4 { padding-top: 1rem; padding-bottom: 1rem; } .pt-1 { padding-top: .25rem; } .pt-2 { padding-top: .5rem; } .pt-8 { padding-top: 2rem; } .pl-3 { padding-left: .75rem; } .pl-8 { padding-left: 2rem; } .pr-1 { padding-right: .25rem; } .pr-3 { padding-right: .75rem; }
    .gap-1 { gap: .25rem; } .gap-1\.5 { gap: .375rem; } .gap-2 { gap: .5rem; } .gap-2\.5 { gap: .625rem; } .gap-3 { gap: .75rem; } .gap-4 { gap: 1rem; }
    .space-y-1 > * + * { margin-top: .25rem; } .space-y-1\.5 > * + * { margin-top: .375rem; } .space-y-2 > * + * { margin-top: .5rem; } .space-y-3 > * + * { margin-top: .75rem; } .space-y-4 > * + * { margin-top: 1rem; } .space-y-5 > * + * { margin-top: 1.25rem; }
    .w-full { width: 100%; } .w-1\.5 { width: .375rem; } .h-1\.5 { height: .375rem; } .w-3\.5 { width: .875rem; } .h-3\.5 { height: .875rem; } .w-7 { width: 1.75rem; } .h-7 { height: 1.75rem; } .w-8 { width: 2rem; } .h-8 { height: 2rem; } .w-10 { width: 2.5rem; } .h-10 { height: 2.5rem; } .w-12 { width: 3rem; } .h-12 { height: 3rem; } .w-16 { width: 4rem; } .h-16 { height: 4rem; } .w-48 { width: 12rem; } .h-3 { height: .75rem; } .h-44 { height: 11rem; }
    .max-w-xs { max-width: 20rem; } .max-w-sm { max-width: 24rem; } .max-w-md { max-width: 28rem; } .max-w-lg { max-width: 32rem; } .max-w-2xl { max-width: 42rem; } .min-w-0 { min-width: 0; }
    .overflow-hidden { overflow: hidden; } .overflow-y-auto { overflow-y: auto; } .select-none { user-select: none; } .pointer-events-none { pointer-events: none; }
    .rounded { border-radius: .25rem; } .rounded-lg { border-radius: .5rem; } .rounded-full { border-radius: 9999px; } .rounded-sm { border-radius: .125rem; }
    .border { border-width: 1px; border-style: solid; } .border-2 { border-width: 2px; border-style: solid; } .border-t { border-top-width: 1px; border-top-style: solid; } .border-t-2 { border-top-width: 2px; border-top-style: solid; }
    .border-slate-950 { border-color: #020617; } .border-slate-900 { border-color: #0f172a; } .border-slate-800 { border-color: #1e293b; }
    .bg-cover { background-size: cover; } .bg-center { background-position: center; } .bg-launcherDark\/95 { background: rgba(14,18,29,.95); } .bg-slate-950 { background: #020617; } .bg-slate-950\/60 { background: rgba(2,6,23,.6); } .bg-slate-950\/80 { background: rgba(2,6,23,.8); } .bg-slate-900 { background: #0f172a; } .bg-slate-800 { background: #1e293b; } .bg-pokeYellow { background: #FACC15; } .bg-pokeRed\/10 { background: rgba(225,48,48,.1); } .bg-emerald-500\/20 { background: rgba(16,185,129,.2); } .bg-emerald-500 { background: #10b981; }
    .text-white { color: #fff; } .text-slate-950 { color: #020617; } .text-slate-600 { color: #475569; } .text-slate-500 { color: #64748b; } .text-slate-400 { color: #94a3b8; } .text-slate-300 { color: #cbd5e1; } .text-slate-200 { color: #e2e8f0; } .text-pokeYellow { color: #FACC15; } .text-pokeRed { color: #E13030; } .text-emerald-400 { color: #34d399; } .text-emerald-500 { color: #10b981; }
    .text-\[9px\] { font-size: 9px; } .text-\[10px\] { font-size: 10px; } .text-\[13px\] { font-size: 13px; } .text-xs { font-size: 12px; } .text-sm { font-size: 14px; } .text-lg { font-size: 18px; } .text-xl { font-size: 20px; }
    .font-pixel { font-family: 'Press Start 2P', Consolas, monospace; } .font-silkscreen { font-family: 'Silkscreen', Consolas, monospace; } .font-terminal { font-family: 'VT323', Consolas, monospace; } .font-bold { font-weight: 700; } .font-black { font-weight: 900; } .font-semibold { font-weight: 600; } .font-medium { font-weight: 500; } .italic { font-style: italic; }
    .tracking-wide { letter-spacing: 0; } .tracking-wider { letter-spacing: .05em; } .tracking-widest { letter-spacing: .1em; } .uppercase { text-transform: uppercase; } .leading-relaxed { line-height: 1.625; } .leading-normal { line-height: 1.5; }
    .transition-all, .transition-colors { transition: all .2s ease; } .duration-300 { transition-duration: .3s; } .opacity-0 { opacity: 0; } .opacity-50 { opacity: .5; } .opacity-75 { opacity: .75; } .opacity-100 { opacity: 1; }
    .cursor-pointer { cursor: pointer; } .cursor-not-allowed { cursor: not-allowed; }
    .bg-gradient-to-r { background: linear-gradient(90deg, #E13030, #FACC15); } .bg-gradient-to-br { background: linear-gradient(135deg, #10b981, #059669); }
    .drop-shadow-\[0_2px_0px_\#000\] { text-shadow: 0 2px 0 #000; }
    html, body { margin: 0 !important; width: 100vw !important; height: 100vh !important; min-height: 100vh !important; padding: 0 !important; overflow: hidden !important; background: #0e121d; }
    body { display: flex; align-items: stretch; justify-content: stretch; position: relative; color: #f1f5f9; font-family: 'Plus Jakarta Sans', Segoe UI, sans-serif; }
    main { width: 100vw; max-width: none; min-height: 100vh; height: 100vh; max-height: 100vh; display: flex; flex-direction: column; justify-content: space-between; position: relative; z-index: 10; overflow: hidden; background: rgba(14,18,29,.96); border-radius: 0; }
    #app-main { width: 100vw !important; max-width: none !important; min-height: 100vh !important; height: 100vh !important; max-height: 100vh !important; border-radius: 0 !important; margin: 0 !important; }
    header { padding: 26px 24px 0; display: flex; flex-direction: column; align-items: center; text-align: center; flex: 0 0 auto; }
    section { padding: 10px 52px; flex: 1 1 auto; min-height: 0; display: flex; flex-direction: column; justify-content: center; overflow-y: auto; overflow-x: hidden; }
    footer { padding: 16px 20px; background: rgba(2,6,23,.92); border-top: 2px solid #0f172a; display: flex; align-items: center; justify-content: space-between; gap: 16px; flex: 0 0 auto; position: sticky; bottom: 0; z-index: 20; }
    input { min-width: 0; }
    button { border: 2px solid #020617; font-family: 'Silkscreen', Consolas, monospace; }
    button:hover { filter: brightness(1.08); }
    button:disabled { opacity: .3; pointer-events: none; }
    #terminal-logs { background: #020617; color: #94a3b8; }
    #view-step-2 #terminal-logs { height: min(10rem, 24vh); }
    #view-step-1 .bg-slate-950\/80 { max-width: 580px; }
    #view-step-1 button { white-space: nowrap; }
    #input-folder { min-width: 285px; }
    #view-step-1 .space-y-5 > * + * { margin-top: .75rem; }
    .detected-shell { border-top: 1px solid rgba(15,23,42,.85); padding-top: 8px; }
    .detected-shell.hidden { display: none; }
    .detected-card { background: #07090f; border: 2px solid #0f172a; border-radius: 6px; overflow: hidden; }
    .detected-toggle { width: 100%; display: flex; align-items: center; justify-content: space-between; gap: 10px; background: transparent; color: #cbd5e1; padding: 8px 10px; font-family: 'Silkscreen', Consolas, monospace; font-size: 9px; text-transform: uppercase; border: 0; border-bottom: 1px solid rgba(15,23,42,.75); }
    .detected-toggle:last-of-type { border-bottom: 0; }
    .detected-toggle i { transition: transform .2s ease; }
    .detected-toggle.open i { transform: rotate(180deg); }
    .detected-panel { display: none; padding: 6px 8px; background: rgba(2,6,23,.35); border-bottom: 1px solid rgba(15,23,42,.75); }
    .detected-panel.open { display: block; }
    .detected-panel:last-child { border-bottom: 0; }
    .detected-scrollbox { width: 100%; max-height: 92px; overflow-x: hidden; overflow-y: auto; padding-right: 2px; }
    .detected-scrollbox::-webkit-scrollbar { width: 8px; height: 8px; }
    .detected-scrollbox::-webkit-scrollbar-track { background: #05070a; }
    .detected-scrollbox::-webkit-scrollbar-thumb { background: #FACC15; border: 2px solid #05070a; }
    .detected-list { display: flex; flex-direction: column; flex-wrap: nowrap; gap: 4px; min-width: 0; align-items: stretch; }
    .detected-item { width: 100%; min-width: 0; max-width: none; text-align: left; border: 2px solid #0f172a; background: linear-gradient(180deg, rgba(9,12,20,.98), rgba(2,6,23,.98)); border-radius: 6px; padding: 5px 7px; color: #cbd5e1; white-space: nowrap; overflow: hidden; }
    .detected-item.active { border-color: #FACC15; box-shadow: 0 0 0 1px rgba(250,204,21,.22) inset; }
    .detected-item-row { display: flex; align-items: center; gap: 6px; min-width: 0; }
    .detected-item-name { min-width: 0; overflow: hidden; text-overflow: ellipsis; font-family: 'Silkscreen', Consolas, monospace; font-size: 8px; color: #f8fafc; }
    .detected-item-desc { flex-shrink: 0; font-family: 'VT323', Consolas, monospace; font-size: 11px; color: #94a3b8; opacity: .92; }
    .detected-empty { font-family: 'VT323', Consolas, monospace; font-size: 15px; color: #64748b; }
    .launcher-group { width: 100%; min-width: 0; max-width: none; border: 1px solid rgba(15,23,42,.75); border-radius: 6px; overflow: hidden; background: rgba(2,6,23,.28); }
    .launcher-group-header { width: 100%; display: flex; align-items: center; justify-content: space-between; gap: 10px; background: transparent; color: #cbd5e1; border: 0; padding: 7px 9px; font-family: 'Silkscreen', Consolas, monospace; font-size: 8px; }
    .launcher-group-header i { transition: transform .2s ease; }
    .launcher-group-header.open i { transform: rotate(180deg); }
    .launcher-group-body { display: none; padding: 5px; border-top: 1px solid rgba(15,23,42,.75); }
    .launcher-group-body.open { display: block; }
    .download-stages { display: grid; grid-template-columns: repeat(5, minmax(0, 1fr)); gap: 8px; padding-top: 8px; border-top: 1px solid rgba(15,23,42,.8); }
    .stage-pill { min-width: 0; padding: 7px 6px; border: 2px solid #0f172a; border-radius: 4px; background: #07090f; color: #64748b; font-family: 'Silkscreen', Consolas, monospace; font-size: 9px; text-align: center; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .stage-pill.active { color: #FACC15; border-color: #FACC15; box-shadow: 0 0 0 1px rgba(250,204,21,.2) inset; }
    .stage-pill.done { color: #34d399; border-color: rgba(52,211,153,.55); }
    #toast-notification { transform: translateY(80px); }
    #toast-notification.translate-y-0 { transform: translateY(0); }
    #toast-notification.translate-y-20 { transform: translateY(80px); }
    @media (max-height: 680px) { header { padding-top: 18px; } section { padding-top: 8px; padding-bottom: 8px; } footer { padding-top: 12px; padding-bottom: 12px; } .w-16.h-16 { width: 3.25rem; height: 3.25rem; } #view-step-2 #terminal-logs { height: 8.5rem; } }
    @media (max-width: 760px) { section { padding-left: 32px; padding-right: 32px; } #input-folder { min-width: 220px; } .download-stages { grid-template-columns: repeat(3, minmax(0, 1fr)); } }
    @media (max-width: 640px) { section { padding-left: 24px; padding-right: 24px; } footer { flex-direction: column; } }
  </style>
</head>
<body class="text-gray-100 font-sans min-h-screen flex items-center justify-center p-4 relative">
  <div class="absolute inset-0 z-0 bg-cover bg-center bg-world"></div>
  <main id="app-main" class="w-full max-w-2xl bg-launcherDark/95 rounded-lg pixel-border pixel-shadow z-10 overflow-hidden relative flex flex-col min-h-[560px] justify-between">
    <header class="pt-8 px-6 flex flex-col items-center text-center">
      <div class="flex flex-col items-center gap-2 cursor-pointer select-none mb-3 group" onclick="playBeep(600,100)">
        <svg class="w-16 h-16 transform transition-transform group-hover:scale-105 duration-300" viewBox="0 0 100 100" fill="none" xmlns="http://www.w3.org/2000/svg">
          <path d="M15 15 H35 V35 H15 Z" fill="#E13030" stroke="#000" stroke-width="4"/><path d="M20 20 H30 V30 H20 Z" fill="#9e1e1e"/>
          <path d="M65 15 H85 V35 H65 Z" fill="#E13030" stroke="#000" stroke-width="4"/><path d="M70 20 H80 V30 H70 Z" fill="#9e1e1e"/>
          <circle cx="50" cy="55" r="30" fill="white" stroke="#000" stroke-width="5"/>
          <path d="M20 55 C20 38.43 33.43 25 50 25 C66.57 25 80 38.43 80 55 H20 Z" fill="#E13030" stroke="#000" stroke-width="5"/>
          <line x1="20" y1="55" x2="80" y2="55" stroke="#000" stroke-width="5"/>
          <circle cx="50" cy="55" r="10" fill="white" stroke="#000" stroke-width="4"/>
          <circle cx="50" cy="56" r="4" fill="#000"/><circle cx="46" cy="50" r="1.5" fill="#000"/><circle cx="50" cy="48" r="1.5" fill="#000"/><circle cx="54" cy="50" r="1.5" fill="#000"/>
        </svg>
        <div class="font-pixel tracking-tight text-xl flex items-center gap-1.5 mt-2">
          <span class="text-white drop-shadow-[0_2px_0px_#000]">POKE</span><span class="text-pokeYellow drop-shadow-[0_2px_0px_#000]">DOG</span>
        </div>
        <span class="font-silkscreen text-[10px] text-slate-500 tracking-widest mt-0.5">MODPACK INSTALLER</span>
      </div>

      <div class="w-48 relative flex items-center justify-between mt-1 mb-4">
        <div class="absolute left-0 right-0 h-[4px] bg-slate-950 border border-slate-800 -z-10"></div>
        <div id="steps-progress-bar" class="absolute left-0 h-[2px] bg-pokeYellow -z-10 transition-all duration-300" style="width:0%"></div>
        <div id="step-node-1" class="w-3.5 h-3.5 bg-pokeYellow border-2 border-slate-950 transition-all"></div>
        <div id="step-node-2" class="w-3.5 h-3.5 bg-slate-950 border-2 border-slate-800 transition-all"></div>
        <div id="step-node-3" class="w-3.5 h-3.5 bg-slate-950 border-2 border-slate-800 transition-all"></div>
        <div id="step-node-4" class="w-3.5 h-3.5 bg-slate-950 border-2 border-slate-800 transition-all"></div>
      </div>
    </header>

    <section class="px-6 md:px-10 flex-grow flex flex-col justify-center py-4">
      <div id="view-step-1" class="step space-y-5">
        <div class="text-center space-y-2">
          <h2 class="text-lg font-pixel text-white tracking-wide leading-relaxed">Instalar Modpack<br><span class="text-pokeYellow">PokeDOG v2</span></h2>
          <p class="font-silkscreen text-xs text-slate-400">Minecraft 1.21.1 + Fabric</p>
          <p class="text-xs text-slate-400 max-w-md mx-auto leading-relaxed pt-1">Este assistente instala, repara e atualiza mods, texturas, shaderpacks, configs e o Client Guard diretamente na sua pasta do Minecraft.</p>
        </div>
        <div class="bg-slate-950/80 border-2 border-slate-900 p-4 rounded-lg space-y-3 max-w-lg mx-auto w-full">
          <div class="flex justify-between items-center gap-3">
            <label class="block font-silkscreen text-[10px] text-slate-300 uppercase tracking-wide">Caminho da pasta do Minecraft:</label>
            <span class="bg-slate-900 text-slate-400 font-terminal text-xs uppercase px-2 py-0.5 rounded border border-slate-800">Windows</span>
          </div>
          <div class="flex flex-col sm:flex-row gap-2">
            <div class="relative flex-grow min-w-0">
              <span class="absolute inset-y-0 left-0 pl-3 flex items-center text-slate-600"><i class="fa-solid fa-folder-open text-xs"></i></span>
              <input id="input-folder" type="text" class="folder-input w-full bg-[#07090f] border-2 border-slate-900 rounded py-2 pl-8 pr-3 font-terminal text-sm text-slate-300 focus:outline-none focus:border-pokeYellow/80 transition-all" />
            </div>
            <button onclick="send('chooseFolder')" class="bg-slate-900 hover:bg-slate-800 text-slate-200 hover:text-white px-4 py-2 rounded font-silkscreen text-[10px] transition-all flex items-center justify-center gap-1 border-2 border-slate-950 active:scale-95">
              <i class="fa-solid fa-folder-tree text-pokeYellow"></i> ABRIR
            </button>
            <button onclick="send('autoDetect')" class="bg-slate-900 hover:bg-slate-800 text-slate-200 hover:text-white px-4 py-2 rounded font-silkscreen text-[10px] transition-all flex items-center justify-center gap-1 border-2 border-slate-950 active:scale-95">
              <i class="fa-solid fa-wand-magic-sparkles text-pokeYellow"></i> AUTO DETETAR
            </button>
          </div>
          <div id="detected-shell" class="detected-shell hidden">
            <div class="flex justify-between items-center gap-3">
              <span class="font-silkscreen text-[10px] text-slate-400 uppercase tracking-wide">Instancias e launchers detectados</span>
              <span id="detected-summary" class="font-terminal text-xs text-slate-500">0 instancias</span>
            </div>
            <div class="detected-card">
              <button id="toggle-detected-instances" type="button" class="detected-toggle" onclick="toggleDetectedSection('instances')">
                <span>Instancias achadas</span>
                <i class="fa-solid fa-chevron-down text-pokeYellow"></i>
              </button>
              <div id="detected-panel-instances" class="detected-panel">
                <div class="detected-scrollbox">
                  <div id="detected-instances-list" class="detected-list"></div>
                </div>
              </div>
              <button id="toggle-detected-launchers" type="button" class="detected-toggle" onclick="toggleDetectedSection('launchers')">
                <span>Launchers e instalacoes detectados</span>
                <i class="fa-solid fa-chevron-down text-pokeYellow"></i>
              </button>
              <div id="detected-panel-launchers" class="detected-panel">
                <div class="detected-scrollbox">
                  <div id="detected-launchers-list" class="detected-list"></div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>

      <div id="view-step-2" class="step space-y-4 hidden-step">
        <div class="text-center space-y-1">
          <h2 class="font-pixel text-sm text-white">Analise de Ficheiros</h2>
          <p class="font-silkscreen text-[10px] text-slate-400">Analisando o diretorio de destino e preparando download.</p>
        </div>
        <div class="space-y-1.5 max-w-lg mx-auto w-full">
          <div class="flex justify-between items-center px-1">
            <span class="font-silkscreen text-[10px] text-slate-500 uppercase tracking-wider flex items-center gap-1"><span id="status-pulse" class="w-1.5 h-1.5 bg-pokeYellow rounded-full"></span> Status</span>
            <span id="log-counter" class="font-terminal text-xs text-slate-500">0 eventos</span>
          </div>
          <div class="relative bg-slate-950 border-2 border-slate-900 rounded p-3 overflow-hidden">
            <div id="terminal-logs" class="h-44 overflow-y-auto font-terminal text-[13px] text-slate-400 space-y-1 pr-1 leading-normal">
              <div class="text-slate-600 terminal-line">[CONSOLE] Pronto para iniciar verificacao. Clique em "Iniciar Verificacao".</div>
            </div>
          </div>
        </div>
        <div class="flex justify-center">
          <div class="flex flex-wrap justify-center gap-2">
            <button id="btn-start-verify" onclick="startVerify()" class="bg-slate-900 hover:bg-slate-800 text-white font-silkscreen text-[10px] px-4 py-2 rounded transition-all flex items-center gap-2 border-2 border-slate-950">
              <i class="fa-solid fa-magnifying-glass text-pokeYellow"></i> INICIAR VERIFICACAO
            </button>
            <button id="btn-start-repair" onclick="startRepair()" class="bg-slate-950 text-slate-500 font-silkscreen text-[10px] px-4 py-2 rounded transition-all flex items-center gap-2 border-2 border-slate-900 opacity-50 cursor-not-allowed" disabled>
              <i class="fa-solid fa-screwdriver-wrench text-pokeRed"></i> REPARAR LIMPO
            </button>
          </div>
        </div>
      </div>

      <div id="view-step-3" class="step space-y-5 hidden-step">
        <div class="text-center space-y-1">
          <h2 class="font-pixel text-sm text-white">Descarregando Pacotes</h2>
          <p id="dl-current-file" class="font-terminal text-sm text-slate-400">Conectando ao GitHub PokeDOG...</p>
        </div>
        <div class="bg-slate-950/80 border-2 border-slate-900 p-4 rounded-lg max-w-lg mx-auto space-y-3 w-full">
          <div class="space-y-2">
            <div class="flex justify-between font-silkscreen text-[10px] text-slate-400"><span>Sincronizando Modpack</span><span id="dl-percentage" class="text-pokeYellow font-bold">0%</span></div>
            <div class="w-full h-3 bg-[#07090f] border-2 border-slate-900 rounded overflow-hidden p-[1px]"><div id="dl-progress-bar" class="h-full bg-gradient-to-r from-pokeRed to-pokeYellow rounded-sm transition-all duration-100 ease-out" style="width:0%"></div></div>
            <div id="dl-current-action" class="font-terminal text-xs text-slate-300 min-h-[18px]">Aguardando inicio da sincronizacao...</div>
            <div class="flex justify-between font-terminal text-xs text-slate-500"><span id="dl-loaded-mb">0.0 MB / 494.8 MB</span><span>Tempo Restante: <span id="dl-eta" class="text-slate-400">Calculando...</span></span></div>
          </div>
          <div class="grid grid-cols-2 gap-2 pt-2 border-t border-slate-900/60 font-terminal text-xs">
            <div class="text-left"><span class="block text-slate-500 text-[10px] font-silkscreen">Origem</span><span id="dl-speed" class="text-sm font-bold text-white">GitHub/Local</span></div>
            <div class="text-right"><span class="block text-slate-500 text-[10px] font-silkscreen">Anticheat</span><span class="text-sm font-bold text-emerald-400">Guard Ativo</span></div>
          </div>
          <div class="download-stages">
            <div id="stage-manifest" class="stage-pill">Manifesto</div>
            <div id="stage-updater" class="stage-pill">Atualizador</div>
            <div id="stage-payload" class="stage-pill">Cobbleverse</div>
            <div id="stage-files" class="stage-pill">Arquivos</div>
            <div id="stage-clean" class="stage-pill">Limpeza</div>
          </div>
        </div>
      </div>

      <div id="view-step-4" class="step space-y-5 text-center hidden-step">
        <div class="max-w-md mx-auto space-y-3">
          <div class="relative w-12 h-12 mx-auto flex items-center justify-center"><div class="absolute inset-0 bg-emerald-500/20 rounded-full animate-ping opacity-75"></div><div class="relative w-10 h-10 bg-gradient-to-br from-emerald-500 to-emerald-600 border-2 border-slate-950 rounded flex items-center justify-center shadow-lg"><i class="fa-solid fa-check text-white text-md"></i></div></div>
          <div class="space-y-1"><h2 class="font-pixel text-sm text-white tracking-wide">Tudo Pronto!</h2><p id="done-message" class="text-xs text-slate-400">O modpack PokeDOG foi instalado e atualizado no seu diretorio de jogo.</p></div>
          <div id="installer-update-alert" class="hidden bg-pokeYellow text-slate-950 p-3 rounded border-2 border-slate-950 text-left max-w-sm mx-auto">
            <div class="font-silkscreen text-[10px] font-bold flex items-center gap-2"><i class="fa-solid fa-rotate"></i><span>Instalador atualizado</span></div>
            <p class="font-terminal text-sm mt-1">Feche esta janela. O PokeDOG-Modpack-Installer sera atualizado e aberto novamente; clique em Instalar/Atualizar outra vez para concluir os mods.</p>
          </div>
          <div class="bg-slate-950/80 p-3 rounded-lg border-2 border-slate-900 space-y-1 text-left max-w-xs mx-auto font-terminal text-xs text-slate-400">
            <div class="flex justify-between"><span class="font-silkscreen text-[9px] text-slate-500">MODPACK:</span><span class="text-emerald-400 font-bold">PokeDOG v2</span></div>
            <div class="flex justify-between"><span class="font-silkscreen text-[9px] text-slate-500">STATUS:</span><span class="text-slate-300">Sincronizado</span></div>
            <div class="flex justify-between"><span class="font-silkscreen text-[9px] text-slate-500">GUARD:</span><span class="text-slate-300">Obrigatorio</span></div>
          </div>
          <p class="text-xs text-slate-500 font-medium italic max-w-sm mx-auto leading-normal">Abra o launcher do Minecraft e entre com o perfil do PokeDOG atualizado.</p>
        </div>
      </div>
    </section>

    <footer class="p-5 bg-slate-950/60 border-t-2 border-slate-900 flex flex-col sm:flex-row items-center justify-between gap-4">
      <div class="text-[9px] font-silkscreen text-slate-500 tracking-wider text-center sm:text-left"><span>INSTALADOR: PokeDOG Studio</span><span class="mx-1.5">-</span><span>CREDITOS JP ANDRE</span></div>
      <div class="flex gap-2.5 w-full sm:w-auto font-silkscreen text-[10px]">
        <button id="btn-back" disabled onclick="handleBack()" class="flex-grow sm:flex-none bg-slate-900 hover:bg-slate-800 text-slate-300 font-bold px-5 py-2 rounded transition-colors disabled:opacity-30 disabled:pointer-events-none border-2 border-slate-950">VOLTAR</button>
        <button id="btn-next" onclick="handleNext()" class="flex-grow sm:flex-none bg-pokeYellow hover:bg-yellow-500 text-slate-950 font-black px-6 py-2 rounded transition-colors shadow-md flex items-center justify-center gap-1 border-2 border-slate-950 active:scale-95"><span>AVANCAR</span> <i class="fa-solid fa-chevron-right text-[9px]"></i></button>
      </div>
    </footer>
  </main>

  <div id="toast-notification" class="fixed top-5 right-5 z-50 w-[calc(100vw-2.5rem)] sm:w-auto transform -translate-y-16 opacity-0 transition-all duration-300 pointer-events-none origin-top-right">
    <div class="bg-launcherDark border-2 border-slate-900 p-4 rounded-lg shadow-2xl flex items-center gap-3 max-w-sm">
      <div class="w-8 h-8 rounded bg-pokeRed/10 flex items-center justify-center text-pokeRed" id="toast-icon"><i class="fa-solid fa-bell"></i></div>
      <div><h4 id="toast-title" class="font-silkscreen text-[10px] font-bold text-slate-200">Aviso</h4><p id="toast-message" class="font-terminal text-sm text-slate-400 mt-0.5">Mensagem do sistema.</p></div>
    </div>
  </div>

  <script>
    const audioCtx = new (window.AudioContext || window.webkitAudioContext)();
    let activeStep = 1, isVerified = false, isInstalling = false, repairMode = false, verificationUpToDate = false, logCount = 0, payloadMb = 494.8, toastTimeout = null, installWatchdog = null, verifyWatchdog = null;
    let detectedInstances = [];
    let customPathPinned = false;

    function send(type, extra) {
      const input = document.getElementById('input-folder');
      window.chrome.webview.postMessage(Object.assign({ type, folder: input ? input.value : '' }, extra || {}));
    }

    function playBeep(freq, duration) {
      try {
        const osc = audioCtx.createOscillator(); const gain = audioCtx.createGain();
        osc.connect(gain); gain.connect(audioCtx.destination); osc.type = 'sine';
        osc.frequency.setValueAtTime(freq, audioCtx.currentTime);
        gain.gain.setValueAtTime(0.03, audioCtx.currentTime);
        gain.gain.exponentialRampToValueAtTime(0.00001, audioCtx.currentTime + duration / 1000);
        osc.start(); osc.stop(audioCtx.currentTime + duration / 1000);
      } catch {}
    }
    function playSuccessChime(){ setTimeout(()=>playBeep(523.25,120),0); setTimeout(()=>playBeep(659.25,120),100); setTimeout(()=>playBeep(783.99,250),200); }
    function showToast(title, message, icon){ clearTimeout(toastTimeout); document.getElementById('toast-title').innerText = title; document.getElementById('toast-message').innerText = message; document.getElementById('toast-icon').innerHTML = `<i class="${icon || 'fa-solid fa-bell'}"></i>`; const toast = document.getElementById('toast-notification'); toast.classList.remove('-translate-y-16','opacity-0'); toast.classList.add('translate-y-0','opacity-100'); toastTimeout = setTimeout(()=>{ toast.classList.add('-translate-y-16','opacity-0'); toast.classList.remove('translate-y-0','opacity-100'); }, 3500); }

    function handleNext() {
      if (activeStep === 1) return goToStep(2);
      if (activeStep === 2 && !isVerified) return showToast('Aviso', 'Inicie a verificacao de arquivos primeiro.', 'fa-solid fa-circle-exclamation text-pokeYellow');
      if (activeStep === 2) {
        if (verificationUpToDate) {
          const wantsRepair = window.confirm('A instalacao ja esta atualizada e funcional. Deseja fazer um reparo limpo mesmo assim?');
          if (wantsRepair) { return startRepair(); }
          showToast('Sem alteracoes', 'A instalacao ja esta em dia. Use Reparar Limpo apenas quando quiser reinstalar tudo.', 'fa-solid fa-circle-check text-emerald-500');
          return;
        }
        goToStep(3); return startInstall(false);
      }
      if (activeStep === 3 && isInstalling) return showToast('Processando', 'Aguarde a instalacao terminar.', 'fa-solid fa-cloud-arrow-down text-pokeYellow');
      if (activeStep === 3) return goToStep(4);
      send('close');
    }
    function handleBack(){ if (activeStep > 1 && !isInstalling) goToStep(activeStep - 1); }
    function updateNextButtonState(highlight) {
      const next = document.getElementById('btn-next');
      const unavailable = (activeStep === 2 && !isVerified) || isInstalling;
      next.classList.toggle('opacity-50', unavailable);
      next.classList.toggle('cursor-not-allowed', unavailable);
      next.classList.toggle('opacity-100', !unavailable);
      next.classList.toggle('cursor-pointer', !unavailable);
      next.setAttribute('aria-disabled', unavailable ? 'true' : 'false');
      next.style.opacity = unavailable ? '0.5' : '1';
      next.style.backgroundColor = unavailable ? '#ca8a04' : '#facc15';
      next.style.color = unavailable ? '#475569' : '#020617';
      next.style.boxShadow = unavailable ? 'none' : '0 8px 18px rgba(250, 204, 21, .22)';
      if (highlight && !unavailable) {
        next.animate([
          { transform: 'scale(1)', boxShadow: '0 0 0 0 rgba(250, 204, 21, 0)' },
          { transform: 'scale(1.04)', boxShadow: '0 0 0 5px rgba(250, 204, 21, .28)' },
          { transform: 'scale(1)', boxShadow: '0 0 0 0 rgba(250, 204, 21, 0)' }
        ], { duration: 520, easing: 'ease-out' });
      }
    }
    function goToStep(step) {
      playBeep(350,80);
      for (let i=1;i<=4;i++) document.getElementById(`view-step-${i}`).classList.add('hidden-step');
      document.getElementById(`view-step-${step}`).classList.remove('hidden-step');
      activeStep = step; updateStepNodes(step);
      const back = document.getElementById('btn-back'), next = document.getElementById('btn-next');
      back.disabled = step === 1 || isInstalling;
      updateNextButtonState(false);
      next.innerHTML = step === 4 ? '<span>FECHAR</span> <i class="fa-solid fa-check text-[9px]"></i>' : '<span>AVANCAR</span> <i class="fa-solid fa-chevron-right text-[9px]"></i>';
    }
    function updateStepNodes(step) {
      document.getElementById('steps-progress-bar').style.width = `${[0,0,33,66,100][step]}%`;
      for (let i=1;i<=4;i++){ const n = document.getElementById(`step-node-${i}`); n.className = i <= step ? 'w-3.5 h-3.5 bg-pokeYellow border-2 border-slate-950 transition-all' : 'w-3.5 h-3.5 bg-slate-950 border-2 border-slate-800 transition-all'; }
    }
    function appendLog(line, tone) {
      const area = document.getElementById('terminal-logs'); const ts = new Date().toLocaleTimeString('pt-BR');
      const div = document.createElement('div'); div.className = `terminal-line ${tone || 'text-slate-400'}`; div.textContent = `[${ts}] ${line}`;
      area.appendChild(div); area.scrollTop = area.scrollHeight; logCount++; document.getElementById('log-counter').innerText = `${logCount} eventos`;
    }
    function startVerify() {
      playBeep(400,100); isVerified = false; verificationUpToDate = false; updateNextButtonState(false); logCount = 0; document.getElementById('terminal-logs').innerHTML = '';
      const btn = document.getElementById('btn-start-verify'); btn.disabled = true; btn.innerHTML = '<i class="fa-solid fa-spinner animate-spin"></i> ESCANEANDO...'; btn.classList.add('text-slate-500');
      const repairBtn = document.getElementById('btn-start-repair'); repairBtn.disabled = true; repairBtn.classList.add('opacity-50','cursor-not-allowed'); repairBtn.classList.remove('text-white','border-pokeRed');
      clearTimeout(verifyWatchdog);
      verifyWatchdog = setTimeout(() => {
        showToast('Compatibilidade', 'A interface moderna nao respondeu. Abrindo modo de compatibilidade...', 'fa-solid fa-screwdriver-wrench text-pokeYellow');
        send('openCompatibility');
      }, 8000);
      send('verify');
    }
    function startRepair() {
      if (!isVerified) return showToast('Aviso', 'Verifique a instancia antes de iniciar um reparo limpo.', 'fa-solid fa-circle-exclamation text-pokeYellow');
      goToStep(3);
      startInstall(true);
    }
    function startInstall(forceRepair) {
      repairMode = !!forceRepair;
      isInstalling = true; updateNextButtonState(false); setProgress(1); setStage('manifest'); document.getElementById('btn-back').disabled = true; document.getElementById('dl-current-file').innerText = repairMode ? 'Preparando reparo limpo do modpack...' : 'Preparando sincronizacao real do modpack...';
      document.getElementById('dl-current-action').innerText = repairMode ? 'Aguardando exclusao e reinstalacao do payload...' : 'Aguardando validacao do manifesto e do payload...';
      clearTimeout(installWatchdog);
      installWatchdog = setTimeout(() => {
        showToast('Compatibilidade', 'A interface moderna travou em 0%. Abrindo modo de compatibilidade...', 'fa-solid fa-screwdriver-wrench text-pokeYellow');
        send('openCompatibility');
      }, 8000);
      send(repairMode ? 'repair' : 'install');
    }
    let currentProgress = 0, currentStage = 'manifest';
    function setProgress(percent) {
      const p = Math.max(0, Math.min(100, Number(percent || 0)));
      currentProgress = p;
      document.getElementById('dl-progress-bar').style.width = `${p}%`; document.getElementById('dl-percentage').innerText = `${Math.floor(p)}%`;
      document.getElementById('dl-loaded-mb').innerText = `Progresso geral: ${Math.floor(p)}%`;
      document.getElementById('dl-eta').innerText = p >= 100 ? 'Finalizando...' : currentStage === 'clean' ? 'Limpando...' : 'Calculando...';
    }
    function setStage(stage) {
      currentStage = stage;
      const order = ['manifest','updater','payload','files','clean'];
      const index = order.indexOf(stage);
      for (let i = 0; i < order.length; i++) {
        const node = document.getElementById(`stage-${order[i]}`);
        if (!node) continue;
        node.classList.toggle('done', index > i);
        node.classList.toggle('active', index === i);
      }
      document.getElementById('dl-eta').innerText = currentProgress >= 100 ? 'Finalizando...' : stage === 'clean' ? 'Limpando...' : 'Calculando...';
    }
    function summarizeInstallLine(text) {
      if (!text) return 'Processando arquivos do modpack...';
      if (/^ATUALIZAR /i.test(text)) return 'Instalando: ' + text.replace(/^ATUALIZAR /i, '');
      if (/^VERIFICAR /i.test(text)) return 'Verificando payload: ' + text.replace(/^VERIFICAR /i, '');
      if (/^REMOVER /i.test(text)) return 'Excluindo: ' + text.replace(/^REMOVER /i, '');
      if (/^BAIXAR /i.test(text)) return 'Baixando: ' + text.replace(/^BAIXAR /i, '');
      if (/^DOWNLOAD iniciando /i.test(text)) return 'Baixando: ' + text.replace(/^DOWNLOAD iniciando /i, '');
      if (/^DOWNLOAD concluido /i.test(text)) return 'Baixado: ' + text.replace(/^DOWNLOAD concluido /i, '');
      if (/^DOWNLOAD /i.test(text)) return text;
      if (/^Payload local:/i.test(text)) return 'Descompactando: cobbleverse_payload.zip';
      if (/^Payload aplicado:/i.test(text)) return text;
      if (/^Instalacao limpa:/i.test(text)) return text;
      if (/^Limpeza:/i.test(text)) return text;
      return text;
    }
    function updateInstallStatus(line) {
      const text = line || '';
      if (/manifesto/i.test(text)) setStage('manifest');
      if (/Nova versao|auto-update|atualizador|instalador atualizado/i.test(text)) setStage('updater');
      if (/payload|Cobbleverse|cobbleverse/i.test(text)) setStage('payload');
      if (/DELTA|ATUALIZAR|VERIFICAR|BAIXAR mods|mods\\|mods\//i.test(text)) setStage('files');
      if (/REMOVER|Limpeza/i.test(text)) setStage('clean');
      if (activeStep === 3) document.getElementById('dl-current-action').innerText = summarizeInstallLine(text);
    }
    function setFolderValue(folder) {
      const input = document.getElementById('input-folder');
      input.value = folder || '';
      input.title = folder || '';
      requestAnimationFrame(() => { input.scrollLeft = input.scrollWidth; });
    }
    function instanceField(instance, name) {
      if (!instance) return '';
      const lower = name.charAt(0).toLowerCase() + name.slice(1);
      return instance[lower] ?? instance[name] ?? '';
    }
    function getDetectedSummaryText(selectedPath) {
      if (customPathPinned && selectedPath) return 'caminho custom ativo';
      if (!selectedPath) return `${detectedInstances.length} instancias encontradas`;
      const match = detectedInstances.find(x => String(instanceField(x, 'Path') || '').toLowerCase() === selectedPath.toLowerCase());
      return match ? `${detectedInstances.length} instancias encontradas` : 'caminho custom ativo';
    }
    function setDetectedInstances(instances, preferredFolder) {
      detectedInstances = Array.isArray(instances) ? instances : [];
      const shell = document.getElementById('detected-shell');
      const summary = document.getElementById('detected-summary');
      const instancesList = document.getElementById('detected-instances-list');
      const launchersList = document.getElementById('detected-launchers-list');
      if (!shell || !summary || !instancesList || !launchersList) return;
      instancesList.innerHTML = '';
      launchersList.innerHTML = '';
      if (!detectedInstances.length) {
        shell.classList.add('hidden');
        summary.innerText = '0 instancias';
        return;
      }
      shell.classList.remove('hidden');
      const selectedPathRaw = preferredFolder || document.getElementById('input-folder').value || '';
      summary.innerText = getDetectedSummaryText(selectedPathRaw);
      const selectedPath = selectedPathRaw.toLowerCase();
      detectedInstances.forEach((instance, index) => {
        const instancePath = String(instanceField(instance, 'Path') || '');
        const launcher = String(instanceField(instance, 'Launcher') || 'Minecraft');
        const instanceName = String(instanceField(instance, 'InstanceName') || 'Instancia detectada');
        const rootLabel = String(instanceField(instance, 'RootLabel') || '');
        const installationType = String(instanceField(instance, 'InstallationType') || 'Instancia');
        const description = [launcher, installationType === 'Instancia' ? '' : installationType].filter(Boolean).join(' · ');
        const isActive = !!instancePath && instancePath.toLowerCase() === selectedPath;
        const item = document.createElement('button');
        item.type = 'button';
        item.className = `detected-item${isActive ? ' active' : ''}`;
        item.innerHTML = `
          <div class="detected-item-row" title="${escapeHtml(instancePath || rootLabel)}">
            <span class="detected-item-name">${escapeHtml(instanceName)}</span>
            <span class="detected-item-desc">${escapeHtml(description || launcher)}</span>
          </div>`;
        item.onclick = () => applyDetectedInstance(index);
        instancesList.appendChild(item);
      });
      const launcherGroups = {};
      detectedInstances.forEach((instance, index) => {
        const key = String(instanceField(instance, 'Launcher') || 'Minecraft');
        if (!launcherGroups[key]) launcherGroups[key] = [];
        launcherGroups[key].push({ instance, index });
      });
      Object.keys(launcherGroups).sort((a, b) => a.localeCompare(b, 'pt-BR')).forEach((launcher, groupIndex) => {
        const entries = launcherGroups[launcher];
        const wrapper = document.createElement('div');
        wrapper.className = 'launcher-group';
        const header = document.createElement('button');
        header.type = 'button';
        header.className = `launcher-group-header${groupIndex === 0 ? ' open' : ''}`;
        header.innerHTML = `<span>${escapeHtml(launcher)} <span class="text-slate-500">(${entries.length})</span></span><i class="fa-solid fa-chevron-down text-pokeYellow"></i>`;
        const body = document.createElement('div');
        body.className = `launcher-group-body${groupIndex === 0 ? ' open' : ''}`;
        const bodyScroll = document.createElement('div');
        bodyScroll.className = 'detected-scrollbox';
        const bodyList = document.createElement('div');
        bodyList.className = 'detected-list';
        entries.forEach(entry => {
          const instancePath = String(instanceField(entry.instance, 'Path') || '');
          const instanceName = String(instanceField(entry.instance, 'InstanceName') || 'Instancia detectada');
          const rootLabel = String(instanceField(entry.instance, 'RootLabel') || '');
          const installationType = String(instanceField(entry.instance, 'InstallationType') || 'Instancia');
          const description = installationType === 'Instancia' ? launcher : `${launcher} · ${installationType}`;
          const isActive = !!instancePath && instancePath.toLowerCase() === selectedPath;
          const item = document.createElement('button');
          item.type = 'button';
          item.className = `detected-item${isActive ? ' active' : ''}`;
          item.innerHTML = `
            <div class="detected-item-row" title="${escapeHtml(instancePath || rootLabel)}">
              <span class="detected-item-name">${escapeHtml(instanceName)}</span>
              <span class="detected-item-desc">${escapeHtml(description)}</span>
            </div>`;
          item.onclick = () => applyDetectedInstance(entry.index);
          bodyList.appendChild(item);
        });
        header.onclick = () => {
          header.classList.toggle('open');
          body.classList.toggle('open');
        };
        bodyScroll.appendChild(bodyList);
        body.appendChild(bodyScroll);
        wrapper.appendChild(header);
        wrapper.appendChild(body);
        launchersList.appendChild(wrapper);
      });
      document.getElementById('toggle-detected-instances').classList.add('open');
      document.getElementById('detected-panel-instances').classList.add('open');
    }
    function toggleDetectedSection(section) {
      const toggle = document.getElementById(`toggle-detected-${section}`);
      const panel = document.getElementById(`detected-panel-${section}`);
      if (!toggle || !panel) return;
      toggle.classList.toggle('open');
      panel.classList.toggle('open');
    }
    function applyDetectedInstance(index) {
      const instance = detectedInstances[index];
      const instancePath = String(instanceField(instance, 'Path') || '');
      const instanceName = String(instanceField(instance, 'InstanceName') || 'Instancia');
      const launcher = String(instanceField(instance, 'Launcher') || 'Minecraft');
      if (!instance || !instancePath) return;
      customPathPinned = false;
      setFolderValue(instancePath);
      send('selectDetectedInstance', { selectedPath: instancePath });
      setDetectedInstances(detectedInstances, instancePath);
      showToast('Instancia selecionada', `${instanceName} em ${launcher} foi aplicada ao caminho.`, 'fa-solid fa-circle-check text-emerald-500');
    }
    function escapeHtml(value) {
      return String(value || '')
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('\"', '&quot;')
        .replaceAll('\'', '&#39;');
    }
    window.pokedogFromHost = function(msg) {
      if (!msg) return;
      if (msg.type === 'init') { setFolderValue(msg.folder); payloadMb = msg.payloadMb || payloadMb; customPathPinned = false; setDetectedInstances(msg.instances || [], msg.folder); return; }
      if (msg.type === 'folder') { setFolderValue(msg.folder); customPathPinned = false; setDetectedInstances(msg.instances || detectedInstances, msg.folder); const count = Number(msg.detectedCount || 0); showToast('Sucesso', count > 1 ? `${count} instancias encontradas. Escolha abaixo a instancia ou launcher detectado.` : 'Instancia do Minecraft selecionada.', 'fa-solid fa-circle-check text-emerald-500'); return; }
      if (msg.type === 'verifyStarted') { clearTimeout(verifyWatchdog); appendLog('Verificacao real iniciada.', 'text-slate-300'); return; }
      if (msg.type === 'installStarted') { clearTimeout(installWatchdog); repairMode = !!msg.forceRepair; setProgress(Math.max(currentProgress, 2)); appendLog(repairMode ? 'Reparo limpo iniciado.' : 'Instalacao real iniciada.', 'text-slate-300'); setStage('manifest'); document.getElementById('dl-current-file').innerText = repairMode ? 'Baixando payload e preparando reinstalacao limpa...' : 'Baixando manifesto e preparando dependencias...'; document.getElementById('dl-current-action').innerText = repairMode ? 'Preparando exclusao de mods, resourcepacks e shaderpacks...' : 'Preparando verificacao de arquivos e dependencias...'; return; }
      if (msg.type === 'log') { clearTimeout(installWatchdog); appendLog(msg.line || '', /DELTA|BAIXAR|ATUALIZAR|REMOVER|Nova versao|DOWNLOAD/.test(msg.line || '') ? 'text-pokeYellow font-semibold' : 'text-slate-400'); updateInstallStatus(msg.line || ''); return; }
      if (msg.type === 'progress') { clearTimeout(installWatchdog); setProgress(msg.percent); return; }
      if (msg.type === 'verified') { clearTimeout(verifyWatchdog); isVerified = true; verificationUpToDate = !!msg.upToDate; updateNextButtonState(true); const btn = document.getElementById('btn-start-verify'); btn.disabled = false; btn.innerHTML = '<i class="fa-solid fa-magnifying-glass text-pokeYellow"></i> VERIFICAR NOVAMENTE'; btn.classList.remove('text-slate-500'); const repairBtn = document.getElementById('btn-start-repair'); repairBtn.disabled = false; repairBtn.classList.remove('opacity-50','cursor-not-allowed'); repairBtn.classList.add('text-white','border-pokeRed'); appendLog(verificationUpToDate ? 'Verificacao concluida. Instalacao ja esta em dia; o instalador pode reparar se voce quiser.' : 'Verificacao concluida. Clique em Avancar ou use Reparo Limpo.', 'text-emerald-400 font-bold'); showToast('Verificado!', verificationUpToDate ? 'Instalacao atualizada e funcional. O instalador vai perguntar antes de reparar.' : 'Pronto para sincronizar ou reparar o modpack.', 'fa-solid fa-circle-check text-emerald-500'); playSuccessChime(); return; }
      if (msg.type === 'installed') {
        clearTimeout(installWatchdog);
        isInstalling = false;
        setProgress(100);
        const installerUpdated = !!msg.installerUpdated;
        repairMode = !!msg.forceRepair;
        document.getElementById('dl-current-file').innerText = installerUpdated ? 'Instalador atualizado. Reabra para concluir.' : 'Instalacao finalizada.';
        document.getElementById('dl-current-action').innerText = installerUpdated ? 'Atualizacao do instalador concluida.' : (repairMode ? 'Reinstalacao limpa concluida na instancia selecionada.' : 'Sincronizacao concluida na instancia selecionada.');
        document.getElementById('done-message').innerText = installerUpdated
          ? 'O PokeDOG-Modpack-Installer foi atualizado antes de continuar a instalacao do modpack.'
          : (repairMode ? 'O modpack PokeDOG foi reparado com reinstalacao limpa na instancia selecionada.' : 'O modpack PokeDOG foi instalado e atualizado no seu diretorio de jogo.');
        document.getElementById('installer-update-alert').classList.toggle('hidden', !installerUpdated);
        if (installerUpdated) {
          showToast('Instalador atualizado!', 'Feche e abra novamente para concluir a instalacao.', 'fa-solid fa-rotate text-pokeYellow');
        }
        playSuccessChime();
        setTimeout(()=>goToStep(4), 450);
        return;
      }
      if (msg.type === 'toast') { showToast(msg.title || 'Aviso', msg.message || '', 'fa-solid fa-bell text-pokeYellow'); return; }
      if (msg.type === 'error') { clearTimeout(installWatchdog); clearTimeout(verifyWatchdog); isInstalling = false; const btn = document.getElementById('btn-start-verify'); btn.disabled = false; btn.innerHTML = '<i class="fa-solid fa-magnifying-glass text-pokeYellow"></i> VERIFICAR NOVAMENTE'; appendLog('ERRO: ' + (msg.message || 'falha desconhecida'), 'text-pokeRed font-bold'); showToast('Erro', msg.message || 'Falha desconhecida.', 'fa-solid fa-triangle-exclamation text-pokeRed'); }
    };
    window.addEventListener('DOMContentLoaded', () => {
      const input = document.getElementById('input-folder');
      if (input) {
        input.addEventListener('input', () => {
          customPathPinned = true;
          setDetectedInstances(detectedInstances, input.value || '');
        });
      }
      send('ready');
    });
  </script>
</body>
</html>
""".Replace("__BG__", backgroundUri);
    }
}

internal sealed class InstallerForm : Form
{
    private static readonly Color Surface = Color.FromArgb(10, 13, 22);
    private static readonly Color SurfaceAlt = Color.FromArgb(16, 21, 34);
    private static readonly Color SurfaceCard = Color.FromArgb(8, 11, 20);
    private static readonly Color Border = Color.FromArgb(41, 58, 92);
    private static readonly Color Accent = Color.FromArgb(255, 203, 46);
    private static readonly Color AccentMuted = Color.FromArgb(54, 68, 96);
    private static readonly Color Success = Color.FromArgb(51, 214, 153);
    private static readonly Color MutedText = Color.FromArgb(180, 198, 219);
    private static readonly Color Danger = Color.FromArgb(230, 88, 88);

    private readonly TextBox _targetBox = new();
    private readonly TextBox _manifestBox = new();
    private readonly TextBox _payloadBox = new();
    private readonly TextBox _logBox = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Button _installButton = new();
    private readonly Button _repairButton = new();
    private readonly Button _checkButton = new();
    private readonly Button _targetButton = new();
    private readonly Button _targetAutoButton = new();
    private readonly Button _targetOpenButton = new();
    private readonly Button _manifestButton = new();
    private readonly Button _payloadButton = new();
    private readonly CancellationTokenSource _disposeToken = new();
    private readonly bool? _autoRunDryRun;
    private bool _lastVerificationUpToDate;
    private readonly Panel[] _stepNodes = new Panel[5];
    private readonly Panel[] _stepLines = new Panel[4];
    private readonly Label[] _stepLabels = new Label[5];
    private readonly Label _stageTitle = new();
    private readonly Label _stageHint = new();
    private readonly Label _targetSummary = new();
    private readonly Label _progressCaption = new();
    private readonly Label _logCaption = new();

    public InstallerForm(string? initialTargetRoot = null, bool? autoRunDryRun = null)
    {
        _autoRunDryRun = autoRunDryRun;
        Text = "PokeDOG Modpack Installer";
        MinimumSize = new Size(1020, 760);
        Size = new Size(1280, 820);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Surface;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = LoadIconSafe();
        BackgroundImage = LoadBackgroundSafe();
        BackgroundImageLayout = ImageLayout.Stretch;

        var shell = new Panel
        {
            Size = new Size(1000, 700),
            BackColor = SurfaceAlt,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(shell);

        void LayoutShell()
        {
            shell.Left = Math.Max(20, (ClientSize.Width - shell.Width) / 2);
            shell.Top = Math.Max(20, (ClientSize.Height - shell.Height) / 2);
        }
        Resize += (_, _) => LayoutShell();

        var header = new Panel { Dock = DockStyle.Top, Height = 88, BackColor = SurfaceAlt, Padding = new Padding(24, 16, 24, 14) };

        var logo = new PictureBox
        {
            Size = new Size(52, 52),
            Location = new Point(0, 8),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = Icon?.ToBitmap()
        };
        header.Controls.Add(logo);

        var title = new Label
        {
            Text = "PokeDOG Modpack Installer",
            Location = new Point(72, 10),
            Size = new Size(580, 24),
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor = Color.White
        };
        header.Controls.Add(title);

        var subtitle = new Label
        {
            Text = "Fluxo nativo por etapas para verificar, limpar e sincronizar mods, resourcepacks, shaderpacks e o Client Guard.",
            Location = new Point(72, 36),
            Size = new Size(760, 20),
            Font = new Font("Segoe UI", 8.8f),
            ForeColor = MutedText
        };
        header.Controls.Add(subtitle);

        var badge = new Label
        {
            Text = "NATIVO + AUTO UPDATE",
            AutoSize = false,
            Size = new Size(190, 28),
            Location = new Point(860, 16),
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = SurfaceCard,
            ForeColor = Accent,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            BorderStyle = BorderStyle.FixedSingle
        };
        header.Controls.Add(badge);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28, 24, 28, 24),
            ColumnCount = 1,
            RowCount = 6,
            BackColor = Surface,
            AutoScroll = true
        };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 214));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 122));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        shell.Controls.Add(header);
        shell.Controls.Add(body);

        body.Controls.Add(BuildStepCard(), 0, 0);
        body.Controls.Add(BuildStageHeroCard(), 0, 1);
        body.Controls.Add(BuildPathsCard(), 0, 2);
        body.Controls.Add(BuildProgressCard(), 0, 3);
        body.Controls.Add(BuildLogCard(), 0, 4);
        body.Controls.Add(BuildFooterActions(), 0, 5);

        _targetBox.Text = string.IsNullOrWhiteSpace(initialTargetRoot)
            ? InstallerUserSettings.GetPreferredMinecraftFolder()
            : initialTargetRoot;
        _manifestBox.Text = InstallerPaths.FindDefaultManifest();
        _payloadBox.Text = InstallerPaths.FindDefaultPayload();

        _targetButton.Click += (_, _) => PickFolder(_targetBox);
        _targetAutoButton.Click += (_, _) => AutoDetectTarget();
        _targetOpenButton.Click += (_, _) => OpenTargetFolder();
        _manifestButton.Click += (_, _) => PickFile(_manifestBox, "Manifestos JSON (*.json)|*.json|Todos (*.*)|*.*");
        _payloadButton.Click += (_, _) => PickFile(_payloadBox, "Payload ZIP (*.zip)|*.zip|Todos (*.*)|*.*");
        _targetBox.TextChanged += (_, _) => UpdateTargetSummary();
        Shown += async (_, _) =>
        {
            LayoutShell();
            UpdateTargetSummary();
            SetStepState(1);
            if (_autoRunDryRun.HasValue)
            {
                await RunInstallAsync(_autoRunDryRun.Value);
            }
        };

        AppendLog("Pronto. O caminho escolhido sera respeitado para limpar e instalar dentro da instancia selecionada.");
        AppendLog("Se o manifesto local nao existir, o instalador usa o manifesto remoto e depois o fallback embutido.");
        AppendLog("O Client Guard continua sincronizado com a politica server-side por versao, manifesto e hashes.");
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

    private async Task RunInstallAsync(bool dryRun, bool forceRepair = false)
    {
        if (!dryRun && !forceRepair && _lastVerificationUpToDate)
        {
            var choice = MessageBox.Show(
                this,
                "A instalacao ja esta atualizada e funcional. Deseja fazer um reparo limpo mesmo assim?",
                "PokeDOG Modpack Installer",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (choice == DialogResult.Yes)
            {
                forceRepair = true;
            }
            else
            {
                AppendLog("Instalacao em dia. Nenhuma alteracao aplicada.");
                return;
            }
        }

        SetBusy(true);
        _progressBar.Value = 0;
        _logBox.Clear();
        SetStepState(2);
        SetStageText(
            dryRun ? "Analisando ficheiros" : forceRepair ? "Reparo limpo do modpack" : "Sincronizando modpack",
            dryRun
                ? "Conferindo manifesto, atualizador e inventario antes de alterar a instancia."
                : forceRepair
                    ? "Reinstalando o Cobbleverse do zero com limpeza de mods, resourcepacks e shaderpacks."
                    : "Baixando e aplicando somente o que faltar ou estiver desatualizado.");
        try
        {
            var actionsNeeded = false;
            var options = new InstallerOptions(
                _manifestBox.Text.Trim(),
                _targetBox.Text.Trim(),
                dryRun,
                _payloadBox.Text.Trim(),
                forceRepair);
            InstallerUserSettings.SaveLastTargetRoot(options.TargetRoot);
            var installerUpdated = false;
            var log = new FormProgressLog(line =>
            {
                if (line.Contains("Instalador atualizado", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Auto-update agendado", StringComparison.OrdinalIgnoreCase))
                {
                    installerUpdated = true;
                }
                if (dryRun && InstallerEngine.LogLineSuggestsRepairOrUpdate(line))
                {
                    actionsNeeded = true;
                }
                UpdateStageFromLog(line);
                AppendLog(line);
            }, value =>
            {
                UpdateProgress(value);
            });
            await Task.Run(async () => await InstallerEngine.RunAsync(options, log, _disposeToken.Token), _disposeToken.Token);
            if (dryRun)
            {
                _lastVerificationUpToDate = !actionsNeeded;
            }
            AppendLog(dryRun ? "Verificacao concluida." : forceRepair ? "Reparo limpo concluido." : "Instalacao/atualizacao concluida.");
            SetStepState(4);
            SetStageText(
                dryRun ? "Verificacao concluida" : forceRepair ? "Reparo concluido" : "Instalacao concluida",
                dryRun
                    ? _lastVerificationUpToDate
                        ? "A instancia escolhida ja esta atualizada e funcional. O reparo limpo permanece disponivel se voce quiser reinstalar tudo."
                        : "A instancia escolhida foi validada. Se precisar, siga para instalar."
                    : forceRepair
                        ? "Mods, resourcepacks, shaderpacks e guard foram reinstalados com limpeza na pasta selecionada."
                        : "Mods, resourcepacks, shaderpacks e guard foram sincronizados na pasta selecionada.");
            if (installerUpdated)
            {
                MessageBox.Show(this,
                    "O PokeDOG-Modpack-Installer foi atualizado. Feche esta janela, abra o instalador novamente e clique em Instalar/Atualizar para concluir os mods.",
                    "Instalador atualizado",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            SetStageText("Falha durante a sincronizacao", "Revise o log abaixo. O destino selecionado foi mantido para nova tentativa.");
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
        _repairButton.Enabled = !busy;
        _checkButton.Enabled = !busy;
        _targetButton.Enabled = !busy;
        _targetAutoButton.Enabled = !busy;
        _targetOpenButton.Enabled = !busy;
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

    private Control BuildStepCard()
    {
        var card = BuildCard();
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = new Label
        {
            Text = "Fluxo em etapas",
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold)
        };
        layout.Controls.Add(title, 0, 0);

        var strip = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 7,
            RowCount = 2,
            Padding = new Padding(10, 14, 10, 2),
            BackColor = Color.Transparent
        };
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        strip.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        strip.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        var stepTexts = new[] { "", "Instancia", "Analise", "Sincronizacao", "Concluido" };
        for (var step = 1; step <= 4; step++)
        {
            var node = new Panel
            {
                Width = 18,
                Height = 18,
                Margin = new Padding(0, 2, 0, 0),
                BackColor = step == 1 ? Accent : Surface,
                BorderStyle = BorderStyle.FixedSingle,
                Anchor = AnchorStyles.None
            };
            _stepNodes[step] = node;
            strip.Controls.Add(node, (step - 1) * 2, 0);

            var label = new Label
            {
                Text = stepTexts[step],
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = step == 1 ? Accent : MutedText,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _stepLabels[step] = label;
            strip.Controls.Add(label, (step - 1) * 2, 1);

            if (step < 4)
            {
                var line = new Panel
                {
                    Height = 2,
                    Dock = DockStyle.Fill,
                    BackColor = AccentMuted,
                    Margin = new Padding(0, 10, 0, 0),
                    Anchor = AnchorStyles.Left | AnchorStyles.Right
                };
                _stepLines[step] = line;
                strip.Controls.Add(line, (step - 1) * 2 + 1, 0);
            }
        }

        layout.Controls.Add(strip, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildStageHeroCard()
    {
        var card = BuildCard();
        card.Padding = new Padding(22, 16, 22, 14);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

        _stageTitle.Text = "Instalar Modpack PokeDOG";
        _stageTitle.Dock = DockStyle.Fill;
        _stageTitle.Font = new Font("Segoe UI", 19f, FontStyle.Bold);
        _stageTitle.ForeColor = Color.White;
        _stageTitle.TextAlign = ContentAlignment.MiddleCenter;

        _stageHint.Text = "Escolha a instancia do Minecraft. O instalador vai limpar e reaplicar o conteudo gerenciado nela.";
        _stageHint.Dock = DockStyle.Fill;
        _stageHint.ForeColor = MutedText;
        _stageHint.TextAlign = ContentAlignment.MiddleCenter;

        _targetSummary.Dock = DockStyle.Fill;
        _targetSummary.ForeColor = Accent;
        _targetSummary.Font = new Font("Consolas", 9f, FontStyle.Bold);
        _targetSummary.TextAlign = ContentAlignment.MiddleCenter;

        layout.Controls.Add(_stageTitle, 0, 0);
        layout.Controls.Add(_stageHint, 0, 1);
        layout.Controls.Add(_targetSummary, 0, 2);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildPathsCard()
    {
        var card = BuildCard();
        card.Padding = new Padding(20, 16, 20, 16);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Color.Transparent
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));

        var title = new Label
        {
            Text = "Destino e fontes",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = Color.White
        };
        layout.Controls.Add(title, 0, 0);

        layout.Controls.Add(BuildTargetRow(), 0, 1);
        layout.Controls.Add(BuildFileRow("Manifesto", _manifestBox, _manifestButton, "Abrir"), 0, 2);
        layout.Controls.Add(BuildFileRow("Payload", _payloadBox, _payloadButton, "Abrir"), 0, 3);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildTargetRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            Margin = new Padding(0),
            BackColor = Color.Transparent
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 102));

        var label = new Label
        {
            Text = "Instancia",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };
        row.Controls.Add(label, 0, 0);

        ConfigurePathBox(_targetBox);
        row.Controls.Add(_targetBox, 1, 0);

        ConfigureActionButton(_targetButton, "Escolher", SurfaceAlt, Color.White);
        row.Controls.Add(_targetButton, 2, 0);

        ConfigureActionButton(_targetAutoButton, "Auto detectar", SurfaceAlt, Accent);
        row.Controls.Add(_targetAutoButton, 3, 0);

        ConfigureActionButton(_targetOpenButton, "Abrir", SurfaceAlt, Color.White);
        row.Controls.Add(_targetOpenButton, 4, 0);
        return row;
    }

    private Control BuildFileRow(string labelText, TextBox box, Button button, string buttonText)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Margin = new Padding(0, 6, 0, 0),
            BackColor = Color.Transparent
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));

        var label = new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = MutedText
        };
        row.Controls.Add(label, 0, 0);

        ConfigurePathBox(box);
        row.Controls.Add(box, 1, 0);

        ConfigureActionButton(button, buttonText, SurfaceAlt, Color.White);
        row.Controls.Add(button, 2, 0);
        return row;
    }

    private Control BuildProgressCard()
    {
        var card = BuildCard();
        card.Padding = new Padding(20, 16, 20, 16);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        var title = new Label
        {
            Text = "Status da sincronizacao",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = Color.White
        };
        layout.Controls.Add(title, 0, 0);

        _progressCaption.Text = "Preparado para verificar manifesto, atualizador e arquivos.";
        _progressCaption.Dock = DockStyle.Fill;
        _progressCaption.ForeColor = MutedText;
        layout.Controls.Add(_progressCaption, 0, 1);

        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Style = ProgressBarStyle.Continuous;
        layout.Controls.Add(_progressBar, 0, 2);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildLogCard()
    {
        var card = BuildCard();
        card.Padding = new Padding(20, 16, 20, 16);

        _logCaption.Text = "Console do instalador";
        _logCaption.Dock = DockStyle.Top;
        _logCaption.Height = 24;
        _logCaption.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        _logCaption.ForeColor = Color.White;
        card.Controls.Add(_logCaption);

        _logBox.Dock = DockStyle.Fill;
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.BackColor = SurfaceCard;
        _logBox.ForeColor = Color.FromArgb(218, 232, 238);
        _logBox.BorderStyle = BorderStyle.FixedSingle;
        _logBox.Font = new Font("Consolas", 9f);
        card.Controls.Add(_logBox);
        return card;
    }

    private Control BuildFooterActions()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1,
            BackColor = Color.Transparent
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));

        var note = new Label
        {
            Text = "A limpeza gerenciada remove itens extras apenas nas pastas controladas pelo pack.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = MutedText
        };
        footer.Controls.Add(note, 0, 0);

        ConfigureActionButton(_checkButton, "Verificar", SurfaceAlt, Color.White);
        _checkButton.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _checkButton.Click += async (_, _) => await RunInstallAsync(dryRun: true);
        footer.Controls.Add(_checkButton, 1, 0);

        var spacer = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        footer.Controls.Add(spacer, 2, 0);

        ConfigureActionButton(_repairButton, "Reparar Limpo", Color.FromArgb(181, 40, 40), Color.White);
        _repairButton.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _repairButton.Click += async (_, _) => await RunInstallAsync(dryRun: false, forceRepair: true);
        footer.Controls.Add(_repairButton, 3, 0);

        var spacer2 = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        footer.Controls.Add(spacer2, 4, 0);

        ConfigureActionButton(_installButton, "Instalar / Atualizar", Accent, Color.Black);
        _installButton.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _installButton.Click += async (_, _) => await RunInstallAsync(dryRun: false);
        footer.Controls.Add(_installButton, 5, 0);
        return footer;
    }

    private static Panel BuildCard()
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = SurfaceAlt,
            Padding = new Padding(18, 14, 18, 14),
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 0, 14)
        };
    }

    private static void ConfigurePathBox(TextBox box)
    {
        box.Dock = DockStyle.Fill;
        box.BackColor = SurfaceCard;
        box.ForeColor = Color.White;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Font = new Font("Consolas", 9.5f);
    }

    private static void ConfigureActionButton(Button button, string text, Color backColor, Color foreColor)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.BackColor = backColor;
        button.ForeColor = foreColor;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Surface;
        button.FlatAppearance.MouseDownBackColor = SurfaceCard;
    }

    private void AutoDetectTarget()
    {
        var detectedFolder = MinecraftInstanceLocator.FindInstances().FirstOrDefault();
        if (string.IsNullOrWhiteSpace(detectedFolder))
        {
            MessageBox.Show(this,
                "Nenhuma instancia compativel foi encontrada automaticamente. Escolha a pasta manualmente.",
                "PokeDOG Modpack Installer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        _targetBox.Text = detectedFolder;
        InstallerUserSettings.SaveLastTargetRoot(detectedFolder);
        UpdateTargetSummary();
        AppendLog($"Instancia detectada automaticamente: {detectedFolder}");
    }

    private void OpenTargetFolder()
    {
        var target = _targetBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(target);
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Nao foi possivel abrir a pasta", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateTargetSummary()
    {
        var target = _targetBox.Text.Trim();
        _targetSummary.Text = string.IsNullOrWhiteSpace(target)
            ? "Destino atual: nenhuma instancia selecionada."
            : $"Destino atual: {target}";
    }

    private void UpdateProgress(int percent)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateProgress(percent));
            return;
        }

        var bounded = Math.Max(0, Math.Min(100, percent));
        _progressBar.Value = bounded;
        _progressCaption.Text = $"Progresso geral: {bounded}%";
        if (bounded >= 12)
        {
            SetStepState(2);
        }
        if (bounded >= 45)
        {
            SetStepState(3);
        }
    }

    private void UpdateStageFromLog(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateStageFromLog(line));
            return;
        }

        if (line.Contains("Manifesto", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Atualizador", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Nova revisao do instalador", StringComparison.OrdinalIgnoreCase))
        {
            SetStepState(2);
            SetStageText("Analisando manifesto e atualizador", "Conferindo versao, fallback, release e auto update antes da sincronizacao.");
            return;
        }

        if (line.Contains("Cobbleverse", StringComparison.OrdinalIgnoreCase)
            || line.Contains("BAIXAR ", StringComparison.OrdinalIgnoreCase)
            || line.Contains("ATUALIZAR ", StringComparison.OrdinalIgnoreCase)
            || line.Contains("DELTA ", StringComparison.OrdinalIgnoreCase)
            || line.Contains("OK ", StringComparison.OrdinalIgnoreCase))
        {
            SetStepState(3);
            SetStageText("Sincronizando arquivos", "Baixando ou reaproveitando payload, mods e dependencias faltantes na instancia selecionada.");
            return;
        }

        if (line.Contains("Limpeza", StringComparison.OrdinalIgnoreCase)
            || line.Contains("REMOVER ", StringComparison.OrdinalIgnoreCase)
            || line.Contains("REMOVIDO ", StringComparison.OrdinalIgnoreCase))
        {
            SetStepState(3);
            SetStageText("Limpando conteudo antigo", "Removendo itens extras de mods, resourcepacks e shaderpacks com backup seguro.");
            return;
        }
    }

    private void SetStepState(int step)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetStepState(step));
            return;
        }

        for (var index = 1; index <= 4; index++)
        {
            var isActive = index <= step;
            _stepNodes[index].BackColor = isActive ? Accent : Surface;
            _stepNodes[index].ForeColor = isActive ? Accent : Border;
            _stepLabels[index].ForeColor = isActive ? Accent : MutedText;
            if (index < 4 && _stepLines[index] != null)
            {
                _stepLines[index].BackColor = index < step ? Accent : AccentMuted;
            }
        }
    }

    private void SetStageText(string title, string hint)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetStageText(title, hint));
            return;
        }

        _stageTitle.Text = title;
        _stageHint.Text = hint;
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
            InstallerUserSettings.SaveLastTargetRoot(dialog.SelectedPath);
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
        return InstallerUserSettings.GetPreferredMinecraftFolder();
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

    private static Image? LoadBackgroundSafe()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "assets", "PokeDOGBg.png");
            if (File.Exists(path))
            {
                return Image.FromFile(path);
            }

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("pokedog_background_main.png");
            return stream == null ? null : Image.FromStream(stream);
        }
        catch
        {
            return null;
        }
    }
}

internal static class MinecraftInstanceLocator
{
    public static IReadOnlyList<DetectedInstanceEntry> FindInstanceEntries()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var defaultMinecraft = Path.Combine(appData, ".minecraft");
        AddCandidate(candidates, defaultMinecraft);
        AddChildren(candidates, Path.Combine(defaultMinecraft, "instances"));
        AddLauncherProfileGameDirectories(candidates, Path.Combine(defaultMinecraft, "launcher_profiles.json"));
        AddChildren(candidates, Path.Combine(appData, "sklauncher", "instances"));
        AddChildren(candidates, Path.Combine(localAppData, "sklauncher", "instances"));
        AddChildren(candidates, Path.Combine(appData, "PrismLauncher", "instances"), "minecraft", ".minecraft");
        AddChildren(candidates, Path.Combine(appData, "MultiMC", "instances"), ".minecraft", "minecraft");
        AddChildren(candidates, Path.Combine(localAppData, "PrismLauncher", "instances"), "minecraft", ".minecraft");
        AddChildren(candidates, Path.Combine(localAppData, "MultiMC", "instances"), ".minecraft", "minecraft");
        AddChildren(candidates, Path.Combine(appData, "com.modrinth.theseus", "profiles"));
        AddChildren(candidates, Path.Combine(appData, "ModrinthApp", "profiles"));
        AddChildren(candidates, Path.Combine(appData, ".atlauncher", "instances"));
        AddChildren(candidates, Path.Combine(appData, ".technic", "modpacks"));
        AddChildren(candidates, Path.Combine(appData, "gdlauncher_carbon", "data", "instances"));
        AddChildren(candidates, Path.Combine(appData, "gdlauncher_next", "instances"));
        AddChildren(candidates, Path.Combine(localAppData, "CurseForge", "minecraft", "Instances"));
        AddChildren(candidates, Path.Combine(userProfile, "curseforge", "minecraft", "Instances"));
        AddChildren(candidates, Path.Combine(documents, "Curse", "Minecraft", "Instances"));

        return candidates
            .Where(IsMinecraftInstance)
            .Select(CreateEntry)
            .OrderByDescending(entry => Score(entry.Path))
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> FindInstances()
    {
        return FindInstanceEntries()
            .Select(entry => entry.Path)
            .ToArray();
    }

    private static void AddChildren(HashSet<string> candidates, string instancesRoot, params string[] gameSubdirectories)
    {
        if (!Directory.Exists(instancesRoot))
        {
            return;
        }

        try
        {
            foreach (var instance in Directory.EnumerateDirectories(instancesRoot))
            {
                if (gameSubdirectories.Length == 0)
                {
                    AddCandidate(candidates, instance);
                    continue;
                }

                foreach (var subdirectory in gameSubdirectories)
                {
                    AddCandidate(candidates, Path.Combine(instance, subdirectory));
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static void AddCandidate(HashSet<string> candidates, string path)
    {
        if (Directory.Exists(path))
        {
            candidates.Add(Path.GetFullPath(path));
        }
    }

    private static void AddLauncherProfileGameDirectories(HashSet<string> candidates, string profilesPath)
    {
        if (!File.Exists(profilesPath))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(profilesPath));
            if (!document.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var profile in profiles.EnumerateObject())
            {
                if (!profile.Value.TryGetProperty("gameDir", out var gameDirectory) || gameDirectory.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var path = Environment.ExpandEnvironmentVariables(gameDirectory.GetString() ?? "");
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }
                if (!Path.IsPathRooted(path))
                {
                    path = Path.Combine(Path.GetDirectoryName(profilesPath)!, path);
                }
                AddCandidate(candidates, path);
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsMinecraftInstance(string path)
    {
        return Directory.Exists(Path.Combine(path, "mods")) ||
            Directory.Exists(Path.Combine(path, "config")) ||
            Directory.Exists(Path.Combine(path, "versions")) ||
            File.Exists(Path.Combine(path, "options.txt")) ||
            File.Exists(Path.Combine(path, "instance.json")) ||
            File.Exists(Path.Combine(path, "minecraftinstance.json")) ||
            File.Exists(Path.Combine(Directory.GetParent(path)?.FullName ?? path, "instance.cfg"));
    }

    private static int Score(string path)
    {
        var score = 0;
        if (File.Exists(Path.Combine(path, ".pokedog-cache", "installed-state.json"))) score += 10000;
        if (Directory.Exists(Path.Combine(path, "mods")) && Directory.EnumerateFiles(Path.Combine(path, "mods"), "pokedog-client-guard-*.jar").Any()) score += 8000;
        if (path.Contains("pokedog", StringComparison.OrdinalIgnoreCase) || path.Contains("cobbleverse", StringComparison.OrdinalIgnoreCase)) score += 4000;
        if (Directory.Exists(Path.Combine(path, "config"))) score += 500;
        if (Directory.Exists(Path.Combine(path, "mods")))
        {
            try
            {
                score += Math.Min(Directory.EnumerateFiles(Path.Combine(path, "mods"), "*.jar").Take(501).Count(), 500);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return score;
    }

    private static DetectedInstanceEntry CreateEntry(string path)
    {
        var launcher = DetectLauncher(path);
        var pathInfo = new DirectoryInfo(path);
        var instanceName = string.Equals(pathInfo.Name, ".minecraft", StringComparison.OrdinalIgnoreCase) && pathInfo.Parent != null
            ? pathInfo.Parent.Name + " / .minecraft"
            : pathInfo.Name;
        var rootLabel = DetectRootLabel(path);
        var installationType = DetectInstallationType(path);
        return new DetectedInstanceEntry(path, launcher, instanceName, rootLabel, installationType);
    }

    private static string DetectLauncher(string path)
    {
        if (path.Contains("curseforge", StringComparison.OrdinalIgnoreCase)) return "CurseForge";
        if (path.Contains("sklauncher", StringComparison.OrdinalIgnoreCase)) return "SKLauncher";
        if (path.Contains("prismlauncher", StringComparison.OrdinalIgnoreCase)) return "Prism Launcher";
        if (path.Contains("multimc", StringComparison.OrdinalIgnoreCase)) return "MultiMC";
        if (path.Contains("modrinth", StringComparison.OrdinalIgnoreCase) || path.Contains("theseus", StringComparison.OrdinalIgnoreCase)) return "Modrinth";
        if (path.Contains(".atlauncher", StringComparison.OrdinalIgnoreCase)) return "ATLauncher";
        if (path.Contains(".technic", StringComparison.OrdinalIgnoreCase)) return "Technic";
        if (path.Contains("gdlauncher", StringComparison.OrdinalIgnoreCase)) return "GDLauncher";
        if (path.Contains(@"\.minecraft\instances\", StringComparison.OrdinalIgnoreCase)) return "Minecraft Launcher";
        if (path.Contains(@"\.minecraft", StringComparison.OrdinalIgnoreCase)) return "Minecraft";
        return "Launcher detectado";
    }

    private static string DetectRootLabel(string path)
    {
        if (path.Contains(@"\.minecraft\instances\", StringComparison.OrdinalIgnoreCase)) return "%AppData%\\.minecraft\\instances";
        if (path.Contains(@"\.minecraft", StringComparison.OrdinalIgnoreCase)) return "%AppData%\\.minecraft";
        if (path.Contains(@"\curseforge\minecraft\instances", StringComparison.OrdinalIgnoreCase)) return "%UserProfile%\\curseforge\\minecraft\\Instances";
        if (path.Contains(@"\CurseForge\minecraft\Instances", StringComparison.OrdinalIgnoreCase)) return "%LocalAppData%\\CurseForge\\minecraft\\Instances";
        if (path.Contains("sklauncher", StringComparison.OrdinalIgnoreCase)) return "SKLauncher";
        if (path.Contains("PrismLauncher", StringComparison.OrdinalIgnoreCase)) return "Prism Launcher";
        if (path.Contains("MultiMC", StringComparison.OrdinalIgnoreCase)) return "MultiMC";
        if (path.Contains("Modrinth", StringComparison.OrdinalIgnoreCase) || path.Contains("theseus", StringComparison.OrdinalIgnoreCase)) return "Modrinth";
        if (path.Contains(".atlauncher", StringComparison.OrdinalIgnoreCase)) return "ATLauncher";
        if (path.Contains(".technic", StringComparison.OrdinalIgnoreCase)) return "Technic";
        if (path.Contains("gdlauncher", StringComparison.OrdinalIgnoreCase)) return "GDLauncher";
        return "Instalacao detectada";
    }

    private static string DetectInstallationType(string path)
    {
        if (File.Exists(Path.Combine(path, ".pokedog-cache", "installed-state.json"))) return "PokeDOG";
        if (path.Contains("pokedog", StringComparison.OrdinalIgnoreCase)) return "Modpack";
        if (path.Contains("cobbleverse", StringComparison.OrdinalIgnoreCase)) return "Modpack";
        if (string.Equals(new DirectoryInfo(path).Name, ".minecraft", StringComparison.OrdinalIgnoreCase)) return "Raiz";
        return "Instancia";
    }
}

internal sealed record DetectedInstanceEntry(
    string Path,
    string Launcher,
    string InstanceName,
    string RootLabel,
    string InstallationType
);

internal static class InstallerUserSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PokeDOG",
        "ModpackInstaller");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "user-settings.json");

    public static string GetPreferredMinecraftFolder()
    {
        var saved = LoadLastTargetRoot();
        if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
        {
            return saved;
        }

        return MinecraftInstanceLocator.FindInstances().FirstOrDefault()
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
    }

    public static void SaveLastTargetRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(path.Trim());
            Directory.CreateDirectory(SettingsDirectory);
            var payload = JsonSerializer.Serialize(new InstallerUserSettingsModel(fullPath), JsonOptions);
            File.WriteAllText(SettingsPath, payload, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static string? LoadLastTargetRoot()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return null;
            }

            var payload = File.ReadAllText(SettingsPath, Encoding.UTF8);
            var model = JsonSerializer.Deserialize<InstallerUserSettingsModel>(payload, JsonOptions);
            return model?.LastTargetRoot;
        }
        catch
        {
            return null;
        }
    }
}

internal sealed record InstallerUserSettingsModel(string LastTargetRoot);

internal static class InstallerEngine
{
    private const long ResumableDownloadThresholdBytes = 64L * 1024 * 1024;
    private const int DownloadChunkBytes = 2 * 1024 * 1024;
    private const int DownloadMaxAttempts = 5;
    private static readonly TimeSpan DownloadHeadersTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(45);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static bool LogLineSuggestsRepairOrUpdate(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        return line.Contains("Instancia incompleta", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("DRY DOWNLOAD", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("faltando ", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("divergente", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Payload verificado:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Limpeza planejada:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("VERIFICAR ", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("ATUALIZAR ", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("BAIXAR ", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("REMOVER ", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task RunAsync(InstallerOptions options, IInstallerLog log, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var thisVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        var targetRoot = Path.GetFullPath(options.TargetRoot);
        var backupRoot = Path.Combine(targetRoot, ".pokedog-backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        log.Write("Preparando manifesto da nuvem...");
        log.ReportProgress(1);
        var manifest = await LoadManifestAsync(options.Manifest, http, log, cancellationToken);

        log.Write($"PokeDOG Modpack Installer {thisVersion}");
        log.Write($"Destino: {targetRoot}");
        log.Write($"Pack: {manifest.PackVersion} | Guard: {manifest.GuardVersion}");
        log.Write(options.DryRun
            ? "Modo verificacao: nada sera alterado."
            : options.ForceRepair
                ? "Modo reparo limpo: backups serao criados, mods/resourcepacks/shaderpacks gerenciados serao apagados e o payload sera reinstalado."
                : "Modo atualizacao: backups serao criados antes de substituir arquivos.");

        if (!options.DryRun)
        {
            Directory.CreateDirectory(targetRoot);
        }

        if (await MaybeUpdateInstallerAsync(manifest, thisVersion, targetRoot, http, options.DryRun, log, cancellationToken))
        {
            return;
        }

        var installState = await LoadInstalledStateAsync(targetRoot, cancellationToken);
        var payloadPath = await ResolvePayloadAsync(options.PayloadZip, manifest, installState, targetRoot, http, options.DryRun, options.ForceRepair, log, cancellationToken);
        PayloadApplyResult? payloadResult = null;
        if (!string.IsNullOrWhiteSpace(payloadPath))
        {
            payloadResult = await ExtractPayloadAsync(payloadPath, targetRoot, backupRoot, manifest.Payload, options.DryRun, log, cancellationToken);
            installState = MarkPayloadInstalled(installState, manifest, payloadResult);
        }
        else
        {
            log.Write("Cobbleverse base nao aplicado nesta etapa. O instalador usara deltas e URLs individuais quando necessario.");
        }

        if (payloadResult != null && (manifest.Payload?.RemoveMissing ?? false))
        {
            await RemoveMissingManagedFilesAsync(
                payloadResult.ManagedFiles,
                payloadResult.ManagedRoots,
                GetManifestManagedRetainedFiles(manifest, payloadResult.ManagedRoots),
                targetRoot,
                backupRoot,
                options.DryRun,
                log,
                cancellationToken);
        }
        else if (payloadResult == null && (manifest.Payload?.RemoveMissing ?? false))
        {
            var stateInventory = BuildPayloadInventoryFromState(installState, manifest.Payload);
            if (stateInventory != null)
            {
                await RemoveMissingManagedFilesAsync(
                    stateInventory.ManagedFiles,
                    stateInventory.ManagedRoots,
                    GetManifestManagedRetainedFiles(manifest, stateInventory.ManagedRoots),
                    targetRoot,
                    backupRoot,
                    options.DryRun,
                    log,
                    cancellationToken);
            }
            else
            {
                var cleanupPayload = await FindPayloadForCleanupAsync(options.PayloadZip, manifest, targetRoot, cancellationToken);
                if (!string.IsNullOrWhiteSpace(cleanupPayload))
                {
                    var inventory = await ReadPayloadInventoryAsync(cleanupPayload, manifest.Payload, log, cancellationToken);
                    installState = MarkPayloadInstalled(installState, manifest, inventory);
                    await RemoveMissingManagedFilesAsync(
                        inventory.ManagedFiles,
                        inventory.ManagedRoots,
                        GetManifestManagedRetainedFiles(manifest, inventory.ManagedRoots),
                        targetRoot,
                        backupRoot,
                        options.DryRun,
                        log,
                        cancellationToken);
                }
                else
                {
                    log.Write("Limpeza: payload em cache/local nao encontrado; limpeza de arquivos extras sera feita na proxima reparacao completa.");
                }
            }
        }

        installState = await ApplyUpdatePackagesAsync(manifest, installState, targetRoot, backupRoot, http, options.DryRun, log, cancellationToken);
        await ApplyManagedFilesAsync(manifest, targetRoot, backupRoot, http, options.DryRun, payloadResult != null, log, cancellationToken);
        if (!options.DryRun)
        {
            await SaveInstalledStateAsync(targetRoot, installState, cancellationToken);
        }
        log.ReportProgress(100);
    }

    private static async Task<string?> ResolvePayloadAsync(string localPayload, PokeDogManifest manifest, InstalledState? installState, string targetRoot, HttpClient http, bool dryRun, bool forceRepair, IInstallerLog log, CancellationToken cancellationToken)
    {
        var payload = manifest.Payload;
        var missingManagedRoots = GetMissingManagedRoots(targetRoot, payload);
        var missingManagedFiles = GetMissingManagedFilesFromState(installState, targetRoot, payload);
        var incompleteManagedRoots = missingManagedRoots.Count > 0;
        var incompleteManagedFiles = missingManagedFiles.Count > 0;
        var incompleteManagedContent = incompleteManagedRoots || incompleteManagedFiles;
        if (incompleteManagedRoots)
        {
            log.Write($"Instancia incompleta: pastas gerenciadas ausentes em {string.Join(", ", missingManagedRoots)}. O Cobbleverse sera reaplicado para corrigir a instalacao.");
        }
        if (incompleteManagedFiles)
        {
            log.Write($"Instancia incompleta: arquivo(s) gerenciados ausentes ({string.Join(", ", missingManagedFiles)}). O Cobbleverse sera reaplicado com o ZIP local/remoto para corrigir mods e dependencias.");
        }

        if (forceRepair)
        {
            log.Write("Reparo limpo solicitado. O Cobbleverse payload sera reaplicado mesmo que a instancia ja esteja atualizada.");
        }
        else if (!incompleteManagedContent && IsPayloadInstalled(manifest, installState))
        {
            if (HasPayloadInventory(installState))
            {
                log.Write("Estado salvo do payload encontrado. Validando inventario local do Cobbleverse...");
                log.ReportProgress(5);
                if (await LooksLikePayloadInstalledFromStateAsync(installState!, targetRoot, log, cancellationToken, 5, 45))
                {
                    log.Write($"Cobbleverse base confirmada pelo estado salvo: {payload?.Version}");
                    log.ReportProgress(45);
                    return null;
                }

                log.Write("Estado salvo do payload divergente. O instalador vai procurar ou baixar o ZIP completo para reparar a instancia.");
            }
            else if (!HasManagedContentInstalled(targetRoot, payload))
            {
                log.Write("Estado salvo do payload encontrado, mas a instancia nao contem o Cobbleverse base. O ZIP sera baixado novamente.");
            }
            else
            {
                log.Write("Estado salvo do payload encontrado, mas sem inventario detalhado. O instalador vai confirmar o Cobbleverse pelo ZIP local/cache ou baixar novamente se necessario.");
            }
        }

        var mirrorPayload = InstallerPaths.FindLocalPayloadMirror(payload);
        var cachedPayload = GetCachedPayloadPath(payload, targetRoot);
        var payloadCandidates = new[] { localPayload, mirrorPayload }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var payloadCandidate in payloadCandidates)
        {
            if (!File.Exists(payloadCandidate))
            {
                continue;
            }

            if (LooksLikeOfficialPayloadByNameAndSize(payloadCandidate, payload))
            {
                log.Write($"Payload local reconhecido por nome+tamanho: {payloadCandidate}");
                if (forceRepair || incompleteManagedContent || dryRun)
                {
                    log.ReportProgress(18);
                    return payloadCandidate;
                }
            }

            if (payload is { Sha256.Length: > 0 })
            {
                log.Write($"Validando hash do payload local: {payloadCandidate}");
                var localHash = await Sha256FileWithProgressAsync(payloadCandidate, log, cancellationToken, 8, 18, "Cobbleverse local");
                if (localHash.Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    if (forceRepair || incompleteManagedContent)
                    {
                        log.Write(forceRepair
                            ? $"Cobbleverse local validado para reparo limpo: {payloadCandidate}"
                            : $"Cobbleverse local validado para corrigir itens ausentes da instalacao: {payloadCandidate}");
                        log.ReportProgress(45);
                        return payloadCandidate;
                    }

                    log.Write($"Validando inventario do payload local: {payloadCandidate}");
                    if (await LooksLikePayloadAlreadyInstalledAsync(payloadCandidate, manifest, targetRoot, log, cancellationToken, 18, 45))
                    {
                        log.Write($"Cobbleverse base confirmada por inventario local: {payload?.Version}");
                        log.ReportProgress(45);
                        return null;
                    }

                    log.Write($"Cobbleverse local valido: {payloadCandidate}");
                    log.ReportProgress(45);
                    return payloadCandidate;
                }

                log.Write($"Payload local com hash diferente: {payloadCandidate}");
                if (string.IsNullOrWhiteSpace(payload.Url))
                {
                    throw new InvalidOperationException("Payload local esta diferente do manifesto e nao ha URL remota para reparar.");
                }
            }
            else
            {
                log.Write($"Cobbleverse local: {payloadCandidate}");
                log.ReportProgress(45);
                return payloadCandidate;
            }
        }

        if (payload is not { Url.Length: > 0, Sha256.Length: > 0 })
        {
            return null;
        }

        if (File.Exists(cachedPayload))
        {
            if (LooksLikeOfficialPayloadByNameAndSize(cachedPayload, payload))
            {
                log.Write($"Payload em cache reconhecido por nome+tamanho: {cachedPayload}");
                if (forceRepair || incompleteManagedContent || dryRun)
                {
                    log.ReportProgress(18);
                    return cachedPayload;
                }
            }

            log.Write($"Validando hash do payload em cache: {cachedPayload}");
            var cachedHash = await Sha256FileWithProgressAsync(cachedPayload, log, cancellationToken, 8, 18, "Cobbleverse em cache");
            if (cachedHash.Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                if (forceRepair || incompleteManagedContent)
                {
                    log.Write(forceRepair
                        ? $"Cobbleverse em cache validado para reparo limpo: {cachedPayload}"
                        : $"Cobbleverse em cache validado para corrigir itens ausentes da instalacao: {cachedPayload}");
                    log.ReportProgress(45);
                    return cachedPayload;
                }

                log.Write($"Validando inventario do payload em cache: {cachedPayload}");
                if (await LooksLikePayloadAlreadyInstalledAsync(cachedPayload, manifest, targetRoot, log, cancellationToken, 18, 45))
                {
                    log.Write($"Cobbleverse base confirmada por cache local: {payload?.Version}");
                    log.ReportProgress(45);
                    return null;
                }

                log.Write($"Cobbleverse em cache valido: {cachedPayload}");
                log.ReportProgress(45);
                return cachedPayload;
            }
        }

        if (dryRun)
        {
            log.Write($"Cobbleverse remoto disponivel: {payload.Url}");
            foreach (var mirror in payload.Mirrors ?? Array.Empty<string>())
            {
                log.Write($"Cobbleverse mirror disponivel: {mirror}");
            }
            log.Write("DRY DOWNLOAD payload completo sera baixado na instalacao.");
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cachedPayload)!);
        log.Write($"BAIXAR payload completo {payload.Version}");
        await DownloadPayloadWithMirrorsAsync(payload, cachedPayload, http, log, cancellationToken, 10, 45);
        return cachedPayload;
    }

    private static async Task<string?> FindPayloadForCleanupAsync(string localPayload, PokeDogManifest manifest, string targetRoot, CancellationToken cancellationToken)
    {
        var payload = manifest.Payload;
        if (payload is not { Sha256.Length: > 0 })
        {
            return null;
        }

        var cachedPayload = GetCachedPayloadPath(payload, targetRoot);
        var candidates = new[]
        {
            localPayload,
            InstallerPaths.FindLocalPayloadMirror(payload),
            cachedPayload
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var hash = await Sha256FileAsync(candidate, cancellationToken);
            if (hash.Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task DownloadPayloadWithMirrorsAsync(PayloadPackage payload, string cachedPayload, HttpClient http, IInstallerLog log, CancellationToken cancellationToken, int progressStart, int progressEnd)
    {
        Exception? lastError = null;
        var urls = GetPayloadDownloadUrls(payload).ToArray();
        for (var i = 0; i < urls.Length; i++)
        {
            var url = urls[i];
            try
            {
                var sourceLabel = i == 0 ? "Cobbleverse payload" : $"Cobbleverse payload mirror {i + 1}";
                await DownloadAndVerifyAsync(url, payload.Sha256, cachedPayload, http, false, log, cancellationToken, payload.Size, progressStart, progressEnd, sourceLabel);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
            {
                lastError = ex;
                log.Write($"DOWNLOAD mirror falhou: {ShortUrl(url)}");
                if (File.Exists(cachedPayload))
                {
                    File.Delete(cachedPayload);
                }
            }
        }

        throw new IOException("Nao foi possivel baixar o Cobbleverse por nenhum mirror configurado.", lastError);
    }

    private static IEnumerable<string> GetPayloadDownloadUrls(PayloadPackage payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.Url))
        {
            yield return NormalizeDownloadUrl(payload.Url);
        }

        foreach (var mirror in payload.Mirrors ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(mirror))
            {
                yield return NormalizeDownloadUrl(mirror);
            }
        }
    }

    private static string NormalizeDownloadUrl(string url)
    {
        if (!TryGetGoogleDriveFileId(url, out var id))
        {
            return url;
        }

        return $"https://drive.google.com/uc?export=download&id={Uri.EscapeDataString(id)}";
    }

    private static string GetQueryValue(string query, string key)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 && pieces[0].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pieces[1]);
            }
        }
        return "";
    }

    private static string ShortUrl(string url)
    {
        return url.Length <= 120 ? url : url[..117] + "...";
    }

    private static string AppendCacheBurst(string url)
    {
        var separator = url.Contains('?') ? "&" : "?";
        return $"{url}{separator}ts={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }

    private static async Task<string> DownloadManifestTextAsync(string url, HttpClient http, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AppendCacheBurst(url));
        request.Headers.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true,
            MustRevalidate = true
        };
        request.Headers.Pragma.ParseAdd("no-cache");
        request.Headers.UserAgent.ParseAdd("PokeDOG-Modpack-Installer/0.1");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static async Task<PokeDogManifest> LoadManifestAsync(string source, HttpClient http, IInstallerLog log, CancellationToken cancellationToken)
    {
        string json;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            log.Write($"Baixando manifesto: {source}");
            log.ReportProgress(3);
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(25));
                json = await DownloadManifestTextAsync(source, http, timeoutCts.Token);
                log.ReportProgress(8);
            }
            catch (Exception ex)
            {
                var fallback = InstallerPaths.FindLocalManifestFallback();
                if (string.IsNullOrWhiteSpace(fallback) || !File.Exists(fallback))
                {
                    throw new InvalidOperationException("Nao foi possivel baixar o manifesto online e nenhum manifesto local de fallback foi encontrado.", ex);
                }

                log.Write($"Manifesto online indisponivel. Usando fallback local: {fallback}");
                json = await File.ReadAllTextAsync(fallback, cancellationToken);
                log.ReportProgress(8);
            }
        }
        else if (!string.IsNullOrWhiteSpace(source) && File.Exists(source))
        {
            log.Write($"Manifesto local: {source}");
            log.ReportProgress(8);
            json = await File.ReadAllTextAsync(source, cancellationToken);
        }
        else
        {
            log.Write("Manifesto local nao encontrado. Usando manifesto embutido do instalador.");
            log.ReportProgress(8);
            await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("pokedog_manifest.default.json")
                ?? throw new FileNotFoundException("Manifesto local ausente e manifesto embutido nao encontrado.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            json = await reader.ReadToEndAsync(cancellationToken);
        }

        json = json.TrimStart('\uFEFF');
        var manifest = JsonSerializer.Deserialize<PokeDogManifest>(json, JsonOptions);
        if (manifest == null)
        {
            throw new InvalidOperationException("Manifesto vazio ou invalido.");
        }
        return manifest;
    }

    private static async Task<bool> MaybeUpdateInstallerAsync(PokeDogManifest manifest, string thisVersion, string targetRoot, HttpClient http, bool dryRun, IInstallerLog log, CancellationToken cancellationToken)
    {
        if (manifest.Installer is not { Url.Length: > 0, Sha256.Length: > 0 } installer)
        {
            log.Write("Atualizador: instalador ja esta atualizado.");
            log.ReportProgress(10);
            return false;
        }

        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
        {
            throw new InvalidOperationException("Nao foi possivel localizar o executavel atual para auto-update.");
        }

        var currentHash = await Sha256FileAsync(currentExe, cancellationToken);
        if (currentHash.Equals(installer.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            log.Write("Atualizador: instalador ja esta atualizado.");
            log.ReportProgress(10);
            return false;
        }

        var hasNewerVersion = IsNewerVersion(installer.Version, thisVersion);
        if (!hasNewerVersion)
        {
            log.Write($"Atualizador: mesma versao {thisVersion}, mas hash remoto diferente detectado. Aplicando revisao publicada do instalador.");
        }
        else
        {
            log.Write($"Nova revisao do instalador disponivel: {installer.Version}");
        }

        var destination = currentExe + ".update";
        await DownloadAndVerifyAsync(installer.Url, installer.Sha256, destination, http, dryRun, log, cancellationToken, installer.Size > 0 ? installer.Size : null, 10, 25, "Atualizador do installer");
        if (dryRun)
        {
            return false;
        }
        ScheduleSelfReplace(currentExe, destination, log);
        log.Write("Instalador atualizado. Feche esta janela e abra o PokeDOG-Modpack-Installer novamente para concluir com a nova versao.");
        return true;
    }

    internal static async Task<bool> TrySelfUpdateBeforeInstallAsync(string manifestSource, IInstallerLog log, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var manifest = await LoadManifestAsync(manifestSource, http, log, cancellationToken);
        var thisVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        return await MaybeUpdateInstallerAsync(manifest, thisVersion, AppContext.BaseDirectory, http, false, log, cancellationToken);
    }

    private static void ScheduleSelfReplace(string currentExe, string updateExe, IInstallerLog log)
    {
        var script = Path.Combine(Path.GetTempPath(), "pokedog-installer-self-update-" + Guid.NewGuid().ToString("N") + ".cmd");
        var content = $"""
@echo off
set "POKEDOG_UPDATE={updateExe}"
set "POKEDOG_CURRENT={currentExe}"
for /l %%i in (1,1,120) do (
  copy /y "%POKEDOG_UPDATE%" "%POKEDOG_CURRENT%" >nul 2>nul && goto copied
  timeout /t 1 /nobreak >nul
)
exit /b 1
:copied
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

    private static async Task<PayloadApplyResult> ExtractPayloadAsync(string zipPath, string targetRoot, string backupRoot, PayloadPackage? payload, bool dryRun, IInstallerLog log, CancellationToken cancellationToken)
    {
        log.Write($"Payload local: {zipPath}");
        log.ReportProgress(45);
        using var archive = ZipFile.OpenRead(zipPath);
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        var managedRoots = NormalizeManagedRoots(payload?.ManagedRoots);
        var managedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var managedFileStates = new List<PayloadFileState>();
        var done = 0;
        var plannedUpdates = 0;
        var preservedUserFiles = 0;
        if (!dryRun)
        {
            await RemoveManagedRootContentsAsync(targetRoot, backupRoot, managedRoots, log, cancellationToken);
        }
        foreach (var entry in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(entry.FullName);
            var managedEntry = IsUnderManagedRoots(relativePath, managedRoots);
            string? expectedHash = null;
            if (managedEntry)
            {
                managedFiles.Add(NormalizeKey(relativePath));
                expectedHash = await Sha256ZipEntryAsync(entry, cancellationToken);
                managedFileStates.Add(new PayloadFileState(relativePath, expectedHash, entry.Length));
            }

            var destination = Path.Combine(targetRoot, relativePath);
            EnsureInsideRoot(destination, targetRoot);
            if (IsUserOwnedRootFile(relativePath) || (File.Exists(destination) && IsUserConfigFile(relativePath)))
            {
                preservedUserFiles++;
                done++;
                log.ReportProgress(45 + done * 40 / Math.Max(1, files.Count));
                continue;
            }
            var needsUpdate = !File.Exists(destination);
            if (!needsUpdate)
            {
                expectedHash ??= await Sha256ZipEntryAsync(entry, cancellationToken);
                needsUpdate = await Sha256FileAsync(destination, cancellationToken) != expectedHash;
            }
            if (!needsUpdate)
            {
                done++;
                log.ReportProgress(45 + done * 40 / Math.Max(1, files.Count));
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
            log.ReportProgress(45 + done * 40 / Math.Max(1, files.Count));
        }
        if (dryRun && plannedUpdates > 40)
        {
            log.Write($"... mais {plannedUpdates - 40} arquivos seriam verificados/atualizados pelo payload.");
        }
        if (preservedUserFiles > 0)
        {
            log.Write($"Dados do usuario preservados: {preservedUserFiles} arquivo(s) de configuracao/opcoes/lista de servidores.");
        }
        log.Write(dryRun ? $"Payload verificado: {plannedUpdates} arquivo(s) precisam instalar/atualizar." : $"Payload aplicado: {plannedUpdates} arquivo(s) instalados/atualizados.");
        return new PayloadApplyResult(managedFiles, managedRoots, managedFileStates);
    }

    private static async Task RemoveManagedRootContentsAsync(string targetRoot, string backupRoot, IReadOnlyList<string> managedRoots, IInstallerLog log, CancellationToken cancellationToken)
    {
        await Task.Yield();
        foreach (var root in managedRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absoluteRoot = Path.Combine(targetRoot, root);
            EnsureInsideRoot(absoluteRoot, targetRoot);
            if (!Directory.Exists(absoluteRoot))
            {
                continue;
            }

            var files = Directory.EnumerateFiles(absoluteRoot, "*", SearchOption.AllDirectories).ToList();
            var deleted = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BackupFile(file, backupRoot, targetRoot);
                File.Delete(file);
                deleted++;
            }

            if (deleted > 0)
            {
                log.Write($"Instalacao limpa: {deleted} arquivo(s) removidos de {root} com backup.");
            }
        }
    }

    private static bool IsUserOwnedRootFile(string relativePath)
    {
        var normalized = NormalizeKey(relativePath);
        if (normalized.Contains('/'))
        {
            return false;
        }
        return normalized is "servers.dat" or "servers.dat_old" ||
            (normalized.StartsWith("options", StringComparison.OrdinalIgnoreCase) && normalized.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUserConfigFile(string relativePath)
    {
        return NormalizeKey(relativePath).StartsWith("config/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<PayloadApplyResult> ReadPayloadInventoryAsync(string zipPath, PayloadPackage? payload, IInstallerLog log, CancellationToken cancellationToken, bool announce = true)
    {
        await Task.Yield();
        if (announce)
        {
            log.Write($"Limpeza: usando inventario do payload {zipPath}");
        }
        using var archive = ZipFile.OpenRead(zipPath);
        var managedRoots = NormalizeManagedRoots(payload?.ManagedRoots);
        var managedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var managedFileStates = new List<PayloadFileState>();
        foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(entry.FullName);
            if (IsUnderManagedRoots(relativePath, managedRoots))
            {
                managedFiles.Add(NormalizeKey(relativePath));
                managedFileStates.Add(new PayloadFileState(relativePath, await Sha256ZipEntryAsync(entry, cancellationToken), entry.Length));
            }
        }

        return new PayloadApplyResult(managedFiles, managedRoots, managedFileStates);
    }

    private static async Task RemoveMissingManagedFilesAsync(HashSet<string> payloadFiles, IReadOnlyList<string> managedRoots, HashSet<string> retainedFiles, string targetRoot, string backupRoot, bool dryRun, IInstallerLog log, CancellationToken cancellationToken)
    {
        await Task.Yield();
        log.Write("Limpeza: verificando arquivos removidos do modpack.");
        log.ReportProgress(88);
        var plannedRemovals = 0;
        var candidates = new List<string>();
        foreach (var root in managedRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absoluteRoot = Path.Combine(targetRoot, root);
            EnsureInsideRoot(absoluteRoot, targetRoot);
            if (!Directory.Exists(absoluteRoot))
            {
                continue;
            }

            candidates.AddRange(Directory.EnumerateFiles(absoluteRoot, "*", SearchOption.AllDirectories));
        }

        var processed = 0;
        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(targetRoot, file));
            var normalized = NormalizeKey(relativePath);
            if (retainedFiles.Contains(normalized))
            {
                processed++;
                if (processed % 250 == 0 || processed == candidates.Count)
                {
                    log.Write($"Limpeza: {processed}/{candidates.Count} arquivos verificados.");
                }
                continue;
            }

            if (!payloadFiles.Contains(normalized))
            {
                plannedRemovals++;
                if (!dryRun || plannedRemovals <= 40)
                {
                    log.Write($"REMOVER arquivo fora do payload {relativePath}");
                }

                if (!dryRun)
                {
                    BackupFile(file, backupRoot, targetRoot);
                    File.Delete(file);
                    log.Write($"REMOVIDO {relativePath}");
                }
            }

            processed++;
            log.ReportProgress(88 + processed * 4 / Math.Max(1, candidates.Count));
            if (processed % 250 == 0 || processed == candidates.Count)
            {
                log.Write($"Limpeza: {processed}/{candidates.Count} arquivos verificados.");
            }
        }

        if (dryRun && plannedRemovals > 40)
        {
            log.Write($"... mais {plannedRemovals - 40} arquivos seriam removidos por nao estarem no payload gerenciado.");
        }

        if (plannedRemovals > 0)
        {
            log.Write(dryRun ? $"Limpeza planejada: {plannedRemovals} arquivo(s) fora do payload." : $"Limpeza aplicada: {plannedRemovals} arquivo(s) fora do payload removidos com backup.");
        }
        log.ReportProgress(92);
    }

    private static async Task<InstalledState> ApplyUpdatePackagesAsync(PokeDogManifest manifest, InstalledState? state, string targetRoot, string backupRoot, HttpClient http, bool dryRun, IInstallerLog log, CancellationToken cancellationToken)
    {
        var current = state ?? CreateInstalledState(manifest);
        var packages = manifest.UpdatePackages ?? Array.Empty<UpdatePackage>();
        if (packages.Count == 0)
        {
            log.Write("Deltas: nenhum pacote incremental pendente.");
            log.ReportProgress(90);
            return current;
        }

        var applied = new HashSet<string>(current.AppliedPackages ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var version = package.Version ?? "";
            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            if (applied.Contains(version))
            {
                log.Write($"DELTA {version} ja aplicado.");
                continue;
            }

            log.Write($"DELTA pendente {version}");
            await ApplyDeltaRemovalsAsync(package, targetRoot, backupRoot, dryRun, log, cancellationToken);

            if (!string.IsNullOrWhiteSpace(package.Url) && !string.IsNullOrWhiteSpace(package.Sha256))
            {
                var cacheDir = Path.Combine(targetRoot, ".pokedog-cache", "updates");
                var cachePath = Path.Combine(cacheDir, $"pokedog-delta-{SanitizeFileName(version)}.zip");
                if (!dryRun)
                {
                    Directory.CreateDirectory(cacheDir);
                    if (!File.Exists(cachePath) || !string.Equals(await Sha256FileAsync(cachePath, cancellationToken), package.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        await DownloadAndVerifyAsync(package.Url, package.Sha256, cachePath, http, false, log, cancellationToken, package.Size, 45, 88, $"Delta {version}");
                    }
                    await ExtractDeltaPackageAsync(cachePath, targetRoot, backupRoot, dryRun, log, cancellationToken);
                }
                else
                {
                    log.Write($"DRY DOWNLOAD delta {version}: {package.Url}");
                    if (package.Files is { Count: > 0 })
                    {
                        foreach (var file in package.Files.Take(20))
                        {
                            log.Write($"DELTA atualizaria {NormalizeRelativePath(file)}");
                        }
                        if (package.Files.Count > 20)
                        {
                            log.Write($"... mais {package.Files.Count - 20} arquivo(s) no delta {version}.");
                        }
                    }
                }
            }

            if (!dryRun)
            {
                applied.Add(version);
            }
        }

        return current with
        {
            PackVersion = manifest.PackVersion,
            PayloadVersion = current.PayloadVersion,
            PayloadSha256 = current.PayloadSha256,
            AppliedPackages = applied.ToArray(),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static Task ApplyDeltaRemovalsAsync(UpdatePackage package, string targetRoot, string backupRoot, bool dryRun, IInstallerLog log, CancellationToken cancellationToken)
    {
        foreach (var remove in package.Removes ?? Array.Empty<string>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(remove);
            var destination = Path.Combine(targetRoot, relativePath);
            EnsureInsideRoot(destination, targetRoot);
            if (!File.Exists(destination))
            {
                log.Write($"DELTA remover ausente {relativePath}");
                continue;
            }

            log.Write($"DELTA remover {relativePath}");
            if (!dryRun)
            {
                BackupFile(destination, backupRoot, targetRoot);
                File.Delete(destination);
            }
        }
        return Task.CompletedTask;
    }

    private static Task ExtractDeltaPackageAsync(string zipPath, string targetRoot, string backupRoot, bool dryRun, IInstallerLog log, CancellationToken cancellationToken)
    {
        log.Write($"DELTA aplicando {zipPath}");
        using var archive = ZipFile.OpenRead(zipPath);
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        var done = 0;
        foreach (var entry in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(entry.FullName);
            var destination = Path.Combine(targetRoot, relativePath);
            EnsureInsideRoot(destination, targetRoot);
            log.Write($"DELTA atualizar {relativePath}");
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
            log.ReportProgress(45 + done * 43 / Math.Max(1, files.Count));
        }
        return Task.CompletedTask;
    }

    private static async Task ApplyManagedFilesAsync(PokeDogManifest manifest, string targetRoot, string backupRoot, HttpClient http, bool dryRun, bool payloadPresent, IInstallerLog log, CancellationToken cancellationToken)
    {
        foreach (var file in manifest.Files ?? Array.Empty<ManifestFile>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(file.Path);
            var destination = Path.Combine(targetRoot, relativePath);
            EnsureInsideRoot(destination, targetRoot);
            var policy = file.Policy ?? "required";

            if (policy.Equals("delete", StringComparison.OrdinalIgnoreCase))
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

            if (policy.Equals("preserve-if-exists", StringComparison.OrdinalIgnoreCase) && File.Exists(destination))
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

            var localMirror = await InstallerPaths.FindLocalManagedFileAsync(relativePath, file.Sha256, cancellationToken);
            if (!string.IsNullOrWhiteSpace(localMirror))
            {
                log.Write($"COPIAR mirror local {relativePath}");
                if (!dryRun)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(localMirror, destination, true);
                }
                continue;
            }

            if (dryRun && payloadPresent)
            {
                log.Write($"OK {relativePath} sera fornecido pelo payload.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(file.Url) || file.Url.Contains("example.invalid", StringComparison.OrdinalIgnoreCase))
            {
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
            await DownloadAndVerifyAsync(file.Url, file.Sha256, temp, http, false, log, cancellationToken, file.Size, 92, 98, relativePath);
            File.Move(temp, destination, true);
        }
    }

    private static async Task DownloadAndVerifyAsync(
        string url,
        string sha256,
        string destination,
        HttpClient http,
        bool dryRun,
        IInstallerLog log,
        CancellationToken cancellationToken,
        long? expectedSize = null,
        int progressStart = 0,
        int progressEnd = 0,
        string? label = null)
    {
        if (dryRun)
        {
            log.Write($"DRY DOWNLOAD {url}");
            return;
        }

        url = NormalizeDownloadUrl(url);
        var displayLabel = string.IsNullOrWhiteSpace(label) ? Path.GetFileName(destination) : label;
        url = await ResolveDownloadUrlAsync(url, http, displayLabel, cancellationToken);
        if ((expectedSize ?? 0) >= ResumableDownloadThresholdBytes)
        {
            try
            {
                await DownloadResumableAsync(url, sha256, destination, http, log, cancellationToken, expectedSize, progressStart, progressEnd, displayLabel);
                return;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Nao foi possivel descobrir o tamanho", StringComparison.OrdinalIgnoreCase))
            {
                log.Write($"DOWNLOAD retomavel indisponivel para {displayLabel}; usando copia direta.");
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("nao aceitou Range", StringComparison.OrdinalIgnoreCase))
            {
                log.Write($"Servidor sem suporte a Range para {displayLabel}; usando copia direta.");
            }
        }

        log.Write($"DOWNLOAD iniciando {displayLabel}");
        await DownloadStreamAsync(url, destination, http, log, cancellationToken, expectedSize, progressStart, progressEnd, displayLabel);

        var actual = await Sha256FileAsync(destination, cancellationToken);
        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destination);
            throw new InvalidOperationException($"Hash invalido para {url}. Esperado {sha256}, recebido {actual}.");
        }
        if (progressEnd > progressStart)
        {
            log.ReportProgress(progressEnd);
        }
        log.Write($"DOWNLOAD concluido {displayLabel}");
    }

    private static async Task DownloadStreamAsync(
        string url,
        string destination,
        HttpClient http,
        IInstallerLog log,
        CancellationToken cancellationToken,
        long? expectedSize,
        int progressStart,
        int progressEnd,
        string displayLabel)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await EnsureBinaryDownloadResponseAsync(response, displayLabel, cancellationToken);
        var totalBytes = response.Content.Headers.ContentLength ?? expectedSize ?? 0;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(destination);
        var buffer = new byte[1024 * 128];
        long downloaded = 0;
        var lastLoggedPercent = -1;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            if (totalBytes > 0)
            {
                var percent = (int)Math.Floor(downloaded * 100d / totalBytes);
                if (progressEnd > progressStart)
                {
                    log.ReportProgress(progressStart + percent * (progressEnd - progressStart) / 100);
                }

                if (percent >= lastLoggedPercent + 10 || percent == 100)
                {
                    lastLoggedPercent = percent;
                    log.Write($"DOWNLOAD {displayLabel}: {FormatBytes(downloaded)} / {FormatBytes(totalBytes)} ({percent}%)");
                }
            }
            else if (downloaded == read || downloaded % (1024 * 1024) < read)
            {
                log.Write($"DOWNLOAD {displayLabel}: {FormatBytes(downloaded)} recebidos...");
                if (progressEnd > progressStart)
                {
                    var pulse = progressStart + (int)Math.Min(progressEnd - progressStart, 5 + (downloaded / (1024 * 1024)) * 2);
                    log.ReportProgress(pulse);
                }
            }
        }

        if (progressEnd > progressStart)
        {
            log.ReportProgress(progressEnd);
        }
    }

    private static async Task DownloadResumableAsync(
        string url,
        string sha256,
        string destination,
        HttpClient http,
        IInstallerLog log,
        CancellationToken cancellationToken,
        long? expectedSize,
        int progressStart,
        int progressEnd,
        string displayLabel)
    {
        var partPath = destination + ".part";
        var totalBytes = expectedSize ?? await GetRemoteContentLengthAsync(url, http, cancellationToken);
        if (totalBytes <= 0)
        {
            throw new InvalidOperationException($"Nao foi possivel descobrir o tamanho de {displayLabel} para download retomavel.");
        }

        if (!File.Exists(partPath) && File.Exists(destination))
        {
            var existingSize = new FileInfo(destination).Length;
            if (existingSize > 0 && existingSize < totalBytes)
            {
                File.Move(destination, partPath, true);
            }
        }

        var downloaded = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (downloaded > totalBytes)
        {
            File.Delete(partPath);
            downloaded = 0;
        }

        log.Write(downloaded > 0
            ? $"DOWNLOAD retomando {displayLabel}: {FormatBytes(downloaded)} / {FormatBytes(totalBytes)}"
            : $"DOWNLOAD iniciando {displayLabel} em blocos retomaveis");

        var lastLoggedPercent = totalBytes > 0 ? (int)Math.Floor(downloaded * 100d / totalBytes) - 10 : -1;
        var lastReportedProgress = -1;
        var activityTimer = Stopwatch.StartNew();
        var activityBytes = downloaded;
        void ReportDownloadActivity(long currentBytes)
        {
            var percent = (int)Math.Floor(currentBytes * 100d / totalBytes);
            if (progressEnd > progressStart)
            {
                var mappedProgress = progressStart + percent * (progressEnd - progressStart) / 100;
                if (mappedProgress != lastReportedProgress)
                {
                    lastReportedProgress = mappedProgress;
                    log.ReportProgress(mappedProgress);
                }
            }

            if (activityTimer.Elapsed < TimeSpan.FromSeconds(10))
            {
                return;
            }

            var elapsedSeconds = Math.Max(activityTimer.Elapsed.TotalSeconds, 0.001);
            var bytesPerSecond = Math.Max(0, currentBytes - activityBytes) / elapsedSeconds;
            log.Write($"DOWNLOAD ativo {displayLabel}: {FormatBytes(currentBytes)} / {FormatBytes(totalBytes)} ({currentBytes * 100d / totalBytes:0.0}%) - {FormatBytes((long)bytesPerSecond)}/s");
            activityBytes = currentBytes;
            activityTimer.Restart();
        }

        while (downloaded < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkEnd = Math.Min(downloaded + DownloadChunkBytes - 1, totalBytes - 1);
            var success = false;
            Exception? lastError = null;

            for (var attempt = 1; attempt <= DownloadMaxAttempts && !success; attempt++)
            {
                try
                {
                    await DownloadRangeAsync(url, partPath, downloaded, chunkEnd, http, cancellationToken, ReportDownloadActivity);
                    downloaded = new FileInfo(partPath).Length;
                    success = true;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    lastError = ex;
                    if (File.Exists(partPath))
                    {
                        downloaded = Math.Min(new FileInfo(partPath).Length, totalBytes);
                    }
                    log.Write($"DOWNLOAD {displayLabel}: tentativa {attempt}/{DownloadMaxAttempts} falhou no bloco {FormatBytes(downloaded)}. Retentando...");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * attempt, 10)), cancellationToken);
                }
            }

            if (!success)
            {
                throw new IOException($"Falha ao baixar {displayLabel} apos {DownloadMaxAttempts} tentativas. O arquivo parcial foi preservado para retomar depois.", lastError);
            }

            var percent = (int)Math.Floor(downloaded * 100d / totalBytes);
            if (progressEnd > progressStart)
            {
                log.ReportProgress(progressStart + percent * (progressEnd - progressStart) / 100);
            }

            if (percent >= lastLoggedPercent + 5 || percent == 100)
            {
                lastLoggedPercent = percent;
                log.Write($"DOWNLOAD {displayLabel}: {FormatBytes(downloaded)} / {FormatBytes(totalBytes)} ({percent}%)");
            }
        }

        File.Move(partPath, destination, true);
        var actual = await Sha256FileAsync(destination, cancellationToken);
        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destination);
            throw new InvalidOperationException($"Hash invalido para {url}. Esperado {sha256}, recebido {actual}.");
        }

        if (progressEnd > progressStart)
        {
            log.ReportProgress(progressEnd);
        }
        log.Write($"DOWNLOAD concluido {displayLabel}");
    }

    private static async Task DownloadRangeAsync(string url, string partPath, long start, long end, HttpClient http, CancellationToken cancellationToken, Action<long>? reportActivity = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);
        using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headersTimeout.CancelAfter(DownloadHeadersTimeout);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token);
        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            throw new HttpRequestException($"Servidor nao aceitou Range {start}-{end}: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        await EnsureBinaryDownloadResponseAsync(response, Path.GetFileName(partPath), cancellationToken);
        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange?.From != start || contentRange.To != end)
        {
            throw new HttpRequestException($"Servidor respondeu faixa inesperada para {start}-{end}: {contentRange}.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        output.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[1024 * 128];
        var remaining = end - start + 1;
        long written = 0;
        while (remaining > 0)
        {
            using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idleTimeout.CancelAfter(DownloadIdleTimeout);
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), idleTimeout.Token);
            if (read == 0)
            {
                throw new IOException("Conexao encerrada antes de completar o bloco.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
            written += read;
            reportActivity?.Invoke(start + written);
        }
        output.SetLength(end + 1);
    }

    private static async Task<long> GetRemoteContentLengthAsync(string url, HttpClient http, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response.Content.Headers.ContentLength ?? 0;
    }

    private static bool TryGetGoogleDriveFileId(string url, out string id)
    {
        id = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        if (!host.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase) &&
            !host.Contains("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = Regex.Match(uri.AbsoluteUri, @"/file/d/([^/]+)");
        id = match.Success ? match.Groups[1].Value : GetQueryValue(uri.Query, "id");
        return !string.IsNullOrWhiteSpace(id);
    }

    private static async Task<string> ResolveDownloadUrlAsync(string url, HttpClient http, string displayLabel, CancellationToken cancellationToken)
    {
        if (!TryGetGoogleDriveFileId(url, out _))
        {
            return url;
        }

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (html.Contains("Virus scan warning", StringComparison.OrdinalIgnoreCase) &&
            html.Contains("download-form", StringComparison.OrdinalIgnoreCase))
        {
            var actionMatch = Regex.Match(html, "<form id=\"download-form\" action=\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (!actionMatch.Success)
            {
                throw new InvalidOperationException($"Google Drive pediu confirmacao para {displayLabel}, mas o instalador nao conseguiu localizar a URL final de download.");
            }

            var inputs = Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\"", RegexOptions.IgnoreCase)
                .Select(match => $"{Uri.EscapeDataString(match.Groups[1].Value)}={Uri.EscapeDataString(match.Groups[2].Value)}");
            var query = string.Join("&", inputs);
            return $"{actionMatch.Groups[1].Value}?{query}";
        }

        if (html.Contains("Quota exceeded", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("Too many users have viewed or downloaded this file recently", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Google Drive bloqueou temporariamente o download de {displayLabel} por excesso de cota/visualizacoes. O instalador tentara outro mirror.");
        }

        throw new InvalidOperationException($"Google Drive retornou uma pagina HTML inesperada em vez do arquivo de {displayLabel}.");
    }

    private static async Task EnsureBinaryDownloadResponseAsync(HttpResponseMessage response, string displayLabel, CancellationToken cancellationToken)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
            !contentType.Contains("text/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Contains("Quota exceeded", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("Too many users have viewed or downloaded this file recently", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Google Drive bloqueou temporariamente o download de {displayLabel} por excesso de cota/visualizacoes. O instalador tentara outro mirror.");
        }

        if (body.Contains("Virus scan warning", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("download-form", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Google Drive ainda retornou uma pagina de confirmacao para {displayLabel} em vez do arquivo binario.");
        }

        throw new InvalidOperationException($"O servidor respondeu HTML/texto ao baixar {displayLabel}, em vez do arquivo esperado.");
    }

    private static void BackupFile(string file, string backupRoot, string targetRoot)
    {
        var rel = Path.GetRelativePath(targetRoot, file);
        var backup = Path.Combine(backupRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(file, backup, true);
    }

    private static string InstalledStatePath(string targetRoot)
    {
        return Path.Combine(targetRoot, ".pokedog-cache", "installed-state.json");
    }

    private static async Task<InstalledState?> LoadInstalledStateAsync(string targetRoot, CancellationToken cancellationToken)
    {
        var path = InstalledStatePath(targetRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<InstalledState>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task SaveInstalledStateAsync(string targetRoot, InstalledState state, CancellationToken cancellationToken)
    {
        var path = InstalledStatePath(targetRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        var json = JsonSerializer.Serialize(state with { UpdatedAtUtc = DateTimeOffset.UtcNow }, JsonOptions);
        await File.WriteAllTextAsync(temporaryPath, json, Encoding.UTF8, cancellationToken);
        File.Move(temporaryPath, path, true);
    }

    private static InstalledState CreateInstalledState(PokeDogManifest manifest)
    {
        return new InstalledState(
            manifest.PackVersion,
            "",
            "",
            Array.Empty<string>(),
            Array.Empty<PayloadFileState>(),
            DateTimeOffset.UtcNow);
    }

    private static InstalledState MarkPayloadInstalled(InstalledState? state, PokeDogManifest manifest, PayloadApplyResult? payloadResult)
    {
        var current = state ?? CreateInstalledState(manifest);
        return current with
        {
            PackVersion = manifest.PackVersion,
            PayloadVersion = manifest.Payload?.Version ?? "",
            PayloadSha256 = manifest.Payload?.Sha256 ?? "",
            PayloadFiles = payloadResult?.ManagedFileStates ?? current.PayloadFiles ?? Array.Empty<PayloadFileState>(),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static bool IsPayloadInstalled(PokeDogManifest manifest, InstalledState? state)
    {
        return manifest.Payload is { } payload && state != null &&
            string.Equals(payload.Version, state.PayloadVersion, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(payload.Sha256, state.PayloadSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasPayloadInventory(InstalledState? state)
    {
        return state?.PayloadFiles?.Count > 0;
    }

    private static bool HasManagedContentInstalled(string targetRoot, PayloadPackage? payload)
    {
        foreach (var root in NormalizeManagedRoots(payload?.ManagedRoots))
        {
            var absoluteRoot = Path.Combine(targetRoot, root);
            if (!Directory.Exists(absoluteRoot))
            {
                continue;
            }

            try
            {
                if (Directory.EnumerateFiles(absoluteRoot, "*", SearchOption.AllDirectories).Take(1).Any())
                {
                    return true;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    private static IReadOnlyList<string> GetMissingManagedRoots(string targetRoot, PayloadPackage? payload)
    {
        var missing = new List<string>();
        foreach (var root in NormalizeManagedRoots(payload?.ManagedRoots))
        {
            var absoluteRoot = Path.Combine(targetRoot, root);
            EnsureInsideRoot(absoluteRoot, targetRoot);
            if (!Directory.Exists(absoluteRoot))
            {
                missing.Add(root);
            }
        }

        return missing;
    }

    private static IReadOnlyList<string> GetMissingManagedFilesFromState(InstalledState? state, string targetRoot, PayloadPackage? payload)
    {
        if (!HasPayloadInventory(state))
        {
            return Array.Empty<string>();
        }

        var managedRoots = NormalizeManagedRoots(payload?.ManagedRoots);
        var missing = new List<string>();
        foreach (var file in state!.PayloadFiles ?? Array.Empty<PayloadFileState>())
        {
            var relativePath = NormalizeRelativePath(file.Path);
            if (!IsUnderManagedRoots(relativePath, managedRoots))
            {
                continue;
            }

            var destination = Path.Combine(targetRoot, relativePath);
            EnsureInsideRoot(destination, targetRoot);
            if (!File.Exists(destination))
            {
                missing.Add(relativePath);
                if (missing.Count >= 5)
                {
                    break;
                }
            }
        }

        return missing;
    }

    private static bool LooksLikeOfficialPayloadByNameAndSize(string path, PayloadPackage? payload)
    {
        if (payload == null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        var fileName = Path.GetFileName(path);
        if (!string.Equals(fileName, "cobbleverse_payload.zip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (payload.Size <= 0)
        {
            return true;
        }

        try
        {
            return new FileInfo(path).Length == payload.Size;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<bool> LooksLikePayloadAlreadyInstalledAsync(string payloadPath, PokeDogManifest manifest, string targetRoot, IInstallerLog log, CancellationToken cancellationToken, int progressStart = 5, int progressEnd = 45)
    {
        await Task.Yield();
        if (!File.Exists(payloadPath))
        {
            return false;
        }

        using var archive = ZipFile.OpenRead(payloadPath);
        var managedRoots = NormalizeManagedRoots(manifest.Payload?.ManagedRoots);
        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retainedFiles = GetManifestManagedRetainedFiles(manifest, managedRoots);
        var entries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        var totalEntries = Math.Max(1, entries.Count);
        var processedEntries = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(entry.FullName);
            if (!IsUnderManagedRoots(relativePath, managedRoots))
            {
                processedEntries++;
                log.ReportProgress(progressStart + processedEntries * (progressEnd - progressStart) / totalEntries);
                continue;
            }

            var normalized = NormalizeKey(relativePath);
            expectedFiles.Add(normalized);
            var destination = Path.Combine(targetRoot, relativePath);
            EnsureInsideRoot(destination, targetRoot);
            if (!File.Exists(destination))
            {
                log.Write($"Cobbleverse local incompleto: faltando {relativePath}");
                return false;
            }

            var info = new FileInfo(destination);
            if (info.Length != entry.Length)
            {
                log.Write($"Cobbleverse local divergente: tamanho diferente em {relativePath}");
                return false;
            }

            var installedHash = await Sha256FileAsync(destination, cancellationToken);
            var expectedHash = await Sha256ZipEntryAsync(entry, cancellationToken);
            if (!installedHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                log.Write($"Cobbleverse local divergente: hash diferente em {relativePath}");
                return false;
            }

            processedEntries++;
            log.ReportProgress(progressStart + processedEntries * (progressEnd - progressStart) / totalEntries);
            if (processedEntries % 250 == 0 || processedEntries == totalEntries)
            {
                log.Write($"Cobbleverse local: {processedEntries}/{totalEntries} arquivo(s) verificados.");
            }
        }

        foreach (var root in managedRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absoluteRoot = Path.Combine(targetRoot, root);
            EnsureInsideRoot(absoluteRoot, targetRoot);
            if (!Directory.Exists(absoluteRoot))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(absoluteRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = NormalizeRelativePath(Path.GetRelativePath(targetRoot, file));
                var normalized = NormalizeKey(relativePath);
                if (retainedFiles.Contains(normalized))
                {
                    continue;
                }

                if (!expectedFiles.Contains(normalized))
                {
                    log.Write($"Cobbleverse local divergente: arquivo extra fora do payload em {relativePath}");
                    return false;
                }
            }
        }

        return expectedFiles.Count > 0;
    }

    private static async Task<bool> LooksLikePayloadInstalledFromStateAsync(InstalledState state, string targetRoot, IInstallerLog log, CancellationToken cancellationToken, int progressStart = 5, int progressEnd = 45)
    {
        await Task.Yield();
        var files = (state.PayloadFiles ?? Array.Empty<PayloadFileState>())
            .Where(file => !string.IsNullOrWhiteSpace(file.Path))
            .ToList();
        if (files.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[i];
            var relativePath = NormalizeRelativePath(file.Path);
            var destination = Path.Combine(targetRoot, relativePath);
            EnsureInsideRoot(destination, targetRoot);
            if (!File.Exists(destination))
            {
                log.Write($"Cobbleverse salvo incompleto: faltando {relativePath}");
                return false;
            }

            var info = new FileInfo(destination);
            if (file.Size > 0 && info.Length != file.Size)
            {
                log.Write($"Cobbleverse salvo divergente: tamanho diferente em {relativePath}");
                return false;
            }

            var installedHash = await Sha256FileAsync(destination, cancellationToken);
            if (!installedHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                log.Write($"Cobbleverse salvo divergente: hash diferente em {relativePath}");
                return false;
            }

            var processed = i + 1;
            log.ReportProgress(progressStart + processed * (progressEnd - progressStart) / Math.Max(1, files.Count));
            if (processed % 250 == 0 || processed == files.Count)
            {
                log.Write($"Cobbleverse salvo: {processed}/{files.Count} arquivo(s) verificados.");
            }
        }

        return true;
    }

    private static string GetCachedPayloadPath(PayloadPackage? payload, string targetRoot)
    {
        return Path.Combine(AppContext.BaseDirectory, "cobbleverse_payload.zip");
    }

    private static PayloadApplyResult? BuildPayloadInventoryFromState(InstalledState? state, PayloadPackage? payload)
    {
        if (!HasPayloadInventory(state))
        {
            return null;
        }

        var managedFiles = new HashSet<string>(
            (state!.PayloadFiles ?? Array.Empty<PayloadFileState>())
                .Select(file => NormalizeKey(NormalizeRelativePath(file.Path))),
            StringComparer.OrdinalIgnoreCase);

        return new PayloadApplyResult(
            managedFiles,
            NormalizeManagedRoots(payload?.ManagedRoots),
            state.PayloadFiles ?? Array.Empty<PayloadFileState>());
    }

    private static HashSet<string> GetManifestManagedRetainedFiles(PokeDogManifest manifest, IReadOnlyList<string> managedRoots)
    {
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files ?? Array.Empty<ManifestFile>())
        {
            if (string.Equals(file.Policy, "delete", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = NormalizeRelativePath(file.Path);
            if (IsUnderManagedRoots(relativePath, managedRoots))
            {
                retained.Add(NormalizeKey(relativePath));
            }
        }

        return retained;
    }

    internal static Task<string> Sha256FileForMirrorAsync(string path, CancellationToken cancellationToken) => Sha256FileAsync(path, cancellationToken);

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string> Sha256FileWithProgressAsync(string path, IInstallerLog log, CancellationToken cancellationToken, int progressStart, int progressEnd, string label)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var totalBytes = Math.Max(stream.Length, 1L);
        var buffer = new byte[1024 * 128];
        long processed = 0;
        var lastLoggedPercent = -1;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            sha.TransformBlock(buffer, 0, read, null, 0);
            processed += read;
            var percent = (int)Math.Floor(processed * 100d / totalBytes);
            if (progressEnd > progressStart)
            {
                log.ReportProgress(progressStart + percent * (progressEnd - progressStart) / 100);
            }

            if (percent >= lastLoggedPercent + 10 || percent == 100)
            {
                lastLoggedPercent = percent;
                log.Write($"HASH {label}: {FormatBytes(processed)} / {FormatBytes(totalBytes)} ({percent}%)");
            }
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        if (progressEnd > progressStart)
        {
            log.ReportProgress(progressEnd);
        }
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
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

    private static IReadOnlyList<string> NormalizeManagedRoots(IReadOnlyList<string>? roots)
    {
        var selected = roots is { Count: > 0 }
            ? roots
            : new[] { "mods", "config", "resourcepacks", "shaderpacks" };
        return selected
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsUnderManagedRoots(string relativePath, IReadOnlyList<string> managedRoots)
    {
        var key = NormalizeKey(relativePath);
        return managedRoots.Any(root =>
        {
            var rootKey = NormalizeKey(root);
            return key.Equals(rootKey, StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith(rootKey + "/", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string NormalizeKey(string relativePath)
    {
        return relativePath.Replace('\\', '/').Trim('/');
    }

    private static void EnsureInsideRoot(string path, string targetRoot)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) && !fullPath.Equals(fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Caminho fora da pasta de destino: {path}");
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }
        return builder.Length == 0 ? "latest" : builder.ToString();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.0} {units[unit]}";
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

internal sealed class WebProgressLog(Action<string> write, Action<int> progress) : IInstallerLog
{
    public void Write(string line) => write(line);
    public void ReportProgress(int percent) => progress(percent);
}

internal sealed record InstallerOptions(string Manifest, string TargetRoot, bool DryRun, string PayloadZip, bool ForceRepair = false)
{
    public static InstallerOptions Parse(string[] args)
    {
        var manifest = InstallerPaths.FindDefaultManifest();
        var target = Directory.GetCurrentDirectory();
        var payload = InstallerPaths.FindDefaultPayload();
        var dryRun = false;
        var forceRepair = false;
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
                case "--force-repair":
                    forceRepair = true;
                    break;
            }
        }
        return new InstallerOptions(manifest, target, dryRun, payload, forceRepair);
    }
}

internal sealed record PokeDogManifest(
    string PackVersion,
    string GuardVersion,
    InstallerUpdate? Installer,
    PayloadPackage? Payload,
    IReadOnlyList<UpdatePackage>? UpdatePackages,
    IReadOnlyList<ManifestFile> Files
);

internal sealed record InstallerUpdate(string Version, string Url, string Sha256, long Size = 0);

internal sealed record PayloadPackage(
    string Version,
    string Url,
    string Sha256,
    long Size,
    IReadOnlyList<string>? Mirrors,
    IReadOnlyList<string>? ManagedRoots,
    bool RemoveMissing
);

internal sealed record PayloadApplyResult(
    HashSet<string> ManagedFiles,
    IReadOnlyList<string> ManagedRoots,
    IReadOnlyList<PayloadFileState> ManagedFileStates
);

internal sealed record UpdatePackage(
    string Version,
    string Url,
    string Sha256,
    long Size,
    IReadOnlyList<string>? Files,
    IReadOnlyList<string>? Removes
);

internal sealed record InstalledState(
    string PackVersion,
    string PayloadVersion,
    string PayloadSha256,
    IReadOnlyList<string>? AppliedPackages,
    IReadOnlyList<PayloadFileState>? PayloadFiles,
    DateTimeOffset UpdatedAtUtc
);

internal sealed record PayloadFileState(
    string Path,
    string Sha256,
    long Size
);

internal sealed record ManifestFile(
    string Path,
    string Sha256,
    long Size,
    string Url,
    string Policy
);

internal static class InstallerPaths
{
    private const string RemoteManifestUrl = "https://github.com/JpAndreBTA/PokeDOG-Modpack-Installer/releases/latest/download/manifest.json";
    private static readonly string[] LocalMirrorRoots =
    [
        Path.Combine(AppContext.BaseDirectory, "PokeDOG"),
        Path.Combine(AppContext.BaseDirectory, "PokeDOG_Cliente"),
        AppContext.BaseDirectory
    ];

    public static string FindDefaultManifest()
    {
        return RemoteManifestUrl;
    }

    public static string FindLocalManifestFallback()
    {
        return FirstExisting(
            Path.Combine(AppContext.BaseDirectory, "manifest.json"),
            Path.Combine(AppContext.BaseDirectory, "pokedog_manifest.json"),
            Path.Combine(AppContext.BaseDirectory, "PokeDOG", "manifest.json"),
            Path.Combine(AppContext.BaseDirectory, "PokeDOG", "pokedog_manifest.json"),
            Path.Combine(AppContext.BaseDirectory, "PokeDOG_Cliente", "manifest.json"),
            Path.Combine(AppContext.BaseDirectory, "PokeDOG_Cliente", "pokedog_manifest.json"));
    }

    public static string FindDefaultPayload()
    {
        return FirstExisting(
            Path.Combine(AppContext.BaseDirectory, "cobbleverse_payload.zip"),
            Path.Combine(AppContext.BaseDirectory, "PokeDOG", "cobbleverse_payload.zip"),
            Path.Combine(AppContext.BaseDirectory, "PokeDOG", "PokeDOG_Cliente", "cobbleverse_payload.zip"));
    }

    public static string FindLocalPayloadMirror(PayloadPackage? payload)
    {
        var versionedName = $"cobbleverse_payload-{SanitizeForPath(payload?.Version ?? "latest")}.zip";
        return FirstExisting(
            LocalMirrorRoots
                .SelectMany(root => new[]
                {
                    Path.Combine(root, "cobbleverse_payload.zip"),
                    Path.Combine(root, versionedName),
                    Path.Combine(root, "PokeDOG_Cliente", "cobbleverse_payload.zip"),
                    Path.Combine(root, "payload", "cobbleverse_payload.zip")
                })
                .ToArray());
    }

    public static async Task<string?> FindLocalManagedFileAsync(string relativePath, string sha256, CancellationToken cancellationToken)
    {
        var safeRelative = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        foreach (var root in LocalMirrorRoots)
        {
            foreach (var candidate in new[]
            {
                Path.Combine(root, safeRelative),
                Path.Combine(root, "PokeDOG_Cliente", safeRelative),
                Path.Combine(root, "client", safeRelative)
            })
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(sha256))
                {
                    return candidate;
                }

                var hash = await InstallerEngine.Sha256FileForMirrorAsync(candidate, cancellationToken);
                if (hash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public static void FlushTransientCaches()
    {
        TryDeleteDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PokeDOG",
            "ModpackInstaller",
            "WebView2"));

        var tempDir = Path.GetTempPath();
        if (Directory.Exists(tempDir))
        {
            foreach (var script in Directory.EnumerateFiles(tempDir, "pokedog-installer-self-update-*.cmd", SearchOption.TopDirectoryOnly))
            {
                TryDeleteFile(script);
            }
        }

        TryDeleteFile(Path.Combine(AppContext.BaseDirectory, "PokeDOG-Modpack-Installer.exe.update"));
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string SanitizeForPath(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '-');
        }
        return value;
    }
}
