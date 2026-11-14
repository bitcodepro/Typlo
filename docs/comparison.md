# Typlo v0.5.14 and Typlo2.0

Measurements were taken on November 11, 2026, on Windows x64 with an Intel Core i7-10750H. The baseline was the published Typlo v0.5.14 archive, verified by SHA-256. The GitHub main branch and release both pointed to commit `a0b9a89269cc61f89ac86144e8a497decd634d4d`. The source tree used for the .NET benchmark matched that commit. [Baseline release](https://github.com/bitcodepro/Typlo/releases/tag/v0.5.14)

The table shows medians from five runs. Each background process used a separate test profile. Memory was sampled 600 ms after the ready signal, with Settings closed.

| Metric | Typlo .NET Native AOT | Typlo Rust |
|---|---:|---:|
| 700,000 conversions with a known source layout | 1,036.78 ms | 227.84 ms |
| Background startup to ready signal | 103.95 ms | 36.69 ms |
| Background working set | 24.68 MiB | 7.26 MiB |
| Background private bytes | 8.14 MiB | 1.24 MiB |

Both engines returned checksum `5e6ddbe5bf5d5e3e` on the same seven inputs. The Rust implementation used a precomputed table and a single pass with a known source layout. These timings cover the conversion engine. Shortcut-to-result latency in external editors requires a separate benchmark.

The background probes ran with different sets of features enabled. The Rust probe omitted the tray and global shortcut registration. The C# diagnostic probe disabled clipboard monitoring and update checks while starting the application's other services. Use these figures as an early baseline.

At the time of the benchmark, the Rust background executable was 443,904 bytes and the GUI executable was 5,857,792 bytes. The run also verified the first egui/Glow frame, Clippy, eight unit tests, and cross-process conversion and Undo in a standard Windows Edit field.

## Reproduce the benchmark

Build both engines in release mode. Point the C# benchmark to a checkout of the baseline commit.

```powershell
cargo build --release --locked --bin typlo
dotnet publish .\tools\TyploBench\TyploBench.csproj -c Release -r win-x64 -p:PublishAot=true -p:TyploSource="C:\path\to\Typlo" -o .\target\dotnet-bench
.\tools\compare-windows.ps1 -TyploReleaseExe "C:\path\to\Typlo.exe" -DotNetBenchmarkExe .\target\dotnet-bench\TyploBench.exe
```

[Engine samples](engine.csv) · [Background process samples](resident.csv)
