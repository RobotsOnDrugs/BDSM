using System.Collections.Immutable;

using NLog;
using Spectre.Console;

using static BDSM.Lib.Configuration;
using static BDSM.Lib.Exceptions;
using static BDSM.Lib.BetterRepackRepositoryDefinitions;
using FluentFTP.Exceptions;

namespace BDSM;
public static partial class BDSM
{
	// https://spectreconsole.net/appendix/colors
	public const string HighlightColor = "orchid2";
	public const string ModpackNameColor = "springgreen1";
	public const string FileListingColor = "skyblue1";
	public const string SuccessColor = "green1";
	public const string WarningColor = "yellow3_1";
	public const string CancelColor = "gold3_1";
	public const string ErrorColor = "red1";
	public const string ErrorColorAlt = "red3";
	public const string DeleteColor = "orangered1";
	public static string Colorize<T1>(this T1 plain, Color color) => $"[{color.ToMarkup()}]{plain}[/]";
	public static string Colorize<T1>(this T1 plain, string color_name) => $"[{color_name}]{plain}[/]";

	internal readonly record struct DownloadCategories
	{
		internal required IEnumerable<KeyValuePair<string, FileDownloadProgressInformation>> QueuedDownloads { get; init; }
		internal required IEnumerable<KeyValuePair<string, FileDownloadProgressInformation>> CanceledDownloads { get; init; }
		internal required IEnumerable<KeyValuePair<string, FileDownloadProgressInformation>> CompletedDownloads { get; init; }
		internal required IEnumerable<KeyValuePair<string, FileDownloadProgressInformation>> FailedDownloads { get; init; }
		internal IEnumerable<KeyValuePair<string, FileDownloadProgressInformation>> UnfinishedDownloads => QueuedDownloads.Concat(CanceledDownloads);
	}
	internal class BDSMInternalFaultException : Exception
	{
		const string BugReportSuffix = " Please file a bug report and provide this information. https://github.com/RobotsOnDrugs/BDSM/issues\r\n";
		internal BDSMInternalFaultException() { }
		internal BDSMInternalFaultException(string? message) : base(message + BugReportSuffix) { }
		internal BDSMInternalFaultException(string? message, bool include_bug_report_link) : base(message + (include_bug_report_link ? "" : BugReportSuffix)) { }
		internal BDSMInternalFaultException(string? message, Exception? innerException) : base(message, innerException) { }
	}
#if DEBUG
	private static void RaiseInternalFault(ILogger logger, string message) { logger.Debug(message); System.Diagnostics.Debugger.Break(); }
#else
	private static void RaiseInternalFault(ILogger logger, string message)
	{
		BDSMInternalFaultException int_ex = new(message);
		LoggingConfiguration.LogExceptionAndDisplay(logger, int_ex);
		throw int_ex;
	}
#endif
	private static List<Task> ProcessTasks(List<Task> tasks, CancellationTokenSource cts)
	{
		bool user_canceled = false;
		void CtrlCHandler(object sender, ConsoleCancelEventArgs args) { cts.Cancel(false); user_canceled = true; args.Cancel = true; LogManager.Shutdown(); }
		Console.CancelKeyPress += CtrlCHandler!;
		List<Task> finished_tasks = new(tasks.Count);
		List<AggregateException> exceptions = new();
		do
		{
			int completed_task_idx = Task.WaitAny(tasks.ToArray());
			Task completed_task = tasks[completed_task_idx];
			switch (completed_task.Status)
			{
				case TaskStatus.RanToCompletion:
					break;
				case TaskStatus.Canceled:
					logger.Log(LogLevel.Info, completed_task.Exception, "A task was canceled.");
					break;
				case TaskStatus.Faulted:
					AggregateException taskex = completed_task.Exception!;
					exceptions.Add(taskex);
					Exception innerex = taskex.InnerException!;
					logger.Log(LogLevel.Warn, taskex.Flatten().InnerException, "A task was faulted.");
					if (innerex is FtpCommandException fcex && fcex.CompletionCode is "421")
						break;
					if (innerex is not FTPOperationException)
						cts.Cancel();
					break;
				default:
					exceptions.Add(new AggregateException(new BDSMInternalFaultException("Internal error while processing task exceptions.")));
					break;
			}
			finished_tasks.Add(completed_task);
			tasks.RemoveAt(completed_task_idx);
		}
		while (tasks.Count != 0);
		Console.CancelKeyPress -= CtrlCHandler!;
		return !user_canceled ? finished_tasks : throw new OperationCanceledException();
	}
	private static FullUserConfiguration GenerateNewUserConfig()
	{
		bool is_hs2;
		while (true)
		{
			string gamepath = AnsiConsole.Ask<string>("Where is your game located? (e.g. " + "D:\\HS2".Colorize(FileListingColor) + ")");
			bool? _is_hs2 = GamePathIsHS2(gamepath);
			if (_is_hs2 is not null)
			{
				is_hs2 = (bool)_is_hs2;
				AnsiConsole.MarkupLine($"- Looks like {(is_hs2 ? "Honey Select 2" : "AI-Shoujo")} -".Colorize(SuccessColor));
			}
			else
			{
				AnsiConsole.MarkupLine($"{gamepath} doesn't appear to be a valid game directory.");
				if (AnsiConsole.Confirm("Enter a new game folder?"))
					continue;
				throw new OperationCanceledException("User canceled user configuration creation.");
			}
			bool studio = AnsiConsole.Confirm("Download studio mods?", DefaultModpacksSimpleHS2.Studio);
			bool studio_maps = AnsiConsole.Confirm("Download extra studio maps?", studio);
			bool hs2_maps = is_hs2 && AnsiConsole.Confirm("Download extra main game maps?", DefaultModpacksSimpleHS2.StudioMaps);
			bool bleedingedge = AnsiConsole.Confirm("Download bleeding edge mods? (Warning: these can break things)", DefaultModpacksSimpleHS2.BleedingEdge);
			bool userdata = AnsiConsole.Confirm("Download modpack user data such as character and clothing cards?", DefaultModpacksSimpleHS2.Userdata);
			bool prompt_to_continue = AnsiConsole.Confirm("Pause between steps to review information? (recommended)", DefaultPromptToContinue);
			SimpleUserConfiguration.Modpacks desired_modpacks = DefaultModpacksSimpleHS2 with { Studio = studio, StudioMaps = studio_maps, HS2Maps = hs2_maps, BleedingEdge = bleedingedge, Userdata = userdata };
			ImmutableHashSet<string> desired_modpack_names = GetDesiredModpackNames(is_hs2, desired_modpacks);
			RepoConnectionInfo connection_info = DefaultConnectionInfo;
			FullUserConfiguration userconfig = new()
			{
				GamePath = gamepath,
				ConnectionInfo = connection_info,
				BasePathMappings = ModpackNamesToPathMappings(desired_modpack_names, gamepath, connection_info.RootPath),
				PromptToContinue = prompt_to_continue
			};
			SerializeUserConfiguration(FullUserConfigurationToSimple(userconfig));
			AnsiConsole.MarkupLine("- New user configuration successfully created! -".Colorize(SuccessColor));
			return userconfig;
		}
	}
}
