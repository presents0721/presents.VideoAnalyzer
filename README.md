# presents.Video · 视频理解

流式抽帧 + 本地视觉模型逐帧理解 + 本机语音识别转写，让 AI 真正看懂长视频。

- **流式抽帧**：边解码边把关键帧交给视觉模型，不必先把整段视频读进内存。
- **逐帧理解**：每帧带上下文（「变化 / 延续」）分析，结合字幕互校。
- **本地转写**：没有字幕时可用本机 ffmpeg + whisper 转写（可选、可降级）。

依赖 `Alife.Function.FunctionCaller`、`Alife.Function.AIModelUtility`、`Alife.Function.Vision`、`Alife.Function.Auditory`。

## 安装

解压后目录名必须与插件 id 一致（`presents.Video`），放到 Alife 的 `Storage/Plugins/` 下，重启后由客户端自行编译加载。本地转写需要自备 ffmpeg 与 whisper。

## 版本

当前版本 **5.5.0**，发布包见 [Releases](https://github.com/presents0721/presents.VideoAnalyzer/releases)。

插件大版本与 Alife 大版本对应：5.x 只给 Alife 5.0.0+ 用，4.x 请勿安装或更新到 5.x（两条线功能一致，底层接口按版本适配）。

## 源码

仓库结构即插件目录结构：`manifest.json` + `*.cs` + `Resources/`。运行时产生的 `out/`、`state.json` 等不进仓库（见 `.gitignore`）。
