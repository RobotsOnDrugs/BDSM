using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;

using FluentFTP;

using NLog;

using YamlDotNet.Serialization;
using YamlDotNet.Core;

using Spectre.Console;

using static BDSM.Lib.FTPFunctions;
using static BDSM.DownloadProgress;
using static BDSM.LoggingConfiguration;
using static BDSM.Lib.Configuration;
using static BDSM.Lib.Utility;
using BDSM.Lib;

namespace BDSM;

public static partial class BDSM
{
	public const string VERSION = "0.3.7";
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetConsoleOutputCP(uint wCodePageID);
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetConsoleCP(uint wCodePageID);
	private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
	internal static FullUserConfiguration UserConfig;
	static void CtrlCHandler(object sender, ConsoleCancelEventArgs args)
	{
		Console.WriteLine("");
		LogMarkupText(logger, LogLevel.Fatal, $"[{ErrorColor}]Update aborted, shutting down.[/]");
		LogManager.Flush();
		LogManager.Shutdown();
		args.Cancel = false; Environment.Exit(1);
	}
	static void DeletionCtrlCHandler(object sender, ConsoleCancelEventArgs args)
	{
		Console.WriteLine("");
		LogMarkupText(logger, LogLevel.Fatal, $"[{ErrorColor}]Update aborted, shutting down.[/]");
		LogManager.Flush();
		LogManager.Shutdown();
		throw new BDSMInternalFaultException("File deletion was aborted, likely due to an error in scanning.");
	}

	public static async Task<int> Main()
	{
		_ = SetConsoleOutputCP(65001);
		_ = SetConsoleCP(65001);
		Console.CancelKeyPress += CtrlCHandler!;
#if DEBUG
		LogManager.Configuration = LoadCustomConfiguration(out bool is_custom_logger, LogLevel.Debug);
#else
		LogManager.Configuration = LoadCustomConfiguration(out bool is_custom_logger, LogLevel.Info);
#endif
		logger.Info($"== Begin BDSM {VERSION} log ==");
		logger.Debug($"== Begin BDSM {VERSION} debug log ==");
		logger.Info("Logger initialized.");

		if (is_custom_logger)
			LogMarkupText(logger, LogLevel.Info, $"Custom logging configuration loaded [{SuccessColor}]successfully[/].");
		InitalizeLibraryLoggers(logger);
		FTPFunctionOptions FTPOptions = new() { BufferSize = 65536 };

		const string CurrentConfigVersion = "0.3.2";
		string user_config_version = CurrentConfigVersion;
		try { UserConfig = GetUserConfiguration(out user_config_version); }
		catch (UserConfigurationException ex)
		{
			switch (ex.InnerException)
			{
				case FileNotFoundException:
					if (AnsiConsole.Confirm("No configuration file found. Create one now?"))
					{
						try { UserConfig = GenerateNewUserConfig(); break; }
						catch (OperationCanceledException) { }
					}
					LogMarkupText(logger, LogLevel.Fatal, $"[{CancelColor}]No configuration file was found and the creation of a new one was canceled.[/]");
					return 1;
				case TypeInitializationException or YamlException:
					UserConfig = await GetOldUserConfigurationAsync();
					user_config_version = "0.1";
					break;
				case null:
					LogMarkupText(logger, LogLevel.Fatal, $"[{ErrorColor}]{ex.Message.EscapeMarkup()}[/]");
					PromptBeforeExit();
					return 1;
				default:
					throw;
			}
		}

		if (GamePathIsHS2(UserConfig.GamePath) is null)
		{
			LogMarkupText(logger, LogLevel.Error, $"[{ErrorColor}]Your game path {UserConfig.GamePath.EscapeMarkup()} is not valid.[/]");
			return 1;
		}

		if (user_config_version != CurrentConfigVersion)
		{
			try
			{
				SerializeUserConfiguration(FullUserConfigurationToSimple(UserConfig));
				LogMarkupText(logger, LogLevel.Info, $"[{FileListingColor}]Configuration file was updated from {user_config_version} to the {CurrentConfigVersion} format.[/]");
			}
			catch (Exception serial_ex)
			{
				logger.Warn(serial_ex);
				AnsiConsole.MarkupLine($"[{CancelColor}]Your configuration was updated, but the new configuration file could not be written. See BDSM.log for details.[/]");
				PromptUserToContinue();
			}
			AnsiConsole.MarkupLine($"[{FileListingColor}]See [link]https://github.com/RobotsOnDrugs/BDSM/wiki/User-Configuration[/] for more details on the new format.[/]");
		}
		switch (user_config_version)
		{
			case CurrentConfigVersion:
				break;
			case "0.3":
				SimpleUserConfiguration simple_config = FullUserConfigurationToSimple(UserConfig);
				string studio_mod_download_state = simple_config.OptionalModpacks.Studio ? "both packs" : "neither pack";
				LogMarkupText(logger, LogLevel.Warn, $"[{CancelColor}]Notice: Downloading extra studio maps was turned on by default in 0.3, but is now turned on by default only if studio mods are also being downloaded.[/]");
				LogMarkupText(logger, LogLevel.Warn, $"[{CancelColor}]With your configuration, {studio_mod_download_state} will be downloaded. If you wish to change this, you may exit now and edit UserConfiguration.yaml to your liking.[/]");
				PromptUserToContinue();
				break;
			case "0.1":
				LogMarkupText(logger, LogLevel.Warn, $"[{CancelColor}]Notice: As of 0.3, server connection info and server path mappings and sync behavior are no longer in the user configuration. If you have a good use case for customizing these, file a feature request on GitHub.[/]");
				break;
			default:
				throw new BDSMInternalFaultException("Detection of user configuration version failed.");
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
			? new Deserializer().Deserialize<SkipScanConfiguration>(ReadConfigAndDispose(SKIP_SCAN_CONFIG_FILENAME))
			: new() { SkipScan = false, FileMappings = Array.Empty<string>() };
		SkipScan = _skip_config.SkipScan;

		if (SkipScan)
		{
			logger.Info("SkipScan is enabled.");
			using FtpClient _scanner = SetupFTPClient(UserConfig.ConnectionInfo);
			_scanner.Connect();
			foreach (PathMapping pathmap in GetPathMappingsFromSkipScanConfig(_skip_config, UserConfig))
			{
				FtpListItem? _dl_file_info = _scanner.GetObjectInfo(pathmap.RemoteFullPath);
				if (_dl_file_info is not null)
					FilesToDownload.Add(PathMappingToFileDownload(pathmap with { FileSize = _dl_file_info.Size }));
				else
					LogMarkupText(logger, LogLevel.Fatal, $"[{ErrorColor}]Couldn't get file info for {pathmap.RemoteFullPath.EscapeMarkup()}.[/]");
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
				LogMarkupText(logger, LogLevel.Error, "Your configuration file is malformed. Please reference the example and read the documentation.");
				PromptBeforeExit();
				return 1;
			}
			foreach (PathMapping mapping in BaseDirectoriesToScan)
				DirectoriesToScan.Add(mapping);
		}

		logger.Debug($"Using {UserConfig.ConnectionInfo.Address}");
		Stopwatch OpTimer = new();
		bool none_successful = true;
		bool all_faulted = true;
		Console.CancelKeyPress -= CtrlCHandler!;
		bool successful_scan = AnsiConsole.Status()
			.AutoRefresh(true)
			.SpinnerStyle(new(Color.Cyan1, null, null, null))
			.Spinner(Spinner.Known.BouncingBar)
			.Start("Scanning the server.", _ =>
			{
				OpTimer.Start();
				List<Task> scan_tasks = new();
				List<Task> finished_scan_tasks = new();
				using CancellationTokenSource scan_cts = new();
				CancellationToken scan_ct = scan_cts.Token;
				for (int i = 0; i < UserConfig.ConnectionInfo.MaxConnections; i++)
					scan_tasks.Add(Task.Run(() => GetFilesOnServer(ref DirectoriesToScan, ref FilesOnServer, UserConfig.ConnectionInfo, scan_ct), scan_ct));
				try
				{
					finished_scan_tasks = ProcessTasks(scan_tasks, scan_cts);
					List<Exception> scan_exceptions = new();
					foreach (Task finished_scan_task in finished_scan_tasks)
					{
						switch (finished_scan_task.Status)
						{
							case TaskStatus.Faulted:
								scan_exceptions.Add(finished_scan_task.Exception!);
								break;
							case TaskStatus.RanToCompletion:
								all_faulted = false;
								none_successful = false;
								break;
							case TaskStatus.Canceled:
								all_faulted = false;
								LogMarkupText(logger, LogLevel.Fatal, $"[{CancelColor}]Scanning was canceled.[/]");
								break;
							default:
								all_faulted = false;
								break;
						}
					}
					if (all_faulted)
						throw new AggregateException(scan_exceptions);
				}
				catch (OperationCanceledException)
				{
					LogMarkupText(logger, LogLevel.Fatal, $"[{CancelColor}]Scanning was canceled.[/]");
					return false;
				}
				catch (AggregateException aex)
				{
					if (all_faulted || none_successful)
					{
						LogMarkupText(logger, LogLevel.Fatal, $"[{ErrorColor}]Could not scan the server. Failed scan tasks had the following errors:[/]");
						foreach (Exception inner_ex in aex.Flatten().InnerExceptions)
						{
							AnsiConsole.MarkupLine($"[{ErrorColorAlt}]{inner_ex.Message}[/]");
							logger.Warn(inner_ex);
						}
						AnsiConsole.MarkupLine($"[{ErrorColor}]See the log for full error details.[/]");
						return false;
					}
				}
				return true;
			});
		if (!successful_scan)
		{
			if (UserConfig.PromptToContinue)
				PromptBeforeExit();
			return 1;
		}
		Console.CancelKeyPress += CtrlCHandler!;
		if (none_successful || (FilesOnServer.IsEmpty && !DirectoriesToScan.IsEmpty))
		{
			LogMarkupText(logger, LogLevel.Error,$"[{ErrorColor}]Scanning could not complete due to network or other errors.[/]");
			if (UserConfig.PromptToContinue)
				PromptBeforeExit();
			return 1;
		}
		OpTimer.Stop();
		bool file_count_is_low = false;
		if (FilesOnServer.Count < 9001)
			file_count_is_low = true;
		LogMarkupText(logger, LogLevel.Info,$"Scanned [{HighlightColor}]{FilesOnServer.Count}[/] files in [{HighlightColor}]{OpTimer.ElapsedMilliseconds}ms[/].");

		LogMarkupText(logger, LogLevel.Info,"Comparing files.");
		OpTimer.Restart();
		bool local_access_successful = true;
		ConcurrentQueue<Exception> local_access_exceptions = new();
		foreach (PathMapping pm in BaseDirectoriesToScan)
		{
			DirectoryInfo base_dir_di = new(pm.LocalFullPath);
			Directory.CreateDirectory(pm.LocalFullPath);
			IEnumerable<FileInfo> file_enumeration = base_dir_di.EnumerateFiles("*", new EnumerationOptions() { RecurseSubdirectories = true, IgnoreInaccessible = true });
			try
			{
				string filepath_idx = "";
				bool is_disabled_zipmod = false;
				foreach (FileInfo fileondiskinfo in file_enumeration)
				{
					is_disabled_zipmod = fileondiskinfo.Extension == ".zi_mod";
					filepath_idx = is_disabled_zipmod ?
						Path.ChangeExtension(fileondiskinfo.FullName, ".zipmod").ToLower() :
						filepath_idx = fileondiskinfo.FullName.ToLower();
				if (FilesOnServer.TryGetValue(filepath_idx, out PathMapping match_pm) && (is_disabled_zipmod || match_pm.FileSize == fileondiskinfo.Length))
					_ = FilesOnServer.TryRemove(match_pm.LocalFullPathLower, out _);
				else if (pm.DeleteClientFiles)
					FilesToDelete.Add(fileondiskinfo);
				}
			}
			catch (Exception ex)
			{
				local_access_successful = false;
				LogMarkupText(logger, LogLevel.Error,$"[{ErrorColor}][{ErrorColorAlt}]Could not access {pm.LocalFullPath.EscapeMarkup()}[/]. Ensure that you have the correct path specified in your configuration and that you have permission to access it.[/]");
				local_access_exceptions.Enqueue(ex);
				continue;
			}
			if (!local_access_successful)
				throw new AggregateException(local_access_exceptions);
		}
		foreach (KeyValuePair<string, PathMapping> pm_kvp in FilesOnServer)
			FilesToDownload.Add(PathMappingToFileDownload(pm_kvp.Value));
		OpTimer.Stop();
		LogMarkupText(logger, LogLevel.Info,$"Comparison took [{HighlightColor}]{OpTimer.ElapsedMilliseconds}ms[/].");
		LogMarkupText(logger, LogLevel.Info,$"[{HighlightColor}]{Pluralize(FilesToDownload.Count, " file")}[/] to download and [{HighlightColor}]{Pluralize(FilesToDelete.Count, " file")}[/] to delete.");
		if (!FilesToDelete.IsEmpty)
		{
			ConcurrentBag<FileInfo> failed_to_delete = new();
			List<Exception> failed_deletions = new();
			foreach (FileInfo pm in FilesToDelete)
				logger.Info($"{pm.FullName}");
			LogMarkupText(logger, LogLevel.Info,"Will delete files:");
			if (FilesToDelete.Count > 100)
			{
				Console.CancelKeyPress -= CtrlCHandler!;
				Console.CancelKeyPress += DeletionCtrlCHandler!;
				LogMarkupText(logger, LogLevel.Warn, $"[{WarningColor}]There are more than 100 files to delete.[/]");
				if (file_count_is_low)
					AnsiConsole.WriteLine("There are many files to delete and few found on the server. This is a sign of a serious error and you should press Ctrl-C now.");
				else
					AnsiConsole.WriteLine("This could be due to a large deletion in the bleeding edge pack, but could also be due to an internal error.\n" +
						"Please check the log now and confirm that this seems to be the case. If not, press Ctrl-C now to exit.");
				AnsiConsole.WriteLine("Type 'continue anyway' to continue or press Ctrl-C to abort.");
				while (true)
				{
					string? continue_anyway = Console.ReadLine();
					if (continue_anyway?.Replace("'", null) is "continue anyway")
					{
						LogMarkupText(logger, LogLevel.Warn, $"[{WarningColor}]Proceeding with deletion of {FilesToDelete.Count} files.");
						break;
					}
					AnsiConsole.WriteLine("Fully type 'continue anyway' to continue or press Ctrl-C to abort.");
				}
				Console.CancelKeyPress -= DeletionCtrlCHandler!;
				Console.CancelKeyPress += CtrlCHandler!;
			}
			foreach (FileInfo pm in FilesToDelete)
				AnsiConsole.MarkupLine($"[{DeleteColor}]{pm.FullName.EscapeMarkup()}[/]");
			if (UserConfig.PromptToContinue)
			{
				string marked_for_deletion_message = $"[{HighlightColor}]{Pluralize(FilesToDelete.Count, " file")}[/] marked for deletion.";
				LogMarkupText(logger, LogLevel.Info,marked_for_deletion_message);
				PromptUserToContinue();
				Console.Write('\r' + new string(' ', marked_for_deletion_message.Length) + '\r');
			}
			foreach (FileInfo pm in FilesToDelete)
			{
				try { File.Delete(pm.FullName); }
				catch (Exception ex)
				{
					failed_to_delete.Add(pm);
					failed_deletions.Add(ex);
					LogMarkupText(logger, LogLevel.Warn,$"[{WarningColor}]{ex.Message.EscapeMarkup()}[/]");
				}
			}
			LogMarkupText(logger, LogLevel.Info,$"[{HighlightColor}]{Pluralize(FilesToDelete.Count - failed_to_delete.Count, " file")}[/] deleted.");
			Debug.Assert(failed_deletions.Count == failed_to_delete.Count);
			if (failed_deletions.Count > 0)
			{
				LogMarkupText(logger, LogLevel.Error,$"[{ErrorColorAlt}]{Pluralize(failed_to_delete.Count, " file")}[/][{ErrorColor}] could not be deleted.[/]");
				throw new AggregateException(failed_deletions);
			}
		}

		if (!FilesToDownload.IsEmpty)
		{
			DLStatus.TotalNumberOfFilesToDownload = FilesToDownload.Count;
			DLStatus.NumberOfFilesToDownload = FilesToDownload.Count;
			DLStatus.TotalBytesToDownload = FilesToDownload.Select(file_dl => file_dl.TotalFileSize).Sum();
			DLStatus.TotalNumberOfFilesToDownload = FilesToDownload.Count;
			DLStatus.NumberOfFilesToDownload = FilesToDownload.Count;
			LogMarkupText(logger, LogLevel.Info, $"[{HighlightColor}]{Pluralize(DLStatus.NumberOfFilesToDownload, " file")}[/] ([{HighlightColor}]{FormatBytes(DLStatus.TotalBytesToDownload)}[/]) to download.");

			bool display_summary_before = UserConfig.PromptToContinue && AnsiConsole.Confirm("Show summary?", true);
#if DEBUG
			if (true)
			{
				logger.Debug("Files to download:");
#else
			if (display_summary_before)
			{
#endif
				AnsiConsole.WriteLine("Files to download:");
				foreach (FileDownload file_dl in FilesToDownload.OrderBy(fd => fd.LocalPath))
				{
					AnsiConsole.MarkupLine($"[deepskyblue1]{Path.GetRelativePath(UserConfig.GamePath, file_dl.LocalPath).EscapeMarkup()}[/] ([{HighlightColor}]{FormatBytes(file_dl.TotalFileSize)}[/])");
#if DEBUG
					logger.Debug($"{file_dl.LocalPath}");
#endif
				}
				PromptUserToContinue();
			}

			OpTimer.Restart();
			HashSet<FileDownloadProgressInformation> failed_files = new();
			DLStatus.TrackTotalCurrentSpeed();
			TotalProgressBar = new((int)(DLStatus.TotalBytesToDownload / 1024), "Downloading files:", DefaultTotalProgressBarOptions);
			ConcurrentQueue<DownloadChunk> chunks = new();
			while (FilesToDownload.TryTake(out FileDownload current_file_download))
			{
				DLStatus.FileDownloadsInformation[current_file_download.LocalPath] = new FileDownloadProgressInformation()
				{
					FilePath = current_file_download.LocalPath,
					TotalFileSize = current_file_download.TotalFileSize
				};
				foreach (DownloadChunk chunk in current_file_download.DownloadChunks)
					chunks.Enqueue(chunk);
			}
			int download_task_count = (UserConfig.ConnectionInfo.MaxConnections < chunks.Count) ? UserConfig.ConnectionInfo.MaxConnections : chunks.Count;
			logger.Trace("Chunks to download:");
			foreach (DownloadChunk chunk in chunks)
				logger.Trace($"{chunk.FileName} at offset {chunk.Offset}");
			List<Task> download_tasks = new();
			List<Task> finished_download_tasks = new();
			using CancellationTokenSource download_cts = new();
			CancellationToken download_ct = download_cts.Token;
			bool download_canceled = false;
			AggregateException? download_failures = null;
			Console.CancelKeyPress -= CtrlCHandler!;

			DLStatus.DownloadSpeedStopwatch.Start();
			for (int i = 0; i < UserConfig.ConnectionInfo.MaxConnections; i++)
				download_tasks.Add(Task.Run(() => DownloadFileChunks(UserConfig.ConnectionInfo, in chunks, DLStatus.ReportProgress, download_ct), download_ct));
			try { finished_download_tasks = ProcessTasks(download_tasks, download_cts); }
			catch (OperationCanceledException) { download_canceled = true; }
			catch (AggregateException ex) { download_failures = ex; }
			DLStatus.DownloadSpeedStopwatch.Stop();
			logger.Debug($"Chunks left after processing: {chunks.Count}");
			Console.CancelKeyPress += CtrlCHandler!;
			foreach (KeyValuePair<string, FileDownloadProgressInformation> progress_info_kvp in DLStatus.FileDownloadsInformation)
			{
				FileDownloadProgressInformation progress_info = progress_info_kvp.Value;
				if (progress_info.IsInitialized && !progress_info.IsComplete)
				{
					progress_info.Complete(false);
					try { File.Delete(progress_info.FilePath); }
					catch (Exception ex)
					{
						LogMarkupText(logger, LogLevel.Error, $"[red3]Tried to delete incomplete file [red1]{progress_info.FilePath.EscapeMarkup()}[/] during cleanup but encountered an error:[/]");
						LogException(logger, ex);
					}
				}
			}
			TotalProgressBar.Message = "";
			TotalProgressBar.Dispose();
			OpTimer.Stop();
			if (download_failures is not null)
			{
				LogMarkupText(logger, LogLevel.Error, "[red3]Could not download some files. Check the log for error details.[/]");
				foreach (Exception inner_ex in download_failures.Flatten().InnerExceptions)
					logger.Warn(inner_ex);
			}
			int number_of_downloads_finished = DLStatus.TotalNumberOfFilesToDownload - DLStatus.NumberOfFilesToDownload;
			IEnumerable<KeyValuePair<string, FileDownloadProgressInformation>> queued_downloads = DLStatus.FileDownloadsInformation.Where(info => !info.Value.IsInitialized);
			IEnumerable<KeyValuePair<string, FileDownloadProgressInformation>> canceled_downloads = DLStatus.FileDownloadsInformation.Where(info => info.Value.IsInitialized && (info.Value.TotalBytesDownloaded < info.Value.TotalFileSize));
			IEnumerable<KeyValuePair<string, FileDownloadProgressInformation>> completed_downloads = DLStatus.FileDownloadsInformation.Where(info => info.Value.IsInitialized && (info.Value.TotalBytesDownloaded == info.Value.TotalFileSize));
			IEnumerable<KeyValuePair<string, FileDownloadProgressInformation>> unfinished_downloads = queued_downloads.Concat(canceled_downloads);
			long bytes_of_completed_files = DLStatus.FileDownloadsInformation
				.Where(info => info.Value.IsInitialized && (info.Value.TotalBytesDownloaded == info.Value.TotalFileSize))
				.Sum(info => info.Value.TotalBytesDownloaded);
			if (download_canceled)
			{
				LogMarkupText(logger, LogLevel.Warn, $"Canceled download of [gold3_1]{Pluralize(unfinished_downloads.Count(), " file")}[/]" +
					$" ([orchid2]{FormatBytes(DLStatus.TotalBytesDownloaded - bytes_of_completed_files)}[/] wasted).");
			}
			LogMarkupText(logger, LogLevel.Info, $"Completed download of [orchid2]{Pluralize(number_of_downloads_finished, " file")}[/] ([orchid2]{FormatBytes(bytes_of_completed_files)}[/])" +
				$" in [orchid2]{(OpTimer.Elapsed.Minutes > 0 ? $"{OpTimer.Elapsed.Minutes} minutes and " : "")}" +
				$"{Pluralize(OpTimer.Elapsed.Seconds, " second")}[/].");
			LogMarkupText(logger, LogLevel.Info, $"Average speed: [orchid2]{FormatBytes(DLStatus.TotalBytesDownloaded / OpTimer.Elapsed.TotalSeconds)}/s[/]");

			bool display_summary = UserConfig.PromptToContinue && AnsiConsole.Confirm("Display file download summary?");
			void LogSummary(string message)
			{
				if (display_summary) LogMarkupText(logger, LogLevel.Info, message);
				else logger.Info(message);
			}
			if (unfinished_downloads.Any())
				LogSummary("Canceled downloads:");
			foreach (KeyValuePair<string, FileDownloadProgressInformation> canceled_path in unfinished_downloads.OrderBy(pm => pm.Key))
				LogSummary($"[gold3_1]{Path.GetRelativePath(UserConfig.GamePath, canceled_path.Key).EscapeMarkup()}[/]");
			if (completed_downloads.Any())
				LogSummary("Completed downloads:");
			foreach (KeyValuePair<string, FileDownloadProgressInformation> completed_path in completed_downloads.OrderBy(pm => pm.Key))
				LogSummary($"[green]{Path.GetRelativePath(UserConfig.GamePath, completed_path.Key).EscapeMarkup()}[/]");
		}
		if (!FilesToDownload.IsEmpty) RaiseInternalFault(logger, $"There are still {Pluralize(FilesToDownload.Count, " file")} after processing.");
		LogMarkupText(logger, LogLevel.Info,"Finished updating.");
		if (UserConfig.PromptToContinue)
			PromptBeforeExit();
		return 0;
	}
}