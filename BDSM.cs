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
using static BDSM.LoggingConfiguration;

namespace BDSM;

public static partial class BDSM
{
	private static List<Task<T>> ProcessTasks<T>(List<Task<T>> tasks, CancellationTokenSource cts)
	{
		bool canceled = false;
		void CtrlCHandler(object sender, ConsoleCancelEventArgs args) { cts.Cancel(); canceled = true; args.Cancel = true; }
		Console.CancelKeyPress += CtrlCHandler!;
		List<Task<T>> finished_tasks = new();
		List<AggregateException> exceptions = new();
		while (tasks.Count != 0)
		{
			int completed_task_idx = Task.WaitAny(tasks.ToArray());
			Task<T> completed_task = tasks[completed_task_idx];
			switch (completed_task.Status)
			{
				case TaskStatus.RanToCompletion or TaskStatus.Canceled:
					break;
				case TaskStatus.Faulted:
					cts.Cancel();
					exceptions.Add(completed_task.Exception!);
					break;
				default:
					exceptions.Add(new AggregateException(new BDSMInternalFaultException("Internal error while processing task exceptions.")));
					break;
			}
			finished_tasks.Add(completed_task);
			tasks.RemoveAt(completed_task_idx);
		}
		Console.CancelKeyPress -= CtrlCHandler!;
		return canceled
			? throw new OperationCanceledException()
			: exceptions.Count == 0 ? finished_tasks : throw new AggregateException(exceptions);
	}
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetConsoleOutputCP(uint wCodePageID);
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetConsoleCP(uint wCodePageID);

	public static async Task<int> Main()
	{
		ILogger logger = LogManager.GetCurrentClassLogger();
		LogManager.Configuration = LoadCustomConfiguration(out bool is_custom_logger);

		if (is_custom_logger)
			LogMarkupText(logger, LogLevel.Info, "Custom logging configuration loaded [green]successfully[/].");

		_ = SetConsoleOutputCP(65001);
		_ = SetConsoleCP(65001);

		Configuration.UserConfiguration UserConfig = await Configuration.GetUserConfigurationAsync();
		if (UserConfig.GamePath == @"X:\Your HoneySelect 2 DX folder here\")
		{
			LogMarkupText(logger, LogLevel.Error,"[maroon]Your mod directory has not been set.[/]");
			PromptBeforeExit();
			return 1;
		}
		ImmutableHashSet<PathMapping> BaseDirectoriesToScan;
		ConcurrentBag<PathMapping> DirectoriesToScan = new();
		ConcurrentDictionary<string, PathMapping> FilesOnServer = new();
		ConcurrentBag<FileDownload> FilesToDownload = new();
		ConcurrentBag<FileInfo> FilesToDelete = new();

		const string SKIP_SCAN_CONFIG_FILENAME = "SkipScan.yaml";
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
				LogMarkupText(logger, LogLevel.Error,"Your configuration file is malformed. Please reference the example and read the documentation.");
				PromptBeforeExit();
				return 1;
			}
			foreach (PathMapping mapping in BaseDirectoriesToScan)
				DirectoriesToScan.Add(mapping);
		}

		Stopwatch OpTimer = new();

		LogMarkupText(logger, LogLevel.Info, "Scanning the server.");
		OpTimer.Start();
		List<string> bad_entries = SanityCheckBaseDirectories(BaseDirectoriesToScan, UserConfig.ConnectionInfo);
		if (bad_entries.Count > 0)
		{
			foreach (string bad_entry in bad_entries)
				LogMarkupText(logger, LogLevel.Error,$"[red][bold]'{bad_entry}'[/] does not exist on the server. Are your remote paths configured correctly?[/]");
			OpTimer.Stop();
			if (UserConfig.PromptToContinue)
				PromptBeforeExit();
			return 1;
		}

		List<Task<(ConcurrentBag<PathMapping>, ImmutableList<string>)>> scan_tasks = new();
		List<Task<(ConcurrentBag<PathMapping>, ImmutableList<string>)>> finished_scan_tasks = new();
		using CancellationTokenSource scan_cts = new();
		CancellationToken scan_ct = scan_cts.Token;
		for (int i = 0; i < UserConfig.ConnectionInfo.MaxConnections; i++)
			scan_tasks.Add(Task.Run(() => GetFilesOnServer(ref DirectoriesToScan, UserConfig.ConnectionInfo, scan_ct), scan_ct));
		try { finished_scan_tasks = ProcessTasks(scan_tasks, scan_cts); }
		catch (OperationCanceledException)
		{
			LogMarkupText(logger, LogLevel.Fatal,"[gold3_1]Scanning was canceled.[/]");
			if (UserConfig.PromptToContinue)
				PromptBeforeExit();
			return 1;
		}
		catch (AggregateException ex)
		{
			LogMarkupText(logger, LogLevel.Fatal,"[maroon]Could not scan the server. Failed scan tasks had the following errors:[/]");
			foreach (Exception inner_ex in ex.Flatten().InnerExceptions)
				LogException(logger, ex);
			if (UserConfig.PromptToContinue)
				PromptBeforeExit();
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
			LogMarkupText(logger, LogLevel.Error,"[maroon]No files could be scanned due to network or other errors.[/]");
			return 1;
		}
		if (missed_some_files)
		{
			logger.Debug("Could not scan all files on the server. The following files could not be compared:");
			foreach (string missed_file in missed_files)
				logger.Debug(missed_file);
			LogMarkupText(logger, LogLevel.Warn,"Some files could not be scanned due to network errors.");
		}
		OpTimer.Stop();
		LogMarkupText(logger, LogLevel.Info,$"Scanned [orchid2]{FilesOnServer.Count}[/] files in [orchid2]{OpTimer.ElapsedMilliseconds}ms[/].");

		LogMarkupText(logger, LogLevel.Info,"Comparing files.");
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
				LogMarkupText(logger, LogLevel.Error,$"[maroon][red1]Could not access {pm.LocalFullPath}[/]. Ensure that you have the correct path specified in your configuration and that you have permission to access it.[/]");
				local_access_exceptions.Enqueue(ex);
				continue;
			}
			if (!local_access_successful)
				throw new AggregateException(local_access_exceptions);
		}
		foreach (KeyValuePair<string, PathMapping> pm_kvp in FilesOnServer)
			FilesToDownload.Add(PathMappingToFileDownload(pm_kvp.Value));
		OpTimer.Stop();
		LogMarkupText(logger, LogLevel.Info,$"Comparison took [orchid2]{OpTimer.ElapsedMilliseconds}ms[/].");
		LogMarkupText(logger, LogLevel.Info,$"[orchid2]{Pluralize(FilesToDownload.Count, " file")}[/] to download and [orchid2]{Pluralize(FilesToDelete.Count, " file")}[/] to delete.");
		if (!FilesToDelete.IsEmpty)
		{
			ConcurrentBag<FileInfo> failed_to_delete = new();
			List<Exception> failed_deletions = new();
			LogMarkupText(logger, LogLevel.Info,"Will delete files:");
			foreach (FileInfo pm in FilesToDelete)
				LogMarkupText(logger, LogLevel.Info,$"[orangered1]{pm.FullName}[/]");
			if (UserConfig.PromptToContinue)
			{
				LogMarkupText(logger, LogLevel.Info,$"[orchid2]{Pluralize(FilesToDelete.Count, " file")}[/] marked for deletion.");
				PromptUserToContinue();
			}
			foreach (FileInfo pm in FilesToDelete)
			{
				try { File.Delete(pm.FullName); }
				catch (Exception ex)
				{
					failed_to_delete.Add(pm);
					failed_deletions.Add(ex);
					LogMarkupText(logger, LogLevel.Warn,$"[yellow3_1]{ex.Message}[/]");
				}
			}
			LogMarkupText(logger, LogLevel.Info,$"[orchid2]{Pluralize(FilesToDelete.Count - failed_to_delete.Count, " file")}[/] deleted.");
			Debug.Assert(failed_deletions.Count == failed_to_delete.Count);
			if (failed_deletions.Count > 0)
			{
				LogMarkupText(logger, LogLevel.Error,$"[red1]{Pluralize(failed_to_delete.Count, " file")}[/][maroon] could not be deleted.[/]");
				throw new AggregateException(failed_deletions);
			}
		}

		if (!FilesToDownload.IsEmpty)
		{
			TotalNumberOfFilesToDownload = FilesToDownload.Count;
			NumberOfFilesToDownload = FilesToDownload.Count;
			foreach (FileDownload file_download in FilesToDownload)
				TotalBytesToDownload += file_download.TotalFileSize;
			LogMarkupText(logger, LogLevel.Info,$"[orchid2]{Pluralize(NumberOfFilesToDownload, " file")}[/] ([orchid2]{FormatBytes(TotalBytesToDownload)}[/]) to download.");

			if (UserConfig.PromptToContinue)
				PromptUserToContinue();

			OpTimer.Restart();
			TrackTotalCurrentSpeed();
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
			List<Task<bool>> download_tasks = new();
			List<Task<bool>> finished_download_tasks = new();
			using CancellationTokenSource download_cts = new();
			CancellationToken download_ct = download_cts.Token;
			bool download_canceled = false;

			DownloadSpeedStopwatch.Start();
			for (int i = 0; i < UserConfig.ConnectionInfo.MaxConnections; i++)
				download_tasks.Add(Task.Run(() => { DownloadFileChunks(UserConfig.ConnectionInfo, in chunks, ReportProgress, download_ct); return true; }, download_ct));
			try
			{
				ProcessTasks(download_tasks, download_cts);
			}
			catch (OperationCanceledException) { download_canceled = true; }
			catch (AggregateException ex)
			{
				LogMarkupText(logger, LogLevel.Error,"[maroon]Could not download some files. Failed download tasks had the following errors:[/]");
				foreach (Exception inner_ex in ex.Flatten().InnerExceptions)
					LogException(logger, ex);
				if (UserConfig.PromptToContinue)
					PromptUserToContinue();
				return 1;
			}
			DownloadSpeedStopwatch.Stop();
			foreach (KeyValuePair<string, FileDownloadProgressInformation> progress_info_kvp in FileDownloadsInformation)
			{
				FileDownloadProgressInformation progress_info = progress_info_kvp.Value;
				if (progress_info.IsInitialized && !progress_info.IsComplete)
				{
					progress_info.Complete(false);
					try { File.Delete(progress_info.FilePath); }
					catch (Exception ex)
					{
						LogMarkupText(logger, LogLevel.Error,$"[maroon]Tried to delete incomplete file [red1]{progress_info.FilePath}[/] during cleanup but encountered an error:[/]");
						LogException(logger, ex);
					}
				}
			}
			TotalProgressBar.Message = "";
			TotalProgressBar.Dispose();

			OpTimer.Stop();
			int downloads_finished = TotalNumberOfFilesToDownload - NumberOfFilesToDownload;
			int downloads_in_progress = FileDownloadsInformation.Count(info => info.Value.IsInitialized && (info.Value.TotalBytesDownloaded < info.Value.TotalFileSize));
			long bytes_of_completed_files = FileDownloadsInformation.Where(info => info.Value.IsInitialized && (info.Value.TotalBytesDownloaded == info.Value.TotalFileSize)).Sum(info => info.Value.TotalBytesDownloaded);
			string canceled_files_message = "";
			if (download_canceled)
			{
				LogMarkupText(logger, LogLevel.Warn, $"Canceled download of [gold3_1]{Pluralize(downloads_in_progress, " file")}[/]" +
					$" ([orchid2]{FormatBytes(TotalBytesDownloaded - bytes_of_completed_files)}[/] wasted).");
				canceled_files_message = $" ([gold3_1]{downloads_in_progress} canceled[/])";
			}
			LogMarkupText(logger, LogLevel.Info, $"Completed download of [orchid2]{Pluralize(downloads_finished, " file")}[/] ([orchid2]{FormatBytes(bytes_of_completed_files)}[/])" +
				$" in [orchid2]{(OpTimer.Elapsed.Minutes > 0 ? $"{OpTimer.Elapsed.Minutes} minutes and " : "")}" +
				$"{Pluralize(OpTimer.Elapsed.Seconds, " second")}[/].");
			LogMarkupText(logger, LogLevel.Info,$"Average speed: [orchid2]{FormatBytes(TotalBytesDownloaded / OpTimer.Elapsed.TotalSeconds)}/s[/]");
		}
		LogMarkupText(logger, LogLevel.Info,"Finished updating.");
		if (UserConfig.PromptToContinue)
			PromptUserToContinue();
		return 0;
	}
}