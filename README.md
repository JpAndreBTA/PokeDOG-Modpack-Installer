# PokeDOG Modpack Installer

Repositorio publico para hospedar o instalador/update manifest do PokeDOG.

## Downloads

- `PokeDOG-Modpack-Installer.exe`: publicado em GitHub Releases.
- `pokedog-client-guard-0.1.0.jar`: publicado em GitHub Releases para reparo/atualizacao do client guard.
- `pokedog_manifest.json`: manifesto remoto consumido pelo installer.

## Como o auto-update funciona

O instalador procura `pokedog_manifest.json` local primeiro. Se nao encontrar, usa:

`https://github.com/JpAndreBTA/PokeDOG-Modpack-Installer/raw/refs/heads/main/pokedog_manifest.json`

Quando o manifesto remoto tiver uma versao maior em `installer.version`, o installer baixa o novo `.exe` da Release e valida o SHA-256.

## Build

```powershell
dotnet publish src\PokeDOG.ModpackInstaller.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```


