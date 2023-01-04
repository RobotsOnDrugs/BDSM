using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using FluentFTP;

using static BDSM.Exceptions;

namespace BDSM;
public static class FTPFunctions
{
	public const int BufferSize = 65536;
	private static readonly ConcurrentDictionary<int, bool> ScanQueueWaitStatus = new();
	public static FtpClient SetupFTPClient(Configuration.RepoConnectionInfo repoinfo) =>
		new(repoinfo.Address, repoinfo.Username, repoinfo.EffectivePassword, repoinfo.Port) { Config = BetterRepackRepositoryDefinitions.DefaultRepoConnectionConfig, Encoding = Encoding.UTF8 };
	public static FtpClient DefaultSideloaderClient() => SetupFTPClient(BetterRepackRepositoryDefinitions.DefaultConnectionInfo);
	public static bool TryConnect(FtpClient client, int max_retries = 3)
	{
		bool success = false;
		int retries = 0;
		while (retries <= max_retries)
		{
			try
			{
				client.Connect();
				success = true;
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
						// log this
						Debugger.Break();
						retries++;
						break;
					default:
						throw;
				}
			}
		}
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
		using FtpClient scanclient = SetupFTPClient(repoinfo);
		if (!TryConnect(scanclient))
			throw new FTPConnectionException();
		ScanQueueWaitStatus[tid] = true;
		while (!ct.IsCancellationRequested)
		{
			ScanQueueWaitStatus[tid] = false;
			if (accumulated_exceptions.Count > 2)
			{
				scanclient.Dispose();
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
				scanned_files = scanclient.GetListing(remotepath);
				retrying_scan = false;
			}
			catch (Exception ex) when (ex is FtpCommandException or IOException)
			{
				scan_attempts++;
				Thread.Sleep(500);
				if (scan_attempts == 3)
				{
					paths_to_scan.Add(pathmap);
					scanclient.Dispose();
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
		scanclient.Dispose();
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

	public static void DownloadFileChunks(Configuration.RepoConnectionInfo repoinfo, in ConcurrentQueue<DownloadChunk> chunks, Action<ChunkDownloadProgressInformation, string> reportprogress, CancellationToken ct)
	{
		FTPTaskAbortedException SetupAbortException(string message, string filepath, Exception? inner_ex = null)
		{
			FTPTaskAbortedException task_abort_ex = inner_ex is not null ? new(message) : new(message, inner_ex);
			task_abort_ex.Data["File"] = filepath;
			return task_abort_ex;
		}
		byte[] buffer = new byte[BufferSize];
		FileStream local_filestream = null!;
		using FtpClient client = SetupFTPClient(repoinfo);
		void Cleanup() { client.Dispose(); local_filestream.Dispose(); }
		ChunkDownloadProgressInformation? progressinfo = null;
		DownloadChunk chunk = default;
		Stopwatch CurrentStopwatch = new();
		bool canceled = ct.IsCancellationRequested;
		while (!canceled && chunks.TryDequeue(out chunk))
		{
			if (!TryConnect(client))
				throw new FTPConnectionException();
			if (!chunks.TryDequeue(out chunk))
				break;
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
				try
				{
					ftp_filestream = client.OpenRead(chunk.RemotePath, FtpDataType.Binary, chunk.Offset, chunk.Offset + chunk.Length);
					break;
				}
				catch (Exception) when (connection_retries <= 2) { connection_retries++; }
				catch (Exception)
				{
					Cleanup();
					reportprogress((ChunkDownloadProgressInformation)progressinfo!, chunk.LocalPath);
					throw;
				}
			}
			local_filestream.Lock(chunk.Offset, chunk.Length);
			local_filestream.Position = chunk.Offset;
			int total_chunk_bytes = 0;
			int remaining_bytes = chunk.Length;
			int bytes_to_process = 0;
			Stopwatch write_time = new();
			while (true)
			{
				if (ct.IsCancellationRequested)
				{
					try { local_filestream.Unlock(chunk.Offset, chunk.Length); }
					catch (IOException ex) when (ex.Message.StartsWith("The segment is already unlocked.")) { Debugger.Break(); }
					reportprogress(new ChunkDownloadProgressInformation() { BytesDownloaded = 0, TimeElapsed = CurrentStopwatch.Elapsed, TotalChunkSize = chunk.Length }, chunk.LocalPath);
					break;
				}
				if (total_chunk_bytes > chunk.Length) { throw new BDSM.BDSMInternalFaultException("total chunk bytes is greater than the chunk length"); }
				if (total_chunk_bytes == chunk.Length) break;
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
					reportprogress(new ChunkDownloadProgressInformation() { BytesDownloaded = 0, TimeElapsed = CurrentStopwatch.Elapsed, TotalChunkSize = chunk.Length }, chunk.LocalPath);
				}
				local_filestream.Unlock(chunk.Offset, chunk.Length);
				progressinfo = new()
				{
					BytesDownloaded = bytes_to_process,
					TimeElapsed = CurrentStopwatch.Elapsed,
					TotalChunkSize = chunk.Length
				};
				local_filestream.Flush();
				reportprogress((ChunkDownloadProgressInformation)progressinfo!, chunk.LocalPath);
			}
			try { local_filestream.Unlock(chunk.Offset, chunk.Length); }
			catch (IOException ex) when (ex.Message.StartsWith("The segment is already unlocked.")) { Debugger.Break(); }
			ftp_filestream.Dispose();
			CurrentStopwatch.Stop();
			reportprogress((ChunkDownloadProgressInformation)progressinfo!, chunk.LocalPath);
		}
		local_filestream?.Flush();
		local_filestream?.Dispose();
		client.Disconnect();
		client.Dispose();
		if (chunk.FileName is not null)
			reportprogress((ChunkDownloadProgressInformation)progressinfo!, chunk.LocalPath);
	}
}
