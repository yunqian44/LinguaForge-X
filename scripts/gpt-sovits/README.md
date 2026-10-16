# 本地 GPT-SoVITS

这个目录把 LinguaForge-X 接到官方 GPT-SoVITS 的本机 API 和真实训练程序。首版采用 **v2 基座**，源码核查版本为 `1b47ca07ef43a284e0b8bd7f29d3e1e3ff76082a`。不要求 Azure Personal Voice 审批，不使用云端音色 API。原有视频转写和翻译如仍选择 Azure，会继续使用相应服务。

## 安装与启动

Windows PowerShell，在仓库根目录执行：

```powershell
.\scripts\gpt-sovits\Setup-GptSoVits.ps1 -InstallDependencies -DownloadModels `
  -FfmpegPath 'C:\你的路径\ffmpeg.exe'

$runtime = Join-Path $PWD 'artifacts\gpt-sovits\GPT-SoVITS'
$python = Join-Path $PWD 'artifacts\gpt-sovits\.venv\Scripts\python.exe'
.\scripts\gpt-sovits\Test-Runtime.ps1 -RuntimeRoot $runtime -PythonPath $python
.\scripts\gpt-sovits\Start-GptSoVits.ps1 -RuntimeRoot $runtime -PythonPath $python -Device cpu
```

安装全部写入 `artifacts/gpt-sovits`，包括独立 Python、虚拟环境、包缓存、源码、模型和文本资源，不改变系统 PATH，也不修改其他 Python。可用 `-InstallDirectory` 指定其他目录。只有接入脚本和说明随应用发布，GPT-SoVITS 源码、模型和 Python 单独安装，不打包进 exe。发布后的程序仍需指向这个安装目录。

官方 API 会固定使用根目录下的 `runtime/nltk_data` 和 `runtime/ffmpeg.exe`，安装脚本会准备这些路径；不能只把 NLTK 数据装进系统 Python。中文 G2PW 放在 `GPT_SoVITS/text/G2PWModel`，语言识别模型放在 `GPT_SoVITS/pretrained_models/fast_langdetect`。

v2 的 GPT/SoVITS/BERT/HuBERT 模型约 1.11 GiB，中文 G2PW 压缩包另约 562 MiB，还需 PyTorch、语言资源、解压和缓存空间。建议准备至少 10 GB 可用空间，训练检查点需要额外空间。下载从官方源码仓库、PyTorch/PyPI、上游指定的 Hugging Face 与 ModelScope 获取；源码和主要模型使用固定版本及 SHA256 校验。网络中断时保留已校验文件，修复网络后重新执行。

安装脚本默认部署 CPU 版 PyTorch。CPU 可以训练和合成，但耗时需在实际电脑上测试。已有支持 CUDA 的官方整合包也可接入；选择其 Python 和根目录，并在软件中选择 `auto` 或 `cuda`。单纯把设备改成 CUDA 不会安装显卡驱动或 CUDA 版 PyTorch。为避免 CPU 工作时占用过多线程，桥接使用 2 个计算线程。

Windows 上 `jieba_fast` 缺少现成 wheel，安装脚本使用同 API 的纯 Python `jieba` 适配；日文前处理使用提供 Windows wheel 的 `pyopenjtalk-plus`。这避免安装系统 C++ 编译工具，不改变 GPT/SoVITS 模型结构。`requirements-local.txt` 说明直接依赖，安装使用 `requirements-windows-cpu.lock.txt` 固定本次验证过的全部包版本；实际安装版本另保存到 `installed-packages.txt`。

在软件设置中填入：

| 项目 | 值 |
|---|---|
| GPT-SoVITS 根目录 | `…\artifacts\gpt-sovits\GPT-SoVITS` |
| Python | `…\artifacts\gpt-sovits\.venv\Scripts\python.exe` |
| API | `http://127.0.0.1:9880/` |
| 设备 | `cpu`，已有 CUDA 环境可选 `auto` |

API 只监听 `127.0.0.1`。不要把无认证的官方 API 暴露到公网。启动日志出现服务器已监听后再测试；设置已保存或依赖检查通过不等于模型成功载入。

## 本人录音、训练和保存

两种使用方式需要区分：

1. **参考录音复刻**：使用清晰本人短录音及逐字文本，配合基座模型生成新句子。它没有微调生成新的模型。
2. **训练个人模型**：准备同一位说话人的多段录音和校对后的转写，调用官方数据预处理及 GPT、SoVITS 两阶段微调。完成后得到实际 `.ckpt` 和 `.pth` 权重，导入「我的声音」并保存到本地档案。

训练输入是 UTF-8 的官方 `.list` 标注文件，每行：

```text
C:\我的录音\clip001.wav|本人|ZH|这是第一段录音里实际说的话。
C:\我的录音\clip002.wav|本人|ZH|这是另一段清楚完整的录音。
```

路径也可相对于 `.list` 所在目录。语言使用 `ZH`、`EN`、`JA`、`KO` 或 `YUE`。不同录音必须使用不同文件名；每个片段与其转写严格对应，去除背景音乐和其他人的声音。文本不能包含 `|` 或制表符。每段必须大于 0.6 秒且小于 54 秒，建议按完整短句切片、校对；不能把整小时录音作为一个训练样本。数据质量比单纯增加时长重要。上游训练还会依据语素数量与语音长度过滤异常样本，需检查日志并试听成果。

本桥接不自动猜测训练转写，也不在训练时调用 Azure 识别。可以在官方工具中切片和校对后导入 `.list`。一次训练只接受一位说话人。推荐先用少量干净数据验证整个流程，再扩大样本；增加 epoch 不保证更接近本人声音。

命令行合同供应用调用：

```powershell
& $python -u .\scripts\gpt-sovits\train_voice.py --manifest 'C:\任务目录\manifest.json'
```

清单示例：

```json
{
  "schema_version": 1,
  "runtime_root": "C:\\GPT-SoVITS",
  "dataset_list": "C:\\我的录音\\voice.list",
  "output_directory": "C:\\我的音色\\训练任务001",
  "gpt_epochs": 5,
  "sovits_epochs": 8,
  "batch_size": 2,
  "device": "cpu"
}
```

每次使用新任务目录。程序依次执行官方 `1-get-text.py`、`2-get-hubert-wav32k.py`、`3-get-semantic.py`、`s2_train.py`、`s1_train.py`。预处理会逐片核对输出，子进程失败或缺少任一权重就不会登记训练成功。正常结束时写 `result.json`，包含 `status=completed`、`gpt_weights_path`、`sovits_weights_path`，且两文件都位于本次任务目录中。错误写入 `error.json`。`LFX_PROGRESS` 后是 JSON，进度范围 0～100；训练阶段进度按阶段显示，实际轮次见日志。

应用取消时应终止训练进程树。未完成目录只保留诊断和中间数据，不自动覆盖已保存音色。原始录音、转写、模型和任务数据均留在本机；首次安装会下载公共基座及语言资源。请备份整个音色档案目录，只有模型文件而没有参考音频/文本不足以恢复相同的合成配置。

## API 对接与验证

官方 `api_v2.py` 合同：

- `GET /set_gpt_weights?weights_path=本地.ckpt`
- `GET /set_sovits_weights?weights_path=本地.pth`
- `POST /tts`，JSON 含 `text`、`text_lang`、`ref_audio_path`、`prompt_text`、`prompt_lang`、`media_type="wav"`、`streaming_mode=false`。

权重切换是整个服务的全局状态，所以调用方必须把两次切换和合成作为一个串行操作，并避免与训练争用设备。参考音频路径由本机 API 读取；不能直接拿本地路径调用远端服务。

```powershell
& $python -m unittest discover -s scripts/gpt-sovits -p 'test_*.py' -v
```

这些测试检查标注校验、中文路径、失败处理和进程参数，不代表实际训练或音色质量已经验证。运行环境检查也不会上传录音或生成个人模型；真实音色训练需要提供本人录音和标注。

官方参考：[GPT-SoVITS 中文说明](https://github.com/RVC-Boss/GPT-SoVITS/blob/main/docs/cn/README.md)、[API 源码](https://github.com/RVC-Boss/GPT-SoVITS/blob/main/api_v2.py)、[训练调用源码](https://github.com/RVC-Boss/GPT-SoVITS/blob/main/webui.py)、[官方引用的模型仓库](https://huggingface.co/lj1995/GPT-SoVITS)。
