# LinguaForge-X

Windows 桌面视频字幕与配音工具，使用 **.NET 10、WPF 和 Clean Architecture**。支持本地视频及 B 站、YouTube 等视频网站链接：先把素材下载到本地，再调用 Azure 完成转写、翻译，配音可以使用 Azure 或本地 GPT-SoVITS，通过 FFmpeg 在本机生成 MP4 和 SRT。

## 已实现

- 在线视频链接解析、单个视频下载、清晰度上限、下载进度及取消；下载后自动导入转换页面。
- YouTube / B 站人工网页登录与验证入口：在独立浏览器中操作，明确点击后保存该站会话并重试读取。
- Azure AI Speech 快速转写、Azure OpenAI Whisper 转写，使用服务返回的真实时间戳。
- Azure OpenAI 文本模型翻译，按片段编号验证完整性；字幕和配音支持分别选择语言。
- Azure Speech / Azure OpenAI TTS 配音；Speech 在线加载音色及多语言音色支持范围。
- 本地 GPT-SoVITS v2：本人参考录音音色、成对训练模型导入、本地微调训练、音色档案和中文配音；训练与合成不上传录音。
- 可编辑原文、译文、配音稿和起止时间；原文或双语字幕、字幕字号、烧录和独立 SRT。
- 超时配音可自动精简文字并重新合成、实测时长；同语言且原本同稿的字幕同步更新。
- 本地 MP4 导出，选择替换原音轨或降低原音后混合，保留句间静音。
- 导出支持自动检测显卡编码，以及 CPU 快速、CPU 画质与体积优先模式。
- 输出画质支持保持源分辨率、4K、1080p、720p 和 480p；保持比例缩小，低分辨率素材不会放大。
- 分段转写、缓存、任务历史、恢复、取消、进度与失败重试。
- 设置持久化；三个服务的密钥均使用 Windows DPAPI 当前用户加密。

## 开发运行

需要 Windows 和 .NET 10 SDK。

```powershell
# 可选：准备经过 SHA256 验证的便携 FFmpeg / FFprobe。
# 已经安装工具的用户，也可以直接在设置页选择两个 exe 文件。
.\scripts\Setup-MediaTools.ps1
# 在线视频功能：准备经过 SHA256 验证的 yt-dlp 和 Deno。
.\scripts\Setup-OnlineVideoTools.ps1

dotnet restore
dotnet run --project src\LinguaForge-X.Presentation.Wpf
```

准备脚本只写入仓库的 `artifacts/media-tools` 和 `artifacts/online-tools`，不修改系统 PATH。构建时会将已准备好的工具及许可证复制到应用 `tools` 目录，默认配置会自动发现它们。FFmpeg 下载地址可能更新，脚本遇到不匹配的版本会停止；可使用 `-ArchivePath` 指定哈希匹配的本地 ZIP，或者在设置页指定已有工具。

```powershell
dotnet test
dotnet publish src\LinguaForge-X.Presentation.Wpf -c Release --no-restore -o artifacts\publish
```

发布为需要 .NET 10 Windows Desktop Runtime 的 Windows 应用。可运行发布目录中的 `LinguaForge-X.Presentation.Wpf.exe`。

项目根目录的 `settings.json` 会在每次编译和发布时复制到 EXE 所在目录，并覆盖输出目录中的同名文件。程序从 EXE 同目录读取和保存配置，不受启动工作目录影响，也不再读取 `%LOCALAPPDATA%\LinguaForge-X\settings.json`。程序内保存的修改只写入运行目录；若需要用于下一次构建，请先将运行目录中的文件同步回项目根目录。

真实 `settings.json` 及其备份保持 Git 忽略，字段说明见 [settings.demo.json](settings.demo.json)。没有根目录配置文件时仍可编译；运行时缺少配置会使用默认值，首次保存后在 EXE 同目录创建文件。请将程序放在当前用户可写的目录；保存失败时会显示具体文件路径。

## 第一次使用

### 使用本地本人音色

运行环境安装、模型下载与训练数据格式见 [GPT-SoVITS 本地运行说明](scripts/gpt-sovits/README.md)。这部分需要单独的 Python 环境及预训练模型，不能仅靠 .NET 程序运行。

1. 打开「我的声音」，填写包含 `api_v2.py` 的 GPT-SoVITS 目录及其 Python 解释器，保持服务地址为 `http://127.0.0.1:9880`，检测并启动本地服务。
2. 填写音色名称，选择 **3～10 秒的清晰本人 WAV 录音**，填写与录音完全一致的文字。留空两个模型路径可以保存参考录音音色，用预训练模型复刻；该操作不会微调模型。
3. 已有训练成果时，同时选择匹配的 GPT `.ckpt` 与 SoVITS `.pth` 文件并保存。应用复制录音和模型到本地音色档案，不依赖原文件之后的位置。
4. 要实际训练，准备本人音频与校对过的逐片文稿 `.list`，在「我的声音」选择训练清单和训练轮数后开始。训练桥接依次执行预处理及 SoVITS/GPT 微调，只有成功产生成对模型才会保存音色。CPU 可运行，但本机速度应以实际测试为准；可取消训练。
5. 选择保存的音色，点击用于视频配音并试听，再回到「视频转换」生成配音稿、校对及导出。已准备好配音稿的任务可更换音色重新导出。

训练与配音都在本机执行，服务只允许本机回环地址。Azure 识别、翻译和自动精简配音稿仍使用原来的服务配置；选择本地配音不会删除已保存的 Azure 配音凭据。音色档案位于当前 Windows 用户应用数据目录 `LinguaForge-X/local-voices`，录音、模型、训练数据不会提交进 Git。

本地连接检查仅验证服务接口可用；模型与音色是否可用应以试听结果为准。本地 API 使用一个全局模型，应用会串行切换模型和合成。请勿让其它程序同时切换同一个 API 实例的模型。

### 使用 Azure 与视频转换

1. 打开「服务设置」，配置下面的转写、翻译、配音服务。可以只配置转写以生成原语言字幕。
2. 保存设置，按需测试连接、刷新能力目录、试听音色。连接测试会发送很小的实际请求，可能计费；保存和加载目录不表示已完成全部能力验证。
3. 检查 FFmpeg / FFprobe；在线素材还需检查 yt-dlp / Deno。返回「视频转换」导入本地视频，或粘贴视频链接并下载导入，然后选择正确音轨。
4. 选择字幕/配音、语言、音色及输出位置，生成字幕和配音稿。
5. 校对文字与时间后导出。「导出编码」默认使用自动加速，也可选择 CPU 模式；「输出画质」默认保持源分辨率，可按需要选择较低档位。已生成字幕的任务可以直接更改这两项重新导出。「自动精简超时字幕／配音稿」默认开启，可在导出前关闭。自动精简后的文字会回填供复核。已有文件不会被覆盖，重新导出时请选择新文件名。
6. 可在「任务历史」恢复任务；准备未完成的任务须先恢复生成，完成后才能导出。

## B 站、YouTube 等在线素材

1. 在「服务设置」的在线下载设置中确认 yt-dlp、Deno 和素材保存目录。已运行准备脚本并重新构建时，无需手动填写工具路径。
2. 在「视频转换」粘贴完整视频地址，选择清晰度上限。可先解析标题、平台和时长，也可直接下载导入。
3. 下载完成后，应用检查音视频轨道并准备可处理的 MP4。选择中文作为字幕和配音语言，勾选所需输出，点击生成字幕与配音稿。
4. 校对后点击导出，保存 MP4 和可选 SRT。来源链接和标题随任务保存，历史恢复使用已下载的本地素材。

下载使用 [yt-dlp](https://github.com/yt-dlp/yt-dlp) 的站点适配器；主要面向 Bilibili 和 YouTube 单个点播视频，也可尝试其支持的其他视频网站。清晰度是上限，实际取决于视频提供的格式与当前访问权限。此版本不批量抓取频道/播放列表，不录制直播，不处理 DRM 视频。

若当前网络需要代理，在设置中填写 HTTP、HTTPS 或 SOCKS 代理地址。留空时保留下载器的系统/环境代理检测；浏览器扩展或 PAC 代理不保证自动生效，必要时填写可播放该视频的同一代理地址和端口。需要登录的素材可显式选择 Netscape 格式的 Cookie 文件；应用不读取日常浏览器的个人配置目录，只在本机调用下载器时使用所选文件的临时副本，操作结束后删除副本。原 Cookie 文件仍由用户管理，设置仅保存其路径；代理地址（可能含账号密码）使用 Windows DPAPI 加密。

若提示「确认不是机器人」，这是网站要求验证当前访问会话，不能据此判断视频是私有或账号无权访问。YouTube 和 B 站可使用下面的人工验证流程。其他情况也可按 [yt-dlp 官方 YouTube Cookie 指南](https://github.com/yt-dlp/yt-dlp/wiki/Extractors#exporting-youtube-cookies) 导出该站的有效 Netscape Cookie，在「服务设置 → 在线视频下载」选择文件并保存后重试。Cookie 是登录凭据，应留在本机，不要发到聊天或提交进仓库。有效 Cookie 也不保证消除平台验证；私有、会员或年龄限制内容需账号具备对应权限，频率限制则需等待后再试。

### 在软件中发起网页登录／验证

1. 粘贴 YouTube 或 B 站的 HTTPS 视频地址，点击「网页登录／验证」。需要本机已安装 Microsoft Edge 或 Google Chrome。
2. 软件打开一个使用独立临时配置目录的真实浏览器窗口。你在网站中亲自登录、完成验证并确认视频可以播放，然后返回软件。此窗口不共享日常浏览器的登录状态，可能需要重新登录。
3. 点击「使用会话并重试」。只有此时软件才读取该窗口中对应视频网站的 Cookie，连同浏览器 User-Agent 加密保存，并关闭本次验证窗口，然后重试读取视频信息。原有的手动 Cookie 文件选择会被清除，改用本次会话。
4. 读取成功后点击「下载并导入」，继续生成字幕／配音与导出。若仍然要求验证，界面保留真实错误，不会把“保存了 Cookie”显示为“验证已通过”。
5. 「关闭验证窗口」放弃本次操作；「清除该站会话」删除软件保存的该站会话，不删除任务和原视频。

会话只供匹配的视频网站使用，不导出 Google 等其他域名的账号 Cookie，也不读取既有的 Edge/Chrome 个人资料。会话使用 Windows DPAPI 当前用户加密；调用下载器时仅在任务目录生成临时 Netscape 文件，结束后清除。保存的会话最多使用 24 小时，网站可能使其更早失效。改变显式代理配置后需要重新验证；交互浏览器暂不支持地址内含账号密码的代理，此类情况可继续手动导入 Cookie。

若组织策略强制浏览器使用固定的用户资料目录，软件会跳过该浏览器；没有可用浏览器时请使用手动 Cookie 文件。该限制用于确保验证窗口使用自己的临时目录，避免 [UserDataDir 策略覆盖独立目录设置](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-policies/userdatadir)。

该功能提供人工验证入口，不自动识别或代填验证码，也不改变网站的验证结果。网站仍可能拒绝下载器请求，或要求当前未接入的额外令牌。Google 对部分内嵌网页登录有限制，因此此功能使用独立的正常浏览器窗口，而非伪造验证页面。

YouTube 使用 [yt-dlp 的 EJS 和 Deno 运行环境](https://github.com/yt-dlp/yt-dlp/wiki/EJS)。准备脚本提供包含 EJS 的官方 yt-dlp 独立程序及 Deno。平台接口、登录验证、地区和网络条件会影响下载结果；工具检查成功不代表某个视频一定可下载。遇到接口变化可在设置中选择更新后的官方工具。

解析/下载不会调用 Azure。下载后点击生成和导出才会进入现有云服务流程。取消或失败不会用不完整素材替换当前已导入的视频；已完成素材会保留，便于后续导出和历史恢复。

### 方案 A：Speech 转写和配音 + Azure OpenAI 翻译

转写及配音选择 **AzureSpeech**，分别填入服务 Endpoint 和 Access Key，也可使用 Access Key + Region。可以复用同一个 Speech 资源，但两个能力仍分别配置、测试。

```text
Endpoint: https://<speech-resource>.cognitiveservices.azure.com
Region:   资源所在区域，例如 eastus
Key:      对应 Speech 资源的密钥
```

翻译选择 **AzureOpenAI**，填入资源 Endpoint、Access Key、文本模型的部署名称和 API Version。部署须支持 Chat Completions 的 JSON 模式，模型应支持所选语言。仅配置 Speech 不能完成这里的文本翻译步骤。

```text
Endpoint:    https://<openai-resource>.openai.azure.com
Deployment:  在 Azure 中创建的文本模型部署名称
API Version: 2024-10-21（按部署支持的版本配置）
```

### 方案 B：全部使用 Azure OpenAI

三个能力均选择 **AzureOpenAI**，按能力分别填写部署名称：

| 能力 | 配置要求 |
|---|---|
| 转写 | Whisper 部署，模型能力填 whisper，必须支持 verbose_json 分段时间戳 |
| 翻译 | 支持 Chat Completions 与 JSON 模式的文本模型部署 |
| 配音 | TTS 部署，例如 tts-1 / tts-1-hd 或支持对应接口的 TTS 模型 |

可使用同一资源 Endpoint 和 Key，但部署名称通常不同。支持资源根地址、完整部署接口地址和 `/openai/v1` 地址；完整地址中的部署与显式配置冲突时会报错。完整地址带 api-version 时使用地址中的版本。Endpoint 必须为 HTTPS。

当前 Azure OpenAI 音色和语言列表是保守的内置能力目录，并非通过 Key 枚举得到；需要使用连接测试、试听和实际样本确认当前部署可用。Speech 音色列表在线获取，并考虑 SecondaryLocaleList。未配置翻译时，不会提供任意跨语言转换选项。

## 视频和配音行为

- 输入支持 FFmpeg 能读取的本地视频；首版统一导出 MP4。没有音轨的视频无法转写。
- 转写音频为 16 kHz 单声道 WAV；每个核心片段约 5 分钟，内部边界有 1 秒上下文，并保留原音轨起点。合并时按片段中心归属去重。
- 字幕烧录要求 FFmpeg 含 subtitles / libass 及可用中文字体；烧录会重新编码画面。输出画质保持源分辨率、未烧录字幕且编码兼容 MP4 时复制原视频流，否则转为 H.264。
- 配音按时间窗放回视频，较短片段后保留静音；较长片段只在设定最大语速内加速。
- 默认开启「自动精简超时字幕／配音稿」。配音超过片段时长与最大加速比例允许的范围时，调用已配置的文本翻译模型，要求保留原意、事实、名称和数字，缩短表达；随后重新合成配音并实测时长，最多尝试 3 轮。这会增加少量文本与语音服务用量。模型结果仍需复核，不能保证任何文本都能在时间窗内完整表达。
- 字幕与配音为同一语言，且该片段的字幕原本与配音稿相同时，自动精简会同步更新二者；单独编辑过的字幕保留。新的配音稿及同步更新的字幕会显示在校对表格中。
- 关闭自动精简、未配置所需文本服务，或 3 轮后仍无法适配时，显示具体片段和时长，需手动缩短配音稿、调整起止时间或合理修改最大加速比例后重试。软件不会硬截断话语来凑时长。
- 「完全替换」会移除原音轨的所有内容，包括音乐；「降低原音后混合」会保留低音量的英文人声。当前没有人声分离、口型同步或多人音色分配；本人音色可使用本地 GPT-SoVITS。
- 选择 Azure 服务时，相应的转写音频、翻译或配音文本会发送到已配置的服务。本地 GPT-SoVITS 的参考录音、训练和语音合成留在本机；视频画面与最终合成也在本机处理。
- 内置播放器的格式支持受 Windows 媒体组件影响，导出结果也可使用外部播放器打开。

### 导出编码、画质与耗时

在「视频转换 → 输出选项」选择「导出编码」和「输出画质」。两者都属于全局媒体设置，修改后直接用于下一次导出，不需要重新转写、翻译或生成字幕；恢复旧任务不会改变当前选择。在「服务设置」保存后，下次打开软件也会沿用。

| 模式 | 行为 |
|---|---|
| 自动加速（推荐） | 实际尝试初始化可用的显卡编码器，确认可运行才启用；没有可用显卡编码器时使用 CPU 快速编码。 |
| CPU 快速 | 使用 libx264 的 veryfast 预设，按输出画质设置压缩强度，优先缩短编码时间；文件可能较大。 |
| CPU 画质与体积优先 | 使用 libx264 的 medium 预设，按输出画质设置压缩强度，通常需要更多时间。 |

| 输出画质 | 横屏最大分辨率 | 竖屏最大分辨率 |
|---|---|---|
| 保持源分辨率（推荐） | 保持源视频分辨率 | 保持源视频分辨率 |
| 4K（最高2160p） | 3840 × 2160 | 2160 × 3840 |
| 全高清（最高1080p） | 1920 × 1080 | 1080 × 1920 |
| 高清（最高720p） | 1280 × 720 | 720 × 1280 |
| 标清（最高480p） | 854 × 480 | 480 × 854 |

各档位按源视频上限输出，在对应范围内保持原比例，不拉伸、不放大。例如 720p 视频选择 4K 后仍不会变成真正的 4K。选择分辨率档位会重新编码并应用该档位的压缩设置，即使源视频已经小于上限也一样；较低档位通常更省空间，转码负担也更低。

显卡与 CPU 编码器使用不同的质量控制参数，不能把显卡质量参数视为与 CPU CRF 等价。实际耗时和文件大小受视频分辨率、帧率、内容、显卡及驱动影响，不保证固定加速倍数。

把字幕烧录进画面需要逐帧处理并重新编码视频。如果只需要独立 SRT 和配音，可在准备任务时取消「烧录到视频画面」、保留「导出独立 SRT」，并选择「保持源分辨率」；原视频编码兼容 MP4 时直接复制视频流，省去重新编码画面的时间。这条路径最省时，也不受编码模式影响；字幕不会直接显示在视频画面内，播放时需加载 SRT。

## 保存、缓存和恢复

服务配置保存在 **EXE 同目录的 `settings.json`**，包含 Windows 当前用户加密后的密钥和代理配置。

其余数据仍保存在 `%LOCALAPPDATA%\LinguaForge-X`：

| 路径 | 内容 |
|---|---|
| downloads/素材编号 | 已下载并供后续任务使用的本地视频（可配置保存目录） |
| website-sessions | 加密保存的 YouTube / B 站验证会话，可从界面清除 |
| verification-browser | 本次人工验证浏览器的独立临时配置，关闭后尝试清理；异常退出可能留下目录 |
| jobs/任务编号/project.json | 输入文件信息、选项、校对结果和状态，不含服务密钥 |
| jobs/cache-stt-* | 与源文件及转写配置匹配的分段转写缓存 |
| jobs/cache-translation | 与原文、目标语言及翻译部署匹配的翻译缓存 |
| jobs/cache-voice | 与配音稿、语言、音色及部署匹配的 WAV 缓存 |
| jobs/cache-speech-shortening | 与原稿、时间窗口和文本服务匹配的精简结果 |

修改输出路径或样式可复用已完成的云端结果；改变配音稿只会重新合成改变的文本。源文件大小或修改时间变化后要求重新准备。API Key 轮换不影响相同服务结果的缓存。

密钥加密绑定保存它的 Windows 用户环境，不能直接复制设置文件到其他用户后解密。任务及缓存保留字幕、译文和音频以便恢复；首版不自动清理。退出应用后可以手动备份或清理整个 jobs 目录，但清理会失去任务历史和缓存。

## 测试

```powershell
dotnet test tests\LinguaForge-X.Tests --filter "Category!=LiveBrowser"
# 界面绑定与离屏截图检查，不连接 Azure、不读取真实密钥。
dotnet run --project scripts\UiSmoke -- artifacts/ui-smoke
# 可选：正常 Windows 用户下检查独立浏览器的启动与清理，仅打开本机临时页。
$env:LINGUAFORGE_BROWSER_SMOKE = "1"
dotnet test tests\LinguaForge-X.Tests --filter "Category=LiveBrowser"
Remove-Item Env:LINGUAFORGE_BROWSER_SMOKE
# 可选的真实联网验证：不读取已保存设置、Cookie 或 Azure 凭据。
# 从仓库根目录运行；--download 会保存素材到 artifacts/online-smoke。
dotnet run --project scripts\OnlineVideoSmoke -- "https://www.w3schools.com/html/mov_bbb.mp4" --download
# 可选：真实本地音色运行检查；使用独立临时音色目录，不读取 Azure 配置。
dotnet run --project scripts\LocalVoiceSmoke -- 'C:\GPT-SoVITS' 'C:\GPT-SoVITS\runtime\python.exe'
# 可追加合成测试参考 WAV 和准确的英文转写，以检查真实中文音频生成。
# Python 路径应替换为安装 GPT-SoVITS 依赖的实际解释器。
```

测试覆盖请求格式、Endpoint 解析、翻译编号与时间戳校验、SSML 转义、密钥加密、失败和取消恢复、SRT 格式，以及真实 FFmpeg 提取、字幕、配音、混音和进程取消。在线下载测试覆盖地址与参数处理、进度、输出校验、登录信息处理及失败/取消；验证功能测试覆盖浏览器协议、会话加密、Cookie 站点范围、过期、代理匹配、临时文件清理和手动文件优先级。UI 检查覆盖打开、显式保存、重试失败、取消、链接/代理变更和退出清理。自动化测试不依赖视频网站和真实 Azure 凭据，也不代替用户实际完成网站验证后的联调。

媒体测试优先使用 LINGUAFORGE_FFMPEG 和 LINGUAFORGE_FFPROBE 环境变量，也可发现仓库 artifacts/media-tools 中的工具；缺少工具时会明确跳过。DPAPI 测试需要正常的 Windows 用户配置文件，受限模拟用户环境可能无法运行。

Azure 测试使用模拟 HTTP 响应，**不等于真实部署联调**。上线前应使用实际资源验证英文短视频、较长视频、中文配音和所需语言的输出质量及服务配额。

## 项目结构

- Domain：任务、时间轴、语言、音色和配置模型。
- Application：两阶段处理流程、缓存规则、字幕生成和服务接口。
- Infrastructure：Azure REST、本地 GPT-SoVITS、yt-dlp 在线素材下载、FFmpeg、DPAPI 和本地存储。
- Presentation.Wpf：MVVM 界面、媒体预览和文件选择。
- tests/LinguaForge-X.Tests：核心、接口与媒体集成测试。

参考：[Speech 快速转写](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/fast-transcription-create)、[Speech 音色接口](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/rest-text-to-speech)、[Azure OpenAI API](https://learn.microsoft.com/en-us/azure/foundry/openai/reference)、[FFmpeg](https://ffmpeg.org/documentation.html)。
