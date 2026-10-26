using System;
using System.ComponentModel;

namespace presents.Video;

/// <summary>单帧分析的思考强度（越深越详细，也越慢越费 token）</summary>
public enum VideoAnalysisDepth
{
    /// <summary>简洁：一句话概括，速度最快</summary>
    Brief,
    /// <summary>标准：场景 + 主体 + 动作 + 文字</summary>
    Standard,
    /// <summary>深度：尽可能详尽地描述一切可辨识细节</summary>
    Deep
}

/// <summary>内容场景预设：按片子类型一键设定采样密度、清晰度与理解方式</summary>
public enum VideoContentPreset
{
    /// <summary>日常轻松：刷 vlog、剧情、日常记录，开销最低</summary>
    Daily,
    /// <summary>通用均衡：纪录片、访谈、教程</summary>
    General,
    /// <summary>技术分享：幻灯片与示意图为主，靠讲解带信息</summary>
    TechShare,
    /// <summary>论文讲解：演示图密集、切换频繁，需要高分辨率</summary>
    Paper,
    /// <summary>AI 日报：快切、大字号标题、数字结论密集</summary>
    AiNews
}

/// <summary>内容类型：信息密集的视频需要更细的覆盖与更长的文字描述</summary>
public enum VideoContentType
{
    /// <summary>通用：影视、vlog、剧情等常规内容</summary>
    General,
    /// <summary>信息密集：技术分享、论文讲解、AI 日报——画面文字多、切换快</summary>
    InfoDense
}

/// <summary>画质/开销档位预设，一键套用即可，套用后仍可单独微调每一项</summary>
public enum VideoQualityPreset
{
    /// <summary>极速省流：几乎不花 token，只求知道这片子大概讲了啥</summary>
    Economy,
    /// <summary>轻量速览：少量帧 + 短描述，长视频首选</summary>
    Light,
    /// <summary>均衡：清晰度与开销的平衡点，新手默认用这个</summary>
    Balanced,
    /// <summary>高画质：帧更密、描述更长，适合要看清细节的片子</summary>
    High,
    /// <summary>极致：4K 满帧 120 帧 + 深度描述，最慢最费，但最全</summary>
    Extreme
}

/// <summary>视频理解插件配置</summary>
public class VideoServiceConfig
{
    // ── 画质与覆盖 ──
    [DisplayName("抽帧数量")]
    [Description("作用：沿时间轴均匀取多少帧，决定覆盖密度，也是开销的第一大头（画面变化处还会额外并入关键帧，实际帧数会比这里多一些）。怎么选：短视频/只要大意 12~24 帧；普通片子 48~60 帧；要看清过程 90~120 帧。每多一帧 = 多一份图片 token + 多一次模型推理")]
    public int FrameCount { get; set; } = 48;

    [DisplayName("抽帧最大宽度")]
    [Description("作用：超过该宽度的帧按比例缩放，防止 4K 帧白送解码时间。怎么选：本地 Qwen3-VL/MiniCPM 后端下，宽度从 768 提到 3840，单帧视觉 token 只从约 105 涨到约 128（处理器会先把画面压到 40 万像素内），所以调宽主要是略微提升小字细节，token 几乎不变；在线视觉模型按图片尺寸计费时才需要往下调")]
    public int FrameMaxWidth { get; set; } = 1920;

    [DisplayName("单帧描述字符上限")]
    [Description("作用：单帧描述超过这个长度就截断，防止某帧啰嗦占满上下文。怎么选：省 token 256~512；常规 1024；需要细节 2048~4096")]
    public int FrameMaxChars { get; set; } = 1024;

    // ── 性能与并发 ──
    [DisplayName("视觉分析并发")]
    [Description("作用：同时排队的视觉分析任务数。怎么选：本地 Qwen/MiniCPM 是单进程串行，这里只是队列长度，8~16 足够；接在线视觉模型时可拉到 32~120 真正并行提速")]
    public int VisionConcurrency { get; set; } = 16;

    [DisplayName("抽帧并发")]
    [Description("作用：同时跑几个 ffmpeg 抽帧进程。怎么选：SSD 12~16；机械硬盘 4~8，太高反而互相抢磁头")]
    public int FrameExtractConcurrency { get; set; } = 12;

    [DisplayName("单帧分析超时（秒）")]
    [Description("作用：单帧最多等多久，超时算失败并进入重试。怎么选：本地小模型 120~200；在线模型或大帧 300")]
    public int FrameTimeoutSeconds { get; set; } = 200;

    // ── 容错 ──
    [DisplayName("失败重试次数")]
    [Description("作用：单帧失败后自动补跑几次（帧已抽好，重试不会重复抽帧）。怎么选：本地模型很稳，2~3 次即可；在线模型偶发限流，建议 5 次")]
    public int MaxRetryCount { get; set; } = 5;

    [DisplayName("重试等待（毫秒）")]
    [Description("作用：第 n 次重试等待 该值×n 毫秒，越等越久地退避。怎么选：本地 1000；在线 2000，给限流留出恢复时间")]
    public int RetryDelayMs { get; set; } = 2000;

    // ── 分析质量 ──
    [DisplayName("单帧最大输出 token")]
    [Description("作用：允许视觉模型为每帧最多写多少 token，是省 token 的第二大头。怎么选：省流 256~512；常规 1024；细节控 2048~4096；8192 只在极致档用")]
    public int MaxOutputTokens { get; set; } = 1024;

    // ── 采样密度 ──
    [DisplayName("内容类型")]
    [Description("作用：告诉插件这条片子是普通内容还是信息密集内容。怎么选：影视/vlog 用通用；技术分享、论文讲解、AI 日报用信息密集——关键帧最多保留 36 个、总览切 3 张、单帧描述上限提到 2048 字，并让抽帧尽量落在讲师开口的时刻。注意本地视觉模型会把画面压到约 818×460，信息密集模式也读不出代码级小字，只有在线高分辨率模型才吃得下")]
    public VideoContentType ContentType { get; set; } = VideoContentType.General;

    [DisplayName("每秒抽帧数（0=改用「抽帧数量」）")]
    [Description("大于 0 时按此密度采样：总帧数 = 视频秒数 × 该值，密度恒定、不随片长漂移（推荐）。怎么选：常规 1；动作密集（FPS 游戏剪辑）2；长片省流 0.5（每2秒）或 0.1（每10秒，2 小时电影约 720 帧）。填 0 则退回按「抽帧数量」的固定总帧数。总帧数上限 3000。")]
    public double SampleFps { get; set; } = 1.0;

    [DisplayName("思考强度")]
    [Description("作用：改写每帧提示词的要求强度。怎么选：简洁=一句话（最快最省）；标准=场景+主体+动作+文字（推荐）；深度=穷举细节（慢、费，但信息最全）")]
    public VideoAnalysisDepth AnalysisDepth { get; set; } = VideoAnalysisDepth.Standard;

    [DisplayName("单帧提示词")]
    [Description("作用：附加在每一帧上的固定提问，AI 调用时传入的问题会追加在其后。怎么选：一般不改；想让它专门盯字幕、人名、比分之类，可以在这里改写")]
    public string FramePrompt { get; set; } = DefaultFramePrompt;

    /// <summary>默认单帧提示词（内容场景预设也用这一份作为「通用」的基准）</summary>
    public const string DefaultFramePrompt =
        "请描述这一帧画面：发生在什么场景，画面主体是谁/是什么，正在发生什么动作，画面里出现了哪些文字（请原文抄录）。";

    // ── 上下文与连贯性 ──
    [DisplayName("上下文连贯")]
    [Description("作用：让每一帧都带上前面几帧看到的内容，并优先讲「相对前文发生了什么变化」，而不是把每帧当成互不相干的截图。怎么选：默认开；关掉会退回逐帧独立描述（略快，但结果是一堆碎片）")]
    public bool ContextEnabled { get; set; } = true;

    [DisplayName("每段帧数")]
    [Description("作用：把时间轴切成若干段，段内帧按时间顺序连续分析（段与段之间仍并行），段内才有真正的先后关系。怎么选：默认 6；想更连贯填 10~12（更慢）；想更快填 3~4")]
    public int SegmentSize { get; set; } = 6;

    [DisplayName("前情帧数")]
    [Description("作用：每帧带上前几帧的描述当上下文。怎么选：默认 2（足以看出动作的连续过程）；1=只看上一帧；0=不带前情")]
    public int ContextLookback { get; set; } = 2;

    [DisplayName("语音上下文窗口（秒）")]
    [Description("作用：每帧同时把这一时刻前后各若干秒的语音/字幕塞进提示词，让画面和台词对得上。怎么选：默认 10；访谈/解说想更准填 15~20；纯歌舞可填 5；0=不喂语音")]
    public int AudioContextSeconds { get; set; } = 10;

    // ── 全片概览与音轨可信度 ──
    [DisplayName("全片概览图")]
    [Description("作用：先把整片按画面变化挑出的关键帧拼成网格图，让模型一次看完整个视频的走向（主题、段落、切换点），再用它当逐帧分析的骨架。怎么选：默认开；关掉会省识图调用，但模型只看得到局部")]
    public bool OverviewEnabled { get; set; } = true;

    [DisplayName("概览每张几格")]
    [Description("作用：一张总览图放几格。格越少每格越大越清楚，格越多覆盖越全。怎么选：默认 8（自动排成 3×3 = 9 格）；想看得更细可到 12（4 列 × 3 行）；不建议超过 16，否则每格字会糊")]
    public int OverviewCells { get; set; } = 8;

    [DisplayName("概览每格宽度（像素）")]
    [Description("作用：每格抽帧的分辨率宽度，直接决定网格里能看清多少细节。怎么选：默认 640；视觉模型认图能力弱就调到 720~800；只想看大结构可降到 384。整张网格总宽会压在 2400px 以内，超过会被读图工具压缩")]
    public int OverviewCellWidth { get; set; } = VideoOverview.DefaultCellWidth;

    [DisplayName("概览最多几张图")]
    [Description("作用：关键帧太多时，总览图最多切成几张（每张都要一次识图）。怎么选：默认 2（最多约 18 格）；长片切换多可到 3~4，代价是多几次识图调用；1 = 只出一张")]
    public int OverviewMaxSheets { get; set; } = 2;

    [DisplayName("概览最多保留几个关键帧")]
    [Description("作用：按画面变化挑帧时最多留多少个。怎么选：默认 18（约等于 2 张 3×3）；想覆盖更全可到 36；太少会漏掉后半段的内容")]
    public int OverviewMaxKeyframes { get; set; } = 18;

    [DisplayName("画面变化阈值")]
    [Description("作用：整片逐帧比差异时，差异分数超过多少才算「画面真的变了」。默认 0 = 自动：取全片差异的中位数（约等于压缩噪点底噪）× 4，并夹在 0.008~0.08 之间。怎么选：一般不要手动调；确实想更敏感填 0.01~0.02，想更严格填 0.05~0.08（调太高会把淡入淡出误判成「变化已结束」，抽到元素还没出现的空白画面）")]
    public double SceneThreshold { get; set; } = SceneKeyframes.DefaultThreshold;

    [DisplayName("声学侦察")]
    [Description("作用：转写前先解码音轨做声学量测（谱平坦度、节拍自相关、逐秒响度波动、85% 谱滚降），判断这条轨到底是人声、音乐还是静音；静音/纯音/器乐为主直接跳过转写，并过滤「请不吝点赞订阅」这类 ASR 幻觉模板句。只算物理量、不调用模型。怎么选：默认开；关掉则无论音轨如何都会转写")]
    public bool AcousticCheck { get; set; } = true;

    // ── 音频转写 ──
    [DisplayName("音频分块采样数")]
    [Description("作用：每块音频多大喂给识别器一次，16000=1 秒。怎么选：本机 SenseVoice 时直接顶满 640000（40 秒/块），内存只多占 2.5MB，不影响显存；在线转写时调小可让请求更碎更快出结果，调大则请求更少更省")]
    public int AudioChunkSamples { get; set; } = 640000;

    [DisplayName("上传转码码率（kbps）")]
    [Description("作用：在线转写前把音频块压成 mp3 的码率，只在本机无模型、走在线直连时生效。怎么选：普通话清晰 64~96 足够；嘈杂/唱歌/多人 128~192；要最准 256（流量翻倍）")]
    public int UploadBitrateKbps { get; set; } = 128;

    [DisplayName("在线转写端点")]
    [Description("作用：本机没有语音识别模型时的兜底通道，OpenAI 兼容的 /audio/transcriptions 地址。怎么选：装了 SenseVoice 就不用填；只有在线转写时才填")]
    public string OnlineTranscribeEndpoint { get; set; } = string.Empty;

    [DisplayName("在线转写模型")]
    public string OnlineTranscribeModel { get; set; } = "whisper-1";

    [DisplayName("在线转写密钥")]
    public string OnlineTranscribeApiKey { get; set; } = string.Empty;

    // ── 其它 ──
    [DisplayName("ffmpeg 路径")]
    [Description("作用：指定 ffmpeg 可执行文件。怎么选：留空自动查找（系统 PATH / python imageio-ffmpeg / 常见安装目录），一般不用管")]
    public string FfmpegPath { get; set; } = string.Empty;

    [DisplayName("保存完整报告")]
    [Description("作用：把完整报告写入 Storage/VideoAnalysis 目录。怎么选：长视频一定要开，AI 只看精简版，需要时再让它按路径去读完整版，省上下文")]
    public bool SaveReport { get; set; } = true;

    [DisplayName("推送字符上限")]
    [Description("作用：报告超过这个长度时，只给 AI 推精简版 + 完整报告路径。怎么选：6000~8000 最省；想让 AI 一次看全就调到 20000 以上（注意别撑爆上下文）")]
    public int PokeCharLimit { get; set; } = 8000;

    [DisplayName("视觉模型")]
    [Description("作用：指定视频分析用哪个视觉模型模块。怎么选：留空则使用当前启用中的视觉模型；若同时启用了多个视觉模型模块（如 Qwen3-VL / MiniCPM），在这里挑一个。改完重启本插件生效。")]
    public string SelectedVisionModel { get; set; } = string.Empty;

    /// <summary>是否已配置在线转写通道（本机无模型时的兜底）</summary>
    public bool HasOnlineTranscribe =>
        string.IsNullOrWhiteSpace(OnlineTranscribeEndpoint) == false &&
        string.IsNullOrWhiteSpace(OnlineTranscribeApiKey) == false;

    /// <summary>按当前配置生成一次分析所用的参数（含边界收敛）</summary>
    public VideoAnalysisOptions ToOptions(int? frameCount = null, bool withAudio = true)
    {
        //信息密集内容：给模型更多描写空间（提示词里已要求看不清就写「无法辨认」，
        //不会因为字数上限低而只挑一部分文字说）
        bool infoDense = ContentType == VideoContentType.InfoDense;
        int maxOutputTokens = Limit(MaxOutputTokens, 64, 8192);
        int frameMaxChars = Limit(FrameMaxChars, 32, 4096);
        if (infoDense)
        {
            maxOutputTokens = Math.Max(maxOutputTokens, 2048);
            frameMaxChars = Math.Max(frameMaxChars, 2048);
        }

        return new VideoAnalysisOptions {
            FrameCount = Limit(frameCount is > 0 ? frameCount.Value : FrameCount, 1, 120),
            FrameMaxWidth = Limit(FrameMaxWidth, 0, 3840),
            FrameExtractConcurrency = Limit(FrameExtractConcurrency, 1, 16),
            VisionConcurrency = Limit(VisionConcurrency, 1, 120),
            FrameTimeoutSeconds = Limit(FrameTimeoutSeconds, 5, 300),
            MaxRetryCount = Limit(MaxRetryCount, 0, 5),
            RetryDelayMs = Limit(RetryDelayMs, 0, 60000),
            MaxOutputTokens = maxOutputTokens,
            FrameMaxChars = frameMaxChars,
            AudioChunkSamples = Limit(AudioChunkSamples, 16000, 640000),
            UploadBitrateKbps = Limit(UploadBitrateKbps, 0, 256),
            OnlineTranscribeEndpoint = OnlineTranscribeEndpoint,
            OnlineTranscribeModel = OnlineTranscribeModel,
            OnlineTranscribeApiKey = OnlineTranscribeApiKey,
            WithAudio = withAudio,
            ContextEnabled = ContextEnabled,
            SegmentSize = Limit(SegmentSize, 1, 30),
            ContextLookback = Limit(ContextLookback, 0, 6),
            AudioContextSeconds = Limit(AudioContextSeconds, 0, 60),
        };
    }

    static int Limit(int value, int min, int max) => Math.Clamp(value, min, max);
}

/// <summary>一次视频分析所固化的参数</summary>
public record VideoAnalysisOptions
{
    public int FrameCount { get; init; } = 48;
    public int FrameMaxWidth { get; init; } = 1920;
    public int FrameExtractConcurrency { get; init; } = 12;
    public int VisionConcurrency { get; init; } = 16;
    public int FrameTimeoutSeconds { get; init; } = 200;
    public int MaxRetryCount { get; init; } = 5;
    public int RetryDelayMs { get; init; } = 2000;
    public int MaxOutputTokens { get; init; } = 1024;
    public int FrameMaxChars { get; init; } = 1024;
    public int AudioChunkSamples { get; init; } = 640000;
    public int UploadBitrateKbps { get; init; } = 128;
    public string OnlineTranscribeEndpoint { get; init; } = string.Empty;
    public string OnlineTranscribeModel { get; init; } = "whisper-1";
    public string OnlineTranscribeApiKey { get; init; } = string.Empty;
    public bool WithAudio { get; init; } = true;
    public bool ContextEnabled { get; init; } = true;
    public int SegmentSize { get; init; } = 6;
    public int ContextLookback { get; init; } = 2;
    public int AudioContextSeconds { get; init; } = 10;

    public bool HasOnlineTranscribe =>
        string.IsNullOrWhiteSpace(OnlineTranscribeEndpoint) == false &&
        string.IsNullOrWhiteSpace(OnlineTranscribeApiKey) == false;
}

/// <summary>预设档位的定义、套用与匹配</summary>
public static class VideoQualityPresets
{
    public static string GetName(VideoQualityPreset preset) => preset switch {
        VideoQualityPreset.Economy => "极速省流",
        VideoQualityPreset.Light => "轻量速览",
        VideoQualityPreset.Balanced => "均衡（推荐）",
        VideoQualityPreset.High => "高画质",
        VideoQualityPreset.Extreme => "极致 4K",
        _ => preset.ToString()
    };

    public static string GetDescription(VideoQualityPreset preset) => preset switch {
        VideoQualityPreset.Economy => "12 帧 / 简洁一句话 / 256 输出 token。本地 3B 模型下约 1~3 分钟跑完，几乎不吃上下文，适合超长视频和纯省 token 党。",
        VideoQualityPreset.Light => "24 帧 / 1280 宽 / 512 输出 token。本地 3B 模型下约 3~8 分钟，长视频（1 小时以上）的性价比之选。",
        VideoQualityPreset.Balanced => "48 帧 / 1920 宽 / 1024 输出 token。本地 3B 模型下约 15~30 分钟，清晰度与开销的平衡点，8G 显存默认用这个最稳。",
        VideoQualityPreset.High => "90 帧 / 2560 宽 / 3072 输出 token。本地 3B 模型下约 40~90 分钟，建议用在半小时以内的片子。",
        VideoQualityPreset.Extreme => "120 帧 / 3840 宽 / 8192 输出 token / 深度描述。本地 3B 模型下可达数小时，最全也最慢，只建议短视频或挂机慢慢跑。",
        _ => string.Empty
    };

    /// <summary>按显存推荐档位（本机本地模型场景）</summary>
    public static VideoQualityPreset RecommendForVram(int vramGb) => vramGb switch {
        <= 2 => VideoQualityPreset.Economy,
        <= 4 => VideoQualityPreset.Light,
        <= 8 => VideoQualityPreset.Balanced,
        <= 12 => VideoQualityPreset.High,
        _ => VideoQualityPreset.Extreme
    };

    public static string GetVramAdvice(int vramGb) => vramGb switch {
        <= 2 => "2G 或更低：用极速省流。视觉模型建议选 MiniCPM-V（最省显存），尽量不要同时开 TTS/记忆等其它模型。",
        <= 4 => "4G：轻量速览起手。MiniCPM-V 4bit 能跑，单帧输出别超 512，帧数别超 24。",
        <= 6 => "6G：均衡档。48 帧、1024 输出 token，本地视觉模型已经比较从容。",
        <= 8 => "8G：均衡档最稳，想看清细节可以上高画质。本地视觉模型权重约 2~3G（MiniCPM-V 更轻），帧是单帧串行推理，显存压力主要不在帧数，而在同时开了多少别的模型（TTS / 记忆向量 / 游戏陪玩会抢显存）。真正吃时间的是帧数：120 帧 × 本地模型单帧数秒 ≈ 十几分钟，赶时间就用 24~48 帧。",
        <= 12 => "12G：高画质档。可以放心上 90 帧 + 2560 宽。",
        _ => "12G 以上：极致档随便开，4K 满帧 + 深度描述也不虚。"
    };

    /// <summary>把预设写入配置（只改画质/开销相关项，不动 ffmpeg 路径与在线转写密钥）</summary>
    public static void Apply(VideoQualityPreset preset, VideoServiceConfig config)
    {
        (int frames, int width, int chars, int tokens, VideoAnalysisDepth depth,
            int visionConcurrency, int extractConcurrency, int timeout, int retry, int retryDelay,
            int bitrate, bool saveReport, int pokeLimit) = preset switch {
                VideoQualityPreset.Economy => (12, 768, 256, 256, VideoAnalysisDepth.Brief, 8, 8, 120, 2, 1000, 64, false, 6000),
                VideoQualityPreset.Light => (24, 1280, 512, 512, VideoAnalysisDepth.Brief, 8, 8, 150, 3, 1500, 96, true, 6000),
                VideoQualityPreset.Balanced => (48, 1920, 1024, 1024, VideoAnalysisDepth.Standard, 16, 12, 200, 5, 2000, 128, true, 8000),
                VideoQualityPreset.High => (90, 2560, 2048, 3072, VideoAnalysisDepth.Standard, 32, 16, 300, 5, 2000, 192, true, 12000),
                VideoQualityPreset.Extreme => (120, 3840, 4096, 8192, VideoAnalysisDepth.Deep, 120, 16, 300, 5, 2000, 256, true, 20000),
                _ => (48, 1920, 1024, 1024, VideoAnalysisDepth.Standard, 16, 12, 200, 5, 2000, 128, true, 8000)
            };

        config.FrameCount = frames;
        config.FrameMaxWidth = width;
        config.FrameMaxChars = chars;
        config.MaxOutputTokens = tokens;
        config.AnalysisDepth = depth;
        config.VisionConcurrency = visionConcurrency;
        config.FrameExtractConcurrency = extractConcurrency;
        config.FrameTimeoutSeconds = timeout;
        config.MaxRetryCount = retry;
        config.RetryDelayMs = retryDelay;
        config.UploadBitrateKbps = bitrate;
        config.SaveReport = saveReport;
        config.PokeCharLimit = pokeLimit;
    }

    /// <summary>判断当前配置是否与某个预设完全一致（用于在界面上显示“当前档位”）</summary>
    public static VideoQualityPreset? Match(VideoServiceConfig config)
    {
        foreach (VideoQualityPreset preset in Enum.GetValues<VideoQualityPreset>())
        {
            VideoServiceConfig reference = new() {
                FfmpegPath = config.FfmpegPath,
                OnlineTranscribeEndpoint = config.OnlineTranscribeEndpoint,
                OnlineTranscribeModel = config.OnlineTranscribeModel,
                OnlineTranscribeApiKey = config.OnlineTranscribeApiKey,
                AudioChunkSamples = config.AudioChunkSamples
            };
            Apply(preset, reference);

            if (reference.FrameCount == config.FrameCount &&
                reference.FrameMaxWidth == config.FrameMaxWidth &&
                reference.FrameMaxChars == config.FrameMaxChars &&
                reference.MaxOutputTokens == config.MaxOutputTokens &&
                reference.AnalysisDepth == config.AnalysisDepth &&
                reference.VisionConcurrency == config.VisionConcurrency &&
                reference.FrameExtractConcurrency == config.FrameExtractConcurrency &&
                reference.FrameTimeoutSeconds == config.FrameTimeoutSeconds &&
                reference.MaxRetryCount == config.MaxRetryCount &&
                reference.RetryDelayMs == config.RetryDelayMs &&
                reference.UploadBitrateKbps == config.UploadBitrateKbps &&
                reference.SaveReport == config.SaveReport &&
                reference.PokeCharLimit == config.PokeCharLimit)
                return preset;
        }

        return null;
    }
}

/// <summary>内容场景预设：一次设定采样密度、清晰度、概览与提示词，点一下就能按片子类型开跑</summary>
public static class VideoContentPresets
{
    const string TechPrompt =
        "请描述这一帧画面：重点写幻灯片上的标题、要点列表，以及示意图里各模块之间的关系；画面里出现了哪些文字（请原文抄录）。";

    const string PaperPrompt =
        "请描述这一帧里的图表/架构图：每一块表示什么、箭头从哪指向哪、对比了哪些方法、图注写了什么；看不清就写「无法辨认」。";

    const string NewsPrompt =
        "请描述这一帧：画面主体、正在讲的事件；画面上的标题、数字、结论请原文抄录，看不清就写「无法辨认」。";

    public static string GetName(VideoContentPreset preset) => preset switch {
        VideoContentPreset.Daily => "日常轻松",
        VideoContentPreset.General => "通用均衡",
        VideoContentPreset.TechShare => "技术分享",
        VideoContentPreset.Paper => "论文讲解",
        VideoContentPreset.AiNews => "AI 日报",
        _ => preset.ToString()
    };

    public static string GetDescription(VideoContentPreset preset) => preset switch {
        VideoContentPreset.Daily => "每 5 秒一帧 / 1280 宽 / 512 输出。刷 vlog、剧情、日常记录够用，开销最低。",
        VideoContentPreset.General => "每 2 秒一帧 / 1920 宽 / 1024 输出。纪录片、访谈、教程的通用起点。",
        VideoContentPreset.TechShare => "每 2 秒一帧 / 1920 宽 / 2048 输出 / 36 关键帧。技术分享的幻灯片和示意图不缺页，讲解靠转写。",
        VideoContentPreset.Paper => "每秒 1 帧 / 2560 宽 / 2048 输出 / 36 关键帧 4 张总览。演示图密集的论文讲解，配合在线视觉 API 才读得清。",
        VideoContentPreset.AiNews => "每秒 1 帧 / 2560 宽 / 2048 输出 / 36 关键帧。快切加大字号标题的 AI 日报，标题与数字优先抄录。",
        _ => string.Empty
    };

    /// <summary>把场景预设写入配置（只改采样、清晰度、概览、上下文与提示词；不动 ffmpeg、密钥与容错）</summary>
    public static void Apply(VideoContentPreset preset, VideoServiceConfig config)
    {
        (VideoContentType type, double sampleFps, int frames, int width, int chars, int tokens,
            VideoAnalysisDepth depth, int keyframes, int cells, int cellWidth, int sheets,
            int segment, int lookback, int audioSeconds, string prompt,
            int visionConcurrency, bool saveReport, int pokeLimit) = preset switch {
            VideoContentPreset.Daily => (VideoContentType.General, 0.2, 24, 1280, 512, 512,
                VideoAnalysisDepth.Brief, 8, 8, 512, 1, 8, 1, 8,
                VideoServiceConfig.DefaultFramePrompt, 16, false, 6000),
            VideoContentPreset.General => (VideoContentType.General, 0.5, 48, 1920, 1024, 1024,
                VideoAnalysisDepth.Standard, 18, 8, 640, 2, 6, 2, 10,
                VideoServiceConfig.DefaultFramePrompt, 16, true, 8000),
            VideoContentPreset.TechShare => (VideoContentType.InfoDense, 0.5, 60, 1920, 2048, 2048,
                VideoAnalysisDepth.Standard, 36, 12, 640, 3, 6, 2, 12,
                TechPrompt, 32, true, 12000),
            VideoContentPreset.Paper => (VideoContentType.InfoDense, 1.0, 90, 2560, 2048, 2048,
                VideoAnalysisDepth.Standard, 36, 12, 640, 4, 6, 2, 15,
                PaperPrompt, 64, true, 20000),
            VideoContentPreset.AiNews => (VideoContentType.InfoDense, 1.0, 60, 2560, 2048, 2048,
                VideoAnalysisDepth.Standard, 36, 12, 640, 3, 8, 2, 8,
                NewsPrompt, 64, true, 12000),
            _ => (VideoContentType.General, 0.5, 48, 1920, 1024, 1024,
                VideoAnalysisDepth.Standard, 18, 8, 640, 2, 6, 2, 10,
                VideoServiceConfig.DefaultFramePrompt, 16, true, 8000)
        };

        config.ContentType = type;
        config.SampleFps = sampleFps;
        config.FrameCount = frames;
        config.FrameMaxWidth = width;
        config.FrameMaxChars = chars;
        config.MaxOutputTokens = tokens;
        config.AnalysisDepth = depth;
        config.OverviewEnabled = true;
        config.OverviewCells = cells;
        config.OverviewCellWidth = cellWidth;
        config.OverviewMaxSheets = sheets;
        config.OverviewMaxKeyframes = keyframes;
        config.ContextEnabled = true;
        config.SegmentSize = segment;
        config.ContextLookback = lookback;
        config.AudioContextSeconds = audioSeconds;
        config.FramePrompt = prompt;
        config.VisionConcurrency = visionConcurrency;
        config.SaveReport = saveReport;
        config.PokeCharLimit = pokeLimit;
    }

    /// <summary>判断当前配置是否与某个场景预设一致（界面用来高亮按钮）</summary>
    public static VideoContentPreset? Match(VideoServiceConfig config)
    {
        foreach (VideoContentPreset preset in Enum.GetValues<VideoContentPreset>())
        {
            VideoServiceConfig reference = new();
            Apply(preset, reference);

            if (reference.ContentType == config.ContentType &&
                reference.SampleFps == config.SampleFps &&
                reference.FrameCount == config.FrameCount &&
                reference.FrameMaxWidth == config.FrameMaxWidth &&
                reference.FrameMaxChars == config.FrameMaxChars &&
                reference.MaxOutputTokens == config.MaxOutputTokens &&
                reference.AnalysisDepth == config.AnalysisDepth &&
                reference.OverviewMaxKeyframes == config.OverviewMaxKeyframes &&
                reference.OverviewCells == config.OverviewCells &&
                reference.OverviewCellWidth == config.OverviewCellWidth &&
                reference.OverviewMaxSheets == config.OverviewMaxSheets &&
                reference.OverviewEnabled == config.OverviewEnabled &&
                reference.ContextEnabled == config.ContextEnabled &&
                reference.SegmentSize == config.SegmentSize &&
                reference.ContextLookback == config.ContextLookback &&
                reference.AudioContextSeconds == config.AudioContextSeconds &&
                reference.FramePrompt == config.FramePrompt)
                return preset;
        }

        return null;
    }
}

/// <summary>token 开销粗略估算（按 Qwen-VL 家族的像素/合并规则量级估算，实际以后端为准）</summary>
public static class VideoTokenEstimator
{
    public static (int ImageTokens, int OutputTokens) Estimate(VideoServiceConfig config)
    {
        int frames = Math.Clamp(config.FrameCount, 1, 120);
        int width = Math.Clamp(config.FrameMaxWidth <= 0 ? 1920 : config.FrameMaxWidth, 320, 3840);
        double height = width * 9.0 / 16.0; //按 16:9 估算
        double pixels = Math.Clamp(width * height, 200_704, 401_408); //Qwen-VL 处理器的 min/max_pixels
        int perImage = (int)Math.Round(pixels / (28 * 28 * 4));
        return (frames * perImage, frames * Math.Clamp(config.MaxOutputTokens, 64, 8192));
    }

    public static string Describe(VideoServiceConfig config)
    {
        (int imageTokens, int outputTokens) = Estimate(config);
        return $"图片输入约 {imageTokens} token，模型输出约 {outputTokens} token，合计约 {imageTokens + outputTokens} token（不含文字稿与对话开销，仅供量级参考）";
    }
}
