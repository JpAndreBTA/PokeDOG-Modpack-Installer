using System.Drawing;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

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
        Application.Run(new WebInstallerForm());
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

internal sealed class WebInstallerForm : Form
{
    private readonly WebView2 _web = new();
    private readonly CancellationTokenSource _disposeToken = new();
    private bool _busy;

    public WebInstallerForm()
    {
        Text = "PokeDOG Modpack Installer";
        MinimumSize = new Size(720, 620);
        Size = new Size(760, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(7, 9, 15);
        Icon = LoadIconSafe();

        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);
        Load += async (_, _) => await InitializeWebAsync();
        FormClosed += (_, _) => _disposeToken.Cancel();
    }

    private async Task InitializeWebAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async();
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _web.NavigateToString(BuildInstallerHtml(GetBackgroundUri()));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Nao foi possivel abrir a interface moderna do PokeDOG Installer.\n\n" + ex.Message,
                "PokeDOG Modpack Installer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            Hide();
            using var fallback = new InstallerForm();
            fallback.ShowDialog(this);
            Close();
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : "";
            switch (type)
            {
                case "ready":
                    await SendAsync(new
                    {
                        type = "init",
                        folder = GetDefaultMinecraftFolder(),
                        version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.4",
                        payloadMb = 494.8
                    });
                    break;
                case "close":
                    Close();
                    break;
                case "chooseFolder":
                    ChooseFolder();
                    break;
                case "autoDetect":
                    await SendAsync(new { type = "folder", folder = GetDefaultMinecraftFolder() });
                    break;
                case "verify":
                    await RunInstallerFromWebAsync(root, dryRun: true);
                    break;
                case "install":
                    await RunInstallerFromWebAsync(root, dryRun: false);
                    break;
            }
        }
        catch (Exception ex)
        {
            await SendAsync(new { type = "error", message = ex.Message });
        }
    }

    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Selecione a pasta .minecraft",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(GetDefaultMinecraftFolder()) ? GetDefaultMinecraftFolder() : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _ = SendAsync(new { type = "folder", folder = dialog.SelectedPath });
        }
    }

    private async Task RunInstallerFromWebAsync(JsonElement root, bool dryRun)
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

        _busy = true;
        await SendAsync(new { type = dryRun ? "verifyStarted" : "installStarted" });
        try
        {
            var options = new InstallerOptions(InstallerPaths.FindDefaultManifest(), target, dryRun, InstallerPaths.FindDefaultPayload());
            var log = new WebProgressLog(line => _ = SendAsync(new { type = "log", line }), percent => _ = SendAsync(new { type = "progress", percent }));
            await InstallerEngine.RunAsync(options, log, _disposeToken.Token);
            await SendAsync(new { type = dryRun ? "verified" : "installed" });
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

    private static string GetDefaultMinecraftFolder()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
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
                using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("pokedog_background.png");
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
    .pixel-border { border: 3px solid #000; }
    .pixel-shadow { box-shadow: 4px 4px 0 0 #000; }
    .step { transition: opacity .22s ease, transform .22s ease; }
    .hidden-step { display: none; }
    .folder-input { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .terminal-line { overflow-wrap: anywhere; }
  </style>
</head>
<body class="text-gray-100 font-sans min-h-screen flex items-center justify-center p-4 relative">
  <div class="absolute inset-0 z-0 bg-cover bg-center bg-world"></div>
  <main class="w-full max-w-2xl bg-launcherDark/95 rounded-lg pixel-border pixel-shadow z-10 overflow-hidden relative flex flex-col min-h-[560px] justify-between">
    <div class="absolute top-4 right-4 z-20">
      <button onclick="send('close')" class="w-7 h-7 flex items-center justify-center rounded bg-slate-950 border border-slate-800 text-slate-500 hover:text-white hover:bg-pokeRed transition-colors">
        <i class="fa-solid fa-xmark text-xs"></i>
      </button>
    </div>

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
          <button id="btn-start-verify" onclick="startVerify()" class="bg-slate-900 hover:bg-slate-800 text-white font-silkscreen text-[10px] px-4 py-2 rounded transition-all flex items-center gap-2 border-2 border-slate-950">
            <i class="fa-solid fa-magnifying-glass text-pokeYellow"></i> INICIAR VERIFICACAO
          </button>
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
            <div class="flex justify-between font-terminal text-xs text-slate-500"><span id="dl-loaded-mb">0.0 MB / 494.8 MB</span><span>Tempo Restante: <span id="dl-eta" class="text-slate-400">Calculando...</span></span></div>
          </div>
          <div class="grid grid-cols-2 gap-2 pt-2 border-t border-slate-900/60 font-terminal text-xs">
            <div class="text-left"><span class="block text-slate-500 text-[10px] font-silkscreen">Origem</span><span id="dl-speed" class="text-sm font-bold text-white">GitHub/Local</span></div>
            <div class="text-right"><span class="block text-slate-500 text-[10px] font-silkscreen">Anticheat</span><span class="text-sm font-bold text-emerald-400">Guard Ativo</span></div>
          </div>
        </div>
      </div>

      <div id="view-step-4" class="step space-y-5 text-center hidden-step">
        <div class="max-w-md mx-auto space-y-3">
          <div class="relative w-12 h-12 mx-auto flex items-center justify-center"><div class="absolute inset-0 bg-emerald-500/20 rounded-full animate-ping opacity-75"></div><div class="relative w-10 h-10 bg-gradient-to-br from-emerald-500 to-emerald-600 border-2 border-slate-950 rounded flex items-center justify-center shadow-lg"><i class="fa-solid fa-check text-white text-md"></i></div></div>
          <div class="space-y-1"><h2 class="font-pixel text-sm text-white tracking-wide">Tudo Pronto!</h2><p id="done-message" class="text-xs text-slate-400">O modpack PokeDOG foi instalado e atualizado no seu diretorio de jogo.</p></div>
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

  <div id="toast-notification" class="fixed bottom-6 right-6 z-50 transform translate-y-20 opacity-0 transition-all duration-300 pointer-events-none">
    <div class="bg-launcherDark border-2 border-slate-900 p-4 rounded-lg shadow-2xl flex items-center gap-3 max-w-sm">
      <div class="w-8 h-8 rounded bg-pokeRed/10 flex items-center justify-center text-pokeRed" id="toast-icon"><i class="fa-solid fa-bell"></i></div>
      <div><h4 id="toast-title" class="font-silkscreen text-[10px] font-bold text-slate-200">Aviso</h4><p id="toast-message" class="font-terminal text-sm text-slate-400 mt-0.5">Mensagem do sistema.</p></div>
    </div>
  </div>

  <script>
    const audioCtx = new (window.AudioContext || window.webkitAudioContext)();
    let activeStep = 1, isVerified = false, isInstalling = false, logCount = 0, payloadMb = 494.8, toastTimeout = null;

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
    function showToast(title, message, icon){ clearTimeout(toastTimeout); document.getElementById('toast-title').innerText = title; document.getElementById('toast-message').innerText = message; document.getElementById('toast-icon').innerHTML = `<i class="${icon || 'fa-solid fa-bell'}"></i>`; const toast = document.getElementById('toast-notification'); toast.classList.remove('translate-y-20','opacity-0'); toast.classList.add('translate-y-0','opacity-100'); toastTimeout = setTimeout(()=>{ toast.classList.add('translate-y-20','opacity-0'); toast.classList.remove('translate-y-0','opacity-100'); }, 3500); }

    function handleNext() {
      if (activeStep === 1) return goToStep(2);
      if (activeStep === 2 && !isVerified) return showToast('Aviso', 'Inicie a verificacao de arquivos primeiro.', 'fa-solid fa-circle-exclamation text-pokeYellow');
      if (activeStep === 2) return goToStep(3);
      if (activeStep === 3 && isInstalling) return showToast('Processando', 'Aguarde a instalacao terminar.', 'fa-solid fa-cloud-arrow-down text-pokeYellow');
      if (activeStep === 3) return goToStep(4);
      send('close');
    }
    function handleBack(){ if (activeStep > 1 && !isInstalling) goToStep(activeStep - 1); }
    function goToStep(step) {
      playBeep(350,80);
      for (let i=1;i<=4;i++) document.getElementById(`view-step-${i}`).classList.add('hidden-step');
      document.getElementById(`view-step-${step}`).classList.remove('hidden-step');
      activeStep = step; updateStepNodes(step);
      const back = document.getElementById('btn-back'), next = document.getElementById('btn-next');
      back.disabled = step === 1 || isInstalling;
      next.classList.toggle('opacity-50', (step === 2 && !isVerified) || isInstalling);
      next.classList.toggle('cursor-not-allowed', (step === 2 && !isVerified) || isInstalling);
      next.innerHTML = step === 4 ? '<span>FECHAR</span> <i class="fa-solid fa-check text-[9px]"></i>' : '<span>AVANCAR</span> <i class="fa-solid fa-chevron-right text-[9px]"></i>';
      if (step === 3 && !isInstalling) startInstall();
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
      playBeep(400,100); isVerified = false; logCount = 0; document.getElementById('terminal-logs').innerHTML = '';
      const btn = document.getElementById('btn-start-verify'); btn.disabled = true; btn.innerHTML = '<i class="fa-solid fa-spinner animate-spin"></i> ESCANEANDO...'; btn.classList.add('text-slate-500');
      send('verify');
    }
    function startInstall() {
      isInstalling = true; setProgress(0); document.getElementById('btn-back').disabled = true; document.getElementById('dl-current-file').innerText = 'Preparando sincronizacao real do modpack...'; send('install');
    }
    function setProgress(percent) {
      const p = Math.max(0, Math.min(100, Number(percent || 0)));
      document.getElementById('dl-progress-bar').style.width = `${p}%`; document.getElementById('dl-percentage').innerText = `${Math.floor(p)}%`;
      document.getElementById('dl-loaded-mb').innerText = `${((payloadMb * p) / 100).toFixed(1)} MB / ${payloadMb.toFixed(1)} MB`;
      document.getElementById('dl-eta').innerText = p >= 100 ? 'Finalizando...' : 'Calculando...';
    }
    window.pokedogFromHost = function(msg) {
      if (!msg) return;
      if (msg.type === 'init') { document.getElementById('input-folder').value = msg.folder || ''; document.getElementById('input-folder').title = msg.folder || ''; payloadMb = msg.payloadMb || payloadMb; return; }
      if (msg.type === 'folder') { document.getElementById('input-folder').value = msg.folder || ''; document.getElementById('input-folder').title = msg.folder || ''; showToast('Sucesso', 'Pasta do Minecraft selecionada.', 'fa-solid fa-circle-check text-emerald-500'); return; }
      if (msg.type === 'verifyStarted') { appendLog('Verificacao real iniciada.', 'text-slate-300'); return; }
      if (msg.type === 'installStarted') { appendLog('Instalacao real iniciada.', 'text-slate-300'); document.getElementById('dl-current-file').innerText = 'Aplicando payload, mods, configs e Client Guard...'; return; }
      if (msg.type === 'log') { appendLog(msg.line || '', /BAIXAR|ATUALIZAR|REMOVER|Nova versao/.test(msg.line || '') ? 'text-pokeYellow font-semibold' : 'text-slate-400'); if (activeStep === 3) document.getElementById('dl-current-file').innerText = msg.line || ''; return; }
      if (msg.type === 'progress') { setProgress(msg.percent); return; }
      if (msg.type === 'verified') { isVerified = true; const btn = document.getElementById('btn-start-verify'); btn.disabled = false; btn.innerHTML = '<i class="fa-solid fa-magnifying-glass text-pokeYellow"></i> VERIFICAR NOVAMENTE'; btn.classList.remove('text-slate-500'); appendLog('Verificacao concluida. Clique em Avancar.', 'text-emerald-400 font-bold'); showToast('Verificado!', 'Pronto para sincronizar o modpack.', 'fa-solid fa-circle-check text-emerald-500'); playSuccessChime(); return; }
      if (msg.type === 'installed') { isInstalling = false; setProgress(100); document.getElementById('dl-current-file').innerText = 'Instalacao finalizada.'; playSuccessChime(); setTimeout(()=>goToStep(4), 450); return; }
      if (msg.type === 'toast') { showToast(msg.title || 'Aviso', msg.message || '', 'fa-solid fa-bell text-pokeYellow'); return; }
      if (msg.type === 'error') { isInstalling = false; const btn = document.getElementById('btn-start-verify'); btn.disabled = false; btn.innerHTML = '<i class="fa-solid fa-magnifying-glass text-pokeYellow"></i> VERIFICAR NOVAMENTE'; appendLog('ERRO: ' + (msg.message || 'falha desconhecida'), 'text-pokeRed font-bold'); showToast('Erro', msg.message || 'Falha desconhecida.', 'fa-solid fa-triangle-exclamation text-pokeRed'); }
    };
    window.addEventListener('DOMContentLoaded', () => send('ready'));
  </script>
</body>
</html>
""".Replace("__BG__", backgroundUri);
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
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
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

        var payloadPath = await ResolvePayloadAsync(options.PayloadZip, manifest.Payload, targetRoot, http, options.DryRun, log, cancellationToken);
        PayloadApplyResult? payloadResult = null;
        if (!string.IsNullOrWhiteSpace(payloadPath))
        {
            payloadResult = await ExtractPayloadAsync(payloadPath, targetRoot, backupRoot, manifest.Payload, options.DryRun, log, cancellationToken);
        }
        else
        {
            log.Write("Payload nao aplicado nesta etapa. O instalador tentara usar URLs individuais do manifesto.");
        }

        if (payloadResult != null && (manifest.Payload?.RemoveMissing ?? false))
        {
            await RemoveMissingManagedFilesAsync(payloadResult.ManagedFiles, payloadResult.ManagedRoots, targetRoot, backupRoot, options.DryRun, log, cancellationToken);
        }

        await ApplyManagedFilesAsync(manifest, targetRoot, backupRoot, http, options.DryRun, payloadResult != null, log, cancellationToken);
        log.ReportProgress(100);
    }

    private static async Task<string?> ResolvePayloadAsync(string localPayload, PayloadPackage? payload, string targetRoot, HttpClient http, bool dryRun, IInstallerLog log, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(localPayload) && File.Exists(localPayload))
        {
            if (payload is { Sha256.Length: > 0 })
            {
                var localHash = await Sha256FileAsync(localPayload, cancellationToken);
                if (localHash.Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    log.Write($"Payload local valido: {localPayload}");
                    return localPayload;
                }

                log.Write($"Payload local com hash diferente: {localPayload}");
                if (string.IsNullOrWhiteSpace(payload.Url))
                {
                    throw new InvalidOperationException("Payload local esta diferente do manifesto e nao ha URL remota para reparar.");
                }
            }
            else
            {
                log.Write($"Payload local: {localPayload}");
                return localPayload;
            }
        }

        if (payload is not { Url.Length: > 0, Sha256.Length: > 0 })
        {
            return null;
        }

        var cacheDir = Path.Combine(targetRoot, ".pokedog-cache");
        var cacheName = $"cobbleverse_payload-{SanitizeFileName(payload.Version ?? "latest")}.zip";
        var cachedPayload = Path.Combine(cacheDir, cacheName);
        if (File.Exists(cachedPayload))
        {
            var cachedHash = await Sha256FileAsync(cachedPayload, cancellationToken);
            if (cachedHash.Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                log.Write($"Payload em cache valido: {cachedPayload}");
                return cachedPayload;
            }
        }

        if (dryRun)
        {
            log.Write($"Payload remoto disponivel: {payload.Url}");
            log.Write("DRY DOWNLOAD payload completo sera baixado na instalacao.");
            return null;
        }

        Directory.CreateDirectory(cacheDir);
        log.Write($"BAIXAR payload completo {payload.Version}");
        await DownloadAndVerifyAsync(payload.Url, payload.Sha256, cachedPayload, http, false, log, cancellationToken);
        return cachedPayload;
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

    private static async Task<PayloadApplyResult> ExtractPayloadAsync(string zipPath, string targetRoot, string backupRoot, PayloadPackage? payload, bool dryRun, IInstallerLog log, CancellationToken cancellationToken)
    {
        log.Write($"Payload local: {zipPath}");
        using var archive = ZipFile.OpenRead(zipPath);
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        var managedRoots = NormalizeManagedRoots(payload?.ManagedRoots);
        var managedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        var plannedUpdates = 0;
        foreach (var entry in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(entry.FullName);
            if (IsUnderManagedRoots(relativePath, managedRoots))
            {
                managedFiles.Add(NormalizeKey(relativePath));
            }

            var destination = Path.Combine(targetRoot, relativePath);
            EnsureInsideRoot(destination, targetRoot);
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
        return new PayloadApplyResult(managedFiles, managedRoots);
    }

    private static async Task RemoveMissingManagedFilesAsync(HashSet<string> payloadFiles, IReadOnlyList<string> managedRoots, string targetRoot, string backupRoot, bool dryRun, IInstallerLog log, CancellationToken cancellationToken)
    {
        await Task.Yield();
        var plannedRemovals = 0;
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
                if (payloadFiles.Contains(NormalizeKey(relativePath)))
                {
                    continue;
                }

                plannedRemovals++;
                if (!dryRun || plannedRemovals <= 40)
                {
                    log.Write($"REMOVER arquivo fora do payload {relativePath}");
                }

                if (!dryRun)
                {
                    BackupFile(file, backupRoot, targetRoot);
                    File.Delete(file);
                }
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
    PayloadPackage? Payload,
    IReadOnlyList<ManifestFile> Files
);

internal sealed record InstallerUpdate(string Version, string Url, string Sha256);

internal sealed record PayloadPackage(
    string Version,
    string Url,
    string Sha256,
    long Size,
    IReadOnlyList<string>? ManagedRoots,
    bool RemoveMissing
);

internal sealed record PayloadApplyResult(
    HashSet<string> ManagedFiles,
    IReadOnlyList<string> ManagedRoots
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
