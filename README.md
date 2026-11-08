# TempCraftBackup

A small, portable Windows tool that backs up the source code of every Craft app
(PhotoCraft, VectorCraft, FilmCraft, EffectCraft, LightCraft, DesignCraft,
PdfCraft, FigCraft, SoundCraft, …) from
[github.com/storytold](https://github.com/storytold) into one archive.

Press **Back up now** and it:

1. reads the list of Craft apps from the
   [Launcher](https://github.com/JavacrafterDevelopment/Launcher)'s catalog
   (falling back to a built-in list), so apps added to the Launcher are backed up too;
2. gets each app's source code from GitHub (see the options below);
3. packs them into `ArtCraftApps_<date>_<time>.7z` (for example `ArtCraftApps_2026-10-21_5-41PM.7z`) in the folder you chose, with one folder
   per app and a `BACKUP-INFO.txt` listing each app's repo, release, commit and any notes.

The 7z is written by Windows' own `tar.exe` (LZMA2). On a Windows whose `tar.exe`
can't write 7z, it makes a `.zip` instead.

## Options

All three are remembered in `data\settings.ini`.

| Option | Off | On |
|---|---|---|
| **Use latest release** | Each app's latest commit on its default branch. | Each app's latest GitHub release. An app with no release gets its latest commit instead. |
| **Include betas / pre-releases** (only with *Use latest release*) | Only stable releases count. | Pre-releases count too, so the newest release of any kind is used. |
| **Include git history** | Source files only. | Each folder is a full git clone (every commit, branch and tag, with its `.git` folder), checked out at the release's tag or the latest commit. |

Folder names inside the archive say what each one holds:

- `PhotoCraft_v0.21.0`: a release, named with its version;
- `PhotoCraft_2026-11-08`: a latest commit, named with the backup's date. That's every
  app with *Use latest release* off, and any app that has no release with it on.

Git history makes a backup several times bigger (roughly 4–7× for the Craft apps)
and slower, since every commit is downloaded. If an app can't be cloned, its
source files are still backed up and `BACKUP-INFO.txt` says so.

Releases are found the way the Launcher finds them: GitHub's API first, then the
release page when the API's hourly limit (60 requests an hour without signing in)
is used up. The release page only shows stable releases, so betas can't be
checked then; the log and `BACKUP-INFO.txt` say when that happened.

## Portable

Nothing is installed. The chosen folder and options are remembered in
`data\settings.ini` beside `TempCraftBackup.exe`; the only other thing it touches is
a scratch folder in `%TEMP%`, removed when the backup finishes.

The `git\` folder beside the exe is a portable Git used for *Include git history*:
[MinGit](https://github.com/git-for-windows/git/wiki/MinGit), the minimal Git that
the official [Git for Windows](https://gitforwindows.org) project (the Windows
download on [git-scm.com](https://git-scm.com/downloads/win)) publishes for apps to
bundle. It's only used by TempCraftBackup and doesn't change any Git you have installed.

## Built with what Windows already has

It's C# compiled by the `csc.exe` that ships with Windows (.NET Framework 4.8), so
there's nothing to install to build or run it:

```powershell
.\build.ps1
```

That writes `dist\TempCraftBackup\TempCraftBackup.exe` and the package the
Launcher installs from. It also downloads the latest MinGit from Git for Windows'
GitHub releases into `dist\TempCraftBackup\git\`, after checking it against the
SHA-256 the release publishes (cached in `obj\`). `-MinGitRelease v2.56.0.windows.4`
pins a particular Git for Windows release, and `-NoGit` leaves Git out.

## Releases

Bump `version.txt`, commit, then tag and push (for example `v0.2.0`). The release
workflow builds it, attaches `TempCraftBackup-windows-x64.zip` to the GitHub
release and updates `update.json` on the `updates` branch, which is where the
Launcher looks for new versions.
