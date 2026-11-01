# Third-party notices / Külső összetevők

## noisereduce

The stationary spectral-gating implementation in `Core/StationaryDenoiser.cs`
is based on the stationary algorithm in **noisereduce 3.0.3** by Tim Sainburg.
Reference: https://github.com/timsainb/noisereduce

Copyright (c) 2019, Tim Sainburg. Licensed under MIT; the complete notice is
included in `LICENSES/noisereduce-MIT.txt`. This C# adaptation uses a managed FFT,
STFT reconstruction and separable mask smoothing. It does not execute or bundle
the Python noisereduce package. No endorsement or affiliation is implied.

A `Core/StationaryDenoiser.cs` a noisereduce 3.0.3 stationary eljárásának C#
átirata. Az eredeti MIT licencet a fenti fájl tartalmazza. A Python-csomagot nem
futtatja és nem csomagolja. A projekt független, nem a noisereduce hivatalos kiadása.

## .NET

The application targets Microsoft .NET 8 Windows Desktop. Framework-dependent
downloads require the Windows Desktop Runtime. .NET is distributed separately
under its own notices: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

The project's original code is GPL-3.0-only. The MIT notice above remains intact.
