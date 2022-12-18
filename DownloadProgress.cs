using System.Collections.Concurrent;
using System.Diagnostics;

using ShellProgressBar;

namespace BDSM;
internal static class DownloadProgress
{
	internal static ProgressBar TotalProgressBar = null!;
	internal static readonly ProgressBarOptions DefaultTotalProgressBarOptions = new()
	{
		CollapseWhenFinished = true,
		DisplayTimeInRealTime = true,
		EnableTaskBarProgress = true,
		ProgressCharacter = ' '
	};
	internal static readonly ProgressBarOptions DefaultChildProgressBarOptions = new()
	{
		CollapseWhenFinished = true,
		ShowEstimatedDuration = true,
		DisplayTimeInRealTime = true,
		ProgressBarOnBottom = true,
		ProgressCharacter = '─',
	};
	internal static int NumberOfFilesToDownload = 0;
	internal static int TotalNumberOfFilesToDownload = 0;
	internal static long TotalBytesToDownload = 0;
	internal static long TotalBytesDownloaded = 0;
	internal static double TotalCurrentSpeed = 0;
	internal static string TotalCurrentSpeedString => UtilityFunctions.FormatBytes(TotalCurrentSpeed) + "/s";
	internal static readonly ConcurrentDictionary<string, FileDownloadProgressInformation> FileDownloadsInformation = new();
	internal static readonly Stopwatch DownloadSpeedStopwatch = new();
	internal static double TotalDownloadSpeed => DownloadSpeedStopwatch.Elapsed.TotalSeconds == 0 ? TotalBytesDownloaded / DownloadSpeedStopwatch.Elapsed.TotalSeconds : 0;
	internal static string TotalDownloadSpeedString => UtilityFunctions.FormatBytes(TotalDownloadSpeed) + "/s";

	internal static async Task TrackCurrentSpeed(FileDownloadProgressInformation file_download_progress)
	{
		while (file_download_progress.TotalBytesDownloaded < file_download_progress.TotalFileSize)
		{
			file_download_progress.PreviousBytesDownloaded = file_download_progress.TotalBytesDownloaded;
			await Task.Delay(2000);
			file_download_progress.CurrentSpeed = Math.Round((double)((file_download_progress.TotalBytesDownloaded - file_download_progress.PreviousBytesDownloaded) / 2), 2);
		}
		file_download_progress.CurrentSpeed = 0;
	}
	internal static async Task TrackTotalCurrentSpeed()
	{
		while (NumberOfFilesToDownload > 0)
		{
			long previous_bytes_downloaded = TotalBytesDownloaded;
			await Task.Delay(2000);
			TotalCurrentSpeed = Math.Round((double)((TotalBytesDownloaded - previous_bytes_downloaded) / 2), 2);
		}
		TotalCurrentSpeed = 0;
	}
}
