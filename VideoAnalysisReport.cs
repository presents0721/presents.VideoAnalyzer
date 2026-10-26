using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace presents.Video;

/// <summary>时间轴上的一段连续画面（段内的帧是有先后关系的，不是互不相干的截图）</summary>
public record VideoSegment(int Part, double StartSeconds, double EndSeconds, FrameAnalysis[] Frames)
{
    public string Range => $"{VideoTime.Format(StartSeconds)}–{VideoTime.Format(EndSeconds)}";
}

/// <summary>视频分析结果：分段画面时间轴 + 语音文字稿</summary>
public record VideoAnalysisReport(
    VideoInfo Info,
    FrameAnalysis[] Frames,
    TranscriptResult Transcript,
    TimeSpan Elapsed,
    int SegmentSize = 0,
    OverviewResult? Overview = null,
    AudioProbeResult? AudioProbe = null)
{
    public int SuccessFrameCount => Frames.Count(frame => frame.IsSuccess);
    public int FailedFrameCount => Frames.Length - SuccessFrameCount;

    /// <summary>按 SegmentSize 把时间轴切成连续段落，段内帧保持时间顺序</summary>
    public IEnumerable<VideoSegment> Segments()
    {
        if (Frames.Length == 0)
            yield break;

        int size = SegmentSize > 0 ? SegmentSize : Math.Max(1, Frames.Length);
        for (int start = 0; start < Frames.Length; start += size)
        {
            FrameAnalysis[] slice = Frames.Skip(start).Take(size).ToArray();
            double from = slice[0].Timestamp;
            double to = start + size < Frames.Length ? Frames[start + size].Timestamp : Info.DurationSeconds;
            yield return new VideoSegment(start / size + 1, from, to, slice);
        }
    }

    public string ToMarkdown()
    {
        StringBuilder builder = new();
        builder.AppendLine("# 视频分析报告");
        builder.AppendLine();
        builder.AppendLine($"- 文件：{Info.FilePath}");
        builder.AppendLine($"- 时长：{VideoTime.Format(Info.DurationSeconds)}（{Info.DurationSeconds:0.###} 秒）");
        builder.AppendLine($"- 分辨率：{(Info.Width > 0 ? $"{Info.Width}x{Info.Height}" : "未知")}");
        builder.AppendLine($"- 编码：视频 {NullToUnknown(Info.VideoCodec)} / 音频 {(Info.HasAudio ? Info.AudioCodec : "无")}");
        builder.AppendLine($"- 抽帧：{SuccessFrameCount}/{Frames.Length} 帧成功（均匀锚点 + 画面变化关键帧，覆盖全片）");
        builder.AppendLine($"- 语音转写：{Transcript.Mode}（{Transcript.Lines.Count} 段）{(Transcript.Note != null ? $"，备注：{Transcript.Note}" : string.Empty)}");
        if (AudioProbe != null)
            builder.AppendLine($"- 声学侦察：{AudioProbe.Summary}");
        builder.AppendLine($"- 分析耗时：{Elapsed:hh\\:mm\\:ss}");
        builder.AppendLine();

        if (Overview != null)
        {
            builder.AppendLine("## 全片概览");
            builder.AppendLine();
            builder.AppendLine($"> 由按画面变化挑出的 {Overview.CellCount} 个关键帧拼成 {(Overview.Sheets > 1 ? $"{Overview.Sheets} 张 {Overview.Columns}×{Overview.Rows} 格网格图" : $"{Overview.Columns}×{Overview.Rows} 格网格图")}一次识图得出，是整个视频的骨架；下面的分段叙事是骨架上的细节。");
            builder.AppendLine();
            builder.AppendLine(Overview.Text);
            builder.AppendLine();
        }

        builder.AppendLine("## 时间轴（分段连续叙事）");
        builder.AppendLine();
        builder.AppendLine("> 段内的帧按时间顺序连续分析，每一帧都带着前几帧的内容和同刻语音，");
        builder.AppendLine("> 读的时候按段顺着读即可，不要把每一行当成独立截图。");
        foreach (VideoSegment segment in Segments())
        {
            builder.AppendLine();
            builder.AppendLine($"### 第 {segment.Part} 段 · {segment.Range}");
            foreach (FrameAnalysis frame in segment.Frames)
            {
                if (frame.IsSuccess == false)
                    continue;
                builder.AppendLine($"- [{VideoTime.Format(frame.Timestamp)}] {frame.Description}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 语音文字稿");
        builder.AppendLine();
        if (Transcript.Lines.Count == 0)
            builder.AppendLine($"- （无）{(Transcript.Note ?? string.Empty)}");
        else
            foreach (TranscriptLine line in Transcript.Lines)
                builder.AppendLine($"- [{VideoTime.Format(line.Timestamp)}] {line.Text}");

        if (FailedFrameCount > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 失败帧");
            builder.AppendLine();
            foreach (FrameAnalysis frame in Frames.Where(frame => frame.IsSuccess == false))
                builder.AppendLine($"- 第 {frame.Index + 1} 帧（{VideoTime.Format(frame.Timestamp)}）：{frame.Error}");
        }

        return builder.ToString();
    }

    /// <summary>精简版：长视频时避免一次性塞爆上下文，配合完整报告文件使用</summary>
    public string ToCondensedText(int perFrameChars = 60, int perLineChars = 80)
    {
        StringBuilder builder = new();
        builder.AppendLine("【视频分析结果（精简版，按段落连续叙事）】");
        builder.AppendLine($"文件：{Info.FilePath}（{VideoTime.Format(Info.DurationSeconds)}，{SuccessFrameCount}/{Frames.Length} 帧）");
        if (Overview != null)
        {
            builder.AppendLine();
            builder.AppendLine("【全片概览】");
            builder.AppendLine(Clip(Overview.Text, 800));
        }
        if (AudioProbe != null && AudioProbe.Verdict is AudioVerdict.Silent or AudioVerdict.VeryQuiet
                or AudioVerdict.Musical or AudioVerdict.Tonal)
        {
            builder.AppendLine();
            builder.AppendLine($"【音轨提示】{AudioProbe.Summary}");
        }
        builder.AppendLine();

        foreach (VideoSegment segment in Segments())
        {
            builder.AppendLine($"【第 {segment.Part} 段 {segment.Range}】");
            foreach (FrameAnalysis frame in segment.Frames)
            {
                if (frame.IsSuccess == false)
                    continue;
                builder.AppendLine($"[{VideoTime.Format(frame.Timestamp)}] {Clip(frame.Description, perFrameChars)}");
            }
            builder.AppendLine();
        }

        builder.AppendLine("【语音文字稿】");
        if (Transcript.Lines.Count == 0)
            builder.AppendLine($"（无）{Transcript.Note ?? string.Empty}");
        else
            foreach (TranscriptLine line in Transcript.Lines.Take(200))
                builder.AppendLine($"[{VideoTime.Format(line.Timestamp)}] {Clip(line.Text, perLineChars)}");

        if (FailedFrameCount > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"【失败帧】{FailedFrameCount} 帧分析失败（已自动重试）");
        }

        return builder.ToString();
    }

    static string Clip(string text, int max)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    static string NullToUnknown(string value) => string.IsNullOrWhiteSpace(value) ? "未知" : value;
}

public static class VideoTime
{
    public static string Format(double seconds)
    {
        if (double.IsFinite(seconds) == false || seconds < 0)
            seconds = 0;

        TimeSpan time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? time.ToString(@"hh\:mm\:ss")
            : time.ToString(@"mm\:ss");
    }
}
