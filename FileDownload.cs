using System.Collections.Immutable;
using System.Diagnostics;

using ShellProgressBar;

namespace BDSM;
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
	public string BytesTransferredString => UtilityFunctions.FormatBytes(BytesDownloaded);
	public required TimeSpan TimeElapsed { get; init; }
	public required long TotalChunkSize { get; init; }
	public double CurrentSpeed => BytesDownloaded / TimeElapsed.TotalSeconds;
	public string CurrentSpeedString => UtilityFunctions.FormatBytes(Math.Round(CurrentSpeed, 2)) + "/s";
}
public record FileDownloadProgressInformation
{
	public required string FilePath { get; init; }
	public long TotalBytesDownloaded { get; set; } = 0;
	public string TotalBytesDownloadedString => UtilityFunctions.FormatBytes(TotalBytesDownloaded);
	private Stopwatch TotalTimeStopwatch { get; set; } = new();
	public TimeSpan TotalTimeElapsed => TotalTimeStopwatch.Elapsed;
	public long CurrentBytesDownloaded { get; set; } = 0;
	public TimeSpan CurrentTimeElapsed { get; set; } = new TimeSpan();
	public double CurrentSpeed { get; private set; }
	public string CurrentSpeedString => UtilityFunctions.FormatBytes(CurrentSpeed) + "/s";
	public Stopwatch ProgressUpdateStopwatch { get; private set; } = new();
	public long PreviousBytesDownloaded { get; private set; } = 0;
	public required long TotalFileSize { get; init; }
	public string TotalFileSizeString => UtilityFunctions.FormatBytes(TotalFileSize);
	public ChildProgressBar FileProgressBar { get; private set; } = null!;
	public double AverageSpeed => TotalTimeElapsed.TotalSeconds != 0 ? TotalBytesDownloaded / TotalTimeElapsed.TotalSeconds : 0;
	public string AverageSpeedString => UtilityFunctions.FormatBytes(Math.Round(AverageSpeed, 2)) + "/s";
	public bool IsInitialized { get; private set; } = false;
	public bool IsComplete { get; private set; } = false;

	private void TrackCurrentSpeed()
	{
		while (TotalBytesDownloaded < TotalFileSize)
		{
			PreviousBytesDownloaded = TotalBytesDownloaded;
			Thread.Sleep(1000);
			CurrentSpeed = Math.Round((double)(TotalBytesDownloaded - PreviousBytesDownloaded), 2);
		}
		CurrentSpeed = 0;
	}
	public void Initialize()
	{
		if (IsInitialized) return;
		TotalTimeStopwatch.Start();
		FileProgressBar = DownloadProgress.TotalProgressBar.Spawn((int)(TotalFileSize / 1024), $"{FilePath} | Awaiting download", DownloadProgress.DefaultChildProgressBarOptions);
		ProgressUpdateStopwatch.Start();
		_ = Task.Run(TrackCurrentSpeed);
		IsInitialized = true;
	}
	public void Complete()
	{
		FileInfo downloaded_file = new(FilePath);
		Debug.Assert(downloaded_file.Length == TotalFileSize);
		TotalTimeStopwatch.Stop();
		ProgressUpdateStopwatch.Stop();
		FileProgressBar.Dispose();
		IsComplete = true;
	}
}