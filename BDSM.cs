using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;

using FluentFTP;

using NLog;

using YamlDotNet.Serialization;

using static BDSM.FTPFunctions;
using static BDSM.DownloadProgress;
using static BDSM.UtilityFunctions;
using static BDSM.Exceptions;

namespace BDSM;

public static partial class BDSM
{
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetConsoleOutputCP(uint wCodePageID);
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetConsoleCP(uint wCodePageID);

	public static async Task<int> Main()
	{
		const string SKIP_SCAN_CONFIG_FILENAME = "SkipScan.yaml";
		ILogger logger = LogManager.GetCurrentClassLogger();
		_ = SetConsoleOutputCP(65001);
		_ = SetConsoleCP(65001);

		Configuration.UserConfiguration UserConfig = await Configuration.GetUserConfigurationAsync();
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
		SkipScanConfiguration _skip_config = File.Exists(SKIP_SCAN_CONFIG_FILENAME)
			? new Deserializer().Deserialize<SkipScanConfiguration>(Configuration.ReadConfigAndDispose(SKIP_SCAN_CONFIG_FILENAME))
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
		if (SkipScan)
			BaseDirectoriesToScan = ImmutableHashSet<PathMapping>.Empty;
		else
		{
			try { BaseDirectoriesToScan = UserConfig.BasePathMappings; }
			catch (FormatException)
			{
				logger.Error("Your configuration file is malformed. Please reference the example and read the documentation.");
				PromptBeforeExit();
				return 1;
			}
			foreach (PathMapping mapping in BaseDirectoriesToScan)
				DirectoriesToScan.Add(mapping);
		}

		Stopwatch OpTimer = new();
		List<Task<(ConcurrentBag<PathMapping>, ImmutableList<string>)>> scan_tasks = new();
		List<Task<(ConcurrentBag<PathMapping>, ImmutableList<string>)>> finished_scan_tasks = new();

		logger.Info("Scanning the server.");
		OpTimer.Start();
		List<string> bad_entries = SanityCheckBaseDirectories(BaseDirectoriesToScan, UserConfig.ConnectionInfo);
		if (bad_entries.Count > 0)
		{
			foreach (string bad_entry in bad_entries)
				logger.Error($"'{bad_entry}' does not exist on the server. Are your remote paths configured correctly?");
			OpTimer.Stop();
			if (UserConfig.PromptToContinue)
				PromptBeforeExit();
			return 1;
		}

		List<AggregateException> scan_exceptions = new();
		using CancellationTokenSource scan_cts = new();
		CancellationToken scan_ct = scan_cts.Token;
		for (int i = 0; i < UserConfig.ConnectionInfo.MaxConnections; i++)
			scan_tasks.Add(Task.Run(() => GetFilesOnServer(ref DirectoriesToScan, UserConfig.ConnectionInfo, scan_ct), scan_ct));
		try
		{
			while (scan_tasks.Count != 0)
			{
				int completed_task_idx = Task.WaitAny(scan_tasks.ToArray());
				Task<(ConcurrentBag<PathMapping>, ImmutableList<string>)> completed_task = scan_tasks[completed_task_idx];
				switch (completed_task.Status)
				{
					case TaskStatus.RanToCompletion or TaskStatus.Canceled:
						break;
					case TaskStatus.Faulted:
						scan_cts.Cancel();
						scan_exceptions.Add(completed_task.Exception!);
						break;
					default:
						scan_exceptions.Add(new AggregateException(new BDSMInternalFaultException("Internal error while processing scan task exceptions.")));
						break;
				}
				finished_scan_tasks.Add(completed_task);
				scan_tasks.RemoveAt(completed_task_idx);
			}
			if (scan_exceptions.Count > 0)
				throw new AggregateException(scan_exceptions);
		}
		catch (AggregateException ex)
		{
			logger.Error("Could not scan the server. Failed scan tasks had the following errors:");
			foreach (Exception inner_ex in ex.Flatten().InnerExceptions)
			{
				logger.Error(inner_ex.Message);
				logger.Error(inner_ex.StackTrace);
			}
			if (UserConfig.PromptToContinue)
				PromptUserToContinue();
			return 1;
		}
		bool missed_some_files = false;
		ConcurrentBag<string> missed_files = new();
		foreach ((ConcurrentBag<PathMapping> pathmaps, ImmutableList<string> missed_ftp_entries) in finished_scan_tasks.Select(task => task.Result))
		{
			foreach (PathMapping pathmap in pathmaps)
				FilesOnServer.TryAdd(pathmap.LocalFullPathLower, pathmap);
			missed_some_files = !missed_ftp_entries.IsEmpty;
			foreach (string missed_ftp_entry in missed_ftp_entries)
				missed_files.Add(missed_ftp_entry);
		}
		foreach (Task<(ConcurrentBag<PathMapping>, ImmutableList<string>)> finished_scan_task in finished_scan_tasks)
			finished_scan_task.Dispose();
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
			{
				logger.Info($"{Pluralize(FilesToDelete.Count, " file")} marked for deletion.");
				PromptUserToContinue();
			}
			foreach (FileInfo pm in FilesToDelete)
			{
				try { File.Delete(pm.FullName); }
				catch (Exception ex)
				{
					failed_to_delete.Add(pm);
					failed_deletions.Add(ex);
					logger.Warn(ex.Message);
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
}