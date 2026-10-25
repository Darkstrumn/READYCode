# Pocket READY. target integration

Current scope: a separate, read-only `PocketReadyClient` and typed console-protocol discovery parser. C64U/VICE clients, BASIC tokenization, assembly/debug stubs, editor language classification and all existing target commands remain unchanged. No Pocket READY. target selector or deployment command is exposed yet.

## Discovery contract

`GetInfoAsync(baseUri,cancellationToken)` requests `GET /pr/v1/info` using `Accept: application/json`. An optional reverse-proxy base path is preserved. The response must be UTF-8 JSON, at most 64 KiB, with protocol name `pocket-ready-console`, major version 1 and an explicit capability declaration. Unknown additive fields and nonnegative minor versions are accepted; incompatible product/language/encoding, malformed identities, omitted flags and invalid state are rejected.

The current fixture in `ReadyCode.Tests/Fixtures/pocket-ready-discovery.json` is shared with Pocket READY.'s `docs/protocol/console-v1.discovery.json`. These are current cross-repository contract fixtures, not historical source snapshots. Update both when changing the current contract. Product versions are descriptive and independent from the protocol major/minor and virtual-device ABI.

The Android-side discovery handler is an in-process, read-only handler; it does not bind a port. Its descriptor advertises empty `transports` and `controlOperations` and false remote output, located diagnostics, filesystem, virtual-memory, CPU-debug and assembly capabilities. Device graphics/audio describe local runtime facilities and do not imply desktop access. A successful fake-handler test is not evidence of an installed Android HTTP service.

`instanceId` identifies the current broker lifetime. Worker replacement/RUN STOP keeps it; application/broker restart or deliberate Pocket READY. reboot changes it. `session.generation` changes for accepted operations inside that lifetime. Future control requests must carry both identity and expected generation, and acquire ownership explicitly; discovery does not grant control or return a lease credential. Rediscover before reconnecting to a restarted host.

## Client ownership and limits

The caller owns and reuses the injected HttpClient. The client retains no WPF objects and never blocks a dispatcher. Every network await uses `ConfigureAwait(false)`. A caller cancellation token and configurable deadline (15 seconds by default, positive and at most five minutes) cover headers and body reads. Declared and streamed lengths are checked independently; streams/responses/request objects and the linked cancellation source are disposed on success, malformed data, HTTP failure and cancellation. The caller's HttpClient is not disposed.

Base addresses must be absolute HTTP(S), without query, fragment or embedded credentials. This does not add transport authentication. An eventual Android service and desktop settings UI must agree on interface selection, control authorization, lifecycle, reconnect and timeout policy before enabling mutations.

## Next implementation gates

1. Pocket READY. must provide an actual user-enabled LAN listener and local/remote ownership transitions through its existing session broker. Implement bounded request bodies, callback dispatch, cancellation and Android lifecycle without blocking the UI or relying on a visible rendering frame loop.
2. Define and implement operation envelopes, structured source locations, output cursors/gaps and idempotent load/run/stop semantics in both repositories. Do not relabel UTF-8 source as binary `load_prg`, or stuff commands into C64 ROM/keyboard memory.
3. Introduce a Pocket READY. BASIC language profile: optional/named labels, FUNCTION/SUB/USE and SDK calls. Gate C64-only tokenization, renumbering/minification and assembly/debug commands by dialect/capability while preserving current C64U/VICE behavior.
4. Connect the profile/client to the smallest needed WPF target seam, then test real Windows-to-S24 upload/run/stop, reconnect and unsaved draft preservation. Filesystem browsing and cooperative source debugging follow demonstrated control-path behavior.

The existing `feature/pocket-ready-target` branch is the integration branch for review. This change intentionally supplies no placeholder success response or inactive deploy button.

## Verification

`dotnet test ReadyCode.Tests/ReadyCode.Tests.csproj -c Debug` builds the WPF application and runs tests on Windows with the existing .NET 8 target. The new verification workflow runs this on PRs and main; feature-branch pushes are covered by their PR so tests are not duplicated. `PocketReadyClientTests` uses injected HTTP handlers and deterministic cancellation signals to cover the shared fixture, route/base path, required metadata, version tolerance, HTTP errors, invalid UTF-8, bounded chunked bodies, timeout and resource disposal. Real Android/network and WPF UI interaction remain separate acceptance gates.
