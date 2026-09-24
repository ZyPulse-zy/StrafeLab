# Third-party components

StrafeLab application code is MIT licensed; see LICENSE. Bundled dependencies retain their own licenses.

- .NET / WPF / ASP.NET Core 8.0.28: Microsoft .NET runtime packages. Runtime LICENSE and THIRD-PARTY-NOTICES files are in `licenses/`.
- HidSharp 2.1.0, Copyright 2010-2019 James Bellinger: Apache License 2.0. See `licenses/HidSharp-LICENSE.txt` and https://www.zer7.com/software/hidsharp.
- Python 3.12.10 embeddable Windows distribution: see `demo-python/LICENSE.txt`.
- demoparser2 0.42.0: https://github.com/LaihoE/demoparser. MIT license in `licenses/demoparser2-LICENSE.txt`; its Rust dependency SBOM is retained in the package dist-info directory.
- numpy, pandas, polars, pyarrow, python-dateutil, six, tzdata, tqdm and colorama: original package metadata and license directories are retained under `demo-runtime/`.

The official MCHOSE frontend was inspected to understand the local HID protocol. Its frontend bundles are not redistributed in the application. HallEffectAnalogMapper was used as a protocol reference, not linked or executed. The application contains no virtual-controller or game-input generation component.

The 60.6 MB parser test Demo used during verification is not redistributed in this package.
