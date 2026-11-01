# Development / Fejlesztés

Target: Windows x64, .NET 8, C# 12, Windows Forms. No external NuGet packages.
Important blocks carry both HU and EN comments. GUI layout is ordinary C# code,
not a generated designer file.

## Module map / Modulok

| File | English | Magyar |
|---|---|---|
| `src/Program.cs` | GUI entry, CLI and diagnostics | Indítás, parancssor, ellenőrzés |
| `src/Gui/Localization.cs` | Presentation-only localization, HU/EN text catalog | Felületi fordítás, külön kezelt nyelv |
| `src/Gui/MainForm.cs` | Batch controls, preferences, progress | Főablak, beállításmentés, napló |
| `src/Gui/SettingsForm.cs` | Working-copy settings and presets | Munkapéldányban szerkesztett beállítások |
| `src/Core/Config.cs` | Strict public settings schema | Nyilvános séma és értékellenőrzés |
| `src/Core/StationaryDenoiser.cs` | Initial-noise stationary spectral gating | Kezdő zajmintás stationary zajszűrés |
| `src/Core/Finishing.cs` | Compression, gate, loudness and peak protection | Kompresszor, gate, LUFS és csúcsvédelem |
| `src/Core/Numeric.cs` | FFT, FIR resampling, SIMD peak estimate | FFT, FIR és csúcsbecslés |
| `src/Core/WaveFile.cs` | PCM I/O and duplicate protection | WAV be-/kimenet, ütközésvédelem |
| `src/Core/BatchEngine.cs` | One output per file, file-boundary control | Köteg, presetmappa, szünet/megállítás |
| `src/Core/PresetFiles.cs` | Safe preset persistence | Presetmentés és felülírási döntés |
| `src/Diagnostics/Verification.cs` | Synthetic DSP and GUI integration checks | Generált hangos és GUI-ellenőrzések |

## Build and verify / Fordítás és ellenőrzés

```powershell
dotnet build src/Mastering.CSharp.csproj -c Release
$app = Resolve-Path src/bin/Release/net8.0-windows/MasteringAudioProcessor.exe
Start-Process $app -Wait -ArgumentList '--verify --test-dir C:\Audio\new-tests --report C:\Audio\checks.json'
```

The test directory must not exist. Tests generate only synthetic audio, cover
all four supported sample-rate/bit-depth combinations, noise reduction,
zero-strength identity, feasible LUFS targets, quantized peak protection, gate,
preset collision decisions, file-boundary controls and GUI bypass states.
The report must contain `passed: true`; a process starting successfully alone
does not establish DSP correctness.

A tesztmappa még nem létezhet. A teszt kizárólag generált hangot használ;
mind a négy támogatott formátumot, a DSP-t, a presetütközést, a kötegvezérlést
és a felületi bypass állapotokat ellenőrzi. A jelentés `passed: true` értéke
jelzi a sikert; a program puszta elindulása nem DSP-teszt.

## Release / Kiadás

```powershell
dotnet publish src/Mastering.CSharp.csproj -c Release --no-restore --self-contained false -o artifacts/windows
```

Include `LICENSE`, the READMEs, `src/THIRD_PARTY_NOTICES.md` and the `LICENSES`
folder in binaries. Distribute corresponding source under GPL-3.0-only.
Do not commit build output, user preferences, audio recordings or processing
logs. The included CI workflow builds and verifies synthetic audio on Windows.

A binárishoz a licencek és a megfelelő forrás is tartozzon. Ne commitolj
fordítási kimenetet, felhasználói beállítást, felvételt vagy feldolgozási naplót.

Public release history begins at **0.1.0 / 20261101.01 / 2026-11-01**.
Update `BuildInfo` and `src/BUILD_INFO.json` together. Make no performance or
bit-identity claim without measurements of this exact public pipeline.

## Localization / Fordítás

`src/Resources/ui-text.json` contains canonical message keys and explicit `hu` /
`en` texts. Keep placeholders in the same order in both languages. `L.T` selects
literal labels; `L.Render` translates formatted presentation messages without
changing substituted filenames. Do not translate protocol keys, JSON/XML
identifiers, preset names or folder components. Native Windows dialogs use the
operating system language. Add catalogue coverage tests for new fields.

A `gui_preferences.json` külön `language` mezőt kap (`hu` vagy `en`). Régi, ilyen
mező nélküli mentés magyarul indul. A Beállítások munkapéldányában lévő nyelv csak
Mentéskor kerül alkalmazásra; a preset JSON-ba nem kerül bele. A nyelvi tesztek
mintára azonos DSP-kimenetet és a tényleges mentési/visszatöltési utat ellenőrzik.

Current public release: **0.2.0 / 20261101.02 / 2026-11-01**.
