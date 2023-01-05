using NLog;

namespace BDSM.Lib;
public class ConsolidatedLogger
{
	private readonly ILogger logger;
	public ConsolidatedLogger(ILogger logger) => this.logger = logger;
	public void Log(LogLevel loglevel, string message) => logger.Log(loglevel, message);
	public void Trace(string message, string? prefix = null) { message = $"{prefix + " " + message ?? message}"; Log(LogLevel.Trace, message); }
	public void Debug(string message, string? prefix = null) { message = prefix + " " + message ?? message; Log(LogLevel.Debug, message); }
	public void Info(string message, string? prefix = null) { message = prefix + " " + message ?? message; Log(LogLevel.Info, message); }
	public void Warn(string message, string? prefix = null) { message = prefix + " " + message ?? message; Log(LogLevel.Warn, message); }
	public void Error(string message, string? prefix = null) { message = prefix + " " + message ?? message; Log(LogLevel.Error, message); }
	public void Fatal(string message, string? prefix = null) { message = prefix + " " + message ?? message; Log(LogLevel.Fatal, message); }
	public static void LogException(ILogger logger, LogLevel log_level, Exception ex) => logger.Log(log_level, ex);
	public static void LogException(ILogger logger, Exception ex) => LogException(logger, LogLevel.Error, ex);
}
