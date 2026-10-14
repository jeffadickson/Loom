# Building, verifying and packaging Loom

Loom uses .NET CLI/MSBuild/NuGet for its C# application and libraries. Browser
verification uses npm and Playwright. Python orchestration is standard-library-only;
uv locks the optional PyMuPDF print-verification dependency. There is no native
C/C++ or HDL source to justify CMake, Make, a compiler provisioning layer or FPGA
tooling. SQLite native binaries arrive through locked NuGet runtime assets.

## New computer

Install Git and Python 3.11.8 or later (Python is the small bootstrap prerequisite).
Clone the repository and run from its root:

```sh
python tools/dev.py setup
python tools/dev.py test
python tools/dev.py run
```

Use `python3` if your platform names Python that way. `setup` downloads the exact
SDK named in `global.json`, verifies its committed SHA-512 and extracts it into
`.tools/dotnet`. Supported bootstrap targets: Windows, macOS and glibc Linux, x64
and ARM64. It never changes system PATH, uses administrator privileges or silently
replaces an existing different installation. The launcher supplies its local PATH
for child processes. Alpine/musl requires a separately installed compatible SDK.

On Linux, .NET still requires the OS runtime libraries (ICU, OpenSSL, zlib and
C/C++ runtime libraries). Use Microsoft's distribution-specific .NET prerequisites
if the downloaded SDK cannot start. The script does not guess your distribution
or run a privileged package manager. Docker Engine/Desktop is a separate optional
installation; container tests require a running Docker daemon. SQL Server tests
use an x64 Linux container host, including when developing on an ARM Mac.

Existing installations can use native commands directly:

```sh
dotnet restore Loom.sln --locked-mode
dotnet build Loom.sln --configuration Release --no-restore
dotnet msbuild build/Loom.proj -t:Test
```

`global.json` pins SDK 8.0.426 without roll-forward. The application still targets
.NET 8; updating the build infrastructure does not silently upgrade its runtime.
NuGet restore is locked by default, including implicit restores during builds.
The generated-model project references the generator project so clean solution
builds establish the correct build order automatically.

## Verification layers

| Command | What runs | Additional requirements |
|---|---|---|
| `python tools/dev.py doctor` | SDK version diagnostics | Installed or local SDK |
| `python tools/dev.py build` | Locked restore and all solution projects | .NET SDK |
| `python tools/dev.py test` | Framework/SQLite console checks, fast xUnit tests, examples | .NET SDK; no Docker |
| `python tools/dev.py browser` | Standalone introduction and actual C# host in Chromium | Node and Chromium |
| `python tools/dev.py database` | PostgreSQL and SQL Server Testcontainers contracts | Working Docker, x64 Linux containers |
| `python tools/dev.py pack --version 0.12.0-alpha.1` | Publish demo, ZIP, per-file manifest and SHA-256 | Python and .NET SDK |

The MSBuild entry point is `build/Loom.proj`; its targets are Restore, Build, Test,
BrowserTest, DatabaseTest and Pack. The Python launcher only locates local tools
and forwards to those targets. `Pack` builds but does not substitute for running
verification; CI runs verification before packaging.

For browsers:

```sh
python tools/dev.py setup --browser
python tools/dev.py browser
```

This installs checksum-verified Node 22.15.0 locally, runs `npm ci`, and installs
Playwright's pinned Chromium. Linux hosts may also need browser system libraries:
with Node on PATH run `npx --no-install playwright install-deps chromium` using your
normal OS administration process. CI explicitly uses `install --with-deps`.
No external temporary node_modules directory is required. Browser storage is
simulated only in the standalone example; the live test starts a real memory host.

Optional PDF/print tooling uses `uv sync --locked --group print`, then
`uv run --locked python tools/check_print.py <pdf>`. Install uv separately if using
this optional tool. Standard build/browser scripts have no third-party Python
imports and do not require uv. `uv.lock` records public PyPI artifacts and hashes.

## Debugging

Open `Loom.sln` in Visual Studio, or open the repository in VS Code and install the
recommended C# extension. The checked-in launch configuration builds and starts
the memory demo; attach is also available. When using the local SDK, start VS Code
from a shell whose PATH includes `.tools/dotnet` (and set DOTNET_ROOT to that path),
or configure the extension's SDK path. Breakpoints belong in DocumentService,
provider sessions or application hooks, depending on the behavior being examined.

## Release artifacts

`Pack` writes `artifacts/Loom-<version>.zip` and its SHA-256 file. The ZIP includes
the published application, model/report metadata, browser assets, the introduction,
documentation and `release-manifest.json` with file hashes. ZIP ordering and timestamps
are fixed. C# deterministic compilation is enabled; this is not a claim that outputs
from different OSes or uncommitted worktrees are byte-identical.

Extract the archive, change into its root, and run:

```sh
dotnet Loom.Demo.dll --memory-demo
```

The deployment is framework-dependent: install the .NET 8 ASP.NET Core runtime.
The demo binds to localhost and uses simulated roles. Packaging does not turn it
into an authenticated production deployment. CI uploads artifacts for inspection;
it does not publish NuGet packages, sign binaries, assign a license or publish a
GitHub release automatically. Licensing remains a separately tracked decision.

## Dependency maintenance

Update SDK version and platform URLs/checksums together (`global.json` and
`build/toolchains.json`); obtain SDK hashes from Microsoft's official release
metadata. Node hashes come from the corresponding official SHASUMS256 file.
Update `.nvmrc`, package engines and toolchain metadata together when changing Node.

For deliberate NuGet updates, edit package references and run
`dotnet restore Loom.sln -p:RestoreLockedMode=false --force-evaluate`, then review
and commit affected `packages.lock.json` files. For browser packages run `npm install
--save-dev --save-exact <package>@<version>` and commit both manifests. For optional
Python packages edit `pyproject.toml`, run `uv lock`, and commit `uv.lock`.
Normal builds must not rewrite lockfiles. CI checks Linux, Windows and macOS;
real database service jobs and Testcontainers run on Linux.

## Known limits

The supported application architecture is cross-platform; hosted CI verifies the
three runner OSes, not every distro/CPU combination. Bootstrap has hashes for six
platform targets; only exercised platforms should be claimed as tested. Network
access is needed on the first restore/browser/container download. Existing caches
are reused, but there is no complete offline installer bundle. Authentication,
server schema migrations, production release signing and native installers remain
separate product work.
