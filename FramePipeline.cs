using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Function.Vision;
using Microsoft.Extensions.Logging;

namespace presents.Video;

/// <summary>单帧分析结果</summary>
public record FrameAnalysis(int Index, double Timestamp, string Description, string? Error)
{
    public bool IsSuccess => Error == null;
}

/// <summary>一帧的提示词上下文：前几帧看到了什么、这一时刻说了什么</summary>
public record FrameContextRequest(
    int Index,
    double Timestamp,
    IReadOnlyList<string> PreviousDescriptions,
    string AudioText);

/// <summary>
/// 抽帧 + 视觉分析流水线。
/// 时间轴先切成若干段（SegmentSize 帧一段），段与段之间并行、段内按时间顺序串行：
/// 这样每一帧都能看到前几帧的描述（真正的前后文），而不是把每帧当成孤立截图。
/// 抽帧与分析在同一段内两级流水：段内先并行抽帧（受抽帧并发限制），再逐帧投喂视觉模型。
/// 帧只以文件形式短暂存在，段分析完即删除，内存占用与视频大小无关。
/// </summary>
public static class FramePipeline
{
    public static async Task<FrameAnalysis[]> RunAsync(
        string ffmpeg,
        string videoPath,
        VideoInfo info,
        VideoAnalysisOptions options,
        Func<FrameContextRequest, string> promptFactory,
        Func<double, string>? audioProvider,
        IReadOnlyList<double>? keyframes,
        IReadOnlyList<double>? speechOnsets,
        IVisionModel visionModel,
        IProgress<FrameProgress>? progress = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        //均匀锚点保证「每个时间段都有画面」；画面变化关键帧保证「真正变过的时刻不漏」。
        //两者合并后叙事既能对上同刻语音，又不会只拍到翻页中间的空白帧。
        (int Index, double Timestamp)[] frames = BuildTimestamps(info.DurationSeconds, options.FrameCount, keyframes, speechOnsets);
        int frameCount = frames.Length;
        FrameAnalysis[] results = new FrameAnalysis[frameCount];
        int completed = 0;

        //关了上下文就不分段（每帧一段），退回旧的「全并行、互不相干」模式
        int segmentSize = options.ContextEnabled ? Math.Clamp(options.SegmentSize, 1, 30) : 1;
        (int Index, double Timestamp)[][] segments = frames.Chunk(segmentSize).ToArray();

        int lookback = options.ContextEnabled ? Math.Max(0, options.ContextLookback) : 0;
        int parallelSegments = Math.Max(1, Math.Min(options.VisionConcurrency, segments.Length));

        await Parallel.ForEachAsync(segments,
            new ParallelOptions { MaxDegreeOfParallelism = parallelSegments, CancellationToken = ct },
            async (segment, token) => {
                string frameDirectory = Path.Combine(AlifePath.TempFolderPath, "video_frames", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(frameDirectory);

                string?[] framePaths = new string?[segment.Length];
                try
                {
                    //一级流水线：段内并行抽帧（并发受磁盘吞吐限制）
                    await Parallel.ForEachAsync(Enumerable.Range(0, segment.Length),
                        new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.FrameExtractConcurrency), CancellationToken = token },
                        async (offset, extractToken) => {
                            (int index, double timestamp) = segment[offset];
                            string framePath = Path.Combine(frameDirectory, $"{index:0000}.jpg");
                            try
                            {
                                await FfmpegTool.ExtractFrameAsync(ffmpeg, videoPath, timestamp, options.FrameMaxWidth, framePath, extractToken);
                                framePaths[offset] = framePath;
                            }
                            catch (Exception e)
                            {
                                logger?.LogWarning(e, "[视频理解] 第 {Index} 帧抽取失败（{Time}s）", index + 1, timestamp);
                                results[index] = new FrameAnalysis(index, timestamp, string.Empty, e.Message);
                                Report();
                            }
                        });

                    //二级流水线：段内按时间顺序逐帧分析，每帧带上前面几帧的描述与同刻语音
                    LinkedList<string> history = new();
                    for (int offset = 0; offset < segment.Length; offset++)
                    {
                        token.ThrowIfCancellationRequested();
                        string? framePath = framePaths[offset];
                        if (framePath == null)
                            continue; //抽帧失败已在上面记过

                        (int index, double timestamp) = segment[offset];
                        string audioText = audioProvider?.Invoke(timestamp) ?? string.Empty;
                        FrameContextRequest request = new(index, timestamp, history.ToArray(), audioText);

                        FrameAnalysis result = await AnalyseFrameAsync(
                            framePath, request, promptFactory, options, visionModel, logger, token);

                        results[index] = result;
                        if (result.IsSuccess && lookback > 0)
                        {
                            history.AddLast(result.Description);
                            while (history.Count > lookback)
                                history.RemoveFirst();
                        }

                        Report();
                        TryDelete(framePath);
                    }
                }
                finally
                {
                    try
                    {
                        Directory.Delete(frameDirectory, true);
                    }
                    catch (Exception e)
                    {
                        logger?.LogWarning(e, "[视频理解] 清理帧缓存目录失败：{Path}", frameDirectory);
                    }
                }
            });

        void Report()
        {
            int done = Interlocked.Increment(ref completed);
            progress?.Report(new FrameProgress(done, frameCount));
        }

        return results;
    }

    /// <summary>相邻时间点近于这个值就算同一时刻（合并掉，避免同一画面分析两次）</summary>
    const double MergeGapSeconds = 0.5;

    /// <summary>
    /// 逐帧叙事的取点：均匀锚点 + 画面变化关键帧 + 语音起始点的混合。
    ///   1) 沿时间轴均匀取中点，首尾不漏——保证每一段都有画面锚点，能和同刻语音对上；
    ///   2) 每个锚点如果在半个间隔内有关键帧，就吸附过去——优先看变化结束后的稳定帧，
    ///      而不是恰好落在翻页/淡入途中的半截画面；
    ///   3) 没有关键帧可吸附时，吸附到半个间隔内最近的语音起始点——让画面落在讲师开口的时刻，
    ///      而不是说话的半截；语音起始点只做吸附、不额外增加帧；
    ///   4) 关键帧全部并入（未被吸附到的那些），保证短时间出现的内容不会整段漏掉；
    ///   5) 相近到 <see cref="MergeGapSeconds"/> 的时间点合并，避免同一画面重复分析。
    /// keyframes / speechOnsets 为空（选帧失败、无人声）时就是原来的均匀取点。
    /// </summary>
    public static (int Index, double Timestamp)[] BuildTimestamps(
        double duration, int frameCount, IReadOnlyList<double>? keyframes = null,
        IReadOnlyList<double>? speechOnsets = null)
    {
        if (duration <= 0)
            return Enumerable.Range(0, frameCount).Select(i => (i, 0.0)).ToArray();

        List<double> keys = Normalize(keyframes, duration);
        List<double> onsets = Normalize(speechOnsets, duration);

        double half = duration / frameCount / 2;
        List<double> times = new(frameCount + keys.Count);
        for (int i = 0; i < frameCount; i++)
        {
            double anchor = duration * (i + 0.5) / frameCount;
            bool snapped = false;

            if (keys.Count > 0)
            {
                double nearest = Nearest(keys, anchor);
                //吸附到附近的关键帧；但这个时刻已经被别的锚点占了，就保留原来的均匀锚点，
                //免得两个锚点吸到同一张图后中间空出一段没有画面
                if (Math.Abs(nearest - anchor) <= half
                    && times.All(t => Math.Abs(t - nearest) >= MergeGapSeconds))
                {
                    anchor = nearest;
                    snapped = true;
                }
            }

            if (snapped == false && onsets.Count > 0)
            {
                double nearest = Nearest(onsets, anchor);
                if (Math.Abs(nearest - anchor) <= half
                    && times.All(t => Math.Abs(t - nearest) >= MergeGapSeconds))
                    anchor = nearest;
            }

            times.Add(anchor);
        }

        times.AddRange(keys);
        times.Sort();

        List<double> merged = new(times.Count);
        foreach (double time in times)
        {
            if (merged.Count == 0 || time - merged[^1] >= MergeGapSeconds)
                merged.Add(time);
        }

        (int, double)[] frames = new (int, double)[merged.Count];
        for (int i = 0; i < merged.Count; i++)
            frames[i] = (i, merged[i]);
        return frames;
    }

    static List<double> Normalize(IReadOnlyList<double>? times, double duration)
    {
        if (times == null || times.Count == 0)
            return new List<double>();
        return times.Where(t => t >= 0 && t <= duration).OrderBy(t => t).ToList();
    }

    static double Nearest(List<double> times, double target)
    {
        double nearest = times[0];
        foreach (double time in times)
        {
            if (Math.Abs(time - target) < Math.Abs(nearest - target))
                nearest = time;
        }
        return nearest;
    }

    static async Task<FrameAnalysis> AnalyseFrameAsync(
        string framePath,
        FrameContextRequest request,
        Func<FrameContextRequest, string> promptFactory,
        VideoAnalysisOptions options,
        IVisionModel visionModel,
        ILogger? logger,
        CancellationToken ct)
    {
        string prompt = promptFactory(request);
        string? error = null;
        string description = string.Empty;

        for (int attempt = 0; attempt <= options.MaxRetryCount; attempt++)
        {
            if (attempt > 0)
            {
                //递增退避：越等越久。帧已抽好，重试不会重复抽帧
                await Task.Delay(options.RetryDelayMs * attempt, ct);
                logger?.LogInformation("[视频理解] 第 {Index} 帧第 {Attempt} 次重试", request.Index + 1, attempt);
            }

            try
            {
                string result = await visionModel
                    .QueryAsync(framePath, prompt, options.MaxOutputTokens, ct)
                    .WaitAsync(TimeSpan.FromSeconds(options.FrameTimeoutSeconds), ct);

                if (string.IsNullOrWhiteSpace(result))
                    throw new Exception("视觉模型返回空结果");

                description = result.Trim();
                if (description.Length > options.FrameMaxChars)
                    description = description[..options.FrameMaxChars];
                error = null;
                break;
            }
            catch (Exception e)
            {
                error = e.Message;
                if (ct.IsCancellationRequested)
                    break;
            }
        }

        return new FrameAnalysis(request.Index, request.Timestamp, description, error);
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 帧已消费，删除失败不影响结果
        }
    }
}

/// <summary>抽帧进度</summary>
public record FrameProgress(int Completed, int Total);
