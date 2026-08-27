# Third-party notices

RelayLoop's application and runner source code are original to this project. They do not incorporate TinyTask binaries, branding, proprietary assets, source, or its `.rec` format.

The following third-party software is used to build, run, or test RelayLoop.

## Microsoft .NET 8 and WPF

RelayLoop targets .NET 8 and its portable release includes the Microsoft .NET runtime and Windows Desktop/WPF runtime components required for self-contained execution.

- Copyright Microsoft Corporation and .NET contributors.
- License: MIT, together with the applicable upstream third-party notices.
- Project: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf

These runtime components are redistributed only as output produced by the .NET SDK's supported self-contained publish process.

## Microsoft.NET.Test.Sdk 17.11.1

Development/test dependency only; it is not part of the RelayLoop portable application.

- Copyright Microsoft Corporation.
- License: MIT.
- Project: https://github.com/microsoft/vstest

## xUnit.net 2.9.2

Development/test dependency only; it is not part of the RelayLoop portable application.

- Copyright .NET Foundation.
- License: Apache License 2.0.
- Project: https://github.com/xunit/xunit

## xunit.runner.visualstudio 2.8.2

Development/test dependency only; it is not part of the RelayLoop portable application.

- Copyright .NET Foundation.
- License: Apache License 2.0.
- Project: https://github.com/xunit/visualstudio.xunit

The complete MIT and Apache-2.0 license texts are available from the referenced projects and package metadata. No other NuGet package is referenced by the application or runner projects.

