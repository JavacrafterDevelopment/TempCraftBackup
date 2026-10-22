# TempCraftBackup

A small, portable Windows tool that backs up the current source code of every
Craft app (PhotoCraft, VectorCraft, FilmCraft, EffectCraft, LightCraft,
DesignCraft, PdfCraft, FigCraft, SoundCraft, …) from
[github.com/storytold](https://github.com/storytold) into one archive.

Press **Back up now** and it:

1. reads the list of Craft apps from the
   [Launcher](https://github.com/JavacrafterDevelopment/Launcher)'s catalog
   (falling back to a built-in list), so apps added to the Launcher are backed up too;
2. downloads each app's default branch from GitHub;
3. packs them into `ArtCraftApps_<date>.7z` in the folder you chose, with one folder
   of source code per app and a `BACKUP-INFO.txt` listing each repo and commit.

The 7z is written by Windows' own `tar.exe` (LZMA2). On a Windows whose `tar.exe`
can't write 7z, it makes a `.zip` instead.

## Portable

Nothing is installed. The chosen folder is remembered in `data\settings.ini` beside
`TempCraftBackup.exe`; the only other thing it touches is a scratch folder in
`%TEMP%`, removed when the backup finishes.

## Built with what Windows already has

It's C# compiled by the `csc.exe` that ships with Windows (.NET Framework 4.8), so
there's nothing to install to build or run it:

```powershell
.\build.ps1
```

That writes `dist\TempCraftBackup\TempCraftBackup.exe` and the package the
Launcher installs from.

## Releases

Bump `version.txt`, commit, then tag and push (for example `v0.1.1`). The release
workflow builds it, attaches `TempCraftBackup-windows-x64.zip` to the GitHub
release and updates `update.json` on the `updates` branch, which is where the
Launcher looks for new versions.
