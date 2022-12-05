using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

using FluentFTP;
using NLog;
using YamlDotNet.Serialization;
using ShellProgressBar;

using static BDSM.Configuration;

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

	public static ConcurrentBag<PathMapping> GetPathMappingsFromConfig(UserConfiguration userconfig)
	{
		ConcurrentBag<PathMapping> _pathmappings = new();
		foreach (string sideloaderdir in userconfig.BaseSideloaderDirectories)
		{
			string[] _sideloadersplit = sideloaderdir.Split(" | ");
			bool _deletefiles = bool.Parse(_sideloadersplit[2]);
			PathMapping _pathmap = new()
			{
				RootPath = userconfig.ConnectionInfo.RootPath,
				RemoteRelativePath = _sideloadersplit[0],
				GamePath = userconfig.GamePath,
				LocalRelativePath = _sideloadersplit[1],
				FileSize = null,
				DeleteClientFiles = _deletefiles
			};
			_pathmappings.Add(_pathmap);
		}
		return _pathmappings;
	}

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
		ConcurrentBag<dynamic> FilesToDelete = new();
		HashSet<Task<ConcurrentDictionary<string, PathMapping>>> ScanTasks = new();
		HashSet<Task> DLTasks = new();

		logger.Info("Scanning the server.");
		OpTimer.Start();

		TaskFactory ScanTaskFactory = new();
		for (int i = 0; i < UserConfig.ConnectionInfo.MaxConnections; i++)
			_ = ScanTasks.Add(ScanTaskFactory.StartNew(() => ProcessFTPDirectories(ref DirectoriesToScan, UserConfig.ConnectionInfo)));
		Task<ConcurrentDictionary<string, PathMapping>>[] ScanTaskArray = ScanTasks.ToArray();
		Task.WaitAll(ScanTaskArray);

		foreach (ConcurrentDictionary<string, PathMapping> pathmaps in ScanTaskArray.Select(task => task.Result))
			foreach (PathMapping pathmap in pathmaps.Values)
				_ = FilesToDownload.TryAdd(pathmap.LocalFullPathLower, pathmap);

		OpTimer.Stop();

		logger.Info($"Scanned {FilesToDownload.Count} files in {OpTimer.ElapsedMilliseconds}ms.");
		logger.Info("Comparing files.");
		OpTimer.Restart();
		foreach (PathMapping pm in BaseDirectoriesToScan)
		{
			_ = Parallel.ForEach(new DirectoryInfo(pm.LocalFullPath).EnumerateFiles("*", new EnumerationOptions() { RecurseSubdirectories = true }).AsParallel(), (fileondisk) =>
			{
				logger.Debug("Processing " + fileondisk.FullName);
				var fileondiskinfo = new { FullPath = fileondisk.FullName, FileSize = fileondisk.Length };
				bool itemFound = false;
				foreach (KeyValuePair<string, PathMapping> todownload in FilesToDownload)
				{
					if (todownload.Key == fileondiskinfo.FullPath.ToLower() && todownload.Value.FileSize == fileondiskinfo.FileSize)
					{
						if (!FilesToDownload.TryRemove(todownload))
							logger.Warn(todownload.Value.LocalFullPathLower);
						itemFound = true;
						break;
					}
					itemFound = false;
				}
				if (!itemFound)
					if (pm.DeleteClientFiles)
						FilesToDelete.Add(fileondiskinfo);
			});
		}
		OpTimer.Stop();
		logger.Info($"Comparison took {OpTimer.ElapsedMilliseconds}ms.");

		if (FilesToDownload.IsEmpty)
			logger.Info("Nothing to download.");
		else
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
			long _bytesdownloaded = 0;
			double OverallSpeed = 0;
			double MillisecondsElapsed = 0;
			TimeSpan TimeElapsed = new();
			DateTime TotalProgressDateTimeStart = DateTime.Now;
			ConcurrentDictionary<int, double> downloadspeeds = new();
			double totalspeed = 0;
			using ProgressBar Progressbar = new(100, "Downloading files:", new ProgressBarOptions { CollapseWhenFinished = true, DisplayTimeInRealTime = false, EnableTaskBarProgress = true, ProgressCharacter = ' ' });
			for (int i = 0; i < UserConfig.ConnectionInfo.MaxConnections; i++)
				_ = DLTasks.Add(ScanTaskFactory.StartNew(() =>
					{
						int id = Environment.CurrentManagedThreadId;
						KeyValuePair<string, PathMapping> _pm_kvp;
						using FtpClient _dlclient = new(UserConfig.ConnectionInfo.Address, UserConfig.ConnectionInfo.Username, UserConfig.ConnectionInfo.EffectivePassword, UserConfig.ConnectionInfo.Port);
						_dlclient.Config.EncryptionMode = FtpEncryptionMode.Auto;
						_dlclient.Config.ValidateAnyCertificate = true;
						_dlclient.Config.LogToConsole = false;
						_dlclient.Encoding = Encoding.UTF8;
						_dlclient.Connect();
						while (!FilesToDownload.IsEmpty)
						{
							bool _pm_removed;
							try
							{
								_pm_kvp = FilesToDownload.First();
								_pm_removed = FilesToDownload.TryRemove(_pm_kvp);
							}
							catch (InvalidOperationException)
							{
								logger.Debug($"No more files to download in {id}");
								break;
							}
							if (!_pm_removed)
								break;
							PathMapping _currentpm = _pm_kvp.Value;
							_ = FilesToDownload.TryRemove(_pm_kvp);

							long bytesremaining = (long)_pm_kvp.Value.FileSize!;
							long bytestransferredsince = 0;
							long oldtransferredbytes = 0;
							ProgressBarOptions cparoptions = new()
							{
								CollapseWhenFinished = true,
								ShowEstimatedDuration = true,
								DisplayTimeInRealTime = false,
								ProgressBarOnBottom = true,
								ProgressCharacter = '-'
							};
							using ChildProgressBar childpbar = Progressbar.Spawn(100, $"{_pm_kvp.Value.FileName} 0/{FormatBytes((double)_pm_kvp.Value.FileSize)}",cparoptions);
							DateTime _progresstimeelapsed = DateTime.Now;
							void ftpprogress(FtpProgress _ftpprogress)
							{
								double _progresstimeelapseddouble = (DateTime.Now - _progresstimeelapsed).TotalMilliseconds;
								if (childpbar is null)
									return;
								if (_progresstimeelapseddouble < 250)
									return;

								bytestransferredsince = _ftpprogress.TransferredBytes - oldtransferredbytes;
								oldtransferredbytes = _ftpprogress.TransferredBytes;
								_ = Interlocked.Add(ref TotalBytesRemaining, 0 - bytestransferredsince);
								float remaining = (float)TotalBytesRemaining / (float)TotalBytesToDownload;
								float percentremaining = remaining * Progressbar!.MaxTicks;
								double rounded = Math.Round(percentremaining);
								int roundedint = Convert.ToInt32(rounded);
								_bytesdownloaded = TotalBytesToDownload - TotalBytesRemaining;
								TimeElapsed = DateTime.Now - TotalProgressDateTimeStart;
								MillisecondsElapsed = TimeElapsed.TotalMilliseconds;
								OverallSpeed = Math.Round(_bytesdownloaded / (MillisecondsElapsed / 1000), 2);
								if (bytestransferredsince != 0)
								{
									downloadspeeds[id] = _ftpprogress.TransferSpeed;
									foreach (double speed in downloadspeeds.Values)
										totalspeed += speed;
									Progressbar.Tick(Progressbar.MaxTicks - roundedint);
									Progressbar.Message = $"Downloading files: {FormatBytes(TotalBytesToDownload - TotalBytesRemaining)} / {FormatBytes(TotalBytesToDownload)} (Current speed: {FormatBytes(totalspeed)}/s) (Average speed: {FormatBytes(OverallSpeed)}/s)";
									totalspeed = 0;
									OverallSpeed = 0;
								}
								_bytesdownloaded = 0;
								_progresstimeelapsed = DateTime.Now;
								_progresstimeelapseddouble = 0;
								if (_ftpprogress.Progress >= 100)
								{
									childpbar.Tick(new TimeSpan());
									childpbar.Message = $"{_pm_kvp.Value.FileName} | Downloaded {FormatBytes((double)_pm_kvp.Value.FileSize)} at {FormatBytes(_ftpprogress.TransferSpeed)}/s";
									return;
								}
								double timeremaining = bytesremaining / _ftpprogress.TransferSpeed;
								int timeremainingint = (int)Math.Round(timeremaining);
								childpbar.Message = $"{_pm_kvp.Value.FileName} | {FormatBytes(_ftpprogress.TransferredBytes)} / {FormatBytes((double)_pm_kvp.Value.FileSize)} ({FormatBytes(_ftpprogress.TransferSpeed)}/s)";
								childpbar.Tick(_ftpprogress.ETA);
								childpbar.Tick((int)Math.Round(_ftpprogress.Progress));
								bytesremaining = (long)_pm_kvp.Value.FileSize! - _ftpprogress.TransferredBytes;
							}
							OverallSpeed = 0;

							FtpStatus status = _dlclient.DownloadFile(_pm_kvp.Value.LocalFullPath, _pm_kvp.Value.RemoteFullPath, FtpLocalExists.Overwrite, FtpVerify.OnlyChecksum, ftpprogress);
							if (status != FtpStatus.Success)
							{
								logger.Error($"Download of {_pm_kvp.Value.RemoteFullPath}: {status}");
								Debugger.Break();
							}
							else
								logger.Info($"Download of {_pm_kvp.Value.RemoteFullPath}: {status}");
						}
						_ = downloadspeeds.Remove(id, out _);
						return;
					}));
			Task[] DLTaskArray = DLTasks.ToArray();

			Task.WaitAll(DLTaskArray);
			OpTimer.Stop();
			Progressbar.WriteLine("Downloads finished.");
			logger.Info($"Downloaded {TotalBytesToDownload} bytes in {OpTimer.Elapsed.Minutes} minutes and {OpTimer.Elapsed.Seconds} seconds.");
			logger.Info($"Average speed: {FormatBytes(TotalBytesToDownload / OpTimer.Elapsed.TotalSeconds)}");
		}
		if (FilesToDelete.IsEmpty)
			logger.Info("Nothing to delete.");
		else
		{
			DeleteFiles(FilesToDelete, UserConfig.PromptToContinue);
			logger.Info($"{FilesToDelete.Count} files{(FilesToDelete.Count == 1 ? "" : "s")} deleted.");
		}

		logger.Info("Finished updating.");
		if (UserConfig.PromptToContinue)
			PromptUserToContinue();
		return 0;
	}

	private static void PromptBeforeExit()
	{
		Console.WriteLine("Press any key to exit.");
		_ = Console.ReadKey();
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
	public static void DeleteFiles(IEnumerable<dynamic> FilesToDelete, bool promptuser)
	{
		logger.Info("Deleting files:");
		foreach (dynamic pm in FilesToDelete)
			logger.Info($"{pm.FullPath}");
		if (promptuser)
			PromptUserToContinue();
		foreach (dynamic pm in FilesToDelete)
			File.Delete(pm.FullPath);
	}
	public static string FormatBytes(double NumberOfBytes)
	{
		NumberOfBytes = Math.Round(NumberOfBytes, 2);
		return NumberOfBytes switch
		{
			< 1100 * 1 => $"{NumberOfBytes} B",
			< 1100 * 1024 => $"{Math.Round(NumberOfBytes / 1024, 2)} KiB",
			< 1100 * 1024 * 1024 => $"{Math.Round(NumberOfBytes / (1024 * 1024), 2)} MiB",
			>= 1100 * 1024 * 1024 => $"{Math.Round(NumberOfBytes / (1024 * 1024 * 1024), 2)} GiB",
			double.NaN => "unknown"
		};
	}
	public static void PromptUserToContinue()
	{
		Console.WriteLine("Press any key to continue or Ctrl-C to abort");
		_ = Console.ReadKey();
	}
}