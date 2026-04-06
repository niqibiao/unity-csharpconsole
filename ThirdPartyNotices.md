# Third-Party Notices

This package redistributes the following third-party managed assemblies under `Editor/Plugins/x86_64/` for Unity Editor use.

The repository does not retain the original `.nupkg` manifests for these assemblies, so the version table below records the versions embedded in the redistributed DLL metadata.

## Bundled assemblies

| Assembly | Upstream package | Recorded version | License | License file | Notes |
| --- | --- | --- | --- | --- | --- |
| `dnlib.dll` | `dnlib` | Assembly `4.5.0.0`; File `4.5.0.0` | MIT | [`LICENSES/dnlib.txt`](LICENSES/dnlib.txt) | Direct editor dependency |
| `Microsoft.CodeAnalysis.dll` | `Microsoft.CodeAnalysis.Common` | Assembly `5.0.0.0`; File `5.0.25.56712`; Product `5.0.0-2.25567.12` | MIT | [`LICENSES/Microsoft.CodeAnalysis.Common.txt`](LICENSES/Microsoft.CodeAnalysis.Common.txt) | Direct editor dependency |
| `Microsoft.CodeAnalysis.CSharp.dll` | `Microsoft.CodeAnalysis.CSharp` | Assembly `5.0.0.0`; File `5.0.25.56712`; Product `5.0.0-2.25567.12` | MIT | [`LICENSES/Microsoft.CodeAnalysis.CSharp.txt`](LICENSES/Microsoft.CodeAnalysis.CSharp.txt) | Direct editor dependency |
| `System.Collections.Immutable.dll` | `System.Collections.Immutable` | Assembly `9.0.0.0`; File `9.0.24.52809` | MIT | [`LICENSES/System.Collections.Immutable.txt`](LICENSES/System.Collections.Immutable.txt) | Direct Roslyn dependency |
| `System.Runtime.CompilerServices.Unsafe.dll` | `System.Runtime.CompilerServices.Unsafe` | Assembly `6.0.0.0`; File `6.100.24.56208`; Product `6.1.0` | MIT | [`LICENSES/System.Runtime.CompilerServices.Unsafe.txt`](LICENSES/System.Runtime.CompilerServices.Unsafe.txt) | Transitive Roslyn dependency |
| `System.Reflection.Metadata.dll` | `System.Reflection.Metadata` | Assembly `9.0.0.0`; File `9.0.24.52809` | MIT | [`LICENSES/System.Reflection.Metadata.txt`](LICENSES/System.Reflection.Metadata.txt) | Transitive Roslyn dependency |

## External dependencies not redistributed in this package

| Dependency | Source | License | Notes |
| --- | --- | --- | --- |
| `com.code-philosophy.hybridclr` | <https://github.com/focus-creative-games/hybridclr_unity> | MIT | Used by the sample project as an external Unity package dependency for IL2CPP runtime dynamic assembly loading validation; not bundled under `Packages/com.zh1zh1.csharpconsole` |

## Upstream references

- dnlib: <https://www.nuget.org/packages/dnlib>
- Microsoft.CodeAnalysis.Common: <https://www.nuget.org/packages/Microsoft.CodeAnalysis.Common/>
- Microsoft.CodeAnalysis.CSharp: <https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/>
- System.Collections.Immutable: <https://www.nuget.org/packages/System.Collections.Immutable/>
- System.Runtime.CompilerServices.Unsafe: <https://www.nuget.org/packages/System.Runtime.CompilerServices.Unsafe/>
- System.Reflection.Metadata: <https://www.nuget.org/packages/System.Reflection.Metadata/>
- HybridCLR Unity package: <https://github.com/focus-creative-games/hybridclr_unity>
- HybridCLR runtime repository: <https://github.com/focus-creative-games/hybridclr>

See the per-dependency files under [`LICENSES/`](LICENSES/) for the full license texts redistributed with this package.
