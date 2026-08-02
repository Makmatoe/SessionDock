# Third-party notices

SessionDock depends on third-party software. Each component remains governed by
its own license; the SessionDock license does not replace those terms.

## Microsoft WebView2

- Package: `Microsoft.Web.WebView2` 1.0.4078.44
- Publisher: Microsoft Corporation
- Project/package information:
  <https://www.nuget.org/packages/Microsoft.Web.WebView2>
- License: see the license identified by the package and the Microsoft Edge
  WebView2 Runtime terms applicable to the installed runtime.

WebView2 provides the isolated embedded browser used for official Roblox sign-in
pages. The WebView2 Runtime is installed and serviced separately by Microsoft.
The redistributable loader's complete package license and third-party notice are
shipped as `licenses/Microsoft.Web.WebView2-LICENSE.txt` and
`licenses/Microsoft.Web.WebView2-NOTICE.txt`.

## Velopack

- Package and tooling: `Velopack` / `vpk` 1.2.0
- Pinned source: <https://github.com/velopack/velopack/tree/1.2.0>
- License: MIT
- Copyright: © 2021 Caelan Sayler; © 2024 Velopack Ltd.

Velopack provides release packaging and the user-confirmed update mechanism.
Its MIT license is shipped as `licenses/Velopack-LICENSE.txt`.

## Development and test tooling

The repository uses `Microsoft.NET.Test.Sdk`, `xunit.v3`, and
`xunit.runner.visualstudio` only to execute automated tests. These packages are
not included in the SessionDock application or release package. They remain
subject to the licenses identified in their NuGet packages and upstream
projects.

## Discord release tools

The guarded automatic release sender uses only Node.js built-in modules. The
separate optional community bot depends on `discord.js` 14.27.0 (Apache-2.0)
and `dotenv` 17.4.2 (BSD-2-Clause). Their exact packages are pinned in
`discord-release-bot/package-lock.json` and are not included in SessionDock
application or release packages.

## .NET and bundled runtime components

Self-contained releases pin and include the .NET 10.0.10 runtime selected by the
repository's exact SDK. The .NET runtime license and its complete bundled
third-party notice are shipped as `licenses/DotNet-LICENSE.txt` and
`licenses/DotNet-THIRD-PARTY-NOTICES.txt`. The Windows Desktop runtime license
is shipped as `licenses/Microsoft.WindowsDesktop-LICENSE.txt`. See
<https://dotnet.microsoft.com/> for upstream project information.

## External optional software

Roblox Player, the Microsoft Edge WebView2 Runtime, and
[HandleScope](https://github.com/Makmatoe/HandleScope) are not licensed as part
of SessionDock. HandleScope is optional and not bundled in SessionDock releases.
After explicit confirmation, SessionDock can download and run the separate
product's pinned v0.1.4 per-user installer from its canonical immutable GitHub
release. HandleScope remains governed by its own license and publisher terms;
the pinned official setup instructions remain available in the user's browser.

These notices travel inside both the installed application and portable ZIP.
The versioned SBOM published with each release identifies the application,
direct runtime dependencies, and pinned framework runtime used for that build.
