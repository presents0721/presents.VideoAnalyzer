using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace presents.Video;

/// <summary>
/// 按「画面真的变了」选关键帧，而不是等间隔抽帧。
///
/// 做法对齐 extract_keyframes 参考实现：整片解码成 200px 宽的灰度裸帧，逐帧和上一帧比差异，
/// 差异分数取「全图平均差异」与「8×8 分块里变化最剧烈那一块的差异」的较大者——
/// 只看全图平均的话，角落出字幕、局部小动效会被大片没变的像素稀释掉。
/// 阈值默认按「差异中位数 × 4」自动估计（中位数约等于压缩噪点/轻微抖动的底噪），夹在 0.008~0.08；
/// 选帧时变化发生不急着取，等变化停下来那一帧（完整展示的画面）才算关键帧，
/// 持续运镜则每 1.5 秒补一张，相邻关键帧至少隔 0.3 秒，到片尾还在变就补结尾帧。
///
/// 探测失败（解码超时/文件损坏）返回空列表，调用方退回均匀采样，不阻断主流程。
/// </summary>
public static class SceneKeyframes
{
    /// <summary>画面变化阈值：0 = 自动（差异中位数 × 4，夹在 0.008~0.08）。手动填时按同一标尺，越大越严格</summary>
    public const double DefaultThreshold = 0;

    const int AnalyzeWidth = 200;      // 分析用缩略图宽度，200 足够判断画面变化
    const int BlockGrid = 8;           // 局部差异检测的分块网格（8×8）
    const double NoiseK = 4.0;         // 自适应阈值 = 底噪 × K
    const double AbsFloor = 0.008;     // 自适应阈值下限，避免静止视频把噪声当变化
    const double AbsCeil = 0.080;      // 自适应阈值上限，避免噪声大的视频什么都抓不到
    const double MinGapSeconds = 0.30; // 相邻关键帧最小间隔，防瞬间抖动炸出一堆重复帧
    const double MotionSeconds = 1.5;  // 画面持续变化（运镜/长动效）时强制补帧的间隔
    const int ScanTimeoutMs = 900_000; // 整片解码最长 15 分钟；超时退回均匀采样

    /// <summary>
    /// 返回按时间排好、数量不超过 maxKeyframes 的关键帧时间点。
    /// 结果少于 2 个（画面几乎不变或探测失败）时返回空列表，让调用方用均匀采样。
    /// </summary>
    public static async Task<IReadOnlyList<double>> DetectAsync(
        string ffmpeg,
        VideoInfo info,
        double threshold,
        int maxKeyframes,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (info.DurationSeconds <= 0 || maxKeyframes < 2)
            return Array.Empty<double>();

        int aw = Math.Min(AnalyzeWidth, info.Width > 0 ? info.Width : AnalyzeWidth);
        aw = Even(aw);
        int ah = info.Width > 0 && info.Height > 0
            ? Even(Math.Max(2, (int)Math.Round(info.Height * (double)aw / info.Width)))
            : Even(Math.Max(2, aw * 9 / 16));
        if (aw <= 0 || ah <= 0)
            return Array.Empty<double>();

        int frameBytes = aw * ah;
        var scores = new List<float>(4096);
        byte[] current = new byte[frameBytes];
        byte[] previous = new byte[frameBytes];
        bool hasPrevious = false;

        List<string> args =
        [
            "-hide_banner", "-nostats", "-loglevel", "error",
            "-i", info.FilePath,
            "-vf", $"scale={aw}:{ah}:flags=area",
            "-pix_fmt", "gray", "-fps_mode", "passthrough",
            "-f", "rawvideo", "-"
        ];

        try
        {
            FfmpegTool.ProcessResult result = await FfmpegTool.RunAsync(ffmpeg, args, ScanTimeoutMs, ct,
                async reader => {
                    Stream stdout = reader.BaseStream;
                    int filled = 0;
                    while (true)
                    {
                        int read = await stdout.ReadAsync(current.AsMemory(filled, frameBytes - filled), ct);
                        if (read <= 0)
                            break;
                        filled += read;
                        if (filled < frameBytes)
                            continue;

                        if (hasPrevious)
                            scores.Add(DiffScore(previous, current, aw, ah));

                        Buffer.BlockCopy(current, 0, previous, 0, frameBytes);
                        hasPrevious = true;
                        filled = 0;
                    }
                });

            if (result.ExitCode != 0)
            {
                logger?.LogWarning("[视频理解] 关键帧探测失败（退出码 {Code}），概览退回均匀采样", result.ExitCode);
                return Array.Empty<double>();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            logger?.LogWarning("[视频理解] 关键帧探测超时，概览退回均匀采样（长片可关掉「按画面变化选帧」省时间）");
            return Array.Empty<double>();
        }
        catch (Exception e)
        {
            logger?.LogWarning(e, "[视频理解] 关键帧探测异常，概览退回均匀采样");
            return Array.Empty<double>();
        }

        int totalFrames = scores.Count + 1;
        if (scores.Count == 0)
            return Array.Empty<double>();

        double thr = threshold > 0
            ? Math.Clamp(threshold, AbsFloor, 0.9)
            : AutoThreshold(scores);

        double secPerFrame = info.DurationSeconds / totalFrames;
        if (secPerFrame <= 0)
            return Array.Empty<double>();

        List<int> indices = SelectKeys(scores, totalFrames, thr, secPerFrame, maxKeyframes);
        if (indices.Count < 2)
        {
            logger?.LogInformation("[视频理解] 画面变化不明显（阈值 {Threshold:0.###} 命中 {Hits} 次），概览退回均匀采样",
                thr, scores.Count(s => s > thr));
            return Array.Empty<double>();
        }

        double last = info.DurationSeconds;
        List<double> keys = indices
            .Select(i => Math.Clamp(i * secPerFrame, 0, Math.Max(0, last - 0.05)))
            .Distinct()
            .ToList();

        logger?.LogInformation("[视频理解] 按画面变化选出 {Count} 个关键帧（阈值 {Threshold:0.###}{Mode}，整片 {Frames} 帧）",
            keys.Count, thr, threshold > 0 ? "手动" : "自动", totalFrames);
        return keys;
    }

    /// <summary>
    /// 两帧（灰度 0~255）之间的差异分数：全图平均差异与 8×8 分块最大差异取较大者。
    /// 每块内部取均值本身就能抵消随机噪点，所以取最大值也不会被噪声带偏。
    /// </summary>
    static float DiffScore(byte[] a, byte[] b, int width, int height)
    {
        long sum = 0;
        int blockW = width / BlockGrid;
        int blockH = height / BlockGrid;
        bool useBlocks = blockW >= 2 && blockH >= 2;

        float localMax = 0;
        if (useBlocks)
        {
            long[] blockSums = new long[BlockGrid * BlockGrid];
            for (int y = 0; y < blockH * BlockGrid; y++)
            {
                int row = y * width;
                int blockRow = (y / blockH) * BlockGrid;
                for (int x = 0; x < blockW * BlockGrid; x++)
                {
                    int d = a[row + x] - b[row + x];
                    if (d < 0) d = -d;
                    sum += d;
                    blockSums[blockRow + x / blockW] += d;
                }
            }

            int blockPixels = blockW * blockH;
            for (int i = 0; i < blockSums.Length; i++)
            {
                float value = blockSums[i] / (float)blockPixels;
                if (value > localMax)
                    localMax = value;
            }

            // 块外（尺寸除不尽的边角）也要计入全图平均，避免边缘全是变化却算不出来
            for (int y = blockH * BlockGrid; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int d = a[row + x] - b[row + x];
                    sum += d < 0 ? -d : d;
                }
            }
            for (int y = 0; y < blockH * BlockGrid; y++)
            {
                int row = y * width;
                for (int x = blockW * BlockGrid; x < width; x++)
                {
                    int d = a[row + x] - b[row + x];
                    sum += d < 0 ? -d : d;
                }
            }
        }
        else
        {
            for (int i = 0; i < a.Length; i++)
            {
                int d = a[i] - b[i];
                sum += d < 0 ? -d : d;
            }
        }

        float global = sum / (float)(width * height) / 255f;
        return Math.Max(global, localMax / 255f);
    }

    /// <summary>自适应阈值：视频大部分时间静止，差异中位数约等于底噪；取底噪 × K 并夹在上下限内</summary>
    static double AutoThreshold(List<float> scores)
    {
        if (scores.Count == 0)
            return AbsFloor;
        var sorted = scores.ToArray();
        Array.Sort(sorted);
        double median = sorted[sorted.Length / 2];
        return Math.Clamp(median * NoiseK, AbsFloor, AbsCeil);
    }

    /// <summary>
    /// 挑出关键帧的帧号，返回顺序即原视频时间顺序。
    ///   1) 第一帧一定保留（初始状态）；
    ///   2) 画面变了不急着选，等它变完——变化结束后稳定下来的帧才是完整展示的画面；
    ///   3) 画面持续在变（运镜/长动效）就每 MotionSeconds 补一帧，免得整段运动只出一张图；
    ///   4) 相邻关键帧至少隔 MinGapSeconds 秒；到片尾还在变就补上结尾画面。
    /// </summary>
    static List<int> SelectKeys(List<float> scores, int totalFrames, double threshold, double secPerFrame, int maxKeyframes)
    {
        if (totalFrames <= 0)
            return [];
        // 帧数本身就极少（短 GIF / 超短视频）：每一帧都是独立内容，全都留下
        if (totalFrames <= 8)
            return Enumerable.Range(0, totalFrames).ToList();

        List<int> selected = [0];
        int minGap = Math.Max(1, (int)Math.Round(MinGapSeconds / secPerFrame));
        int motionGap = Math.Max(1, (int)Math.Round(MotionSeconds / secPerFrame));

        bool inChange = false;
        int changeStart = 0;
        for (int i = 1; i < totalFrames; i++)
        {
            float score = scores[i - 1];
            if (score > threshold)
            {
                if (inChange == false)
                {
                    inChange = true;
                    changeStart = i;
                }
                if (i - changeStart >= motionGap && i - selected[^1] >= minGap)
                {
                    selected.Add(i);
                    changeStart = i;
                }
            }
            else if (inChange)
            {
                // 变化刚结束，这一帧画面已经更新完且稳定——就是它
                if (i - selected[^1] >= minGap)
                    selected.Add(i);
                inChange = false;
            }
        }

        int last = totalFrames - 1;
        // 到片尾还在变（最后一个镜头没停下来）：补上结尾画面，不然会丢掉最后一个状态
        if (inChange && last > selected[^1] && last - selected[^1] >= minGap)
            selected.Add(last);

        if (maxKeyframes > 0 && selected.Count > maxKeyframes)
        {
            List<int> thinned = new(maxKeyframes);
            for (int i = 0; i < maxKeyframes; i++)
                thinned.Add(selected[(int)Math.Round(i * (selected.Count - 1) / (double)(maxKeyframes - 1))]);
            selected = thinned.Distinct().ToList();
        }
        return selected;
    }

    static int Even(int value) => value % 2 == 0 ? value : Math.Max(2, value - 1);
}
