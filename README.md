# PokeDOG Modpack Installer

Novo updater base para substituir publicamente o `Cobbleverse-Modpack-Installer.exe`.

Com clique duplo, abre uma interface Windows com titulo `PokeDOG Modpack Installer`.
Ela procura automaticamente:
- `pokedog_manifest.json` ao lado do exe, em `PokeDOG`, ou em `PokeDOG/PokeDOG_Cliente`;
- `cobbleverse_payload.zip` ao lado do exe, em `PokeDOG`, ou em `PokeDOG/PokeDOG_Cliente`.

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

Atualizacao do client guard:
- O instalador instala/repara arquivos do payload local e valida arquivos gerenciados pelo manifesto.
- O server-side `PokeDOG Client Guard` e quem exige a versao/hash correta no login. Cliente errado/desatualizado e kickado com instrucao para usar o instalador.
