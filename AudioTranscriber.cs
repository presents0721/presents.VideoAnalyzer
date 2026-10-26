using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Function.Auditory;
using Microsoft.Extensions.Logging;

namespace presents.Video;

/// <summary>带时间轴的一行转写文本</summary>
public record TranscriptLine(double Timestamp, string Text);

/// <summary>音轨转写结果</summary>
public record TranscriptResult(string Mode, List<TranscriptLine> Lines, string? Note)
{
    public bool IsEmpty => Lines.Count == 0;
}

/// <summary>
/// 转写进行中的实时缓冲。视觉分析不必等整条音轨转完：
/// 每分析一帧，就从这里取「这一时刻前后几秒说了什么」，把台词和画面对上。
/// </summary>
public sealed class TranscriptBuffer
{
    readonly List<TranscriptLine> lines = new();
    readonly Lock gate = new();

    public int Count
    {
        get
        {
            lock (gate)
                return lines.Count;
        }
    }

    public void Add(double timestamp, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        //模板句幻觉在这里就挡住，免得污染视觉提示词里的「同刻语音」
        if (TranscriptSanitizer.IsBoilerplate(text))
            return;
        lock (gate)
            lines.Add(new TranscriptLine(timestamp, text.Trim()));
    }

    public List<TranscriptLine> Snapshot()
    {
        lock (gate)
            return lines.OrderBy(line => line.Timestamp).ToList();
    }

    /// <summary>取某时刻前后 windowSeconds 秒内的转写文本，拼成一段给视觉提示词用</summary>
    public string Window(double timestamp, double windowSeconds, int maxChars = 800)
    {
        if (windowSeconds <= 0)
            return string.Empty;

        string text;
        lock (gate)
        {
            if (lines.Count == 0)
                return string.Empty;
            text = string.Join(" ", lines
                .Where(line => line.Timestamp >= timestamp - windowSeconds && line.Timestamp <= timestamp + windowSeconds)
                .Select(line => line.Text));
        }

        text = text.Trim();
        return text.Length <= maxChars ? text : text[..maxChars] + "…";
    }
}

/// <summary>
/// 音轨转写：ffmpeg 把整条音轨流式解成 16kHz 单声道，按块（默认 40 秒）逐块喂给识别器。
/// 本机模型（SenseVoice）优先，没有本机模型时自动走在线直连（OpenAI 兼容 /audio/transcriptions）。
/// 全程只有一块音频驻留内存，内存占用与视频长短无关。
/// </summary>
public static class AudioTranscriber
{
    public static async Task<TranscriptResult> TranscribeAsync(
        string ffmpeg,
        string videoPath,
        VideoAnalysisOptions options,
        IAudioRecognizerProvider? localProvider,
        IProgress<double>? progress = null,
        ILogger? logger = null,
        TranscriptBuffer? live = null,
        CancellationToken ct = default)
    {
        bool useLocal = localProvider != null;
        if (useLocal == false && options.HasOnlineTranscribe == false)
            return new TranscriptResult("未启用", [], "未装配本机语音识别模型，也未配置在线转写服务，已跳过音轨分析。");

        string mode = useLocal ? "本机模型（SenseVoice）" : "在线直连";
        string? chunkDirectory = null;
        TranscriptBuffer buffer = live ?? new TranscriptBuffer();

        IAudioRecognizer? recognizer = null;
        HttpClient? http = null;
        int chunkIndex = 0;

        try
        {
            if (useLocal)
            {
                IAudioRecognizer localRecognizer = localProvider!.CreateAudioRecognizer();
                recognizer = localRecognizer;
                double chunkStart = 0;
                localRecognizer.Recognized += text => buffer.Add(chunkStart, text);

                long total = await FfmpegTool.DecodeAudioChunksAsync(ffmpeg, videoPath, options.AudioChunkSamples,
                    (samples, count, startSeconds) => {
                        chunkStart = startSeconds; //VAD 输出是同步的，此时记录的时间戳即为该段语音的大致位置
                        localRecognizer.AcceptWaveform(samples, count);
                        progress?.Report(startSeconds + count / 16000.0);
                        return Task.CompletedTask;
                    }, ct);

                localRecognizer.Flush(); //促使 VAD 吐出末尾语音段
                progress?.Report(total / 16000.0);
            }
            else
            {
                http = Http;
                chunkDirectory = Path.Combine(AlifePath.TempFolderPath, "video_audio", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(chunkDirectory);

                await FfmpegTool.DecodeAudioChunksAsync(ffmpeg, videoPath, options.AudioChunkSamples,
                    async (samples, count, startSeconds) => {
                        int index = chunkIndex++;
                        string wavPath = Path.Combine(chunkDirectory!, $"chunk_{index:0000}.wav");
                        await FfmpegTool.WriteWavAsync(wavPath, samples, count, ct);

                        string uploadPath = wavPath;
                        string mp3Path = Path.ChangeExtension(wavPath, ".mp3");
                        if (options.UploadBitrateKbps > 0 &&
                            await FfmpegTool.TranscodeToMp3Async(ffmpeg, wavPath, mp3Path, options.UploadBitrateKbps, ct))
                            uploadPath = mp3Path;

                        try
                        {
                            string text = await UploadAsync(http!, options, uploadPath, ct);
                            buffer.Add(startSeconds, text);
                        }
                        finally
                        {
                            TryDelete(wavPath);
                            TryDelete(mp3Path);
                        }

                        progress?.Report(startSeconds + count / 16000.0);
                    }, ct);
            }

            return TranscriptSanitizer.Sanitize(new TranscriptResult(mode, buffer.Snapshot(), null));
        }
        catch (Exception e)
        {
            logger?.LogWarning(e, "[视频理解] 音轨转写失败");
            return TranscriptSanitizer.Sanitize(new TranscriptResult(mode, buffer.Snapshot(), $"音轨处理失败：{e.Message}"));
        }
        finally
        {
            recognizer?.Dispose();
            if (chunkDirectory != null)
            {
                try
                {
                    Directory.Delete(chunkDirectory, true);
                }
                catch
                {
                    // 临时目录清理失败不影响结果
                }
            }
        }
    }

    static async Task<string> UploadAsync(HttpClient http, VideoAnalysisOptions options, string filePath, CancellationToken ct)
    {
        await using FileStream stream = File.OpenRead(filePath);
        using StreamContent streamContent = new(stream);
        bool isMp3 = string.Equals(Path.GetExtension(filePath), ".mp3", StringComparison.OrdinalIgnoreCase);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(isMp3 ? "audio/mpeg" : "audio/wav");

        using MultipartFormDataContent content = new();
        content.Add(streamContent, "file", isMp3 ? "audio.mp3" : "audio.wav");
        content.Add(new StringContent(options.OnlineTranscribeModel), "model");

        using HttpRequestMessage request = new(HttpMethod.Post, options.OnlineTranscribeEndpoint) { Content = content };
        if (string.IsNullOrWhiteSpace(options.OnlineTranscribeApiKey) == false)
            request.Headers.Add("Authorization", $"Bearer {options.OnlineTranscribeApiKey}");

        using HttpResponseMessage response = await http.SendAsync(request, ct);
        string json = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode == false)
            throw new Exception($"在线转写失败（{(int)response.StatusCode}）：{Trim(json)}");

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("text", out JsonElement text))
                return text.GetString() ?? string.Empty;
        }
        catch
        {
            // 非标准响应，直接返回原文
        }

        return json;
    }

    static string Trim(string text)
    {
        text = text.Trim();
        return text.Length > 300 ? text[..300] : text;
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 忽略
        }
    }

    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
}
