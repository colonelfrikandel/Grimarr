# Third-party software

Grimarr is distributed under GPL-3.0-only; see the accompanying `LICENSE` and corresponding source archive. Third-party components retain their respective upstream licenses.

The image includes .NET/ASP.NET Core, NuGet dependencies (including Microsoft.Data.Sqlite, SQLitePCLRaw, SQLite and AngleSharp), frontend JavaScript packages, FFmpeg and Ubuntu packages. The release's CycloneDX SBOM records the scanner's component/version/license inventory; it is not a replacement for the upstream license texts.

Frontend production dependency license texts are bundled at `/third-party-licenses.txt`, alongside linked JavaScript legal comments. NuGet-distributed license/notice files found during restore are copied under `/app/licenses/nuget` in the image. Runtime and OS package notices remain in their installed locations, including `/usr/share/doc`; FFmpeg build configuration is available with `ffmpeg -buildconf`. Dependencies that supply license URLs rather than local license files retain their upstream license references in package metadata/inventory.

The corresponding Grimarr source bundle contains the Dockerfile, locked application dependencies, build scripts and license notices. Runtime base images and OS packages are retrieved from their upstream repositories during source builds; those feeds can change, so rebuilding source is not claimed to reproduce an image byte for byte. The exported image and its SHA-256 checksums identify the exact packaged runtime.
