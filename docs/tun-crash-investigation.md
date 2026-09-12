# TUN crash investigation — 2026-09-12

## Scope and evidence

The user reports that original XrayUI 1.18 was stable with the same TUN mode and
gRPC server. This investigation compares that release with fork commit
`4ab637d` (1.19.3), the installed `C:\xray` engine, and the supplied crash log.
No installed binaries, settings, or network configuration were changed.

## Binary comparison

Downloaded `XrayUI-win-x64.zip` from the original repository's 1.18 release:
https://github.com/PhoenixNil/XrayUI-dev/releases/tag/1.18

The archived files and installed files have identical SHA-256 hashes:

| File | SHA-256 |
| --- | --- |
| `Assets/engine/xray.exe` | `0E5044D23A4A9881128D010AEEFA655A5E74A79253B6A70D621DB2E8647F2DCB` |
| `Assets/engine/wintun.dll` | `E5DA8447DC2C320EDC0FC52FA01885C103DE8C118481F683643CACC3220DAFCE` |

The repository's x64 engine has the same hash. The original 1.18 tag resolves
to `d4607f75ee2c137ba197b16810c523fcfaa0a6d7`. The fork base is
`49c91ed04f29466e2acead2d876671ca53cfc045`.

The engine identifies as Xray 26.6.1 Custom, Go 1.26.0. Its build metadata
contains gVisor `v0.0.0-20260122175437-89a5d21be8f0`, but no VCS revision,
so the exact custom source revision cannot be reconstructed from the banner.

## Configuration and lifecycle comparison

Compared 1.18 with fork HEAD:

- `TunService.cs`, `XrayReadySignal.cs`, and `RealLatencyProbeService.cs`
  have no differences.
- `XrayConfigBuilder.cs` differs only by adding `api` and `stats` in 1.19.3.
  TUN inbound construction, routing construction, MTU and outbound binding
  construction are unchanged. This compares code, not historical user settings.
- Core process launch and job-object assignment in `XrayService.cs` are unchanged.
  Changes there only concern reporting an exit and draining startup output.
- The fork changed autostart/elevation, connection restoration after updates,
  subscription refresh scheduling and, in 1.19.3, stats polling. These can change
  activity or timing, but the supplied log does not establish any as the trigger.
- The original UI did not subscribe its connection state to `RunningChanged`.
  The fork now reports unexpected exits, so popup frequency is not directly
  comparable with the original release. This does not prove earlier exits occurred.

## Crash mechanism

The supplied log records successful startup at 02:50:03, then traffic, then:

```text
panic: Net: Unknown address type.
common/net.DestinationFromAddr({0x0?, 0x0?})
proxy/tun.(*Handler).HandleConnection ... handler.go:134
proxy/tun.(*stackGVisor).Start.func1.1 ... stack_gvisor.go:87
```

This matches the call chain and nil argument reported upstream:
https://github.com/XTLS/Xray-core/issues/6364

The maintainer explains that a quickly closed TCP connection can make gVisor's
`RemoteAddr()` return nil. The TUN handler passed that value unconditionally to
`DestinationFromAddr`, whose default case panics. The caller is the TCP
forwarder; this is not evidence of a server gRPC transport failure.

The upstream fix, merged 2026-06-23, reads the remote address once and returns
when it is nil, dropping only that already-closed connection:
https://github.com/XTLS/Xray-core/pull/6365
Commit: `e7e9254630bd0557363a13dc8e5dfe3a9754a3c8`.

## Conclusions and limits

The immediate failure mechanism is supported by both the log and an exact
upstream fix. A changed engine binary is ruled out for original 1.18 versus
this installation. Why the user first encounters it after the fork is not
proven: controlled runs of both UIs with equivalent configuration and traffic
have not been performed.

The crash predates our statistics implementation in the conversation, so stats
polling cannot explain its first occurrence. It could still affect timing now.
Earlier unelevated manual `-test` attempts without the app's asset environment
were not reproductions of this runtime panic; their missing-geodata and access
errors must not be used as its cause.

The bundled engine is now updated to [official Xray v26.6.27](https://github.com/XTLS/Xray-core/releases/tag/v26.6.27) (commit
`45cf289`), the first published release after #6365. The release's TUN handler
contains the nil `RemoteAddr()` guard. Both Wintun DLLs were already byte-for-
byte identical to that release and were left unchanged. The updated executable
hashes are:

| File | SHA-256 |
| --- | --- |
| `Assets/engine/xray.exe` | `9C8A154CAD1DD295F6560D82114609DD845AB8EE6BDF846169F538A80D4463C2` |
| `Assets/engine/arm64/xray.exe` | `FBFDE77079B252657A91C3EBB1D36AC211B11D97DDE798E305E7BBC4151F5BC0` |

The existing UI configuration uses standard Xray fields and remains compatible
with the updated core. A UI auto-restart can recover a process exit but is not a
substitute for shipping the fixed engine.
