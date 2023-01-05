using System.Collections.Concurrent;
using System.Diagnostics;

using ShellProgressBar;

namespace BDSM.Lib;
internal static class DownloadProgress
{
	public const int UPDATE_INTERVAL_MILLISECONDS = 100;
	public static ProgressBar TotalProgressBar = null!;
	internal static readonly ProgressBarOptions DefaultTotalProgressBarOptions = new()
	{
		CollapseWhenFinished = true,
		ShowEstimatedDuration = true,
		DisplayTimeInRealTime = false,
		EnableTaskBarProgress = true,
		ProgressCharacter = ' '
	};
	internal static readonly ProgressBarOptions DefaultChildProgressBarOptions = new()
	{
		CollapseWhenFinished = true,
		ShowEstimatedDuration = true,
		DisplayTimeInRealTime = false,
		ProgressBarOnBottom = true,
		ProgressCharacter = '─',
	};
	internal static int NumberOfFilesToDownload = 0;
	internal static int TotalNumberOfFilesToDownload = 0;
	internal static long TotalBytesToDownload = 0;
	internal static string TotalBytesToDownloadString => UtilityFunctions.FormatBytes(TotalBytesToDownload);
	internal static long TotalBytesDownloaded = 0;
	internal static string TotalBytesDownloadedString => UtilityFunctions.FormatBytes(TotalBytesDownloaded);
	internal static double TotalCurrentSpeed = 0;
	internal static string TotalCurrentSpeedString => UtilityFunctions.FormatBytes(TotalCurrentSpeed) + "/s";
	internal static readonly ConcurrentDictionary<string, FileDownloadProgressInformation> FileDownloadsInformation = new();
	internal static readonly Stopwatch DownloadSpeedStopwatch = new();
	internal static readonly Stopwatch ProgressUpdateStopwatch = Stopwatch.StartNew();
	internal static double TotalDownloadSpeed => DownloadSpeedStopwatch.Elapsed.TotalSeconds != 0 ? TotalBytesDownloaded / DownloadSpeedStopwatch.Elapsed.TotalSeconds : 0;
	internal static string TotalDownloadSpeedString => UtilityFunctions.FormatBytes(TotalDownloadSpeed) + "/s";
	public static TimeSpan ETA => new(0, 0, 0, 0, (int)Math.Round(TotalBytesToDownload / TotalDownloadSpeed * 1000, 0));
	private static bool TrackingTotalCurrentSpeed = false;

	public static void TrackTotalCurrentSpeed()
	{
		if (TrackingTotalCurrentSpeed) return;
		_ = Task.Run(() =>
		{
			while (NumberOfFilesToDownload > 0)
			{
				long previous_bytes_downloaded = TotalBytesDownloaded;
				Thread.Sleep(UPDATE_INTERVAL_MILLISECONDS);
				TotalCurrentSpeed = Math.Round((double)((TotalBytesDownloaded - previous_bytes_downloaded) * (1000 / UPDATE_INTERVAL_MILLISECONDS)), 2);
			}
			TotalCurrentSpeed = 0;
		});
		TrackingTotalCurrentSpeed = true;
	}

	public static void ReportProgress(ChunkDownloadProgressInformation progressinfo, string filepath)
	{
		lock (FileDownloadsInformation)
		{
			FileDownloadProgressInformation file_download_progress = FileDownloadsInformation[filepath];
			file_download_progress.Initialize();
			file_download_progress.TotalBytesDownloaded += progressinfo.BytesDownloaded;

			if (file_download_progress.TotalBytesDownloaded == file_download_progress.TotalFileSize)
			{
				Debug.Assert(NumberOfFilesToDownload > 0);
				_ = Interlocked.Decrement(ref NumberOfFilesToDownload);
				lock (TotalProgressBar)
				{
					file_download_progress.FileProgressBar.Dispose();
					file_download_progress.Complete(true);
				}
			}
			else
			{
				if (file_download_progress.ProgressUpdateStopwatch.ElapsedMilliseconds > UPDATE_INTERVAL_MILLISECONDS)
				{
					string file_progress_message = $"{Path.GetFileName(filepath)} | {file_download_progress.TotalBytesDownloadedString} / {file_download_progress.TotalFileSizeString} (Current speed: {file_download_progress.CurrentSpeedString})";
					file_download_progress.ProgressUpdateStopwatch.Restart();
					file_download_progress.FileProgressBar.Tick((int)(file_download_progress.TotalBytesDownloaded / 1024), file_download_progress.ETA, file_progress_message);
				}
			}
			FileDownloadsInformation[filepath] = file_download_progress;
		}
		_ = Interlocked.Add(ref TotalBytesDownloaded, progressinfo.BytesDownloaded);

		int downloads_finished = TotalNumberOfFilesToDownload - NumberOfFilesToDownload;
		int downloads_in_progress = FileDownloadsInformation.Count(info => info.Value.IsInitialized && (info.Value.TotalBytesDownloaded < info.Value.TotalFileSize));
		int downloads_in_queue = FileDownloadsInformation.Count(info => !info.Value.IsInitialized);

		if (ProgressUpdateStopwatch.ElapsedMilliseconds > UPDATE_INTERVAL_MILLISECONDS)
		{
			lock (TotalProgressBar)
			{
				string total_progress_message = $"Downloading files ({downloads_finished} done / {downloads_in_progress} in progress / {downloads_in_queue} remaining): " +
					$"{TotalBytesDownloadedString} / {TotalBytesToDownloadString} " +
					$"(Current speed: {TotalCurrentSpeedString}) " +
					$"(Average speed: {UtilityFunctions.FormatBytes(TotalBytesDownloaded / DownloadSpeedStopwatch.Elapsed.TotalSeconds)}/s)";
				TotalProgressBar.Tick((int)(TotalBytesDownloaded / 1024), ETA, total_progress_message);
				ProgressUpdateStopwatch.Restart();
			}
		}
	}
}
