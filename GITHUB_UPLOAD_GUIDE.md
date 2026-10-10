# Update the existing GitHub repository and publish a release

This workspace is a source snapshot and does not contain `.git` metadata, so it cannot safely push or identify your current branch. Use your existing clone and review each change before committing. These steps publish source first, then create the installer and release through GitHub Actions.

## 1. Check your existing repository

Open PowerShell and set the path to your existing clone. Replace the example path with the folder where you cloned `Baka-browser`:

```powershell
$Source = 'C:\Users\ps260\Downloads\search'
$Repo = 'C:\path\to\your\Baka-browser'

if (-not (Test-Path (Join-Path $Repo '.git'))) {
    throw 'Repo must point to your existing Git clone, the folder containing .git.'
}

git -C $Repo remote -v
git -C $Repo status --short
```

Make sure `origin` is the repository you intend to update and commit or back up any existing work before continuing. Check the default branch name in GitHub; the examples below use `main`.

## 2. Create a release branch

```powershell
git -C $Repo switch main
git -C $Repo pull --ff-only origin main
git -C $Repo switch -c release/1.2.0
```

If your default branch is named `master`, substitute `master` for `main`.

## 3. Copy the prepared project files

This copies source and release files without copying build outputs or deleting anything from your clone:

```powershell
$Files = @(
    'AutoUpdater.cs',
    'BrowserForm.cs',
    'ProfileProtection.cs',
    'Program.cs',
    'SettingsForm.cs',
    'Ui.cs',
    'baka.csproj',
    'nuget.config',
    'baka.ico',
    'baka-logo.png',
    '.gitignore',
    'README.md',
    'CHANGELOG.md',
    'RELEASE_NOTES.md',
    'GITHUB_UPLOAD_GUIDE.md'
)

foreach ($File in $Files) {
    Copy-Item -LiteralPath (Join-Path $Source $File) -Destination (Join-Path $Repo $File) -Force
}

$WorkflowDir = Join-Path $Repo '.github\workflows'
New-Item -ItemType Directory -Path $WorkflowDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $Source '.github\workflows\ci.yml') -Destination (Join-Path $WorkflowDir 'ci.yml') -Force
Copy-Item -LiteralPath (Join-Path $Source '.github\workflows\release.yml') -Destination (Join-Path $WorkflowDir 'release.yml') -Force
```

If the existing repository contains a `LICENSE` file or other project files not listed above, keep them. This workspace snapshot has no `.git` folder, so compare the rest of the source files with your existing clone before deciding whether any additional files need updating.

## 4. Build and inspect the changes

```powershell
Push-Location $Repo
dotnet restore baka.csproj --configfile nuget.config
dotnet build baka.csproj -c Release --no-restore
git diff --check
git status --short
git diff
Pop-Location
```

Review the diff in your editor or GitHub Desktop. Confirm you are not staging `bin/`, `obj/`, local browser data, credentials, or profile files. The release workflow validates that the pushed tag, `baka.csproj`, and `RELEASE_NOTES.md` all use version `1.2.0`.

## 5. Commit and push the source branch

```powershell
git -C $Repo add AutoUpdater.cs BrowserForm.cs ProfileProtection.cs Program.cs SettingsForm.cs Ui.cs baka.csproj nuget.config baka.ico baka-logo.png .gitignore README.md CHANGELOG.md RELEASE_NOTES.md GITHUB_UPLOAD_GUIDE.md .github/workflows/ci.yml .github/workflows/release.yml
git -C $Repo commit -m "Prepare Baka Browser 1.2.0 release"
git -C $Repo push -u origin release/1.2.0
```

Open a pull request into the repository's default branch. Wait for **Build Windows app** to pass, review the changes, then merge it.

## 6. Publish the installer and update feed

After the release branch is merged, update your default branch and push the matching tag:

```powershell
git -C $Repo switch main
git -C $Repo pull --ff-only origin main
git -C $Repo tag v1.2.0
git -C $Repo push origin v1.2.0
```

Pushing the tag starts **Build and publish Windows release**. It creates a public GitHub Release containing the one-click setup program and Velopack update packages. The first release has a full package; later releases can also include a smaller delta package.

When Actions finishes, open the repository's **Releases** page and confirm `BakaBrowser-Setup.exe` is present. Install it on a clean Windows x64 machine before sharing it. Confirm setup completes, WebView2 is available, the browser opens, and the README/release notes match the release. Automatic updating can only be verified after publishing a later version while an older installed version is present.

> **Pushing this tag publishes a public release automatically.** The workflow uses GitHub Actions' built-in token with `contents: write`; you do not need to add a personal access token. If the repository is renamed or moved, update the repository URL in `AutoUpdater.cs` before building so installed copies continue checking the correct Releases page. The current browser does not support authenticated update feeds for private repositories.

## 7. Publish future updates

For each release, choose a new version that has never been tagged, then update all three version references:

1. Change `<Version>` in `baka.csproj`.
2. Add a dated section at the top of `CHANGELOG.md`.
3. Update the header and summary in `RELEASE_NOTES.md`.
4. Commit and merge the source changes.
5. Push the matching tag, for example `v1.2.1`.

The workflow checks that the tag version matches `baka.csproj` and `RELEASE_NOTES.md`; it fails instead of publishing mismatched files.

## Installer notes

The installer is a one-click, per-user Velopack setup rather than an MSI wizard. It installs under `%LOCALAPPDATA%\BakaBrowser` and does not require administrator access. The browser profile remains separately under `%LOCALAPPDATA%\baka-browser` and is preserved during updates and uninstall. This release is unsigned, so Windows SmartScreen may warn about the publisher until code signing is configured.
