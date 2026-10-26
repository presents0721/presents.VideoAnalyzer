using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.Auditory;
using Alife.Function.Vision;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace presents.Video;

[Module("视频理解",
    "流式抽帧 + 本地视觉模型逐帧理解 + 本机语音识别转写，让 AI 真正看懂长视频。",
    defaultCategory: "presents",
    EditorUI = typeof(VideoServiceUI))]
public class VideoService(
    XmlFunctionCaller functionService,
    ILogger<VideoService> logger,
    Interactor<VideoService> interactor,
    // 单个视觉模型：这是"下拉选择"功能之前一直在用的取模型方式（视觉感知 VisionService 同样如此），
    // 框架能稳定解析。集合形式 IEnumerable<IVisionModel> 在本框架里解析不到，故保留此项作为主路径。
    IVisionModel? visionModel = null,
    IAudioRecognizerProvider? audioRecognizerProvider = null,
    IEnumerable<IVisionModel>? allVisionModels = null,
    IServiceProvider? serviceProvider = null) :
    ChatBehaviour,
    IConfigurable<VideoServiceConfig>
{
    public VideoServiceConfig Configuration { get; set; } = null!;

    /// <summary>按每秒帧数采样时，总帧数的安全上限（防止长视频换算出海量的抽帧/调用量）</summary>
    const int MaxFrameCount = 3000;

    /// <summary>是否正在分析（供 UI 显示）</summary>
    public bool IsAnalysing => isAnalysing != 0;

    /// <summary>当前进度文本（供 UI 显示）</summary>
    public string ProgressText => progressText;

    /// <summary>最近一次完整报告的路径</summary>
    public string LastReportPath => lastReportPath;

    /// <summary>
    /// 每次使用时从容器重新解析视觉模型。
    /// 构造时注入的 IEnumerable 只是构造瞬间的快照：本地视觉模型要加载数百个权重张量，
    /// 注册完成往往晚于本模块构造，导致快照为空且永不更新（下拉永远为空）。
    /// 这里改为实时解析，保证后注册的视觉模型也能被识别。
    /// </summary>
    IEnumerable<IVisionModel> VisionModels()
    {
        // 1) 集合形式（同时启用多个视觉模型时可全部列出）
        if (serviceProvider != null)
        {
            try
            {
                var many = serviceProvider.GetService(typeof(IEnumerable<IVisionModel>))
                    as IEnumerable<IVisionModel>;
                if (many != null && many.Any())
                    return many;
            }
            catch
            {
                // 解析失败则继续走下面的兜底
            }
        }

        // 2) 构造时注入的集合快照
        if (allVisionModels != null && allVisionModels.Any())
            return allVisionModels;

        // 3) 兜底：构造时注入的单个视觉模型。
        //    这是"下拉选择"功能之前一直在用的方式，也是视觉感知 VisionService 取模型的方式，
        //    框架能稳定解析。集合形式解析不到时，靠它保证视觉模型不至于完全识别不到。
        if (visionModel != null)
            return [visionModel];

        return [];
    }

    /// <summary>根据配置挑出要用的视觉模型；未指定时退回第一个已注册模型</summary>
    IVisionModel? ResolveVisionModel()
    {
        var models = VisionModels();
        if (string.IsNullOrWhiteSpace(Configuration.SelectedVisionModel) == false)
        {
            IVisionModel? match = models.FirstOrDefault(m =>
                m.GetType().GetCustomAttribute<Alife.Framework.ModuleAttribute>() is { } attr && attr.Name == Configuration.SelectedVisionModel);
            if (match != null)
                return match;
        }
        return models.FirstOrDefault();
    }

    /// <summary>当前已启用、可供下拉选择的视觉模型名称</summary>
    public List<string> AvailableVisionModels => VisionModels()
        .Select(m => m.GetType().GetCustomAttribute<Alife.Framework.ModuleAttribute>()?.Name)
        .Where(n => string.IsNullOrWhiteSpace(n) == false)
        .Distinct()
        .ToList()!;

    [XmlFunction(FunctionMode.OneShot)]
    [Description("分析视频：先把整片压成一张网格图做全片概览，再沿时间轴分段连续理解（每帧带前情与同刻语音），同时做声学侦察与流式转写，最后汇总成带时间轴的连续叙事。耗时较长，建议配合 #parallel='true' 使用。")]
    public async Task AnalyseVideo(
        [Description("视频文件路径或网络链接")] string path,
        [Description("你想从视频中了解什么（可选，会附加到每一帧的提问中）")] string question = "",
        [Description("抽帧数量，0 表示使用配置默认值")] int frameCount = 0,
        [Description("本次临时使用的画质档位：省流 / 轻量 / 均衡 / 高画质 / 极致（也认 Economy/Light/Balanced/High/Extreme）。长视频想省 token 就填“省流”；留空表示使用插件配置里的值")]
        string preset = "",
        [Description("是否同时转写音轨")] bool withAudio = true,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref isAnalysing, 1, 0) != 0)
        {
            interactor.Poke("已经有一个视频正在分析了，请等它跑完再来。");
            return;
        }

        DateTime startedAt = DateTime.Now;
        progressText = "准备中…";
        string? downloaded = null;

        try
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new Exception("未提供视频路径");

            IVisionModel? vision = ResolveVisionModel();
            if (vision == null)
                throw new Exception("未检测到视觉模型，请在角色中启用视觉模型模块（如 Qwen3-VL、MiniCPM-V、OpenAI 视觉）。");

            string ffmpeg = FfmpegTool.Find(OrNull(Configuration.FfmpegPath));
            string videoPath = await EnsureLocalFileAsync(path, cancellationToken);
            if (videoPath != path)
                downloaded = videoPath;

            VideoInfo info = await FfmpegTool.ProbeAsync(ffmpeg, videoPath, cancellationToken);
            logger.LogInformation("[视频理解] 开始分析：{File}（{Duration}，{Width}x{Height}）",
                info.FilePath, VideoTime.Format(info.DurationSeconds), info.Width, info.Height);

            //本次调用可用档位临时覆盖配置（不写回配置，只影响这一次）
            VideoServiceConfig effectiveConfig = Configuration;
            VideoQualityPreset? parsedPreset = ParsePreset(preset);
            if (parsedPreset != null)
            {
                effectiveConfig = new VideoServiceConfig {
                    FfmpegPath = Configuration.FfmpegPath,
                    SampleFps = Configuration.SampleFps,
                    OnlineTranscribeEndpoint = Configuration.OnlineTranscribeEndpoint,
                    OnlineTranscribeModel = Configuration.OnlineTranscribeModel,
                    OnlineTranscribeApiKey = Configuration.OnlineTranscribeApiKey,
                    AudioChunkSamples = Configuration.AudioChunkSamples,
                    FramePrompt = Configuration.FramePrompt,
                    //上下文相关的开关属于「怎么理解」，不该被一次性的画质档位冲掉
                    ContextEnabled = Configuration.ContextEnabled,
                    SegmentSize = Configuration.SegmentSize,
                    ContextLookback = Configuration.ContextLookback,
                    AudioContextSeconds = Configuration.AudioContextSeconds,
                    OverviewEnabled = Configuration.OverviewEnabled,
                    OverviewCells = Configuration.OverviewCells,
                    OverviewCellWidth = Configuration.OverviewCellWidth,
                    OverviewMaxSheets = Configuration.OverviewMaxSheets,
                    OverviewMaxKeyframes = Configuration.OverviewMaxKeyframes,
                    SceneThreshold = Configuration.SceneThreshold,
                    AcousticCheck = Configuration.AcousticCheck,
                    ContentType = Configuration.ContentType
                };
                VideoQualityPresets.Apply(parsedPreset.Value, effectiveConfig);
                logger.LogInformation("[视频理解] 本次使用画质档位：{Preset}", VideoQualityPresets.GetName(parsedPreset.Value));
            }

            //采样密度：按每秒帧数时，用视频时长换算总帧数，使密度恒定、不随片长漂移
            int resolvedFrameCount = frameCount;
            if (effectiveConfig.SampleFps > 0 && info.DurationSeconds > 0)
            {
                double fps = effectiveConfig.SampleFps > 0 ? effectiveConfig.SampleFps : 1.0;
                resolvedFrameCount = (int)Math.Round(info.DurationSeconds * fps);
                resolvedFrameCount = Math.Clamp(resolvedFrameCount, 1, MaxFrameCount);
                logger.LogInformation("[视频理解] 采样模式=每秒 {Fps} 帧：时长 {Duration} → 共 {Count} 帧",
                    fps, VideoTime.Format(info.DurationSeconds), resolvedFrameCount);
                if (resolvedFrameCount >= MaxFrameCount)
                    logger.LogWarning("[视频理解] 帧数已达上限 {Max} 并被截断（每秒 {Fps} 帧 × 时长 {Duration}），长片建议调低每秒帧数",
                        MaxFrameCount, fps, VideoTime.Format(info.DurationSeconds));
            }

            VideoAnalysisOptions options = effectiveConfig.ToOptions(resolvedFrameCount, withAudio && info.HasAudio);
            logger.LogInformation("[视频理解] 上下文连贯={Enabled}，每段 {Segment} 帧，前情 {Lookback} 帧，同刻语音窗口 ±{Window}s",
                options.ContextEnabled, options.SegmentSize, options.ContextLookback, options.AudioContextSeconds);

            //声学侦察：先量音量，静音/纯底噪音轨直接不转写，避免 ASR 编出整段假台词
            AudioProbeResult? audioProbe = null;
            if (options.WithAudio && effectiveConfig.AcousticCheck)
            {
                progressText = "声学侦察";
                audioProbe = await AudioProbe.ProbeAsync(ffmpeg, videoPath, info.HasAudio, logger, cancellationToken);
            }

            //全片概览：先按画面差异挑关键帧（静止的 PPT 页不重复占位），再拼成网格图一次识图拿骨架；
            //关键帧探测失败时 CaptureAsync 会自动退回均匀采样，不阻断主流程
            bool infoDense = effectiveConfig.ContentType == VideoContentType.InfoDense;
            int overviewCells = infoDense ? Math.Max(effectiveConfig.OverviewCells, 12) : effectiveConfig.OverviewCells;
            int overviewSheets = infoDense ? Math.Max(effectiveConfig.OverviewMaxSheets, 3) : effectiveConfig.OverviewMaxSheets;
            int overviewKeyframes = infoDense ? Math.Max(effectiveConfig.OverviewMaxKeyframes, 36) : effectiveConfig.OverviewMaxKeyframes;

            OverviewResult? overview = null;
            IReadOnlyList<double> keyframes = Array.Empty<double>();
            if (effectiveConfig.OverviewEnabled)
            {
                progressText = "识别画面变化";
                keyframes = await SceneKeyframes.DetectAsync(
                    ffmpeg, info,
                    effectiveConfig.SceneThreshold, overviewKeyframes,
                    logger, cancellationToken);

                progressText = "生成全片概览";
                overview = await VideoOverview.CaptureAsync(ffmpeg, videoPath, info.DurationSeconds, options,
                    keyframes, overviewCells, effectiveConfig.OverviewCellWidth,
                    overviewSheets,
                    BuildOverviewPromptFactory(info, question),
                    vision!, logger, cancellationToken);
            }

            //语音起始点只在确认有人声时使用：把抽帧对齐到「讲师开口」的时刻，不额外增加帧数
            IReadOnlyList<double> speechOnsets = audioProbe?.CanHaveSpeech == true
                ? audioProbe.SpeechOnsets ?? Array.Empty<double>()
                : Array.Empty<double>();
            if (speechOnsets.Count > 0)
                logger.LogInformation("[视频理解] 取到 {Count} 个语音起始点，用于对齐抽帧时刻", speechOnsets.Count);

            Func<FrameContextRequest, string> promptFactory = BuildPromptFactory(effectiveConfig, options, info, question, overview?.Text);

            //音轨转写边跑边把结果写进实时缓冲：视觉分析不必等整条音轨转完，
            //每分析一帧就能取到「这一时刻前后几秒说了什么」，把台词和画面对上。
            TranscriptBuffer transcript = new();

            Progress<FrameProgress> frameProgress = new();
            frameProgress.ProgressChanged += (_, e) => {
                progressText = $"画面 {e.Completed}/{e.Total}";
                if (e.Total > 0 && e.Completed % Math.Max(1, e.Total / 10) == 0)
                    logger.LogInformation("[视频理解] 画面进度 {Completed}/{Total}", e.Completed, e.Total);
            };
            Progress<double> audioProgress = new();
            audioProgress.ProgressChanged += (_, seconds) => progressText = $"画面进度中 · 语音 {VideoTime.Format(seconds)}";

            //抽帧分析与音轨转写并行推进，两条流水线互不阻塞
            Task<FrameAnalysis[]> framesTask = FramePipeline.RunAsync(
                ffmpeg, videoPath, info, options, promptFactory,
                timestamp => transcript.Window(timestamp, options.AudioContextSeconds),
                keyframes,
                speechOnsets,
                vision!, frameProgress, logger, cancellationToken);
            Task<TranscriptResult> audioTask = options.WithAudio && (audioProbe?.CanHaveSpeech ?? true)
                ? AudioTranscriber.TranscribeAsync(ffmpeg, videoPath, options, audioRecognizerProvider,
                    audioProgress, logger, transcript, cancellationToken)
                : Task.FromResult(new TranscriptResult(
                    audioProbe?.CanHaveSpeech == false ? "已跳过" : info.HasAudio ? "未启用" : "无音轨",
                    [], audioProbe?.Summary));

            await Task.WhenAll(framesTask, audioTask);

            VideoAnalysisReport report = new(info, await framesTask, await audioTask, DateTime.Now - startedAt,
                options.SegmentSize, overview, audioProbe);
            string markdown = report.ToMarkdown();

            string? savedPath = null;
            if (Configuration.SaveReport)
            {
                try
                {
                    savedPath = await SaveReportAsync(markdown, info, cancellationToken);
                    lastReportPath = savedPath;
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "[视频理解] 报告保存失败");
                }
            }

            progressText = $"完成（{report.SuccessFrameCount}/{report.Frames.Length} 帧）";
            logger.LogInformation("[视频理解] 分析完成：{Success}/{Total} 帧，语音 {Lines} 段，耗时 {Elapsed}",
                report.SuccessFrameCount, report.Frames.Length, report.Transcript.Lines.Count, report.Elapsed);

            interactor.Poke(BuildPokeText(report, markdown, savedPath));
        }
        catch (OperationCanceledException)
        {
            progressText = "已取消";
            interactor.Poke("视频分析被中断了。");
        }
        catch (Exception e)
        {
            progressText = "失败";
            logger.LogError(e, "[视频理解] 视频分析失败");
            interactor.Poke($"视频分析失败：{e.Message}");
        }
        finally
        {
            if (downloaded != null)
            {
                try
                {
                    File.Delete(downloaded);
                }
                catch
                {
                    // 忽略临时文件清理失败
                }
            }

            Interlocked.Exchange(ref isAnalysing, 0);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查询视频基本信息：时长、分辨率、编码格式")]
    public async Task QueryVideoInfo(
        [Description("视频文件路径或网络链接")] string path,
        CancellationToken cancellationToken = default)
    {
        string? downloaded = null;
        try
        {
            string ffmpeg = FfmpegTool.Find(OrNull(Configuration.FfmpegPath));
            string videoPath = await EnsureLocalFileAsync(path, cancellationToken);
            if (videoPath != path)
                downloaded = videoPath;

            VideoInfo info = await FfmpegTool.ProbeAsync(ffmpeg, videoPath, cancellationToken);
            interactor.Poke($"""
                            视频信息
                            时长：{VideoTime.Format(info.DurationSeconds)}（{info.DurationSeconds:0.###} 秒）
                            分辨率：{(info.Width > 0 ? $"{info.Width}x{info.Height}" : "未知")}
                            视频编码：{(string.IsNullOrWhiteSpace(info.VideoCodec) ? "未知" : info.VideoCodec)}
                            音频编码：{(info.HasAudio ? info.AudioCodec : "无音轨")}
                            """);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "[视频理解] 视频信息查询失败");
            interactor.Poke($"视频信息查询失败：{e.Message}");
        }
        finally
        {
            if (downloaded != null)
            {
                try
                {
                    File.Delete(downloaded);
                }
                catch
                {
                    // 忽略
                }
            }
        }
    }

    Func<FrameContextRequest, string> BuildPromptFactory(VideoServiceConfig config, VideoAnalysisOptions options, VideoInfo info, string question, string? overviewText)
    {
        string depthText = config.AnalysisDepth switch {
            VideoAnalysisDepth.Brief => "请用一句话概括这一帧，只保留最关键的信息。",
            VideoAnalysisDepth.Deep =>
                "请尽可能详尽地描述这一帧：主体、人物、表情、动作、物体、场景、构图、光线、色彩、可见的文字（原文抄录），以及任何值得注意的细节。",
            _ => string.Empty
        };

        return request => {
            StringBuilder builder = new();
            builder.Append(config.FramePrompt);
            if (string.IsNullOrEmpty(depthText) == false)
                builder.Append(depthText);
            builder.Append(
                $"这是视频第 {request.Index + 1} 帧，位于 {VideoTime.Format(request.Timestamp)}（全片 {VideoTime.Format(info.DurationSeconds)}）。");

            //画面文字读不清时模型会照着「原文抄录」编造；明确允许它说看不清，比事后核对省事
            builder.Append("看不清的文字、数字、公式不要猜，直接写「无法辨认」；只描述确实看见的内容。");

            //全片概览当骨架：每帧都知道自己在整段视频的哪个位置，不会把局部当全部
            if (string.IsNullOrWhiteSpace(overviewText) == false)
                builder.Append($"【全片概览】{Clip(overviewText, 240)}");

            if (request.PreviousDescriptions.Count > 0)
            {
                //前情由近到远给出，让模型能判断「变化」而不是把本帧当成全新画面
                builder.Append("【前情】前面几帧看到的内容（由近到远）：");
                for (int i = request.PreviousDescriptions.Count - 1; i >= 0; i--)
                {
                    int distance = i - request.PreviousDescriptions.Count + 1; //-1 表示上一帧，-2 表示上上帧
                    builder.Append($"（{-distance}）{Clip(request.PreviousDescriptions[i], 400)}");
                }
                builder.Append(
                    "【要求】先判断本帧相对前情有没有明显变化：出现新的人/物/动作、场景切换、镜头切换都算变化。" +
                    "有变化就用「变化：」开头写新出现的东西；没有明显变化就用「延续：」开头，只补充前情没提到的新信息，不要复述前情。" +
                    "然后再写这一帧具体可见的内容。");
            }
            else
            {
                builder.Append(
                    "【要求】这是本段的起始帧，请先把基础画面立起来：场景、主体是谁/是什么、正在做什么、画面文字（原文抄录）。");
            }

            if (string.IsNullOrWhiteSpace(request.AudioText) == false)
                builder.Append(
                    $"【同刻语音】这一时刻前后几秒的台词/字幕（本机语音识别转写，可能有错字）：「{request.AudioText}」。" +
                    "语音可以用来帮助理解画面（谁在说话、说到哪一步），但不要臆造画面上没有的东西。");

            builder.Append(
                "【核对】台词与画面冲突时以画面为准（语音识别有同音错字）；画面看不清、信息不足时就直说没看到，不要推测填满。");
            builder.Append($"回答请控制在 {options.FrameMaxChars} 个字以内，只输出对画面的描述，不要复述这些要求。");
            if (string.IsNullOrWhiteSpace(question) == false)
                builder.Append($"另外需要特别关注：{question}");
            return builder.ToString();
        };
    }

    /// <summary>全片概览的提示词：先讲清主题与 3~6 个段落和切换点，别逐格报流水账</summary>
    Func<IReadOnlyList<double>, int, int, int, int, string> BuildOverviewPromptFactory(VideoInfo info, string question)
    {
        return (timestamps, columns, rows, sheetIndex, sheetCount) => {
            StringBuilder builder = new();
            builder.Append(
                $"这是一段视频按画面变化挑出的 {timestamps.Count} 个关键帧（不是等间隔抽帧），拼成 {columns} 列 × {rows} 行的网格" +
                (sheetCount > 1 ? $"；这是第 {sheetIndex}/{sheetCount} 张，多张按时间顺序排列" : "") +
                $"（全片 {VideoTime.Format(info.DurationSeconds)}）。读取顺序从左到右、从上到下。格子与时间对照：");
            for (int i = 0; i < timestamps.Count; i++)
                builder.Append($"第{i + 1}格={VideoTime.Format(timestamps[i])}");
            builder.Append(
                "。请先一句话概括全片主题；再按时间顺序列出 3~6 个段落，每段一句话说清这一段在讲什么、画面在发生什么；" +
                "最后点出发生明显场景或镜头切换的时间点。不要逐格报流水账，不要复述这些要求。");
            if (string.IsNullOrWhiteSpace(question) == false)
                builder.Append($"另外需要特别关注：{question}");
            return builder.ToString();
        };
    }

    static string Clip(string text, int max)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    string BuildPokeText(VideoAnalysisReport report, string markdown, string? savedPath)
    {
        StringBuilder builder = new();
        builder.AppendLine("【视频分析结果】");
        builder.AppendLine(markdown.Length <= Configuration.PokeCharLimit
            ? markdown
            : report.ToCondensedText());

        if (savedPath != null)
            builder.AppendLine().AppendLine($"完整报告已保存到：{savedPath}");

        builder.AppendLine();
        builder.AppendLine("（以上按段落给出：同一段内的帧是按时间顺序连续分析的，已带上前几帧的内容和同刻台词，不是一堆互不相干的截图。请用你自己的话，按因果和先后把这段视频讲给用户听。）");
        return builder.ToString();
    }

    static async Task<string> SaveReportAsync(string markdown, VideoInfo info, CancellationToken ct)
    {
        string directory = Path.Combine(AlifePath.StorageFolderPath, "VideoAnalysis");
        Directory.CreateDirectory(directory);

        string name = Path.GetFileNameWithoutExtension(info.FilePath);
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        if (name.Length > 40)
            name = name[..40];

        string path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd_HHmmss}_{name}.md");
        await File.WriteAllTextAsync(path, markdown, ct);
        return path;
    }

    static async Task<string> EnsureLocalFileAsync(string pathOrUrl, CancellationToken ct)
    {
        if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            string extension = Path.GetExtension(new Uri(pathOrUrl).AbsolutePath);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".mp4";

            string temp = Path.Combine(AlifePath.TempFolderPath, $"video_{Guid.NewGuid():N}{extension}");
            await AlifeUtility.DownloadFileAsync(pathOrUrl, temp, null, TimeSpan.FromHours(2));
            return temp;
        }

        // 去掉首尾空白与包裹的引号，避免 AI 传入 "C:\...\ .mp4" 这类写法
        string local = pathOrUrl.Trim();
        if (local.Length >= 2 && ((local[0] == '"' && local[^1] == '"') ||
            (local[0] == '\'' && local[^1] == '\'')))
            local = local[1..^1].Trim();

        if (File.Exists(local))
            return local;

        // 容错：文件名里可能混用非断行空格(U+00A0)等不可见空格，做一次大小写/空格不敏感的兜底匹配
        string? resolved = TryResolveLocalFile(local);
        if (resolved != null)
            return resolved;

        throw new FileNotFoundException($"视频文件不存在：{pathOrUrl}");
    }

    /// <summary>
    /// 精确匹配失败时，按目录枚举做容错匹配：
    /// 1) 大小写 + 空格（含 U+00A0 等非标准空格）不敏感精确匹配；
    /// 2) 时间戳容错：去掉文件名内嵌的“YYYY-MM-DD HH-MM-SS”后再比基础名，
    ///    解决 AI/用户偶尔把录制时间写错导致找不到文件的问题。
    /// </summary>
    static string? TryResolveLocalFile(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            string file = Path.GetFileName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return null;

            string targetExact = NormalizeWhitespace(file);
            string? targetBase = StripTimestamp(targetExact);

            string? fallback = null;
            foreach (string candidate in Directory.EnumerateFiles(dir))
            {
                string candNorm = NormalizeWhitespace(Path.GetFileName(candidate));
                if (string.Equals(candNorm, targetExact, StringComparison.OrdinalIgnoreCase))
                    return candidate; // 含时间戳的精确匹配优先

                if (targetBase != null)
                {
                    string candBase = StripTimestamp(candNorm);
                    if (string.Equals(candBase, targetBase, StringComparison.OrdinalIgnoreCase))
                    {
                        // 多个候选时取文件名（含时间戳）字典序最大者，即最新录制
                        if (fallback == null ||
                            string.CompareOrdinal(Path.GetFileName(candidate), Path.GetFileName(fallback)) > 0)
                            fallback = candidate;
                    }
                }
            }
            return fallback;
        }
        catch
        {
            // 忽略枚举异常，交回上层抛出原始“文件不存在”
        }
        return null;
    }

    static readonly Regex TimestampPattern = new(
        @"\s*\d{4}-\d{2}-\d{2}[- ]\d{2}-\d{2}-\d{2}",
        RegexOptions.Compiled);

    /// <summary>去掉文件名中内嵌的录制时间戳，保留基础名（含扩展名）。</summary>
    static string? StripTimestamp(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        string stripped = TimestampPattern.Replace(name, "");
        return NormalizeWhitespace(stripped).Trim();
    }

    static string NormalizeWhitespace(string value)
    {
        System.Text.StringBuilder builder = new(value.Length);
        foreach (char ch in value)
            builder.Append(char.IsWhiteSpace(ch) ? ' ' : ch);
        return builder.ToString();
    }

    static string? OrNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>解析 AI 传入的档位名，支持中文名与英文名，无法识别时返回 null</summary>
    static VideoQualityPreset? ParsePreset(string preset)
    {
        if (string.IsNullOrWhiteSpace(preset))
            return null;

        foreach (VideoQualityPreset value in Enum.GetValues<VideoQualityPreset>())
        {
            if (string.Equals(value.ToString(), preset.Trim(), StringComparison.OrdinalIgnoreCase))
                return value;
            if (VideoQualityPresets.GetName(value).Contains(preset.Trim(), StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

    int isAnalysing;
    string progressText = "空闲";
    string lastReportPath = string.Empty;

    protected override Task OnAwake()
    {
        if (ResolveVisionModel() == null)
            logger.LogWarning("未检测到视觉模型，视频理解需要启用视觉模型模块（如 Qwen3-VL、MiniCPM-V、OpenAI 视觉）。");
        if (audioRecognizerProvider == null && Configuration.HasOnlineTranscribe == false)
            logger.LogInformation("未检测到本机语音识别模型，也未配置在线转写服务，视频音轨分析将自动跳过。");

        XmlHandler xmlHandler = new(this) {
            Description =
                $"此服务让你能够看懂视频。把视频路径或链接传入<{nameof(AnalyseVideo)}>，系统会先把整片压成一张按时间顺序排列的网格图做「全片概览」，再沿时间轴分段连续理解（每帧都带前几帧看到的内容和同一时刻的台词），同时做声学侦察与音轨转写（静音音轨会直接跳过，避免编造台词），最后汇总成一份带时间轴的连续叙事。" +
                "档位参数可临时指定画质开销：长视频或想省 token 用“省流”，要看清细节用“高画质”，留空则用插件配置里的默认值。长视频耗时较久，建议配合 #parallel='true' 使用。",
            Explanation =
                "抽帧由 ffmpeg 流式完成，音轨也逐块解码，内存占用与视频大小无关；单帧失败会自动重试且不会重复抽帧。" +
                "段内帧按时间顺序串行分析、段与段之间并行，所以既有前后文，又不会退化成逐帧串行。" +
                "全片概览让模型先有全局骨架，逐帧分析再补细节；声学侦察与幻觉过滤保证「听到的」不会是无中生有。"
        };
        functionService.RegisterHandler(xmlHandler, DocumentMode.Implicit, DestroyCancellationToken);
        return Task.CompletedTask;
    }
}
