using System.Collections.Concurrent;
using System.Collections.Immutable;

using static BDSM.Lib.Configuration;

namespace BDSM.Lib;

public static class Utility
{
	public static ConcurrentBag<PathMapping> GetPathMappingsFromSkipScanConfig(SkipScanConfiguration config, FullUserConfiguration userconfig)
	{
		ConcurrentBag<PathMapping> _mappings = new();
		foreach (string pathmap in config.FileMappings)
		{
			string[] _map_split = pathmap.Split(" | ");
			PathMapping _map = new()
			{
				RootPath = userconfig.ConnectionInfo.RootPath,
				RemoteRelativePath = _map_split[0],
				GamePath = userconfig.GamePath,
				LocalRelativePath = _map_split[1],
				FileSize = null,
				DeleteClientFiles = false
			};
			_mappings.Add(_map);
		}
		return _mappings;
	}
	public static FileDownload PathMappingToFileDownload(PathMapping pm)
	{
		List<DownloadChunk> chunks = new();
		long filesize = (long)pm.FileSize!;
		const int chunksize = 1024 * 1024 * 10;
		(long full_chunks, long remaining_bytes) = Math.DivRem(filesize, chunksize);
		long num_chunks = full_chunks + ((remaining_bytes > 0) ? 1 : 0);
		for (int i = 0; i < num_chunks; i++)
		{
			long _offset = i * (long)chunksize;
			long _remaining = filesize - _offset;
			int _length = (_remaining > chunksize) ? chunksize : (int)_remaining;
			DownloadChunk chunk = new()
			{
				LocalPath = pm.LocalFullPath,
				RemotePath = pm.RemoteFullPath,
				Offset = _offset,
				Length = _length
			};
			chunks.Add(chunk);
		}
		return new()
		{
			LocalPath = pm.LocalFullPath,
			RemotePath = pm.RemoteFullPath,
			TotalFileSize = filesize,
			ChunkSize = chunksize,
			NumberOfChunks = (int)num_chunks,
			DownloadChunks = chunks.ToImmutableArray()
		};
	}

	public static string RelativeModPathToPackName(this string relative_local_path)
	{
		string[] path_parts = relative_local_path.Split('\\');
		return path_parts[0] == "UserData" ? path_parts[0] : path_parts[1];
	}

	public static string FormatBytes(this int number_of_bytes) => FormatBytes((double)number_of_bytes);
	public static string FormatBytes(this long number_of_bytes) => FormatBytes((double)number_of_bytes);
	public static string FormatBytes(this double number_of_bytes)
	{
		return number_of_bytes switch
		{
			< 1100 * 1 => $"{Math.Round(number_of_bytes, 2):N2} B",
			< 1100 * 1024 => $"{Math.Round(number_of_bytes / 1024, 2):N2} KiB",
			< 1100 * 1024 * 1024 => $"{Math.Round(number_of_bytes / (1024 * 1024), 2):N2} MiB",
			>= 1100 * 1024 * 1024 => $"{Math.Round(number_of_bytes / (1024 * 1024 * 1024), 2):N2} GiB",
			double.NaN => "unknown"
		};
	}
	public static string Pluralize(this int quantity, string suffix) => quantity == 1 ? quantity.ToString() + " " + suffix : quantity.ToString() + " " + suffix + "s";
	public static void PromptUser(string message)
	{
		Console.Write(message);
		_ = Console.ReadKey(true);
		Console.Write('\r' + new string(' ', message.Length) + '\r');
	}
	public static void PromptUserToContinue() => PromptUser("Press any key to continue or Ctrl-C to abort.");
	public static void PromptBeforeExit() => PromptUser("Press any key to exit.");
}