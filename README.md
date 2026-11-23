# 视频理解（presents.Video）

让角色理解视频内容：听觉走 SenseVoice，视觉走已有的视觉模型 / 本机 OCR 兜底。

## 听觉：SenseVoice（本来就不用改）

代码里已经是 **`localProvider != null` 就优先 SenseVoice**，不需要为它改一行。
唯一的前提是 **SenseVoice 模块要装在当前角色下**——它没装的话框架提供不了 provider，
插件会继续走原来的路径。**装上就自动生效，没有任何开关要动。**

| 项 | 说明 |
|---|---|
| 优选 | SenseVoice（框架听觉），`localProvider != null` 时自动用 |
| 前提 | 角色必须装配 `Alife.Function.Auditory.SenseVoice` 模块 |
| 未装时 | 退回原路径；不报错、不哑掉 |

SenseVoice 那边的实现要点（独立 VAD + 100ms 分块 + 补起音丢字 + 8s 历史缓冲）见其自身 README，
本插件只负责"有 provider 就用"。

## 视觉与 OCR

| 谁 | 负责什么 |
|---|---|
| 视觉模型 | 画面版式、图形、场景理解（配了就走它） |
| presents.Shared.Media（OCR） | 画面文字原文；**Windows 自带引擎，零依赖、不出网**；无视觉模型时兜底 |

依赖：`presents.Shared.Media`（`MediaOcr.ReadAsync`）。

## 陪看那套 VAD / 幻觉过滤为什么用不上

`presents.WatchTogether`（陪看）和 `presents.BiliScroll`（刷B站）都做过一轮 whisper 修复：
加 `--vad -vm` 跳 BGM、逐行过滤 `(upbeat music)` / 字幕组署名 / 订阅套话 / 重复拟声。
那套是给**整片 whisper 转写**打的补丁——本插件没有那条路径：
听觉侧本来就走 `localProvider != null` 优先 SenseVoice（VAD 在框架识别器内部），
所以不需要再给 whisper 单独加 VAD 和过滤。**不是漏了，是用不上。**

## 关于音乐 / 唱歌

 SenseVoice / whisper 对唱歌的转写准确率都低于说话（中文歌尤甚，会有错字）。
如果视频是 MV 或歌曲，转出来的歌词**本身就是内容**；普通视频里 OP/ED 歌词可能混进转写，
模型通常会自己分辨。这条不是本插件的 bug，是两个转写引擎的固有边界。

## 版本

本插件本次**没有版本变更**——听觉侧无需改动，装上 SenseVoice 即自动生效。
