using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;

using FluentFTP;

using NLog;

using ShellProgressBar;

using YamlDotNet.Serialization;

using static BDSM.Configuration;
using static BDSM.FTPFunctions;
using static BDSM.UtilityFunctions;

namespace BDSM;

public static partial class BDSM
{
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetConsoleOutputCP(uint wCodePageID);
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetConsoleCP(uint wCodePageID);

	private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
	internal static ProgressBar TotalProgressBar = null!;
	public static readonly ProgressBarOptions DefaultTotalProgressBarOptions = new()
	{
		CollapseWhenFinished = true,
		DisplayTimeInRealTime = true,
		EnableTaskBarProgress = true,
		ProgressCharacter = ' '
	};
	public static readonly ProgressBarOptions DefaultChildProgressBarOptions = new()
	{
		CollapseWhenFinished = true,
		ShowEstimatedDuration = true,
		DisplayTimeInRealTime = true,
		ProgressBarOnBottom = true,
		ProgressCharacter = '─',
	};
	private static int NumberOfFilesToDownload = 0;
	private static int TotalNumberOfFilesToDownload = 0;
	private static long TotalBytesToDownload = 0;
	private static long TotalBytesDownloaded = 0;
	private static double TotalCurrentSpeed = 0;
	private static string TotalCurrentSpeedString => FormatBytes(TotalCurrentSpeed) + "/s";
	private static readonly ConcurrentDictionary<string, FileDownloadProgressInformation> FileDownloadsInformation = new();
	private static readonly Stopwatch DownloadSpeedStopwatch = new();
	public static double TotalDownloadSpeed => DownloadSpeedStopwatch.Elapsed.TotalSeconds == 0 ? TotalBytesDownloaded / DownloadSpeedStopwatch.Elapsed.TotalSeconds : 0;
	public static string TotalDownloadSpeedString => FormatBytes(TotalDownloadSpeed) + "/s";

	public static async Task<int> Main()
	{
		_ = SetConsoleOutputCP(65001);
		_ = SetConsoleCP(65001);

		Exception? _config_ex = null;
		UserConfiguration? _config = null;

		const string UserConfigurationFilename = "UserConfiguration.yaml";
		const string SkipScanConfigurationFilename = "SkipScan.yaml";

		static string ReadConfigAndDispose(string filename) { using StreamReader reader = File.OpenText(filename); return reader.ReadToEnd(); }
		try { _config = new Deserializer().Deserialize<UserConfiguration>(ReadConfigAndDispose(UserConfigurationFilename)); }
		catch (TypeInitializationException ex)
		{
			if (ex.InnerException is FileNotFoundException)
				logger.Error("Your configuration file is missing. Please read the documentation and copy the example configuration to your own UserConfiguration.yaml.");
			else
				logger.Error("Your configuration file is malformed. Please reference the example and read the documentation.");
			_config_ex = ex;
		}
		catch (Exception ex) { _config_ex = ex; }
		if (_config_ex is not null)
		{
			logger.Error(_config_ex.StackTrace);
			logger.Error(_config_ex.Message);
			logger.Error("Could not load configuration file. Aborting.");
			PromptBeforeExit();
			return 1;
		}
		UserConfiguration UserConfig = (UserConfiguration)_config!;
		logger.Info("Configuration loaded.");
		if (UserConfig.GamePath == @"X:\Your HoneySelect 2 DX folder here\")
		{
			logger.Error("Your mod directory has not been set.");
			PromptBeforeExit();
			return 1;
		}

		ImmutableHashSet<PathMapping> BaseDirectoriesToScan;
		ConcurrentBag<PathMapping> DirectoriesToScan = new();
		ConcurrentDictionary<string, PathMapping> FilesOnServer = new();
		ConcurrentBag<FileDownload> FilesToDownload = new();
		ConcurrentBag<FileInfo> FilesToDelete = new();

		bool SkipScan = false;
#if DEBUG
		SkipScanConfiguration _skip_config = File.Exists(SkipScanConfigurationFilename)
			? new Deserializer().Deserialize<SkipScanConfiguration>(ReadConfigAndDispose(SkipScanConfigurationFilename))
			: new() { SkipScan = false, FileMappings = Array.Empty<string>() };
		SkipScan = _skip_config.SkipScan;

		if (SkipScan)
		{
			using FtpClient _scanner = SetupFTPClient(UserConfig.ConnectionInfo);
			_scanner.Connect();
			foreach (PathMapping pathmap in GetPathMappingsFromSkipScanConfig(_skip_config, UserConfig))
			{
				FtpListItem _dl_file_info = _scanner.GetObjectInfo(pathmap.RemoteFullPath);
				FilesToDownload.Add(PathMappingToFileDownload(pathmap with { FileSize = _dl_file_info.Size }));
			}
			_scanner.Dispose();
		}
#endif
		if (!SkipScan)
		{
			try { BaseDirectoriesToScan = GetPathMappingsFromUserConfig(UserConfig).ToImmutableHashSet(); }
			catch (FormatException)
			{
				logger.Error("Your configuration file is malformed. Please reference the example and read the documentation.");
				PromptBeforeExit();
				return 1;
			}
			foreach (PathMapping mapping in BaseDirectoriesToScan)
				DirectoriesToScan.Add(mapping);
		}
		else
			BaseDirectoriesToScan = ImmutableHashSet.Create<PathMapping>();

		Stopwatch OpTimer = new();
		Task<(ConcurrentBag<PathMapping>, ImmutableList<string>)>[] scan_tasks = new Task<(ConcurrentBag<PathMapping>, ImmutableList<string>)>[UserConfig.ConnectionInfo.MaxConnections];

		logger.Info("Scanning the server.");
		OpTimer.Start();

		for (int i = 0; i < UserConfig.ConnectionInfo.MaxConnections; i++)
			scan_tasks[i] = Task.Run(() => GetFilesOnServer(ref DirectoriesToScan, UserConfig.ConnectionInfo));
		try { Task.WaitAll(scan_tasks); }
		catch (AggregateException ex)
		{
			logger.Error("Could not scan the server. Failed scan tasks had the following errors:");
			foreach (Exception exception in ex.InnerExceptions)
			{
				logger.Error(exception.Message);
				logger.Error(exception.StackTrace);
			}
			logger.Error("Please file a bug report and provide this information. https://github.com/RobotsOnDrugs/BDSM/issues");
			return 1;
		}
		bool missed_some_files = false;
		ConcurrentBag<string> missed_files = new();
		foreach ((ConcurrentBag<PathMapping> pathmaps, ImmutableList<string> missed_ftp_entries) in scan_tasks.Select(task => task.Result))
		{
			foreach (PathMapping pathmap in pathmaps)
				FilesOnServer.TryAdd(pathmap.LocalFullPathLower, pathmap);
			missed_some_files = !missed_ftp_entries.IsEmpty;
			foreach (string missed_ftp_entry in missed_ftp_entries)
				missed_files.Add(missed_ftp_entry);
		}
		if (FilesOnServer.IsEmpty && !DirectoriesToScan.IsEmpty)
		{
			logger.Error("No files could be scanned due to network or other errors.");
			return 1;
		}
		if (missed_some_files)
		{
			logger.Debug("Could not scan all files on the server. The following files could not be compared:");
			foreach (string missed_file in missed_files)
				logger.Debug(missed_file);
			logger.Warn("Some files could not be scanned due to network errors.");
		}
		OpTimer.Stop();
		logger.Info($"Scanned {FilesOnServer.Count} files in {OpTimer.ElapsedMilliseconds}ms.");

		logger.Info("Comparing files.");
		OpTimer.Restart();
		bool local_access_successful = true;
		ConcurrentQueue<Exception> local_access_exceptions = new();
		foreach (PathMapping pm in BaseDirectoriesToScan)
		{
			DirectoryInfo base_dir_di = new(pm.LocalFullPath);
			IEnumerable<FileInfo> file_enumeration = base_dir_di.EnumerateFiles("*", new EnumerationOptions() { RecurseSubdirectories = true, IgnoreInaccessible = true });
			try
			{
				foreach (FileInfo fileondiskinfo in file_enumeration)
				{
					if (FilesOnServer.TryGetValue(fileondiskinfo.FullName.ToLower(), out PathMapping match_pm) && match_pm.FileSize == fileondiskinfo.Length)
						_ = FilesOnServer.TryRemove(match_pm.LocalFullPathLower, out _);
					else if (pm.DeleteClientFiles)
						FilesToDelete.Add(fileondiskinfo);
				}
			}
			catch (Exception ex)
			{
				local_access_successful = false;
				logger.Error($"Could not access {pm.LocalFullPath}. Ensure that you have the correct path specified in your configuration and that you have permission to access it.");
				local_access_exceptions.Enqueue(ex);
				continue;
			}
			if (!local_access_successful)
				throw new AggregateException(local_access_exceptions);
		}
		foreach (KeyValuePair<string, PathMapping> pm_kvp in FilesOnServer)
			FilesToDownload.Add(PathMappingToFileDownload(pm_kvp.Value));
		OpTimer.Stop();
		logger.Info($"Comparison took {OpTimer.ElapsedMilliseconds}ms.");
		logger.Info($"{Pluralize(FilesToDownload.Count, " file")} to download and {Pluralize(FilesToDelete.Count, " file")} to delete.");

		if (!FilesToDelete.IsEmpty)
		{
			ConcurrentBag<FileInfo> failed_to_delete = new();
			List<Exception> failed_deletions = new();
			logger.Info("Will delete files:");
			foreach (FileInfo pm in FilesToDelete)
				logger.Info($"{pm.FullName}");
			if (UserConfig.PromptToContinue)
				PromptUserToContinue();
			foreach (FileInfo pm in FilesToDelete)
			{
				try { File.Delete(pm.FullName); }
				catch (Exception ex)
				{
					failed_to_delete.Add(pm);
					failed_deletions.Add(ex);
					logger.Warn(ex.Message);
					continue;
				}
			}
			logger.Info($"{Pluralize(FilesToDelete.Count - failed_to_delete.Count, " file")} deleted.");
			Debug.Assert(failed_deletions.Count == failed_to_delete.Count);
			if (failed_deletions.Count > 0)
			{
				logger.Error($"{Pluralize(failed_to_delete.Count, " file")} could not be deleted.");
				throw new AggregateException(failed_deletions);
			}
		}
		if (!FilesToDownload.IsEmpty)
		{
			TotalNumberOfFilesToDownload = FilesToDownload.Count;
			NumberOfFilesToDownload = FilesToDownload.Count;
			logger.Info("Downloading files.");
			foreach (FileDownload file_download in FilesToDownload)
				TotalBytesToDownload += file_download.TotalFileSize;
			logger.Info($"{Pluralize(NumberOfFilesToDownload, " file")} ({FormatBytes(TotalBytesToDownload)}) to download.");
			if (UserConfig.PromptToContinue)
				PromptUserToContinue();

			OpTimer.Restart();
			_ = TrackTotalCurrentSpeed();
			TotalProgressBar = new((int)(TotalBytesToDownload / 1024), "Downloading files:", DefaultTotalProgressBarOptions);
			ConcurrentQueue<DownloadChunk> chunks = new();
			while (FilesToDownload.TryTake(out FileDownload current_file_download))
			{
				FileDownloadsInformation[current_file_download.LocalPath] = new FileDownloadProgressInformation()
				{
					FilePath = current_file_download.LocalPath,
					TotalFileSize = current_file_download.TotalFileSize
				};
				foreach (DownloadChunk chunk in current_file_download.DownloadChunks)
					chunks.Enqueue(chunk);
			}
			int download_task_count = (UserConfig.ConnectionInfo.MaxConnections < chunks.Count) ? UserConfig.ConnectionInfo.MaxConnections : chunks.Count;
			Task[] download_tasks = new Task[download_task_count];
			DownloadSpeedStopwatch.Start();
			for (int i = 0; i < download_tasks.Length; i++)
				download_tasks[i] = Task.Factory.StartNew(() => DownloadFileChunk(UserConfig.ConnectionInfo, in chunks, ReportProgress), TaskCreationOptions.LongRunning);
			Task.WaitAll(download_tasks);
			DownloadSpeedStopwatch.Stop();
			foreach (KeyValuePair<string, FileDownloadProgressInformation> progress_info in FileDownloadsInformation)
			{
				progress_info.Value.FileProgressBar.Message = "";
				progress_info.Value.FileProgressBar.Dispose();
			}
			TotalProgressBar.Message = "";
			TotalProgressBar.Dispose();

			OpTimer.Stop();
			logger.Info($"Downloaded {FormatBytes(TotalBytesToDownload)} in {(OpTimer.Elapsed.Minutes > 0 ? $"{OpTimer.Elapsed.Minutes} minutes and " : "")}{Pluralize(OpTimer.Elapsed.Seconds, " second")}.");
			logger.Info($"Average speed: {FormatBytes(TotalBytesToDownload / OpTimer.Elapsed.TotalSeconds)}/s");
		}
		logger.Info("Finished updating.");
		if (UserConfig.PromptToContinue)
			PromptUserToContinue();
		return 0;
	}
	public static void ReportProgress(ChunkDownloadProgressInformation progressinfo, string filepath)
	{
		lock (FileDownloadsInformation)
		{
			FileDownloadsInformation[filepath].Initialize();
			FileDownloadsInformation[filepath].TotalBytesDownloaded += progressinfo.BytesDownloaded;
			string file_progress_message = $"{Path.GetFileName(filepath)} | {FileDownloadsInformation[filepath].TotalBytesDownloadedString} / {FileDownloadsInformation[filepath].TotalFileSizeString} (Current speed: {FileDownloadsInformation[filepath].CurrentSpeedString})";
			if (FileDownloadsInformation[filepath].TotalBytesDownloaded == FileDownloadsInformation[filepath].TotalFileSize)
			{
				Debug.Assert(NumberOfFilesToDownload > 0);
				_ = Interlocked.Decrement(ref NumberOfFilesToDownload);
				file_progress_message = "";
				lock (TotalProgressBar)
				{
					FileDownloadsInformation[filepath].FileProgressBar.Dispose();
					FileDownloadsInformation[filepath].Complete();
				}
			}
			else
			{
				FileDownloadsInformation[filepath].FileProgressBar.Message = file_progress_message;
				FileDownloadsInformation[filepath].FileProgressBar.Tick((int)(FileDownloadsInformation[filepath].TotalBytesDownloaded / 1024), file_progress_message);
			}
		}
		_ = Interlocked.Add(ref TotalBytesDownloaded, progressinfo.BytesDownloaded);
		int downloads_finished = TotalNumberOfFilesToDownload - NumberOfFilesToDownload;
		int downloads_in_progress = FileDownloadsInformation.Count(info => info.Value.IsInitialized && (info.Value.TotalBytesDownloaded < info.Value.TotalFileSize));
		int downloads_in_queue = FileDownloadsInformation.Count(info => !info.Value.IsInitialized);
		lock (TotalProgressBar)
		{
			TotalProgressBar.Tick((int)(TotalBytesDownloaded / 1024));
			TotalProgressBar.Message = $"Downloading files ({downloads_finished} done / {downloads_in_progress} in progress / {downloads_in_queue} remaining): {FormatBytes(TotalBytesDownloaded)} / {FormatBytes(TotalBytesToDownload)} (Current speed: {TotalCurrentSpeedString}) (Average speed: {FormatBytes(TotalBytesDownloaded / DownloadSpeedStopwatch.Elapsed.TotalSeconds)}/s)";
		}
	}
	public static async Task TrackCurrentSpeed(FileDownloadProgressInformation file_download_progress)
	{
		while (file_download_progress.TotalBytesDownloaded < file_download_progress.TotalFileSize)
		{
			file_download_progress.PreviousBytesDownloaded = file_download_progress.TotalBytesDownloaded;
			await Task.Delay(2000);
			file_download_progress.CurrentSpeed = Math.Round((double)((file_download_progress.TotalBytesDownloaded - file_download_progress.PreviousBytesDownloaded) / 2), 2);
		}
		file_download_progress.CurrentSpeed = 0;
	}
	public static async Task TrackTotalCurrentSpeed()
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