using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

using FluentFTP;
using NLog;
using YamlDotNet.Serialization;
using ShellProgressBar;

using static BDSM.Configuration;
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

	public static int Main()
	{
		_ = SetConsoleOutputCP(65001);
		_ = SetConsoleCP(65001);

		Exception? _configEx = null;
		UserConfiguration? _config = null;
		try
		{
			//logger.Trace("Handling any exception thrown before Main()");
			StreamReader reader = File.OpenText("UserConfiguration.yaml");
			string ReadAndDispose(StreamReader reader) { string yaml = reader.ReadToEnd(); reader.Dispose(); return yaml; }
			_config = new Deserializer().Deserialize<UserConfiguration>(ReadAndDispose(reader));
		}
		catch (TypeInitializationException ex)
		{
			if (ex.InnerException is FileNotFoundException)
				logger.Error("Your configuration file is missing. Please read the documentation and copy the example configuration to your own UserConfiguration.yaml.");
			else
				logger.Error("Your configuration file is malformed. Please reference the example and read the documentation.");
			_configEx = ex;
		}
		catch (Exception ex) { _configEx = ex; }
		if (_configEx is not null)
		{
			logger.Error(_configEx.StackTrace);
			logger.Error(_configEx.Message);
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

		ConcurrentBag<PathMapping> BaseDirectoriesToScan = new();
		ConcurrentBag<PathMapping> DirectoriesToScan = new();

		try { BaseDirectoriesToScan = GetPathMappingsFromConfig(UserConfig); }
		catch (FormatException)
		{
			logger.Error("Your configuration file is malformed. Please reference the example and read the documentation.");
			PromptBeforeExit();
			return 1;
		}
		foreach (PathMapping mapping in BaseDirectoriesToScan)
			DirectoriesToScan.Add(mapping);

		Stopwatch OpTimer = new();

		ConcurrentDictionary<string, PathMapping> FilesToDownload = new();
		ConcurrentBag<FileInfo> FilesToDelete = new();
		HashSet<Task<ConcurrentDictionary<string, PathMapping>>> scantasks = new();
		HashSet<Task> download_tasks = new();

		logger.Info("Scanning the server.");
		OpTimer.Start();

		TaskFactory FTPConnectionTaskFactory = new();
		for (int i = 0; i < UserConfig.ConnectionInfo.MaxConnections; i++)
			_ = scantasks.Add(FTPConnectionTaskFactory.StartNew(() => ProcessFTPDirectories(ref DirectoriesToScan, UserConfig.ConnectionInfo)));
		Task<ConcurrentDictionary<string, PathMapping>>[] scantaskarray = scantasks.ToArray();
		Task.WaitAll(scantaskarray);

		foreach (ConcurrentDictionary<string, PathMapping> pathmaps in scantaskarray.Select(task => task.Result))
			foreach (PathMapping pathmap in pathmaps.Values)
				_ = FilesToDownload.TryAdd(pathmap.LocalFullPathLower, pathmap);

		OpTimer.Stop();

		logger.Info($"Scanned {FilesToDownload.Count} files in {OpTimer.ElapsedMilliseconds}ms.");
		logger.Info("Comparing files.");
		OpTimer.Restart();
		foreach (PathMapping pm in BaseDirectoriesToScan)
		{
			_ = Parallel.ForEach(new DirectoryInfo(pm.LocalFullPath).EnumerateFiles("*", new EnumerationOptions() { RecurseSubdirectories = true }).AsParallel(), (fileondiskinfo) =>
			{
				logger.Debug("Processing " + fileondiskinfo.FullName);
				bool item_found = false;
				foreach (KeyValuePair<string, PathMapping> todownload in FilesToDownload)
				{
					if (todownload.Key == fileondiskinfo.FullName.ToLower() && todownload.Value.FileSize == fileondiskinfo.Length)
					{
						if (!FilesToDownload.TryRemove(todownload))
							logger.Warn(todownload.Value.LocalFullPathLower);
						item_found = true;
						break;
					}
					item_found = false;
				}
				if (!item_found)
					if (pm.DeleteClientFiles)
						FilesToDelete.Add(fileondiskinfo);
			});
		}
		OpTimer.Stop();
		logger.Info($"Comparison took {OpTimer.ElapsedMilliseconds}ms.");
		logger.Info($"{FilesToDownload.Count} file{(FilesToDownload.Count == 1 ? "" : "s")} to download and {FilesToDelete.Count} file{(FilesToDelete.Count == 1 ? "" : "s")} to delete.");

		if (!FilesToDelete.IsEmpty)
		{
			logger.Info("Deleting files:");
			foreach (FileInfo pm in FilesToDelete)
				logger.Info($"{pm.FullName}");
			if (UserConfig.PromptToContinue)
				PromptUserToContinue();
			foreach (FileInfo pm in FilesToDelete)
				File.Delete(pm.FullName);
			logger.Info($"{FilesToDelete.Count} file{(FilesToDelete.Count == 1 ? "" : "s")} deleted.");
		}

		if (!FilesToDownload.IsEmpty)
		{
			long TotalBytesToDownload = 0;
			long TotalBytesRemaining = 0;
			logger.Info("Downloading files.");
			foreach (KeyValuePair<string, PathMapping> pathmap in FilesToDownload)
				TotalBytesToDownload += (long)pathmap.Value.FileSize!;
			logger.Info($"{FilesToDownload.Count} file{(FilesToDownload.Count == 1 ? "" : "s")} ({FormatBytes(TotalBytesToDownload)}) to download.");
			TotalBytesRemaining = TotalBytesToDownload;
			if (UserConfig.PromptToContinue)
				PromptUserToContinue();

			OpTimer.Restart();
			long bytes_downloaded = 0;
			double overall_speed = 0;
			double milliseconds_elapsed = 0;
			TimeSpan time_elapsed = new();
			DateTime total_progress_start_time = DateTime.Now;
			ConcurrentDictionary<int, double> download_speeds = new();
			double total_speed = 0;
			ProgressBarOptions total_pbar_options = new()
			{
				CollapseWhenFinished = true,
				DisplayTimeInRealTime = true,
				EnableTaskBarProgress = true,
				ProgressCharacter = ' '
			};
			using ProgressBar TotalProgressBar = new(100, "Downloading files:", total_pbar_options);
			for (int i = 0; i < UserConfig.ConnectionInfo.MaxConnections; i++)
				_ = download_tasks.Add(FTPConnectionTaskFactory.StartNew(() =>
					{
						int id = Environment.CurrentManagedThreadId;
						KeyValuePair<string, PathMapping> pm_kvp;
						using FtpClient download_client = new(UserConfig.ConnectionInfo.Address, UserConfig.ConnectionInfo.Username, UserConfig.ConnectionInfo.EffectivePassword, UserConfig.ConnectionInfo.Port);
						download_client.Config.EncryptionMode = FtpEncryptionMode.Auto;
						download_client.Config.ValidateAnyCertificate = true;
						download_client.Config.LogToConsole = false;
						download_client.Encoding = Encoding.UTF8;
						download_client.Connect();
						while (!FilesToDownload.IsEmpty)
						{
							bool pm_was_removed;
							try
							{
								pm_kvp = FilesToDownload.First();
								pm_was_removed = FilesToDownload.TryRemove(pm_kvp);
							}
							catch (InvalidOperationException)
							{
								logger.Debug($"No more files to download in {id}");
								break;
							}
							if (!pm_was_removed)
								break;
							//PathMapping _currentpm = pm_kvp.Value;
							_ = FilesToDownload.TryRemove(pm_kvp);

							long bytes_remaining = (long)pm_kvp.Value.FileSize!;
							long bytes_transferred_since = 0;
							long old_transferred_bytes = 0;
							ProgressBarOptions child_pbar_options = new()
							{
								CollapseWhenFinished = true,
								ShowEstimatedDuration = true,
								DisplayTimeInRealTime = false,
								ProgressBarOnBottom = true,
								ProgressCharacter = '-'
							};
							using ChildProgressBar child_pbar = TotalProgressBar.Spawn(100, $"{pm_kvp.Value.FileName} 0/{FormatBytes((double)pm_kvp.Value.FileSize)}",child_pbar_options);
							DateTime progress_start_time = DateTime.Now;
							void ftpprogress(FtpProgress ftp_progress)
							{
								double progress_time_elapsed = (DateTime.Now - progress_start_time).TotalMilliseconds;
								if (progress_time_elapsed < 250)
									return;

								bytes_transferred_since = ftp_progress.TransferredBytes - old_transferred_bytes;
								old_transferred_bytes = ftp_progress.TransferredBytes;
								_ = Interlocked.Add(ref TotalBytesRemaining, 0 - bytes_transferred_since);
								float remaining = (float)TotalBytesRemaining / (float)TotalBytesToDownload;
								float percentremaining = remaining * TotalProgressBar.MaxTicks;
								double rounded = Math.Round(percentremaining);
								int roundedint = Convert.ToInt32(rounded);
								bytes_downloaded = TotalBytesToDownload - TotalBytesRemaining;
								time_elapsed = DateTime.Now - total_progress_start_time;
								milliseconds_elapsed = time_elapsed.TotalMilliseconds;
								overall_speed = Math.Round(bytes_downloaded / (milliseconds_elapsed / 1000), 2);
								if (bytes_transferred_since != 0)
								{
									download_speeds[id] = ftp_progress.TransferSpeed;
									foreach (double speed in download_speeds.Values)
										total_speed += speed;
									TotalProgressBar.Tick(TotalProgressBar.MaxTicks - roundedint);
									TotalProgressBar.Message = $"Downloading files: {FormatBytes(TotalBytesToDownload - TotalBytesRemaining)} / {FormatBytes(TotalBytesToDownload)} (Current speed: {FormatBytes(total_speed)}/s) (Average speed: {FormatBytes(overall_speed)}/s)";
									total_speed = 0;
									overall_speed = 0;
								}
								bytes_downloaded = 0;
								progress_start_time = DateTime.Now;
								progress_time_elapsed = 0;
								if (ftp_progress.Progress >= 100)
								{
									child_pbar.Tick(new TimeSpan());
									child_pbar.Message = $"{pm_kvp.Value.FileName} | Downloaded {FormatBytes((double)pm_kvp.Value.FileSize)} at {FormatBytes(ftp_progress.TransferSpeed)}/s";
									return;
								}
								double time_remaining = bytes_remaining / ftp_progress.TransferSpeed;
								int timeremainingint = (int)Math.Round(time_remaining);
								child_pbar.Message = $"{pm_kvp.Value.FileName} | {FormatBytes(ftp_progress.TransferredBytes)} / {FormatBytes((double)pm_kvp.Value.FileSize)} ({FormatBytes(ftp_progress.TransferSpeed)}/s)";
								child_pbar.Tick(ftp_progress.ETA);
								child_pbar.Tick((int)Math.Round(ftp_progress.Progress));
								bytes_remaining = (long)pm_kvp.Value.FileSize! - ftp_progress.TransferredBytes;
							}
							overall_speed = 0;

							FtpStatus status = download_client.DownloadFile(pm_kvp.Value.LocalFullPath, pm_kvp.Value.RemoteFullPath, FtpLocalExists.Overwrite, FtpVerify.OnlyChecksum, ftpprogress);
							if (status != FtpStatus.Success)
							{
								logger.Error($"Download of {pm_kvp.Value.RemoteFullPath}: {status}");
								Debugger.Break();
							}
							else
								logger.Info($"Download of {pm_kvp.Value.RemoteFullPath}: {status}");
						}
						_ = download_speeds.Remove(id, out _);
						return;
					}));

			Task.WaitAll(download_tasks.ToArray());
			OpTimer.Stop();
			TotalProgressBar.Message = "";
			TotalProgressBar.Dispose();
			logger.Info($"Downloaded {FormatBytes(TotalBytesToDownload)} bytes in {OpTimer.Elapsed.Minutes} minutes and {OpTimer.Elapsed.Seconds} seconds.");
			logger.Info($"Average speed: {FormatBytes(TotalBytesToDownload / OpTimer.Elapsed.TotalSeconds)}/s");
		}
		logger.Info("Finished updating.");
		if (UserConfig.PromptToContinue)
			PromptUserToContinue();
		return 0;
	}

	public static ConcurrentDictionary<string, PathMapping> ProcessFTPDirectories(ref ConcurrentBag<PathMapping> pathmaps, RepoConnectionInfo repoinfo)
	{
		using FtpClient scanclient = new(repoinfo.Address, repoinfo.Username, repoinfo.EffectivePassword, repoinfo.Port);
		scanclient.Config.EncryptionMode = FtpEncryptionMode.Auto;
		scanclient.Config.ValidateAnyCertificate = true;
		scanclient.Config.LogToConsole = false;
		scanclient.Encoding = Encoding.UTF8;
		scanclient.Connect();
		int times_waited = 0;
		ConcurrentDictionary<string, PathMapping> files = new();
		while (true)
		{
			if (!pathmaps.TryTake(out PathMapping pathmap))
			{
				Thread.Sleep(30);
				times_waited++;
				if (times_waited > 3)
				{
					logger.Debug($"Scan task ID {Environment.CurrentManagedThreadId} is done.");
					break;
				}
				continue;
			}
			string remotepath = pathmap.RemoteFullPath;
			string localpath = pathmap.LocalFullPath;
			times_waited = 0;
			PathMapping _pathmap;
			foreach (FtpListItem item in scanclient.GetListing(remotepath))
				switch (item.Type)
				{
					case FtpObjectType.File:
						_pathmap = pathmap with { LocalRelativePath = string.Join('\\', pathmap.LocalRelativePath, item.Name), RemoteRelativePath = string.Join('/', pathmap.RemoteRelativePath, item.Name), FileSize = item.Size };
						_ = files.TryAdd(_pathmap.LocalFullPathLower, _pathmap);
						break;
					case FtpObjectType.Directory:
						_pathmap = pathmap with { LocalRelativePath = string.Join('\\', pathmap.LocalRelativePath, item.Name), RemoteRelativePath = string.Join('/', pathmap.RemoteRelativePath, item.Name) };
						pathmaps.Add(_pathmap);
						break;
					case FtpObjectType.Link:
						logger.Warn($"Skipping a link: {item.FullName} -> {item.LinkTarget}");
						break;
				}
		}
		return files;
	}
}