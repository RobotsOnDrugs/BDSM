using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using static BDSM.Configuration;

namespace BDSM;

public static class UtilityFunctions
{
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
			if (!new DirectoryInfo(_pathmap.LocalFullPath).Exists)
				_ = Directory.CreateDirectory(_pathmap.LocalFullPath);
			_pathmappings.Add(_pathmap);
		}
		return _pathmappings;
	}

	public static string FormatBytes(double number_of_bytes)
	{
		number_of_bytes = Math.Round(number_of_bytes, 2);
		return number_of_bytes switch
		{
			< 1100 * 1 => $"{number_of_bytes} B",
			< 1100 * 1024 => $"{Math.Round(number_of_bytes / 1024, 2)} KiB",
			< 1100 * 1024 * 1024 => $"{Math.Round(number_of_bytes / (1024 * 1024), 2)} MiB",
			>= 1100 * 1024 * 1024 => $"{Math.Round(number_of_bytes / (1024 * 1024 * 1024), 2)} GiB",
			double.NaN => "unknown"
		};
	}
	public static void PromptUserToContinue()
	{
		Console.WriteLine("Press any key to continue or Ctrl-C to abort");
		_ = Console.ReadKey();
	}
	public static void PromptBeforeExit()
	{
		Console.WriteLine("Press any key to exit.");
		_ = Console.ReadKey();
	}
}