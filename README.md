# PokeDOG Modpack Installer

## Download para jogadores

Baixe somente o arquivo **[PokeDOG-Modpack-Installer.exe](https://github.com/JpAndreBTA/PokeDOG-Modpack-Installer/releases/download/v0.1.18/PokeDOG-Modpack-Installer.exe)** ou use `mine.ayellol.com`.

Nao use **Code > Download ZIP**: esse botao baixa o codigo-fonte (`.cs`, `.csproj` e `.json`), que pode aparecer associado ao VS Code e nao e o instalador.

Versao publica atual: `v0.1.18` para installer, manifesto, payload completo e Client Guard. O payload continua completo e o guard segue separado como dependencia gerenciada pelo manifesto.

Novo updater base para substituir publicamente o `Cobbleverse-Modpack-Installer.exe`.

Com clique duplo, abre uma interface Windows com titulo `PokeDOG Modpack Installer`.
O usuario precisa somente do EXE: manifesto, payload inicial e atualizacoes sao obtidos do GitHub com validacao SHA-256.

O destino automatico reconhece a raiz `.minecraft` e instancias de SKlauncher, Prism Launcher, MultiMC, CurseForge, Modrinth, ATLauncher, Technic e GDLauncher. Para o SKlauncher, tambem sao lidos os `gameDir` registrados em `.minecraft/launcher_profiles.json`. Instancias PokeDOG/Cobbleverse e instalacoes que ja possuem o Client Guard recebem prioridade.

Uso de desenvolvimento:

```powershell
dotnet run --project tools/pokedog-modpack-installer -- --manifest tools/pokedog-modpack-installer/pokedog_manifest.example.json --payload C:\Users\jpzin\Downloads\PokeDOG\PokeDOG_Cliente\cobbleverse_payload.zip --target C:\Temp\PokeDOGClient --dry-run
```

Publicacao:

```powershell
dotnet publish tools\pokedog-modpack-installer\PokeDOG.ModpackInstaller.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o tools\pokedog-modpack-installer\publish\win-x64
```

O manifesto controla arquivos gerenciados por SHA-256. Antes de substituir ou apagar um arquivo, o instalador salva backup em `.pokedog-backups/<timestamp>`.
O executavel publico gerado e `PokeDOG-Modpack-Installer.exe`; o icone vem de `assets/pokedog.ico`.

Atualizacoes incrementais:
- `payload` e a base completa, baixada somente na primeira instalacao ou quando a base nao puder ser reconhecida;
- `.pokedog-cache/installed-state.json` guarda a versao/hash da base e os deltas ja aplicados;
- `updatePackages` lista ZIPs pequenos somente com arquivos adicionados ou alterados;
- `updatePackages[].removes` lista caminhos que devem ser removidos, sempre com backup;
- mudar um mod ou config nao exige republicar nem baixar novamente `cobbleverse_payload.zip`.

Exemplo de pacote incremental no manifesto:

```json
{
  "version": "pokedog-v2-delta-20260628-01",
  "url": "https://github.com/.../pokedog-delta-20260628-01.zip",
  "sha256": "HASH_SHA256_DO_ZIP",
  "size": 123456,
  "files": ["mods/mod-alterado.jar", "config/ajuste.json"],
  "removes": ["mods/mod-removido.jar"]
}
```

Atualizacao do client guard:
- O instalador instala/repara arquivos do payload local e valida arquivos gerenciados pelo manifesto.
- O server-side `PokeDOG Client Guard` e quem exige a versao/hash correta no login. Cliente errado/desatualizado e kickado com instrucao para usar o instalador.

