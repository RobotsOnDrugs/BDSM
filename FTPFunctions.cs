using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using FluentFTP;

using static BDSM.Exceptions;

namespace BDSM;
public static class FTPFunctions
{
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
						throw new BDSMInternalFaultException("Error handling a connection failure.", ex);
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
		void ErrorOut(Exception ex) { scanclient.Dispose(); throw ex; }
		while (!ct.IsCancellationRequested)
		{
			ScanQueueWaitStatus[tid] = false;
			if (accumulated_exceptions.Count > 2)
				ErrorOut(new AggregateException(accumulated_exceptions));
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
		byte[] buffer = new byte[65536];
		FileStream local_filestream = null!;
		using FtpClient client = SetupFTPClient(repoinfo);
		void Cleanup() { client.Dispose(); local_filestream.Dispose(); }
		ChunkDownloadProgressInformation progressinfo;
		while (!ct.IsCancellationRequested)
		{
			if (!TryConnect(client))
				throw new FTPConnectionException();
			if (!chunks.TryDequeue(out DownloadChunk chunk))
				break;
			if (chunk.LocalPath != local_filestream?.Name)
			{
				local_filestream?.Dispose();
				_ = Directory.CreateDirectory(Path.GetDirectoryName(chunk.LocalPath)!);
				local_filestream = new(chunk.LocalPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
			}
			Stopwatch CurrentStopwatch = new();
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
				catch (Exception) { Cleanup(); throw; }
			}
			local_filestream.Lock(chunk.Offset, chunk.Length);
			local_filestream.Position = chunk.Offset;
			int total_chunk_bytes = 0;
			int remaining_bytes = chunk.Length;
			int bytes_to_process = 0;
			while ((total_chunk_bytes < chunk.Length) && !ct.IsCancellationRequested)
			{
				CurrentStopwatch.Restart();
				bytes_to_process = (buffer.Length < remaining_bytes) ? buffer.Length : remaining_bytes;
				try
				{
					ftp_filestream.ReadExactly(buffer, 0, bytes_to_process);
					remaining_bytes -= bytes_to_process;
					total_chunk_bytes += bytes_to_process;
					local_filestream.Write(buffer, 0, bytes_to_process);
				}
				catch (Exception)
				{
					local_filestream.Unlock(chunk.Offset, chunk.Length);
					Cleanup();
					CurrentStopwatch.Stop();
					throw;
				}
				progressinfo = new ()
				{
					BytesDownloaded = bytes_to_process,
					TimeElapsed = CurrentStopwatch.Elapsed,
					TotalChunkSize = chunk.Length
				};
				local_filestream.Flush();
				reportprogress(progressinfo, chunk.LocalPath);
			}
			local_filestream.Unlock(chunk.Offset, chunk.Length);
			ftp_filestream.Dispose();
			CurrentStopwatch.Stop();
		}
		local_filestream?.Flush();
		local_filestream?.Dispose();
		client.Disconnect();
		client.Dispose();
	}
}
