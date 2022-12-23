using NLog;

using Spectre.Console;

namespace BDSM;
internal static class LoggingConfiguration
{
	internal static NLog.Config.LoggingConfiguration LoadCustomConfiguration(out bool is_custom)
	{
		NLog.Config.LoggingConfiguration config;
		try
		{
			config = new NLog.Config.XmlLoggingConfiguration("nlog.config");
			is_custom = true;
			return config;
		}
		catch (Exception ex) when (ex is NLogConfigurationException or FileNotFoundException)
		{
			config = new();
			NLog.Targets.FileTarget default_logfile_config = new("logfile")
				{
					Layout = NLog.Layouts.Layout.FromString("[${longdate}] ${level}: ${message}"),
					FileName = "BDSM.log",
					Footer = NLog.Layouts.Layout.FromString("== End BDSM log =="),
					ArchiveOldFileOnStartupAboveSize = 1024 * 1024
				};
			config.AddRule(LogLevel.Info, LogLevel.Fatal, default_logfile_config);
			is_custom = false;
			return config;
		}
	}
	internal static void LogMarkupText(ILogger logger, LogLevel log_level, string markup_text)
	{
		AnsiConsole.MarkupLine(markup_text);
		logger.Log(log_level, Markup.Remove(markup_text));
	}
	internal static void LogException(ILogger logger, Exception ex) => LogException(logger, LogLevel.Error, ex);
	internal static void LogException(ILogger logger, LogLevel log_level, Exception ex)
	{
		AnsiConsole.WriteException(ex);
		logger.Log(log_level, ex);
	}
}
