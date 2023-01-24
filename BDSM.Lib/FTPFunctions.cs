using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

using NLog;
using FluentFTP;
using FluentFTP.Exceptions;

using static BDSM.Lib.Exceptions;

namespace BDSM.Lib;
public static class FTPFunctions
{
	private static readonly FTPFunctionOptions Options = new();
	private static readonly ConcurrentDictionary<int, bool?> ScanQueueWaitStatus = new();
	private static readonly ConcurrentDictionary<string, int> EmptyDirs = new();
	public readonly record struct FTPFunctionOptions
	{
		public FTPFunctionOptions() { }
		public int BufferSize { get; init; } = 65536;
	}
	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "For in-depth debugging of FTP operations only. Not usually needed.")]
	private static void LogFTPMessage(FtpTraceLevel trace, string message) => logger.Log(LogLevel.FromOrdinal((int)trace + 1), $"[Managed thread {Environment.CurrentManagedThreadId}] " + "[FluentFTP] " + message);
	private static ILogger logger = LogManager.GetCurrentClassLogger();
	public static void InitializeLogger(ILogger parent) => logger = parent;
	public static FtpClient SetupFTPClient(Configuration.RepoConnectionInfo repoinfo) =>
		new(repoinfo.Address, repoinfo.Username, repoinfo.Password, repoinfo.Port)
		{
			Config = BetterRepackRepositoryDefinitions.DefaultRepoConnectionConfig,
			Encoding = Encoding.UTF8,
			//LegacyLogger = LogFTPMessage
		};
	public static FtpClient DefaultSideloaderClient() => SetupFTPClient(BetterRepackRepositoryDefinitions.DefaultConnectionInfo);
	public static bool TryConnect(FtpClient client, int max_retries = 3)
	{
		int tid = Environment.CurrentManagedThreadId;
		int retries = 0;
		bool success = false;
		while (true)
		{
			logger.Debug($"[Managed thread {tid}] TryConnect hit with {retries} retries.");
			try
			{
				client.Connect();
				success = true;
				logger.Debug($"[Managed thread {tid}] An FTP connection was successful");
				break;
			}
			catch (FtpCommandException fcex) when (fcex.CompletionCode is not "421")
			{
				switch (fcex.ResponseType)
				{
					case FtpResponseType.TransientNegativeCompletion:
						retries++;
						logger.Debug($"[Managed thread {tid}] Failed to establish an FTP connection with a transient error at attempt {retries}.");
						Thread.Sleep(1000);
						break;
					case FtpResponseType.PermanentNegativeCompletion:
						logger.Warn($"[Managed thread {tid}] Failed to establish an FTP connection with a permanent error: {fcex.Message}");
						throw;
					case FtpResponseType.PositivePreliminary or FtpResponseType.PositiveCompletion or FtpResponseType.PositiveIntermediate:
						logger.Error(fcex, $"[Managed thread {tid}] FtpCommandException was thrown with a positive response.");
						throw new BDSMInternalFaultException("Don't know how to handle FtpCommandException with a positive response.", fcex);
				}
				if (retries > max_retries) { client.Dispose(); throw; }
			}
			catch (FtpCommandException fcex) { logger.Warn($"{fcex.Message}"); client.Dispose(); throw; }
			catch (FtpException fex) { logger.Warn($"[Managed thread {tid}] Failed to establish an FTP connection with an unknown FTP error: {fex.Message}"); client.Dispose(); throw; }
			catch (Exception tex) when (tex is TimeoutException or IOException)
			{
				retries++;
				if (retries > max_retries)
				{
					string error_message = $"[Managed thread {tid}] " + (tex is TimeoutException ? "FTP connection attempt timed out." : "FTP connection had an I/O error.");
					logger.Error(tex, error_message);
					throw new FTPOperationException(error_message, tex);
				}
				Thread.Sleep(2000);
			}
			catch (Exception ex) { logger.Debug($"[Managed thread {tid}] Failed to establish an FTP connection with an unknown error: {ex.Message}"); client.Dispose(); throw; }
		}
		return success;
	}
	public static void GetFilesOnServer(ref ConcurrentBag<PathMapping> paths_to_scan, ref ConcurrentDictionary<string, PathMapping> files_found, Configuration.RepoConnectionInfo repoinfo, CancellationToken ct)
	{
		int tid = Environment.CurrentManagedThreadId;
		try
		{
			ScanQueueWaitStatus[tid] = false;
			bool should_wait_for_queue = false;
			try { ct.ThrowIfCancellationRequested(); }
			catch (Exception) { ScanQueueWaitStatus[tid] = null; throw; }
			ConcurrentBag<PathMapping> files = new();
			PathMapping pathmap = default;
			List<string> missed_ftp_entries = new();
			FtpException? last_ftp_exception = null;
			List<Exception> accumulated_exceptions = new();
			using FtpClient download_client = SetupFTPClient(repoinfo);
			ScanQueueWaitStatus[tid] = null;
			if (!TryConnect(download_client))
				throw new FTPConnectionException();

			while (!ct.IsCancellationRequested)
			{
				ScanQueueWaitStatus[tid] = false;
				if (accumulated_exceptions.Count > 2)
				{
					ScanQueueWaitStatus[tid] = null;
					download_client.Dispose();
					throw new AggregateException(accumulated_exceptions);
				}

				try { ct.ThrowIfCancellationRequested(); }
				catch (Exception) { ScanQueueWaitStatus[tid] = null; throw; }
				if (!paths_to_scan.TryTake(out pathmap))
				{
					ScanQueueWaitStatus[tid] = true;
					Thread.Sleep(500);
					should_wait_for_queue = ScanQueueWaitStatus.Values.Count(waiting => waiting ?? true) < ScanQueueWaitStatus.Count;
					if (should_wait_for_queue)
						continue;
					break;
				}

				try { ct.ThrowIfCancellationRequested(); }
				catch (Exception) { ScanQueueWaitStatus[tid] = null; throw; }
				string remotepath = pathmap.RemoteFullPath;
				string localpath = pathmap.LocalFullPath;
				PathMapping _pathmap;
				FtpListItem[] scanned_files;
				int scan_attempts = 0;
				int timeouts = 0;

				try { ct.ThrowIfCancellationRequested(); }
				catch (Exception) { ScanQueueWaitStatus[tid] = null; throw; }
				try { scanned_files = download_client.GetListing(remotepath); }
				catch (Exception ex) when (ex is FtpCommandException or IOException or System.Net.Sockets.SocketException)
				{
					paths_to_scan.Add(pathmap);
					scan_attempts++;
					Thread.Sleep(100);
					if (scan_attempts == 3)
					{
						download_client.Dispose();
						ScanQueueWaitStatus[tid] = null;
						throw;
					}
					continue;
				}
				catch (FtpException fex) when (fex.Message != last_ftp_exception?.Message)
				{
					accumulated_exceptions.Add(fex);
					paths_to_scan.Add(pathmap);
					continue;
				}
				catch (TimeoutException)
				{
					paths_to_scan.Add(pathmap);
					timeouts++;
					Thread.Sleep(100);
					if (timeouts == 5)
					{
						download_client.Dispose();
						ScanQueueWaitStatus[tid] = null;
						throw;
					}
					continue;
				}
				if (scanned_files.Length == 0)
				{
					string empty_dir = pathmap.RemoteFullPath;
					EmptyDirs[empty_dir] = EmptyDirs.TryGetValue(empty_dir, out int retries) ? retries + 1 : 1;
					if (EmptyDirs[empty_dir] > 2)
						_ = EmptyDirs.TryRemove(empty_dir, out _);
					else
						paths_to_scan.Add(pathmap);
					continue;
				}

				if (EmptyDirs.TryGetValue(pathmap.RemoteFullPath, out int _))
					logger.Warn($"[Managed thread {tid}] Recovered from a faulty directory listing.");
				accumulated_exceptions.Clear();
				scan_attempts = 0;
				if (scanned_files is null)
					throw new FTPOperationException($"Tried to get a listing for {pathmap.RemoteFullPath} but apparently it doesn't exist.");
				foreach (FtpListItem item in scanned_files)
					switch (item.Type)
					{
						case FtpObjectType.File:
							_pathmap = pathmap with { LocalRelativePath = string.Join('\\', pathmap.LocalRelativePath, item.Name), RemoteRelativePath = string.Join('/', pathmap.RemoteRelativePath, item.Name), FileSize = item.Size };
							_ = files_found.TryAdd(_pathmap.LocalFullPathLower, _pathmap);
							break;
						case FtpObjectType.Directory:
							_pathmap = pathmap with { LocalRelativePath = string.Join('\\', pathmap.LocalRelativePath, item.Name), RemoteRelativePath = string.Join('/', pathmap.RemoteRelativePath, item.Name) };
							paths_to_scan.Add(_pathmap);
							break;
						case FtpObjectType.Link:
							break;
					}
			}
			ScanQueueWaitStatus[tid] = null;
			download_client.Dispose();
			try { ct.ThrowIfCancellationRequested(); }
			catch (Exception) { throw; }
		}
		catch (Exception) { throw; }
	}

	[Obsolete("This should no longer be needed with the new simplified configuration and is likely to be removed in a future release.")]
	public static List<string> SanityCheckBaseDirectories(IEnumerable<PathMapping> entries_to_check, Configuration.RepoConnectionInfo repoinfo)
	{
		List<string> bad_entries = new();
		using FtpClient sanity_client = SetupFTPClient(repoinfo);
		sanity_client.Connect();
		foreach (PathMapping entry in entries_to_check)
			if (!sanity_client.FileExists(entry.RemoteFullPath))
				bad_entries.Add(entry.RemoteFullPath);
		sanity_client.Disconnect();
		sanity_client.Dispose();
		return bad_entries;
	}

	public static void DownloadFileChunks(Configuration.RepoConnectionInfo repoinfo, in ConcurrentQueue<DownloadChunk> chunks, in Action<ChunkDownloadProgressInformation, string> reportprogress, CancellationToken ct)
	{
		int tid = Environment.CurrentManagedThreadId;
		byte[] buffer = new byte[Options.BufferSize];
		FileStream local_filestream = null!;
		using FtpClient client = SetupFTPClient(repoinfo);
		void Cleanup() { client.Dispose(); local_filestream.Dispose(); }
		ChunkDownloadProgressInformation? progressinfo = null;
		DownloadChunk chunk = default;
		Stopwatch CurrentStopwatch = new();
		bool canceled = ct.IsCancellationRequested;
		bool is_reported_or_just_starting = true;
		while (!canceled)
		{
			if (!is_reported_or_just_starting)
				Debugger.Break();
			is_reported_or_just_starting = false;
			if (!TryConnect(client))
			{
				logger.Warn($"[Managed thread {tid}] An FTP connection failed to be established.");
				throw new FTPConnectionException();
			}
			if (!chunks.TryDequeue(out chunk))
			{
				logger.Debug($"[Managed thread {tid}] No more chunks left.");
				break;
			}
			logger.Debug($"A chunk was taken: {chunk.FileName} at {chunk.Offset}");
			if (chunk.LocalPath != local_filestream?.Name)
			{
				local_filestream?.Dispose();
				_ = Directory.CreateDirectory(Path.GetDirectoryName(chunk.LocalPath)!);
				local_filestream = new(chunk.LocalPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
			}
			Stream ftp_filestream;
			int connection_retries = 0;
			while (true)
			{
				try { ftp_filestream = client.OpenRead(chunk.RemotePath, FtpDataType.Binary, chunk.Offset, chunk.Offset + chunk.Length); break; }
				catch (Exception) when (connection_retries <= 2) { connection_retries++; }
				catch (Exception ex)
				{
					Cleanup();
					logger.Debug(ex, $"[Managed thread {tid}] A chunk was requeued because of a failed FTP download connection: {chunk.FileName} at {chunk.Offset}");
					chunks.Enqueue(chunk);
					throw new FTPOperationException($"The FTP data stream could not be read for {chunk.FileName} at {chunk.Offset}", ex);
				}
			}
			local_filestream.Lock(chunk.Offset, chunk.Length);
			local_filestream.Position = chunk.Offset;
			int total_chunk_bytes = 0;
			int remaining_bytes = chunk.Length;
			int bytes_to_process = 0;
			Stopwatch write_time = new();
			is_reported_or_just_starting = true;
			while (true)
			{
				if (!is_reported_or_just_starting)
					Debugger.Break();
				is_reported_or_just_starting = false;
				if (ct.IsCancellationRequested)
				{
					logger.Debug($"[Managed thread {tid}] Cancellation was requested after an FTP download connection was established.");
					canceled = true;
					break;
				}
				bytes_to_process = (buffer.Length < remaining_bytes) ? buffer.Length : remaining_bytes;
				try
				{
					ftp_filestream.ReadExactly(buffer, 0, bytes_to_process);
					remaining_bytes -= bytes_to_process;
					total_chunk_bytes += bytes_to_process;
					local_filestream.Write(buffer, 0, bytes_to_process);
				}
				catch (Exception ex)
				{
					ftp_filestream.Dispose();
					local_filestream.Unlock(chunk.Offset, chunk.Length);
					local_filestream.Dispose();
					client.Disconnect();
					client.Dispose();
					string message = ex switch
					{
						OperationCanceledException => "The download operation was canceled.",
						FTPConnectionException => "Could not connect to the server.",
						FTPTaskAbortedException => "write timeout",
						_ => $"Unexpected error: {ex.Message}"
					};
					logger.Debug($"[Managed thread {tid}] A write operation failed. ({message})");
					throw;
				}
				progressinfo = new()
				{
					BytesDownloaded = bytes_to_process,
					TimeElapsed = CurrentStopwatch.Elapsed,
					TotalChunkSize = chunk.Length
				};
				local_filestream.Flush();
				is_reported_or_just_starting = true;
				if (total_chunk_bytes > chunk.Length) { throw new BDSMInternalFaultException("Total chunk bytes is greater than the chunk length."); }
				if (total_chunk_bytes == chunk.Length) break;
				reportprogress((ChunkDownloadProgressInformation)progressinfo!, chunk.LocalPath);
				if (ct.IsCancellationRequested)
				{
					logger.Debug($"[Managed thread {tid}] Cancellation was requested after a chunk download was complete.");
					canceled = true;
					break;
				}
			}
			try { local_filestream.Unlock(chunk.Offset, chunk.Length); }
			catch (IOException ex) when (ex.Message.StartsWith("The segment is already unlocked.")) { }
			ftp_filestream.Dispose();
			CurrentStopwatch.Stop();
			if (!canceled)
			{
				logger.Debug($"[Managed thread {tid}] Reporting completion of a chunk (line 293): {chunk.FileName} at {chunk.Offset}");
				reportprogress((ChunkDownloadProgressInformation)progressinfo!, chunk.LocalPath);
				is_reported_or_just_starting = true;
			}
			logger.Debug($"""[Managed thread {tid}] Exiting chunk processing loop{(canceled ? " (canceled)" : "")}: {chunk.FileName} at {chunk.Offset}""");
		}
		try { local_filestream?.Unlock(chunk.Offset, chunk.Length); }
		catch (IOException ex) when (ex.Message.StartsWith("The segment is already unlocked.")) { }
		local_filestream?.Flush();
		local_filestream?.Dispose();
		client.Disconnect();
		client.Dispose();
		if (chunk.FileName is not null)
		{
			logger.Debug($"[Managed thread {tid}] Reporting completion of a chunk (line 310): {chunk.FileName} at {chunk.Offset}");
			reportprogress((ChunkDownloadProgressInformation)progressinfo!, chunk.LocalPath);
			is_reported_or_just_starting = true;
		}
	}
}
