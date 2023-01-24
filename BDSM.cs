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
	public const string VERSION = "0.3.9";
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
		LogWithMarkup(logger, LogLevel.Fatal, "Update aborted, shutting down.");
		LogManager.Flush();
		LogManager.Shutdown();
		args.Cancel = false; Environment.Exit(1);
	}
	static void DeletionCtrlCHandler(object sender, ConsoleCancelEventArgs args)
	{
		Console.WriteLine("");
		LogWithMarkup(logger, LogLevel.Fatal, "Update aborted, shutting down.");
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
			LogWithMarkup(logger, LogLevel.Info, "Custom logging configuration loaded successfully.", SuccessColor);
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
					LogWithMarkup(logger, LogLevel.Fatal, "No configuration file was found and the creation of a new one was canceled.");
					return 1;
				case TypeInitializationException or YamlException:
					UserConfig = await GetOldUserConfigurationAsync();
					user_config_version = "0.1";
					break;
				case null:
					LogWithMarkup(logger, LogLevel.Fatal, ex.Message.EscapeMarkup());
					PromptBeforeExit();
					return 1;
				default:
					throw;
			}
		}

		if (GamePathIsHS2(UserConfig.GamePath) is null)
		{
			LogWithMarkup(logger, LogLevel.Error, $"Your game path {UserConfig.GamePath.EscapeMarkup()} is not valid.");
			return 1;
		}

		if (user_config_version != CurrentConfigVersion)
		{
			try
			{
				SerializeUserConfiguration(FullUserConfigurationToSimple(UserConfig));
				LogWithMarkup(logger, LogLevel.Info, $"Configuration file was updated from {user_config_version} to the {CurrentConfigVersion} format.", SuccessColor);
			}
			catch (Exception serial_ex)
			{
				logger.Warn(serial_ex);
				AnsiConsole.MarkupLine("Your configuration was updated, but the new configuration file could not be written. See BDSM.log for details.".Colorize(CancelColor));
				PromptUserToContinue();
			}
			AnsiConsole.MarkupLine("See [link]https://github.com/RobotsOnDrugs/BDSM/wiki/User-Configuration[/] for more details on the new format.");
		}
		switch (user_config_version)
		{
			case CurrentConfigVersion:
				break;
			case "0.3":
				SimpleUserConfiguration simple_config = FullUserConfigurationToSimple(UserConfig);
				string studio_mod_download_state = simple_config.OptionalModpacks.Studio ? "both packs" : "neither pack";
				LogWithMarkup(logger, LogLevel.Warn, "Notice: Downloading extra studio maps was turned on by default in 0.3, but is now turned on by default only if studio mods are also being downloaded.");
				LogWithMarkup(logger, LogLevel.Warn, "With your configuration, {studio_mod_download_state} will be downloaded. If you wish to change this, you may exit now and edit UserConfiguration.yaml to your liking.");
				PromptUserToContinue();
				break;
			case "0.1":
				LogWithMarkup(logger, LogLevel.Warn, "Notice: As of 0.3, server connection info and server path mappings and sync behavior are no longer in the user configuration. If you have a good use case for customizing these, file a feature request on GitHub.");
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
			ConcurrentBag<PathMapping> skipscan_pm = GetPathMappingsFromSkipScanConfig(_skip_config, UserConfig);
			foreach (PathMapping pathmap in skipscan_pm)
			{
				FtpListItem? _dl_file_info = _scanner.GetObjectInfo(pathmap.RemoteFullPath);
				if (_dl_file_info is not null)
					FilesToDownload.Add(PathMappingToFileDownload(pathmap with { FileSize = _dl_file_info.Size }));
				else
					LogWithMarkup(logger, LogLevel.Fatal, $"Couldn't get file info for {pathmap.RemoteFullPath.EscapeMarkup()}.");
			}
			_scanner.Dispose();
		}
#endif
		try
		{
			BaseDirectoriesToScan = SkipScan ? ImmutableHashSet<PathMapping>.Empty : UserConfig.BasePathMappings;
		}
		catch (FormatException)
		{
			LogWithMarkup(logger, LogLevel.Error, "Your configuration file is malformed. Please reference the example and read the documentation.");
			PromptBeforeExit();
			return 1;
		}
		foreach (PathMapping mapping in BaseDirectoriesToScan)
			DirectoriesToScan.Add(mapping);

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
								LogWithMarkup(logger, LogLevel.Fatal, "Scanning was canceled.", CancelColor);
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
					LogWithMarkup(logger, LogLevel.Fatal, "Scanning was canceled.", CancelColor);
					return false;
				}
				catch (AggregateException aex)
				{
					if (all_faulted || none_successful)
					{
						LogWithMarkup(logger, LogLevel.Fatal, "Could not scan the server. Failed scan tasks had the following errors:");
						foreach (Exception inner_ex in aex.Flatten().InnerExceptions)
						{
							AnsiConsole.MarkupLine(inner_ex.Message.Colorize(ErrorColorAlt));
							logger.Warn(inner_ex);
						}
						AnsiConsole.MarkupLine("See the log for full error details.".Colorize(ErrorColor));
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
			LogWithMarkup(logger, LogLevel.Error, "Scanning could not complete due to network or other errors.");
			if (UserConfig.PromptToContinue)
				PromptBeforeExit();
			return 1;
		}
		OpTimer.Stop();
		bool file_count_is_low = false;
		if (FilesOnServer.Count < 9001)
			file_count_is_low = true;
		LogMarkupText(logger, LogLevel.Info, $"Scanned {FilesOnServer.Count.Pluralize("file").Colorize(HighlightColor)} in {(OpTimer.ElapsedMilliseconds.ToString() + "ms").Colorize(HighlightColor)}.");

		const string comparing_files_message = "Comparing files.";
		LogWithMarkup(logger, LogLevel.Info, comparing_files_message, newline: false);
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
				LogMarkupText(logger, LogLevel.Error, $"Could not access {pm.LocalFullPath.EscapeMarkup().Colorize(ErrorColorAlt)}. " +
					"Ensure that you have the correct path specified in your configuration and that you have permission to access it.".Colorize(ErrorColor));
				local_access_exceptions.Enqueue(ex);
				continue;
			}
			if (!local_access_successful)
				throw new AggregateException(local_access_exceptions);
		}
		foreach (KeyValuePair<string, PathMapping> pm_kvp in FilesOnServer)
			FilesToDownload.Add(PathMappingToFileDownload(pm_kvp.Value));
		OpTimer.Stop();
		Console.Write(new string(' ', comparing_files_message.Length) + '\r');
		LogMarkupText(logger, LogLevel.Info, $"Comparison took {(OpTimer.ElapsedMilliseconds + "ms").Colorize(HighlightColor)}.");

		DLStatus.TotalNumberOfFilesToDownload = FilesToDownload.Count;
		DLStatus.NumberOfFilesToDownload = FilesToDownload.Count;
		DLStatus.TotalBytesToDownload = FilesToDownload.Select(file_dl => file_dl.TotalFileSize).Sum();
		DLStatus.TotalNumberOfFilesToDownload = FilesToDownload.Count;
		DLStatus.NumberOfFilesToDownload = FilesToDownload.Count;
		if (!FilesToDownload.IsEmpty || !FilesToDelete.IsEmpty)
		{
			string download_count_summary = FilesToDownload.IsEmpty ? string.Empty : $"{FilesToDownload.Count.Pluralize("file").Colorize(HighlightColor)} to download ({DLStatus.TotalBytesToDownloadString.Colorize(HighlightColor)})";
			string deletion_count_summary = FilesToDelete.IsEmpty ? string.Empty : $"{FilesToDelete.Count.Pluralize("file").Colorize(HighlightColor)} to delete";
			string connector = FilesToDownload.IsEmpty || FilesToDelete.IsEmpty ? string.Empty : " and ";
			LogMarkupText(logger, LogLevel.Info, download_count_summary + connector + deletion_count_summary + ".");
		}
		else
			LogWithMarkup(logger, LogLevel.Info, "No files to download or delete.");
		if (!FilesToDelete.IsEmpty)
		{
			ConcurrentBag<FileInfo> failed_to_delete = new();
			List<Exception> failed_deletions = new();
			if (FilesToDelete.Count > 100)
			{
				Console.CancelKeyPress -= CtrlCHandler!;
				Console.CancelKeyPress += DeletionCtrlCHandler!;
				LogWithMarkup(logger, LogLevel.Warn, "There are more than 100 files to delete.");
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
						LogMarkupText(logger, LogLevel.Warn, $"Proceeding with deletion of {FilesToDelete.Count.Colorize(HighlightColor)} files.".Colorize(WarningColor));
						break;
					}
					AnsiConsole.WriteLine("Fully type 'continue anyway' to continue or press Ctrl-C to abort.");
				}
				Console.CancelKeyPress -= DeletionCtrlCHandler!;
				Console.CancelKeyPress += CtrlCHandler!;
			}
			if (UserConfig.PromptToContinue && AnsiConsole.Confirm("Show full list of files to delete?", true))
			{
				AnsiConsole.MarkupLine("Will delete files:");
				foreach (FileInfo pm in FilesToDelete)
					AnsiConsole.MarkupLine(Path.GetRelativePath(UserConfig.GamePath, pm.FullName).EscapeMarkup().Colorize(DeleteColor));
			}
			logger.Info($"{FilesToDelete.Count.Pluralize("file")} marked for deletion.");
			foreach (FileInfo pm in FilesToDelete)
			{
				try
				{
					File.Delete(pm.FullName);
					logger.Info($"Deleted {pm.FullName}.");
				}
				catch (Exception ex)
				{
					failed_to_delete.Add(pm);
					failed_deletions.Add(ex);
					AnsiConsole.WriteLine(ex.Message.EscapeMarkup().Colorize(WarningColor));
				}
			}
			LogMarkupText(logger, LogLevel.Info, $"{(FilesToDelete.Count - failed_to_delete.Count).Pluralize("file").Colorize(HighlightColor)} deleted.");
			Debug.Assert(failed_deletions.Count == failed_to_delete.Count);
			if (failed_deletions.Count > 0)
			{
				LogMarkupText(logger, LogLevel.Error, $"{failed_to_delete.Count.Pluralize("file").Colorize(ErrorColorAlt)} could not be deleted.".Colorize(ErrorColor));
				foreach (Exception ex in failed_deletions)
					logger.Warn(ex);
				throw new AggregateException(failed_deletions);
			}
		}

		if (!FilesToDownload.IsEmpty)
		{
			Dictionary<string, (int TotalFiles, long TotalBytes)> pack_totals = new();
			foreach (PathMapping pack_dir in BaseDirectoriesToScan)
				pack_totals[pack_dir.FileName] = (0, 0L);
			if (SkipScan) pack_totals["Sideloader Modpack"] = (0, 0L);

			foreach (FileDownload file_to_dl in FilesToDownload)
			{
				string base_name = Path.GetRelativePath(UserConfig.GamePath, file_to_dl.LocalPath).RelativeModPathToPackName();
				int new_file_count = pack_totals[base_name].TotalFiles + 1;
				long new_byte_count = pack_totals[base_name].TotalBytes + file_to_dl.TotalFileSize;
				pack_totals[base_name] = (new_file_count, new_byte_count);
			}
			foreach (string pack_name in pack_totals.Keys.OrderBy(name => name))
			{
				int filecount = pack_totals[pack_name].TotalFiles;
				if (filecount == 0)
					continue;
				long bytecount = pack_totals[pack_name].TotalBytes;
				AnsiConsole.MarkupLine($"- {pack_name.Colorize(ModpackNameColor)}: " +
					$"{filecount.Pluralize("file").Colorize(HighlightColor)} " +
					$"({bytecount.FormatBytes().Colorize(HighlightColor)})");
			}
			if (UserConfig.PromptToContinue && AnsiConsole.Confirm("Show full list of files to download?", true))
			{
				foreach (FileDownload file_dl in FilesToDownload.OrderBy(fd => fd.LocalPath))
				{
					AnsiConsole.MarkupLine($"{Path.GetRelativePath(UserConfig.GamePath, file_dl.LocalPath).EscapeMarkup().Colorize(FileListingColor)} ({file_dl.TotalFileSize.FormatBytes().Colorize(HighlightColor)})");
					logger.Debug($"{file_dl.LocalPath}");
				}
				PromptUserToContinue();
			}

			OpTimer.Restart();
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
						LogMarkupText(logger, LogLevel.Error, $"Tried to delete incomplete file {progress_info.FilePath.EscapeMarkup().Colorize(ErrorColor)} during cleanup but encountered an error:".Colorize(ErrorColorAlt));
						LogExceptionAndDisplay(logger, ex);
					}
				}
			}
			TotalProgressBar.Message = "";
			TotalProgressBar.Dispose();
			OpTimer.Stop();
			if (download_failures is not null)
			{
				LogWithMarkup(logger, LogLevel.Error, "Could not download some files. Check the log for error details.", ErrorColorAlt);
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
				LogMarkupText(logger, LogLevel.Warn, $"Canceled download of {unfinished_downloads.Count().Pluralize("file").Colorize(CancelColor)}" +
					$" ({(DLStatus.TotalBytesDownloaded - bytes_of_completed_files).FormatBytes().Colorize(HighlightColor)} wasted).");
			}
			int total_minutes = (int)Math.Floor(OpTimer.Elapsed.TotalMinutes);
			string minutes_summary = total_minutes switch
			{
				1 => $"{(total_minutes + " minute").Colorize(HighlightColor)} and ",
				> 2 => $"{(total_minutes + " minutes").Colorize(HighlightColor)} and ",
				_ => string.Empty
			};
			string seconds_summary = OpTimer.Elapsed.Seconds switch
			{
				1 => $"{(OpTimer.Elapsed.Seconds + " second").Colorize(HighlightColor)}",
				_ => $"{(OpTimer.Elapsed.Seconds + " seconds").Colorize(HighlightColor)}"
			};
			LogMarkupText(logger, LogLevel.Info, $"Completed download of {number_of_downloads_finished.Pluralize("file").Colorize(HighlightColor)} ({bytes_of_completed_files.FormatBytes().Colorize(HighlightColor)})" +
				$" in {minutes_summary}{seconds_summary}.");
			LogMarkupText(logger, LogLevel.Info, $"Average speed: {((DLStatus.TotalBytesDownloaded / OpTimer.Elapsed.TotalSeconds).FormatBytes() + "/s").Colorize(HighlightColor)}");

			bool display_summary = UserConfig.PromptToContinue && AnsiConsole.Confirm("Display file download summary?");
			void LogSummary(string message)
			{
				if (display_summary) LogMarkupText(logger, LogLevel.Info, message);
				else logger.Info(message);
			}
			if (unfinished_downloads.Any())
				LogSummary("Canceled downloads:");
			foreach (KeyValuePair<string, FileDownloadProgressInformation> canceled_path in unfinished_downloads.OrderBy(pm => pm.Key))
				LogSummary(Path.GetRelativePath(UserConfig.GamePath, canceled_path.Key).EscapeMarkup().Colorize(CancelColor));
			if (completed_downloads.Any())
				LogSummary("Completed downloads:");
			foreach (KeyValuePair<string, FileDownloadProgressInformation> completed_path in completed_downloads.OrderBy(pm => pm.Key))
				LogSummary(Path.GetRelativePath(UserConfig.GamePath, completed_path.Key).EscapeMarkup().Colorize(SuccessColor));
		}
		if (!FilesToDownload.IsEmpty) RaiseInternalFault(logger, $"There are still {FilesToDownload.Count.Pluralize("file")} after processing.");
		LogWithMarkup(logger, LogLevel.Info, "Finished updating.");
		if (UserConfig.PromptToContinue)
			PromptBeforeExit();
		return 0;
	}
}