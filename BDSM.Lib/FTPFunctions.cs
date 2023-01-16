using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

using NLog;
using FluentFTP;

using static BDSM.Lib.Exceptions;

namespace BDSM.Lib;
public static class FTPFunctions
{
	private static readonly FTPFunctionOptions Options = new();
	private static readonly ConcurrentDictionary<int, bool> ScanQueueWaitStatus = new();
	public readonly record struct FTPFunctionOptions
	{
		public FTPFunctionOptions() { }
		public int BufferSize { get; init; } = 65536;
	}
	private static ILogger logger = LogManager.CreateNullLogger();
	public static void InitializeLogger(ILogger parent) => logger = parent;
	public static FtpClient SetupFTPClient(Configuration.RepoConnectionInfo repoinfo) =>
		new(repoinfo.Address, repoinfo.Username, repoinfo.Password, repoinfo.Port) { Config = BetterRepackRepositoryDefinitions.DefaultRepoConnectionConfig, Encoding = Encoding.UTF8 };
	public static FtpClient DefaultSideloaderClient() => SetupFTPClient(BetterRepackRepositoryDefinitions.DefaultConnectionInfo);
	public static bool TryConnect(FtpClient client, int max_retries = 3)
	{
		bool success = false;
		int retries = 0;
		while (retries <= max_retries)
		{
			logger.Debug($"TryConnect hit with {retries} retries.");
			try
			{
				client.Connect();
				success = true;
				logger.Debug("An FTP connection was successful");
				break;
			}
			catch (FtpCommandException ex)
			{
				switch (ex.ResponseType)
				{
					case FtpResponseType.TransientNegativeCompletion:
						Thread.Sleep(1000);
						retries++;
						break;
					case FtpResponseType.PermanentNegativeCompletion:
						throw;
					case FtpResponseType.PositivePreliminary or FtpResponseType.PositiveCompletion or FtpResponseType.PositiveIntermediate:
						logger.Warn("FtpCommandException was thrown with a positive response.");
						logger.Log(LogLevel.Error, ex);
						Debugger.Break();
						retries++;
						break;
					default:
						throw;
				}
			}
		}
		if (!success)
			logger.Debug("Failed to establish an FTP connection.");
		return success;
	}
	public static void GetFilesOnServer(ref ConcurrentBag<PathMapping> paths_to_scan, ref ConcurrentDictionary<string, PathMapping> files_found, Configuration.RepoConnectionInfo repoinfo, CancellationToken ct)
	{
		int tid = Environment.CurrentManagedThreadId;
		ScanQueueWaitStatus[tid] = true;
		bool should_wait_for_queue = true;
		if (ct.IsCancellationRequested) return;
		ConcurrentBag<PathMapping> files = new();
		PathMapping pathmap = default;
		List<string> missed_ftp_entries = new();
		FtpException? last_ftp_exception = null;
		List<Exception> accumulated_exceptions = new();
		bool retrying_scan = false;
		using FtpClient download_client = SetupFTPClient(repoinfo);
		if (!TryConnect(download_client))
			throw new FTPConnectionException();

		ScanQueueWaitStatus[tid] = true;
		while (!ct.IsCancellationRequested)
		{
			ScanQueueWaitStatus[tid] = false;
			if (accumulated_exceptions.Count > 2)
			{
				download_client.Dispose();
				throw new AggregateException(accumulated_exceptions);
			}
			if (!retrying_scan)
			{
				if (!paths_to_scan.TryTake(out pathmap))
				{
					ScanQueueWaitStatus[tid] = true;
					should_wait_for_queue = ScanQueueWaitStatus.Values.Count(waiting => waiting) != ScanQueueWaitStatus.Count;
					if (should_wait_for_queue)
						continue;
					break;
				}
				retrying_scan = false;
			}
			ScanQueueWaitStatus[tid] = false;
			string remotepath = pathmap.RemoteFullPath;
			string localpath = pathmap.LocalFullPath;
			PathMapping _pathmap;
			FtpListItem[] scanned_files;
			int scan_attempts = 0;
			ct.ThrowIfCancellationRequested();
			try
			{
				scanned_files = download_client.GetListing(remotepath);
				retrying_scan = false;
			}
			catch (Exception ex) when (ex is FtpCommandException or IOException)
			{
				scan_attempts++;
				Thread.Sleep(500);
				if (scan_attempts == 3)
				{
					paths_to_scan.Add(pathmap);
					download_client.Dispose();
					throw;
				}
				continue;
			}
			catch (FtpException ex) when (ex.Message != last_ftp_exception?.Message)
			{
				accumulated_exceptions.Add(ex);
				continue;
			}
			accumulated_exceptions.Clear();
			retrying_scan = false;
			scan_attempts = 0;
			if (scanned_files is null)
			{
				paths_to_scan.Add(pathmap);
				continue;
			}
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
		download_client.Dispose();
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
				catch (Exception)
				{
					Cleanup();
					logger.Debug($"[Managed thread {tid}] A chunk was requeued because of failed FTP download connection: {chunk.FileName} at {chunk.Offset}");
					chunks.Enqueue(chunk);
					throw;
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
			if (!is_reported_or_just_starting)
				Debugger.Break();
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
