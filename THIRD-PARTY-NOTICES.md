# Third-party components

The optional local media setup downloads Gyan's FFmpeg 9.0.2 essentials Windows build. The binary archive is not committed to this repository. Its included LICENSE and README are copied beside the tools when they are present during build/publish.

- FFmpeg project and source: https://ffmpeg.org/
- Windows build distributor, build information and source links: https://www.gyan.dev/ffmpeg/builds/
- Tested archive SHA256: `60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA`
- The tested build is GPL v3. Any redistribution of that build must retain its notices and satisfy its applicable license and corresponding-source requirements.

Alternatively, configure existing FFmpeg and FFprobe executables in the application settings. Other .NET dependencies retain their respective NuGet package licenses.

## Optional online-video tools (Windows x64)

`scripts/Setup-OnlineVideoTools.ps1` obtains immutable official releases, verifies their SHA256 values before use, and keeps them under `artifacts/online-tools`. The binaries are not committed. Build/publish copies locally prepared binaries and their notices into `tools`; no global installation or PATH change occurs.

### yt-dlp 2026.08.19

- Official release and source: https://github.com/yt-dlp/yt-dlp/releases/tag/2026.08.19
- Windows `yt-dlp.exe` SHA256: `66674953FE251B89F4D08C5F0E35E0728679BD67AB3D7D05C0562AF101DD3E7A`
- Published checksum list: https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/SHA2-256SUMS
- yt-dlp source is under the Unlicense. The official PyInstaller Windows executable combines dependencies licensed under GPL v3 or later, so that combined executable is GPL v3 or later, as described in its versioned README: https://github.com/yt-dlp/yt-dlp/blob/2026.08.19/README.md#license
- `tools/yt-dlp-LICENSE.txt` and `tools/yt-dlp-THIRD-PARTY-LICENSES.txt` retain upstream license texts and dependency attribution. Redistributing the combined executable requires satisfying its applicable licenses and corresponding-source requirements.
- Its bundled EJS scripts are used with Deno; the application does not request runtime installation of remote EJS components. Upstream setup documentation: https://github.com/yt-dlp/yt-dlp/wiki/EJS

### Deno 2.9.7

- Official release and source: https://github.com/denoland/deno/releases/tag/v2.9.7
- `deno-x86_64-pc-windows-msvc.zip` SHA256: `A0C3101B4158D1DFB7D6A78A7BF0F3DE80C96BB423C152BEEC8BEB22786F2238`
- Extracted `deno.exe` SHA256: `E020F3E232BD16E33768DEE528E5983349C962952051CED0A5D58AD42F5D9B33`
- Upstream MIT license, copyright 2018–2026 the Deno authors, is retained in `tools/Deno-LICENSE.txt`.
- The setup also preserves pinned upstream notices for V8 15.0.245.2, Rusty V8 150.4.0, TypeScript 6.0.3 and Deno's Node/Undici-compatible definitions in the corresponding `tools/Deno-*-LICENSE.txt` files.
- Deno includes third-party components, including V8, Rust ecosystem dependencies and Node-compatible definitions, which retain their respective licenses. The versioned dependency and source inventory is https://github.com/denoland/deno/blob/v2.9.7/Cargo.lock and https://github.com/denoland/deno/tree/v2.9.7. Its top-level MIT license does not replace those component licenses.

The application may alternatively use user-configured tool executables; their distributors' notices and licenses apply.

## Optional local GPT-SoVITS

The local voice adapter communicates with the official GPT-SoVITS `api_v2.py` running on loopback. The training bridge invokes upstream preparation and training programs. Runtime source, Python packages and model weights are downloaded separately; they are not embedded in the application's .NET assemblies or committed to this repository.

- Official source and MIT license: https://github.com/RVC-Boss/GPT-SoVITS
- Upstream API: https://github.com/RVC-Boss/GPT-SoVITS/blob/main/api_v2.py
- Runtime preparation and model provenance: `scripts/gpt-sovits/README.md` and its setup manifest.

The top-level source license does not replace the individual licenses for pretrained models and Python dependencies. Keep their accompanying notices when redistributing a prepared runtime. User recordings and trained voice models are local user data.
