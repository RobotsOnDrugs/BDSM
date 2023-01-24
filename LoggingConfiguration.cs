using BDSM.Lib;

using NLog;
using NLog.Targets;

using Spectre.Console;

namespace BDSM;
internal static class LoggingConfiguration
{
	private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
	internal static void InitalizeLibraryLoggers(ILogger logger)
	{
		FTPFunctions.InitializeLogger(logger);
		Configuration.InitializeLogger(logger);
		DownloadProgress.InitializeLogger(logger);
		FileDownloadProgressInformation.InitializeLogger(logger);
	}
	internal static readonly FileTarget default_logfile_config = new("logfile")
	{
		Layout = NLog.Layouts.Layout.FromString("[${longdate}]${when:when=exception != null: [${callsite-filename}${literal:text=\\:} ${callsite-linenumber}]} ${level}: ${message}${exception:format=@}"),
		FileName = "BDSM.log",
		Footer = NLog.Layouts.Layout.FromString("[${longdate}] ${level}: == End BDSM log =="),
		ArchiveOldFileOnStartupAboveSize = 1024 * 1024
	};
	internal static readonly FileTarget debug_or_trace_logfile_config = new("logfile")
	{
		Layout = NLog.Layouts.Layout.FromString("[${longdate}] [${callsite-filename:includeSourcePath=false}${literal:text=\\:} line ${callsite-linenumber}] ${level}: ${message}${exception:format=@}"),
		FileName = "BDSM.debug.log",
		Footer = NLog.Layouts.Layout.FromString("[${longdate}] ${level}: == End BDSM debug log =="),
		ArchiveOldFileOnStartupAboveSize = 1024 * 1024
	};
	internal static NLog.Config.LoggingConfiguration LoadCustomConfiguration(out bool is_custom, string loglevel_name = "Info") => LoadCustomConfiguration(out is_custom, LogLevel.FromString(loglevel_name));
	internal static NLog.Config.LoggingConfiguration LoadCustomConfiguration(out bool is_custom, LogLevel loglevel)
	{
		logger.Debug("Loading BDSM log configuration.");
		NLog.Config.LoggingConfiguration config;
		is_custom = File.Exists("nlog.config");
		if (is_custom)
			config = new NLog.Config.XmlLoggingConfiguration("nlog.config");
		else
		{
			config = new();
			LogLevel base_loglevel = loglevel.Ordinal switch
			{
				< 3 => LogLevel.Info,
				_ => loglevel
			};
			config.AddRule(base_loglevel, LogLevel.Fatal, default_logfile_config);
			if (loglevel.Ordinal is 0 or 1)
				config.AddRule(LogLevel.Debug, LogLevel.Fatal, debug_or_trace_logfile_config);
			if (loglevel.Ordinal is 0)
				config.AddRule(LogLevel.Trace, LogLevel.Fatal, debug_or_trace_logfile_config);
		}
		return config;
	}
	internal static void LogWithMarkup(ILogger logger, LogLevel log_level, string message, string? custom_color_name = null, bool newline = true)
	{
		string markup_text = message;
		markup_text = custom_color_name is not null
			? markup_text.Colorize(custom_color_name)
			: log_level.Ordinal switch
			{
				3 => message.Colorize(BDSM.WarningColor),
				4 or 5 => message.Colorize(BDSM.ErrorColor),
				_ => message
			};
		if (newline) AnsiConsole.MarkupLine(markup_text);
		else AnsiConsole.Markup(markup_text);
		logger.Log(log_level, message);
	}
	internal static void LogMarkupText(ILogger logger, LogLevel log_level, string markup_text)
	{
		AnsiConsole.MarkupLine(markup_text);
		logger.Log(log_level, markup_text.RemoveMarkup());
	}
	internal static void LogExceptionAndDisplay(ILogger logger, Exception ex) => LogExceptionAndDisplay(logger, LogLevel.Error, ex);
	internal static void LogExceptionAndDisplay(ILogger logger, LogLevel log_level, Exception ex)
	{
		AnsiConsole.WriteException(ex);
		logger.Log(log_level, ex);
	}
}
