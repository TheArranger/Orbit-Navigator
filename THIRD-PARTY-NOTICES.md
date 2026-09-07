# Third-party notices

Orbit Navigator is licensed under MPL-2.0, but it depends on and may distribute
components under their own licenses. Those licenses govern the corresponding
third-party components.

## Runtime and distributed components

| Component | Version | Use | License/terms |
|---|---:|---|---|
| Microsoft .NET Runtime and WPF | 10.0.x | Self-contained application runtime | MIT and component-specific notices; reproduced under `third-party/dotnet/` |
| Microsoft.Web.WebView2 SDK | 1.0.4129.50 | Embedded browser API and loader | Microsoft package license and notices; reproduced under `third-party/Microsoft.Web.WebView2/` |
| Microsoft Edge WebView2 Evergreen Standalone Runtime | Installer staged at package-build time | Offline browser-runtime prerequisite | Microsoft Software License Terms accompanying the Microsoft download; it is not committed to the public source repository |

## Build and test dependencies

| Component | Version | License |
|---|---:|---|
| Microsoft.NET.Test.Sdk and Microsoft Test Platform | 18.8.1 | MIT |
| xUnit.net packages | 2.9.3; runner 3.1.5 | Apache-2.0 |
| Pillow | 11.3.0 used by the checked-in asset builders | HPND |
| Inno Setup | Version supplied by the local packaging prerequisite | Inno Setup license; build tool only |

The exact resolved NuGet dependency graph can be reproduced with:

```powershell
.\.tools\dotnet\dotnet.exe list .\OrbitNavigator.sln package --include-transitive
```

This inventory is not a substitute for the complete license texts. Release
packaging must include this file and the reproduced notices under
`third-party/`.
