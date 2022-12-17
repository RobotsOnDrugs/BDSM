using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using FluentFTP;

using static BDSM.Configuration;

namespace BDSM;
public static class FTPFunctions
{
	private static readonly ConcurrentDictionary<int, bool> ScanQueueWaitStatus = new();
	public static FtpConfig SideloaderConfig => new()
	{
		EncryptionMode = FtpEncryptionMode.Auto,
		ValidateAnyCertificate = true,
		LogToConsole = false,
	};
	public static FtpClient DefaultSideloaderClient(RepoConnectionInfo repoinfo) =>
		new(repoinfo.Address, repoinfo.Username, repoinfo.EffectivePassword, repoinfo.Port) { Config = SideloaderConfig, Encoding = Encoding.UTF8 };
	public static FtpClient SetupFTPClient(RepoConnectionInfo repoinfo)
	{
		FtpClient client = DefaultSideloaderClient(repoinfo);
		client.Connect();
		return client;
	}
	public static ConcurrentBag<PathMapping> GetFilesOnServerIgnoreErrors(ref ConcurrentBag<PathMapping> pathmaps, RepoConnectionInfo repoinfo, CancellationToken ct) => GetFilesOnServer(ref pathmaps, repoinfo, ct).PathMappings;
	public static (ConcurrentBag<PathMapping> PathMappings, ImmutableList<string> MissedFTPEntries) GetFilesOnServer(ref ConcurrentBag<PathMapping> pathmaps, RepoConnectionInfo repoinfo, CancellationToken ct)
	{
		int tid = Environment.CurrentManagedThreadId;
		ScanQueueWaitStatus[tid] = false;
		bool should_wait_for_queue = true;
		if (ct.IsCancellationRequested)
			return (new(), ImmutableList<string>.Empty);
		ConcurrentBag<PathMapping> files = new();
		PathMapping pathmap = default;
		List<string> missed_ftp_entries = new();
		FtpException? last_ftp_exception = null;
		List<Exception> accumulated_exceptions = new();
		using FtpClient scanclient = SetupFTPClient(repoinfo);
		scanclient.Connect();

		void Cleanup() { scanclient.Disconnect(); scanclient.Dispose(); }
		void ErrorOut(Exception ex) { Cleanup(); throw ex; }
		(ConcurrentBag<PathMapping> files, ImmutableList<string> missed_ftp_entries) ExitGracefully() { Cleanup(); return (files, missed_ftp_entries.ToImmutableList()); }
		bool retrying_scan = false;
		Stopwatch waiting_timer = new();
		while (!ct.IsCancellationRequested)
		{
			ScanQueueWaitStatus[tid] = false;
			if (accumulated_exceptions.Count > 2)
				ErrorOut(new AggregateException(accumulated_exceptions));
			if (!retrying_scan)
			{
				if (!pathmaps.TryTake(out pathmap))
				{
					ScanQueueWaitStatus[tid] = true;
					should_wait_for_queue = ScanQueueWaitStatus.Values.Count(waiting => waiting) != ScanQueueWaitStatus.Count;
					if (should_wait_for_queue)
						continue;
					break;
				}
			}
			ScanQueueWaitStatus[tid] = false;
			string remotepath = pathmap.RemoteFullPath;
			string localpath = pathmap.LocalFullPath;
			PathMapping _pathmap;
			FtpListItem[]? _scanned_files = null;
			FtpListItem[] scanned_files;
			int scan_attempts = 0;
			ct.ThrowIfCancellationRequested();
			try { _scanned_files = scanclient.GetListing(remotepath); }
			catch (IOException)
			{
				scan_attempts++;
				Thread.Sleep(500);
				if (scan_attempts < 3)
					_scanned_files = null;
			}
			catch (FtpException ex) when (ex.Message != last_ftp_exception?.Message)
			{
				accumulated_exceptions.Add(ex);
				retrying_scan = true;
				continue;
			}
			accumulated_exceptions.Clear();
			retrying_scan = false;
			if (_scanned_files is null)
			{
				missed_ftp_entries.Add(remotepath);
				continue;
			}
			scanned_files = _scanned_files;
			foreach (FtpListItem item in scanned_files)
				switch (item.Type)
				{
					case FtpObjectType.File:
						_pathmap = pathmap with { LocalRelativePath = string.Join('\\', pathmap.LocalRelativePath, item.Name), RemoteRelativePath = string.Join('/', pathmap.RemoteRelativePath, item.Name), FileSize = item.Size };
						files.Add(_pathmap);
						break;
					case FtpObjectType.Directory:
						_pathmap = pathmap with { LocalRelativePath = string.Join('\\', pathmap.LocalRelativePath, item.Name), RemoteRelativePath = string.Join('/', pathmap.RemoteRelativePath, item.Name) };
						pathmaps.Add(_pathmap);
						break;
					case FtpObjectType.Link:
						break;
				}
		}
		Console.WriteLine(waiting_timer.ElapsedMilliseconds);
		ct.ThrowIfCancellationRequested();
		return ExitGracefully();
	}
	public static List<string> SanityCheckBaseDirectories(IEnumerable<PathMapping> entries_to_check, RepoConnectionInfo repoinfo)
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

	public static void DownloadFileChunk(RepoConnectionInfo repoinfo, in ConcurrentQueue<DownloadChunk> chunks, Action<ChunkDownloadProgressInformation, string> reportprogress)
	{
		byte[] buffer = new byte[65536];
		FileStream local_filestream = null!;
		using FtpClient client = SetupFTPClient(repoinfo);
		ChunkDownloadProgressInformation progressinfo;
		while (true)
		{
			client.Connect();
			if (!chunks.TryDequeue(out DownloadChunk chunk))
				break;
			if (chunk.LocalPath != local_filestream?.Name)
			{
				local_filestream?.Dispose();
				_ = Directory.CreateDirectory(Path.GetDirectoryName(chunk.LocalPath)!);
				local_filestream = new(chunk.LocalPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
			}
			Stopwatch CurrentStopwatch = new();
			using Stream ftp_filestream = client.OpenRead(chunk.RemotePath, FtpDataType.Binary, chunk.Offset, chunk.Offset + chunk.Length);
			local_filestream.Lock(chunk.Offset, chunk.Length);
			local_filestream.Position = chunk.Offset;
			int total_chunk_bytes = 0;
			int remaining_bytes = chunk.Length;
			int bytes_to_process = 0;
			while (total_chunk_bytes < chunk.Length)
			{
				CurrentStopwatch.Restart();
				bytes_to_process = (buffer.Length < remaining_bytes) ? buffer.Length : remaining_bytes;
				ftp_filestream.ReadExactly(buffer, 0, bytes_to_process);
				remaining_bytes -= bytes_to_process;
				total_chunk_bytes += bytes_to_process;
				local_filestream.Write(buffer, 0, bytes_to_process);
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
			client.Disconnect();
			CurrentStopwatch.Stop();
		}
		local_filestream?.Flush();
		local_filestream?.Dispose();
		client.Disconnect();
		client.Dispose();
	}
}
