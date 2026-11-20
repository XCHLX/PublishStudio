using Aliyun.OSS;

namespace PublishStudio.Core;

/// <summary>
/// 阿里云 OSS 上传。移植自 E:\Code\ProjectManager\Services\OssUploader.cs，
/// 分片、断点续传、合并分片全交给 SDK 的 ResumableUploadObject，不手写三段逻辑。
/// </summary>
public sealed class OssUploader
{
    /// <summary>
    /// OSS 访问域名。和参考项目（内务府发版工具）用同一个 bucket / region，
    /// 这样两边发出去的包下载地址是同一套规则。
    /// </summary>
    public const string Endpoint = "oss-cn-zhangjiakou.aliyuncs.com";

    /// <summary>目标 bucket 名，回调内务府时拼下载地址也用它。</summary>
    public const string Bucket = "duomaipublic";

    /// <summary>分片大小：5 MB，与参考项目保持一致。</summary>
    private const long PartSizeBytes = 5 * 1024 * 1024;

    /// <summary>
    /// 进度上报粒度（百分比）。SDK 的 StreamTransferProgress 按读取块触发，
    /// 60 MB 的文件能回调几千次，直接透传会把日志刷爆，按 5% 归并后最多 21 条。
    /// </summary>
    private const int ProgressStepPercent = 5;

    /// <summary>OSS 客户端（由 endpoint + AK/SK 构造）。</summary>
    private readonly OssClient _client;

    /// <summary>构造上传器。</summary>
    /// <param name="accessKeyId">AccessKey ID（来自「设置 → 设置发布配置」）。</param>
    /// <param name="accessKeySecret">AccessKey Secret。</param>
    public OssUploader(string accessKeyId, string accessKeySecret)
        => _client = new OssClient(Endpoint, accessKeyId, accessKeySecret);

    /// <summary>
    /// 上传文件。<paramref name="onProgress"/> 按 5% 粒度回调：百分比、已传字节、总字节。
    /// </summary>
    /// <param name="key">OSS 对象键（含前缀），例如 NWF/Project/{站点}/{时间戳}.zip。</param>
    /// <param name="filePath">本地待上传文件路径。</param>
    /// <param name="onProgress">进度回调，可为 null。</param>
    public void Upload(string key, string filePath, Action<double, long, long>? onProgress = null)
    {
        var checkpointDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PublishStudio_oss");
        Directory.CreateDirectory(checkpointDir);

        // 关键：自己开流交给 SDK，不要把文件路径丢给它去读。
        //
        // ResumableUploadObject 拿到文件路径时会自己 new FileStream，而它的分片管理线程在方法返回后
        // 仍然活着，句柄不释放 —— 后续要删除/移动这个 zip 必然失败（参考项目实测锁 60s 以上）。
        // 传我们自己开的流则句柄只由我们持有，返回后 Dispose 即释放。
        // FileShare.Delete 是兜底：万一释放晚一步，也不挡别人删。
        using var source = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16);

        var request = new UploadObjectRequest(Bucket, key, filePath)
        {
            UploadStream = source,
            PartSize = PartSizeBytes,
            CheckpointDir = checkpointDir,
        };

        if (onProgress is not null)
        {
            var lastStep = -1;

            request.StreamTransferProgress += (_, args) =>
            {
                if (!ShouldReport(args.PercentDone, ref lastStep)) return;
                onProgress(args.PercentDone, args.TransferredBytes, args.TotalBytes);
            };
        }

        _client.ResumableUploadObject(request);
    }

    /// <summary>
    /// 进度归并闸门：跨过新的 5% 档位才返回 true。
    /// 分片并行上传，回调可能来自多个线程，所以用 Interlocked 做闸门。
    /// </summary>
    /// <param name="percentDone">SDK 报的当前完成百分比（0~100）。</param>
    /// <param name="lastStep">上一次已上报的档位，跨线程共享，会被本次调用更新。</param>
    /// <returns>本次跨进了新的 5% 档位则为 true（该上报），否则 false（丢弃这次回调）。</returns>
    private static bool ShouldReport(double percentDone, ref int lastStep)
    {
        var step = (int)(percentDone / ProgressStepPercent);
        return Interlocked.Exchange(ref lastStep, step) != step;
    }
}
