using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Function.Vision;
using Microsoft.Extensions.Logging;

namespace presents.Video;

/// <summary>全片概览：按时间顺序拼好的网格图（可多张），加上模型对整片结构的回答</summary>
public record OverviewResult(
    string Text,
    int Columns,
    int Rows,
    IReadOnlyList<double> Timestamps,
    int Sheets = 1)
{
    public int CellCount => Timestamps.Count;

    /// <summary>格子序号 → 时间戳的对照，写进提示词让模型知道每一格对应哪一刻</summary>
    public string CellMap()
    {
        List<string> parts = new();
        for (int i = 0; i < Timestamps.Count; i++)
        {
            string position = i == 0 ? "左上" : i == Timestamps.Count - 1 ? "右下" : $"第 {i + 1} 格";
            parts.Add($"{position}={VideoTime.Format(Timestamps[i])}");
        }
        return string.Join("，", parts);
    }
}

/// <summary>
/// 全片概览：把「画面真正变过」的关键帧按时间顺序拼成网格图，一次识图看清整片走向。
///
/// 和等间隔抽帧的区别：关键帧由 <see cref="SceneKeyframes"/> 按画面差异选出，
/// 静止的 PPT 页不会重复占格子，省下的格子都留给真正翻页/切镜头的时刻。
/// 每张图最多 3×3 格（格数可配），格数多时按时间切成多张——一张塞几十格会让每格小到看不清字。
/// 每格宽度可配（默认 640px），并受总宽 2400px 的安全线约束：整张图超过读图工具的尺寸就会被压回去。
/// </summary>
public static class VideoOverview
{
    /// <summary>每格默认像素宽度。视觉能力弱的模型要更大，靠配置往上调</summary>
    public const int DefaultCellWidth = 640;
    public const int MinCellWidth = 256;
    public const int MaxCellWidth = 800;
    /// <summary>网格总宽上限。超过这个宽度读图工具会整体压回去，格子反而更糊</summary>
    public const int MaxSheetWidth = 2400;
    public const int MinCells = 4;
    public const int MaxCells = 24;
    public const int MaxSheetsLimit = 4;

    public static async Task<OverviewResult?> CaptureAsync(
        string ffmpeg,
        string videoPath,
        double duration,
        VideoAnalysisOptions options,
        IReadOnlyList<double>? keyframes,
        int requestedCells,
        int requestedCellWidth,
        int maxSheets,
        Func<IReadOnlyList<double>, int, int, int, int, string> promptFactory,
        IVisionModel visionModel,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (duration <= 0)
            return null;

        int cellsPerSheet = Math.Clamp(requestedCells, MinCells, MaxCells);
        //列数按格数选：9 格以内 3 列，再多用 4 列；格少则每格更大
        int columns = cellsPerSheet <= 9 ? 3 : 4;
        int rows = (int)Math.Ceiling(cellsPerSheet / (double)columns);
        int perSheet = columns * rows;

        List<double> picked;
        if (keyframes is { Count: >= 2 })
        {
            picked = keyframes.Where(t => t >= 0 && t < duration).OrderBy(t => t).ToList();
            if (picked.Count < 2)
                picked = UniformStamps(duration, perSheet);
        }
        else
        {
            picked = UniformStamps(duration, perSheet);
        }

        int sheets = Math.Clamp((int)Math.Ceiling(picked.Count / (double)perSheet), 1, Math.Clamp(maxSheets, 1, MaxSheetsLimit));
        int capacity = sheets * perSheet;
        if (picked.Count > capacity)
        {
            var thinned = new List<double>(capacity);
            for (int i = 0; i < capacity; i++)
                thinned.Add(picked[(int)Math.Round(i * (picked.Count - 1) / (double)(capacity - 1))]);
            picked = thinned.Distinct().ToList();
        }
        int sheetCount = (int)Math.Ceiling(picked.Count / (double)perSheet);

        //每格宽度可调，但拼起来总宽不能超过安全线，否则整张图会被读图工具压缩，格子白放大
        int cellWidth = Math.Clamp(requestedCellWidth <= 0 ? DefaultCellWidth : requestedCellWidth, MinCellWidth, MaxCellWidth);
        cellWidth = Math.Min(cellWidth, Math.Max(MinCellWidth, (MaxSheetWidth - 20) / columns));

        string directory = Path.Combine(AlifePath.TempFolderPath, "video_overview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var text = new StringBuilder();
        int totalCells = 0;
        int actualColumns = columns;
        int actualRows = rows;

        try
        {
            for (int s = 0; s < sheetCount; s++)
            {
                ct.ThrowIfCancellationRequested();
                List<double> chunk = picked.Skip(s * perSheet).Take(perSheet).ToList();
                if (chunk.Count == 0)
                    continue;

                string sheetDirectory = Path.Combine(directory, $"s{s}");
                Directory.CreateDirectory(sheetDirectory);

                await Parallel.ForEachAsync(Enumerable.Range(0, chunk.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.FrameExtractConcurrency), CancellationToken = ct },
                    async (index, token) => {
                        string cellPath = Path.Combine(sheetDirectory, $"{index:d3}.jpg");
                        await FfmpegTool.ExtractFrameAsync(ffmpeg, videoPath, chunk[index], cellWidth, cellPath, token);
                    });

                //个别帧抽不出来时用任一成功的帧补位，保证 tile 的图片序号不断档
                string? fallback = Directory.EnumerateFiles(sheetDirectory, "*.jpg").OrderBy(p => p).FirstOrDefault();
                if (fallback == null)
                {
                    logger?.LogWarning("[视频理解] 全片概览第 {Sheet} 张一帧都没抽到，跳过", s + 1);
                    continue;
                }
                for (int i = 0; i < chunk.Count; i++)
                {
                    string cellPath = Path.Combine(sheetDirectory, $"{i:d3}.jpg");
                    if (File.Exists(cellPath))
                        continue;
                    try
                    {
                        File.Copy(fallback, cellPath, true);
                    }
                    catch
                    {
                        break;
                    }
                }

                int sheetColumns = Math.Min(columns, chunk.Count);
                int sheetRows = (int)Math.Ceiling(chunk.Count / (double)columns);
                string gridPath = Path.Combine(sheetDirectory, "grid.jpg");

                FfmpegTool.ProcessResult tile = await FfmpegTool.RunAsync(ffmpeg, [
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-framerate", "1", "-start_number", "0",
                    "-i", Path.Combine(sheetDirectory, "%03d.jpg"),
                    "-vf", $"tile={sheetColumns}x{sheetRows}:padding=4:margin=4",
                    "-frames:v", "1", gridPath
                ], 120000, ct);

                if (tile.ExitCode != 0 || File.Exists(gridPath) == false)
                {
                    logger?.LogWarning("[视频理解] 全片概览第 {Sheet} 张拼图失败：{Error}", s + 1, tile.StandardError);
                    continue;
                }

                string prompt = promptFactory(chunk, sheetColumns, sheetRows, s + 1, sheetCount);
                string answer = await visionModel
                    .QueryAsync(gridPath, prompt, options.MaxOutputTokens, ct)
                    .WaitAsync(TimeSpan.FromSeconds(options.FrameTimeoutSeconds), ct);

                if (string.IsNullOrWhiteSpace(answer))
                    continue;

                if (sheetCount > 1)
                    text.AppendLine($"【第 {s + 1}/{sheetCount} 张：{VideoTime.Format(chunk[0])} – {VideoTime.Format(chunk[chunk.Count - 1])}】");
                text.AppendLine(answer.Trim());
                if (totalCells == 0)
                {
                    actualColumns = sheetColumns;
                    actualRows = sheetRows;
                }
                totalCells += chunk.Count;
            }

            if (totalCells == 0)
                return null;

            logger?.LogInformation("[视频理解] 全片概览完成（{Sheets} 张 / {Cells} 格，每格 {CellWidth}px，{Length} 字）",
                sheetCount, totalCells, cellWidth, text.Length);
            return new OverviewResult(text.ToString().Trim(), actualColumns, actualRows, picked, sheetCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger?.LogWarning(e, "[视频理解] 全片概览生成失败，已跳过（不影响逐帧分析）");
            return null;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch
            {
                // 临时目录清不掉不影响结果
            }
        }
    }

    /// <summary>兜底：关键帧探测失败时，退回按时间等间隔取点</summary>
    static List<double> UniformStamps(double duration, int count)
    {
        var stamps = new List<double>(count);
        for (int i = 0; i < count; i++)
            stamps.Add(duration * (i + 0.5) / count);
        return stamps;
    }
}
