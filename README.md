# Prey Eyes 2

Prey Eyes 2 is a MelonLoader mod for **Shin Megami Tensei III: Nocturne HD Remaster**.

It adds an SMTV-style targeting reticle, an enemy affinity board, Cathedral of Shadows affinity display support, and a START-toggle BuffView display.

## Features

- Reticle color changes based on the selected skill's effectiveness.
- Unknown affinities show a question-mark overlay.
- Full affinity board for targeted enemies.
- Compact ailment-resistance row for Curse, Nerve, and Mind.
- Cathedral of Shadows affinity display for fusion previews.
- Persistent knowledge system learned through attacks, kills, Analyze/Spyglass, recruitment, and fusion.
- START-toggle BuffView display using the same working implementation as the Reticles Only build.
- FPS-unlock friendly board visibility behavior as of `2.5.0`.
- Full Prey Eyes 2 uses the stable BuffView path as of `2.5.2`; inline buff icons are reserved for the inactive Prey Eyes 3 fork.
- Normal play logs stay quiet as of `2.5.3`; deep traces are opt-in.
- KO/death-freeze no longer suppresses live affinity-board drawing as of `2.5.5`.

## Requirements

- SMT3 HD Remaster on Windows.
- MelonLoader `0.6.x` installed for SMT3 HD.
- .NET 6 SDK.
- MelonLoader-generated IL2CPP assemblies for SMT3 HD.

Before building, launch the game once with MelonLoader installed so these dependency folders exist:

```text
<SMT3HD>\MelonLoader\net6\
<SMT3HD>\MelonLoader\Il2CppAssemblies\
```

## Build

From this repository:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build ".\PreyEyes2.csproj" -c Release /p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\smt3hd"
```

If your SMT3 HD install is in a different folder, replace the `GameDir` value.

The compiled DLL will be written to:

```text
bin\Release\net6.0\PreyEyes2.dll
```

## Install

Copy the built DLL into your SMT3 HD `Mods` folder:

```powershell
Copy-Item ".\bin\Release\net6.0\PreyEyes2.dll" "C:\Program Files (x86)\Steam\steamapps\common\smt3hd\Mods\PreyEyes2.dll" -Force
```

Copy the included icon assets into the same game install:

```powershell
Copy-Item ".\Mods\icons" "C:\Program Files (x86)\Steam\steamapps\common\smt3hd\Mods\icons" -Recurse -Force
```

The icon assets include the affinity board art, `reticle_white.png`, and centered reticle result icons under:

```text
Mods\icons\reticleresults\
```

Do not run this full build at the same time as the Reticles Only alternative. They use the same MelonLoader mod name and installed DLL path:

```text
Mods\PreyEyes2.dll
```

## Knowledge Data

Prey Eyes stores learned affinity data in:

```text
Mods\PreyEyes2_knowledge.json
```

Deleting that file resets learned affinity knowledge.

## Notes

The assembly name remains `PreyEyes2` for compatibility with existing save/config/knowledge paths.

Optional diagnostic switches can be enabled by placing empty files in the game's `Mods` folder:

```text
PreyEyes2_trace
PreyEyes2_diag
PreyEyes2_luminance
```

`PreyEyes2_trace` enables detailed runtime trace logging. `PreyEyes2_diag` enables trace logging plus raw affinity diagnostics. `PreyEyes2_luminance` switches the reticle tinting strategy.

## Credits

See [CREDITS.md](CREDITS.md).

## License

MIT. See [LICENSE](LICENSE).
