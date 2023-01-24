using System.Collections.Immutable;

namespace BDSM.Lib;
public readonly record struct FileDownload
{
	public required ImmutableArray<DownloadChunk> DownloadChunks { get; init; }
	public required string LocalPath { get; init; }
	public required string RemotePath { get; init; }
	public required long TotalFileSize { get; init; }
	public required int ChunkSize { get; init; }
	public required int NumberOfChunks { get; init; }
	public string FileName => Path.GetFileName(LocalPath);
}

public readonly record struct DownloadChunk
{
	public required string LocalPath { get; init; }
	public required string RemotePath { get; init; }
	public required long Offset { get; init; }
	public required int Length { get; init; }
	public string FileName => Path.GetFileName(LocalPath);
}
public readonly record struct ChunkDownloadProgressInformation
{
	public required long BytesDownloaded { get; init; }
	public string BytesTransferredString => BytesDownloaded.FormatBytes();
	public required TimeSpan TimeElapsed { get; init; }
	public required long TotalChunkSize { get; init; }
	public double CurrentSpeed => BytesDownloaded / TimeElapsed.TotalSeconds;
	public string CurrentSpeedString => Math.Round(CurrentSpeed, 2).FormatBytes() + "/s";
}
public enum DownloadStatus
{
	Success,
	Queued,
	InProgress,
	Failure,
	Canceled,
	Undefined
}