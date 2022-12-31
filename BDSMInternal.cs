using System.Collections.Immutable;

using NLog;
using Spectre.Console;

using static BDSM.Configuration;
using static BDSM.Exceptions;
using static BDSM.BetterRepackRepositoryDefinitions;

namespace BDSM;
public static partial class BDSM
{
#if DEBUG
	private static void RaiseInternalFault(ILogger logger, string message) { logger.Debug(message); System.Diagnostics.Debugger.Break(); }
#else
	private static void RaiseInternalFault(ILogger logger, string message)
	{
		BDSMInternalFaultException int_ex = new(message);
		LoggingConfiguration.LogException(logger, int_ex);
		throw int_ex;
	}
#endif
	private static List<Task> ProcessTasks(List<Task> tasks, CancellationTokenSource cts)
	{
		bool canceled = false;
		bool all_faulted = true;
		void CtrlCHandler(object sender, ConsoleCancelEventArgs args) { cts.Cancel(); canceled = true; args.Cancel = true; }
		Console.CancelKeyPress += CtrlCHandler!;
		List<Task> finished_tasks = new(tasks.Count);
		List<AggregateException> exceptions = new();
		while (tasks.Count != 0)
		{
			int completed_task_idx = Task.WaitAny(tasks.ToArray());
			Task completed_task = tasks[completed_task_idx];
			switch (completed_task.Status)
			{
				case TaskStatus.RanToCompletion or TaskStatus.Canceled:
					break;
				case TaskStatus.Faulted:
					AggregateException taskex = completed_task.Exception!;
					exceptions.Add(taskex);
					if (taskex.InnerException is not FTPConnectionException)
						cts.Cancel();
					break;
				default:
					exceptions.Add(new AggregateException(new BDSMInternalFaultException("Internal error while processing task exceptions.")));
					break;
			}
			finished_tasks.Add(completed_task);
			tasks.RemoveAt(completed_task_idx);
		}
		Console.CancelKeyPress -= CtrlCHandler!;
		if (canceled) throw new OperationCanceledException();
		foreach (Task task in finished_tasks)
			if (task.Status == TaskStatus.RanToCompletion)
				all_faulted = false;
		return all_faulted ? throw new AggregateException(exceptions) : finished_tasks;
	}
	private static FullUserConfiguration GenerateNewUserConfig()
	{
		bool is_hs2;
		while (true)
		{
			string gamepath = AnsiConsole.Ask<string>("Where is your game located? (e.g. [turquoise2]D:\\HS2[/])");
			bool? _is_hs2 = GamePathIsHS2(gamepath);
			if (_is_hs2 is not null)
			{
				is_hs2 = (bool)_is_hs2;
				AnsiConsole.MarkupLine($"- [green]Looks like {(is_hs2 ? "Honey Select 2" : "AI-Shoujo")}[/] -");
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
			AnsiConsole.MarkupLine("- [green]New user configuration successfully created![/] -");
			return userconfig;
		}
	}
}
